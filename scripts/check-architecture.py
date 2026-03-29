#!/usr/bin/env python3
"""
Architecture guard for Babel Player.
Enforces the four-layer contract from CLAUDE.md:
  Domain (Core) <- Infrastructure <- ViewModels <- UI (Desktop)

Run from the repository root:
  python3 scripts/check-architecture.py
"""

import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

FAILED: list[str] = []


def ok(msg: str) -> None:
    print(f"  OK   {msg}")


def fail(msg: str) -> None:
    FAILED.append(msg)
    print(f"  FAIL {msg}")


def package_refs(csproj: Path) -> list[str]:
    """Return all PackageReference Include values from a csproj file."""
    tree = ET.parse(csproj)
    return [el.get("Include", "") for el in tree.getroot().iter("PackageReference")]


def project_refs(csproj: Path) -> list[str]:
    """Return all ProjectReference Include values from a csproj file."""
    tree = ET.parse(csproj)
    return [el.get("Include", "") for el in tree.getroot().iter("ProjectReference")]


# ── Check 1: Babel.Core has zero NuGet dependencies ─────────────────────────

refs = package_refs(Path("Babel.Core/Babel.Core.csproj"))
if refs:
    fail(f"Babel.Core must have zero NuGet deps, found: {refs}")
else:
    ok("Babel.Core has zero NuGet dependencies")

# ── Check 2: Babel.Core has zero project references ─────────────────────────

refs = project_refs(Path("Babel.Core/Babel.Core.csproj"))
if refs:
    fail(f"Babel.Core must have zero ProjectReferences, found: {refs}")
else:
    ok("Babel.Core has zero ProjectReferences")

# ── Check 3: Infrastructure only references Core ────────────────────────────

refs = project_refs(Path("Babel.Infrastructure/Babel.Infrastructure.csproj"))
bad = [r for r in refs if "Babel." in r and "Babel.Core" not in r]
if bad:
    fail(f"Babel.Infrastructure references non-Core projects: {bad}")
else:
    ok("Babel.Infrastructure only references Babel.Core")

# ── Check 4: ViewModels references Core + Infrastructure only ───────────────

refs = project_refs(Path("Babel.ViewModels/Babel.ViewModels.csproj"))
bad = [
    r
    for r in refs
    if "Babel." in r and "Babel.Core" not in r and "Babel.Infrastructure" not in r
]
if bad:
    fail(f"Babel.ViewModels references projects outside Core/Infrastructure: {bad}")
else:
    ok("Babel.ViewModels only references Babel.Core and Babel.Infrastructure")

# ── Check 5: Infrastructure and ViewModels have no Avalonia dependency ───────
# Infrastructure must stay headless; ViewModels must not depend on Avalonia UI.

for proj, label in [
    ("Babel.Infrastructure/Babel.Infrastructure.csproj", "Babel.Infrastructure"),
    ("Babel.ViewModels/Babel.ViewModels.csproj", "Babel.ViewModels"),
]:
    content = Path(proj).read_text()
    if re.search(r"(?i)avalonia", content):
        fail(f"{label} must not depend on Avalonia (layer boundary violation)")
    else:
        ok(f"{label} has no Avalonia dependency")

# ── Check 6: No hardcoded PackageReference versions ─────────────────────────
# All versions must reference a $(PackageVersion_*) property from Directory.Build.props.

HARDCODED = re.compile(r'Version="[0-9]')
project_files = [
    "Babel.Core/Babel.Core.csproj",
    "Babel.Infrastructure/Babel.Infrastructure.csproj",
    "Babel.ViewModels/Babel.ViewModels.csproj",
    "Babel.Desktop/Babel.Desktop.csproj",
    "Babel.Tests/Babel.Tests.csproj",
]
for proj in project_files:
    content = Path(proj).read_text()
    if HARDCODED.search(content):
        fail(f"{proj}: hardcoded PackageReference version — use $(PackageVersion_*) from Directory.Build.props")
    else:
        ok(f"{proj}: all PackageReference versions use property variables")

# ── Check 7: NotImplementedException must carry PLACEHOLDER prefix ───────────
# Catches silent stubs that violate placeholder discipline (CLAUDE.md).

NOT_IMPL = re.compile(r'new NotImplementedException\(')
PLACEHOLDER_MARKER = re.compile(r'"PLACEHOLDER')

cs_files = list(Path(".").rglob("*.cs"))
cs_files = [f for f in cs_files if not any(part in ("bin", "obj") for part in f.parts)]

bad_files: list[str] = []
for cs in cs_files:
    text = cs.read_text(encoding="utf-8", errors="replace")
    for lineno, line in enumerate(text.splitlines(), start=1):
        stripped = line.lstrip()
        if stripped.startswith("//"):
            continue
        if NOT_IMPL.search(line) and not PLACEHOLDER_MARKER.search(line):
            bad_files.append(f"{cs}:{lineno}: {line.strip()}")

if bad_files:
    fail(
        "NotImplementedException without PLACEHOLDER message "
        "(add 'PLACEHOLDER: reason' to the exception string):\n"
        + "\n".join(f"    {b}" for b in bad_files)
    )
else:
    ok("All NotImplementedException usages carry a PLACEHOLDER message")

# ── Check 8: Silent event stubs must have a PLACEHOLDER comment ─────────────
# Catches `event X E { add { } remove { } }` without a nearby PLACEHOLDER comment.

SILENT_EVENT = re.compile(r"add\s*\{\s*\}\s*remove\s*\{\s*\}")

bad_files = []
for cs in cs_files:
    lines = cs.read_text(encoding="utf-8", errors="replace").splitlines()
    for i, line in enumerate(lines):
        if SILENT_EVENT.search(line):
            # Look for PLACEHOLDER in the 3 lines preceding this one
            context = "\n".join(lines[max(0, i - 3) : i + 1])
            if "PLACEHOLDER" not in context:
                bad_files.append(f"{cs}:{i+1}: {line.strip()}")

if bad_files:
    fail(
        "Silent event stub without PLACEHOLDER comment "
        "(add '// PLACEHOLDER(reason): ...' before the event declaration):\n"
        + "\n".join(f"    {b}" for b in bad_files)
    )
else:
    ok("All silent event stubs have PLACEHOLDER comments")


# ── Summary ──────────────────────────────────────────────────────────────────

print()
if FAILED:
    print(f"Architecture check FAILED — {len(FAILED)} violation(s):")
    for msg in FAILED:
        first_line = msg.splitlines()[0]
        print(f"  • {first_line}")
    sys.exit(1)
else:
    print("All architecture checks passed.")

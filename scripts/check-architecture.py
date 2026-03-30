#!/usr/bin/env python3
"""
Architecture guard for Babel-Player.
Enforces basic project structure and placeholder discipline.

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


# ── Check 1: BabelPlayer.csproj exists ─────────────────────────────────────────

if not Path("BabelPlayer.csproj").exists():
    fail("BabelPlayer.csproj not found")
else:
    ok("BabelPlayer.csproj exists")

# ── Check 2: Test project references main project ───────────────────────────

test_csproj = Path("BabelPlayer.Tests/BabelPlayer.Tests.csproj")
if not test_csproj.exists():
    fail("BabelPlayer.Tests/BabelPlayer.Tests.csproj not found")
else:
    refs = project_refs(test_csproj)
    has_ref = any("BabelPlayer.csproj" in r for r in refs)
    if has_ref:
        ok("Test project references main project")
    else:
        fail(f"Test project must reference BabelPlayer.csproj, found: {refs}")

# ── Check 3: Test project has test framework ─────────────────────────────────

refs = package_refs(test_csproj)
test_frameworks = [r for r in refs if r in ("xunit", "nunit", "microsoft.net.test.sdk")]
if test_frameworks:
    ok(f"Test project has test framework: {test_frameworks}")
else:
    fail("Test project must have a test framework package reference")

# ── Check 4: Main project is Avalonia app ───────────────────────────────────

main_csproj = Path("BabelPlayer.csproj")
content = main_csproj.read_text()
if re.search(r"(?i)avalonia", content):
    ok("Main project is an Avalonia application")
else:
    fail("Main project must be an Avalonia application (Avalonia package reference)")

# ── Check 5: Main project has OutputType=WinExe for desktop ─────────────────

main_csproj = Path("BabelPlayer.csproj")
content = main_csproj.read_text()
if "WinExe" in content and "OutputType" in content:
    ok("Main project OutputType is WinExe")
else:
    fail("Main project should have OutputType=WinExe for desktop app")

# ── Check 6: NotImplementedException must carry PLACEHOLDER prefix ───────────

NOT_IMPL = re.compile(r"new NotImplementedException\(")
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

# ── Check 7: Silent event stubs must have a PLACEHOLDER comment ─────────────

SILENT_EVENT = re.compile(r"add\s*\{\s*\}\s*remove\s*\{\s*\}")

bad_files = []
for cs in cs_files:
    lines = cs.read_text(encoding="utf-8", errors="replace").splitlines()
    for i, line in enumerate(lines):
        if SILENT_EVENT.search(line):
            context = "\n".join(lines[max(0, i - 3) : i + 1])
            if "PLACEHOLDER" not in context:
                bad_files.append(f"{cs}:{i + 1}: {line.strip()}")

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

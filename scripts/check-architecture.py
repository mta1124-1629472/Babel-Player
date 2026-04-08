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
content = main_csproj.read_text(encoding="utf-8", errors="replace")
if re.search(r"(?i)avalonia", content):
    ok("Main project is an Avalonia application")
else:
    fail("Main project must be an Avalonia application (Avalonia package reference)")

# ── Check 5: Main project has OutputType=WinExe for desktop ─────────────────

main_csproj = Path("BabelPlayer.csproj")
content = main_csproj.read_text(encoding="utf-8", errors="replace")
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


# ── Check 8: Magic provider strings outside ProviderNames.cs ────────────────
# Any string literal matching a known provider ID in a .cs file that isn't
# ProviderNames.cs signals that the constant was bypassed.

PROVIDER_IDS = {
    "faster-whisper", "openai-whisper-api", "google-stt",
    "google-translate-free", "nllb-200", "deepl",
    "edge-tts", "piper", "elevenlabs", "google-cloud-tts", "openai-tts",
}
PROVIDER_LITERAL = re.compile(r'"(' + "|".join(re.escape(p) for p in PROVIDER_IDS) + r')"')

magic_hits: list[str] = []
for cs in cs_files:
    if cs.name == "ProviderNames.cs":
        continue
    # Test files may use string literals intentionally to pin the string contract
    if any("Tests" in part for part in cs.parts):
        continue
    text = cs.read_text(encoding="utf-8", errors="replace")
    for lineno, line in enumerate(text.splitlines(), start=1):
        stripped = line.lstrip()
        if stripped.startswith("//"):
            continue
        # Skip lines inside raw string literals used for Python scripts (heuristic:
        # if the line also contains 'import' or 'sys.argv' it's embedded Python)
        if "import " in line or "sys.argv" in line:
            continue
        if PROVIDER_LITERAL.search(line):
            magic_hits.append(f"{cs}:{lineno}: {line.strip()}")

if magic_hits:
    fail(
        "Magic provider string literal (use ProviderNames.* constants instead):\n"
        + "\n".join(f"    {h}" for h in magic_hits)
    )
else:
    ok("No magic provider string literals found outside ProviderNames.cs")

# ── Check 9: ViewModel pipeline-execution ban ─────────────────────────────────
# ViewModels must not call raw pipeline execution methods (TranscribeMediaAsync,
# TranslateTranscriptAsync, GenerateTtsAsync) directly. All stage progression
# must go through SessionWorkflowCoordinator.AdvancePipelineAsync.

DIRECT_PIPELINE_CALL = re.compile(
    r"\.(TranscribeMediaAsync|TranslateTranscriptAsync|GenerateTtsAsync)\s*\("
)
vm_files = [f for f in cs_files if "ViewModels" in f.parts]

stage_hits: list[str] = []
for vm in vm_files:
    text = vm.read_text(encoding="utf-8", errors="replace")
    for lineno, line in enumerate(text.splitlines(), start=1):
        if line.lstrip().startswith("//"):
            continue
        if DIRECT_PIPELINE_CALL.search(line):
            stage_hits.append(f"{vm}:{lineno}: {line.strip()}")

if stage_hits:
    fail(
        "ViewModel calls raw pipeline method directly — use "
        "SessionWorkflowCoordinator.AdvancePipelineAsync instead:\n"
        + "\n".join(f"    {h}" for h in stage_hits)
    )
else:
    ok("No ViewModel direct pipeline-execution calls found")

# ── Check 10: SessionWorkflowCoordinator line-count warning ─────────────────
COORDINATOR_WARN_LINES = 1300

coordinator = Path("Services/SessionWorkflowCoordinator.cs")
if coordinator.exists():
    line_count = len(coordinator.read_text(encoding="utf-8", errors="replace").splitlines())
    if line_count > COORDINATOR_WARN_LINES:
        fail(
            f"SessionWorkflowCoordinator.cs is {line_count} lines "
            f"(threshold: {COORDINATOR_WARN_LINES}). "
            "Consider extracting a helper service."
        )
    else:
        ok(f"SessionWorkflowCoordinator.cs is {line_count} lines (within {COORDINATOR_WARN_LINES}-line threshold)")

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

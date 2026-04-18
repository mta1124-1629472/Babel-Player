#!/usr/bin/env python3
"""Apply {local:Localize} markup to AXAML files based on the key dictionary
in scripts/build_strings_resx.py.

Usage:
    python3 scripts/apply_localize.py  # run in-place across Views/*

The script performs three kinds of edits:

1. Ensures each affected AXAML file declares the markup-extension namespace:
       xmlns:local="using:Babel.Player.Converters"

2. For each attribute in ATTR_NAMES, locates any match whose value appears in
   the inverse lookup of STRINGS (english_text -> key) and rewrites it to
       Attribute="{local:Localize KeyName}"

3. Leaves bindings (values starting with '{'), URIs, and non-matching text
   untouched.  Unmatched values are listed at the end so stray strings can
   be added to the dictionary in follow-up runs.

Re-runnable: replacements are idempotent because the script skips values
already starting with '{local:Localize'.
"""
from __future__ import annotations

import html
import importlib.util
import os
import re
import sys
from typing import Dict, Iterable, Tuple

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TARGETS = [
    "Views/MainWindow.axaml",
    "Views/SettingsWindow.axaml",
    "Views/SpeakerReferenceWizardWindow.axaml",
    "Views/ApiKeysDialog.axaml",
    "Views/CrashReportWindow.axaml",
]
ATTR_NAMES = [
    "Text",
    "Content",
    "Header",
    "Title",
    "PlaceholderText",
    "ToolTip.Tip",
    "AutomationProperties.Name",
    "Watermark",
]
NAMESPACE = 'xmlns:local="using:Babel.Player.Converters"'


def load_strings() -> Dict[str, str]:
    spec = importlib.util.spec_from_file_location(
        "build_strings_resx",
        os.path.join(REPO, "scripts", "build_strings_resx.py"),
    )
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.STRINGS  # type: ignore[attr-defined]


def ensure_namespace(content: bytes) -> bytes:
    if NAMESPACE.encode() in content:
        return content
    # Insert after the last xmlns declaration on the root element.
    pattern = re.compile(rb'(<Window\b[^>]*?)(\s*>)', re.DOTALL)
    m = pattern.search(content)
    if not m:
        return content
    head, tail = m.group(1), m.group(2)
    if b"xmlns:local" in head:
        return content
    injected = head + b"\r\n        " + NAMESPACE.encode() + tail
    return content[: m.start()] + injected + content[m.end():]


def make_replacement_re(attrs: Iterable[str]) -> re.Pattern[bytes]:
    # Matches: Attr="value" where value does NOT start with '{' (so we skip
    # bindings and existing markup extensions).  Captures groups:
    #   1 = attribute name
    #   2 = raw (xml-escaped) value
    attr_alt = "|".join(re.escape(a) for a in attrs)
    # Require a non-word boundary before the attribute name so SizeToContent
    # and similar compound names don't accidentally match "Content".
    pattern = rb'(?<![A-Za-z0-9_\.])(' + attr_alt.encode() + rb')="([^"{][^"]*)"'
    return re.compile(pattern)


def apply_replacements(content: bytes, inverse: Dict[str, str]) -> Tuple[bytes, Dict[str, int]]:
    regex = make_replacement_re(ATTR_NAMES)
    stats = {"replaced": 0, "skipped": 0}
    unmatched: Dict[str, int] = {}

    def repl(match: re.Match[bytes]) -> bytes:
        attr = match.group(1)
        raw = match.group(2).decode("utf-8")
        decoded = html.unescape(raw)
        key = inverse.get(decoded)
        if key is None:
            unmatched[decoded] = unmatched.get(decoded, 0) + 1
            stats["skipped"] += 1
            return match.group(0)
        stats["replaced"] += 1
        return f'{attr.decode()}="{{local:Localize {key}}}"'.encode()

    new = regex.sub(repl, content)
    stats["unique_unmatched"] = len(unmatched)  # type: ignore[assignment]
    return new, {**stats, "unmatched_samples": unmatched}  # type: ignore[return-value]


def main() -> None:
    strings = load_strings()
    inverse: Dict[str, str] = {}
    for key, value in strings.items():
        # Skip Language_* entries (used only by LanguageDisplayNames at runtime, not AXAML).
        if key.startswith("Language_"):
            continue
        inverse.setdefault(value, key)

    grand = {"replaced": 0, "skipped": 0}
    for rel in TARGETS:
        path = os.path.join(REPO, rel)
        with open(path, "rb") as f:
            content = f.read()
        original = content
        content = ensure_namespace(content)
        content, stats = apply_replacements(content, inverse)
        if content != original:
            with open(path, "wb") as f:
                f.write(content)
        grand["replaced"] += stats["replaced"]  # type: ignore[arg-type]
        grand["skipped"] += stats["skipped"]  # type: ignore[arg-type]
        print(
            f"{rel}: replaced={stats['replaced']} "
            f"skipped={stats['skipped']} unique_unmatched={stats['unique_unmatched']}"
        )
        # Emit the top unmatched literals so follow-up runs can include them.
        samples = stats.get("unmatched_samples", {})  # type: ignore[arg-type]
        if samples:
            for literal, count in sorted(samples.items(), key=lambda p: -p[1])[:12]:
                print(f"    UNMATCHED x{count}: {literal!r}")

    print("\nTOTAL:", grand)


if __name__ == "__main__":
    main()

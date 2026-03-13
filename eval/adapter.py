from __future__ import annotations

import json
import sys
from typing import Any


def _safe_str(value: Any) -> str:
    return "" if value is None else str(value)


def build_response(item: dict[str, Any]) -> dict[str, Any]:
    """Starter adapter for eval collection.

    Input: one JSON object from stdin.
    Output: one JSON object to stdout with fields expected by eval/collect_responses.py.

    Replace this function with real Babel-Player integration later.
    """

    generated_transcript = _safe_str(item.get("generated_transcript") or item.get("reference_transcript"))
    generated_translation = _safe_str(item.get("generated_translation") or item.get("reference_translation"))
    generated_cues = item.get("generated_cues") or item.get("reference_cues") or []

    return {
        "generated_transcript": generated_transcript,
        "generated_translation": generated_translation,
        "generated_cues": generated_cues,
        "output_format_valid": bool(item.get("output_format_valid", True)),
        "latency_ms": float(item.get("latency_ms", 0.0)),
    }


def main() -> None:
    raw = sys.stdin.read().strip()
    if not raw:
        raise ValueError("adapter.py expected one JSON object on stdin")

    item = json.loads(raw)
    response = build_response(item)
    sys.stdout.write(json.dumps(response, ensure_ascii=True))


if __name__ == "__main__":
    main()

from __future__ import annotations

import argparse
import json
import subprocess
import time
from pathlib import Path
from typing import Any


def load_jsonl(path: Path) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for line in path.read_text(encoding="utf-8").splitlines():
        stripped = line.strip()
        if not stripped:
            continue
        rows.append(json.loads(stripped))
    return rows


def write_jsonl(path: Path, rows: list[dict[str, Any]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as handle:
        for row in rows:
            handle.write(json.dumps(row, ensure_ascii=True))
            handle.write("\n")


def run_adapter(command: str, item: dict[str, Any]) -> dict[str, Any]:
    """Executes an external adapter command that reads JSON on stdin and returns JSON on stdout."""
    started = time.perf_counter()
    completed = subprocess.run(
        command,
        input=json.dumps(item, ensure_ascii=True),
        capture_output=True,
        text=True,
        shell=True,
        check=True,
    )
    latency_ms = (time.perf_counter() - started) * 1000.0
    result = json.loads(completed.stdout)
    result["latency_ms"] = float(result.get("latency_ms", latency_ms))
    return result


def infer_mock_response(item: dict[str, Any]) -> dict[str, Any]:
    """Default mock collector when no adapter is provided."""
    return {
        "generated_transcript": item.get("generated_transcript") or item.get("reference_transcript", ""),
        "generated_translation": item.get("generated_translation") or item.get("reference_translation", ""),
        "generated_cues": item.get("generated_cues") or item.get("reference_cues", []),
        "output_format_valid": bool(item.get("output_format_valid", True)),
        "latency_ms": float(item.get("latency_ms", 0.0)),
    }


def collect(dataset_path: Path, output_path: Path, adapter_cmd: str | None) -> None:
    rows = load_jsonl(dataset_path)
    output_rows: list[dict[str, Any]] = []

    for row in rows:
        response = run_adapter(adapter_cmd, row) if adapter_cmd else infer_mock_response(row)
        merged = dict(row)
        merged.update(response)
        output_rows.append(merged)

    write_jsonl(output_path, output_rows)


def main() -> None:
    parser = argparse.ArgumentParser(description="Collect evaluation responses for Babel-Player metrics.")
    parser.add_argument("--dataset", required=True, help="Input JSONL dataset path")
    parser.add_argument("--output", default="eval/outputs/responses.jsonl", help="Output JSONL path")
    parser.add_argument(
        "--adapter-cmd",
        default=None,
        help="Command to run app adapter. It must read one JSON item from stdin and emit one JSON object on stdout.",
    )
    args = parser.parse_args()

    collect(Path(args.dataset), Path(args.output), args.adapter_cmd)


if __name__ == "__main__":
    main()

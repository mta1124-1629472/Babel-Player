import argparse
import asyncio
import json
import os
import sys
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--simulate", action="store_true")
    parser.add_argument("--state-file")
    return parser.parse_args()


def append_state(state_file: str | None, entry: dict) -> None:
    if not state_file:
        return

    state_path = Path(state_file)
    state_path.parent.mkdir(parents=True, exist_ok=True)
    with state_path.open("a", encoding="utf-8") as handle:
        handle.write(json.dumps(entry, ensure_ascii=False) + "\n")


def validate_payload(payload: object) -> dict:
    if not isinstance(payload, dict):
        raise ValueError("payload must be a JSON object")

    text = payload.get("text")
    output_path = payload.get("output_path")
    voice = payload.get("voice")

    if not isinstance(text, str) or not text.strip():
        raise ValueError("payload.text must be a non-empty string")
    if not isinstance(output_path, str) or not output_path.strip():
        raise ValueError("payload.output_path must be a non-empty string")
    if not isinstance(voice, str) or not voice.strip():
        raise ValueError("payload.voice must be a non-empty string")

    return payload


async def synthesize(payload: dict, simulate: bool) -> dict:
    output_path = payload["output_path"]
    voice = payload["voice"]
    text = payload["text"]

    output_file = Path(output_path)
    output_file.parent.mkdir(parents=True, exist_ok=True)

    if simulate:
        output_file.write_bytes(f"{voice}\n{text}".encode("utf-8"))
    else:
        import edge_tts

        communicate = edge_tts.Communicate(text, voice)
        await communicate.save(output_path)

    return {
        "output_path": output_path,
        "voice": voice,
        "file_size_bytes": output_file.stat().st_size,
    }


def main() -> int:
    args = parse_args()

    for raw_line in sys.stdin:
        line = raw_line.strip()
        if not line:
            continue

        response = {
            "id": None,
            "success": False,
            "payload": None,
            "error": None,
        }

        try:
            envelope = json.loads(line)
            response["id"] = envelope.get("id")
            payload = validate_payload(envelope.get("payload"))
            result = asyncio.run(synthesize(payload, args.simulate))
            append_state(
                args.state_file,
                {
                    "id": response["id"],
                    "pid": os.getpid(),
                    "voice": result["voice"],
                    "output_path": result["output_path"],
                    "file_size_bytes": result["file_size_bytes"],
                },
            )
            response["success"] = True
            response["payload"] = result
        except Exception as exc:  # pragma: no cover - exercised through pool failure path
            response["error"] = str(exc)

        sys.stdout.write(json.dumps(response, ensure_ascii=False) + "\n")
        sys.stdout.flush()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())

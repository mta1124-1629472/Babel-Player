import json
import os
import platform
import shutil
import subprocess
import sys
from typing import Any


def emit(response: dict[str, Any]) -> None:
    print(json.dumps(response, ensure_ascii=False), flush=True)


def log_worker_start() -> None:
    path = os.environ.get("PIPER_WORKER_START_LOG")
    if not path:
        return

    with open(path, "a", encoding="utf-8") as handle:
        handle.write(f"{os.getpid()}\n")


def find_model(voice: str, model_dir: str) -> str | None:
    search_dirs: list[str] = []
    if model_dir:
        search_dirs.append(model_dir)
    if platform.system() == "Windows":
        local_app_data = os.environ.get("LOCALAPPDATA", "")
        search_dirs.append(os.path.join(local_app_data, "piper", "voices"))
    else:
        search_dirs.append(os.path.expanduser("~/.local/share/piper/voices"))

    for directory in search_dirs:
        for name in (f"{voice}.onnx", os.path.join(voice, f"{voice}.onnx")):
            candidate = os.path.join(directory, name)
            if os.path.exists(candidate):
                return candidate
    return None


def handle_request(payload: dict[str, Any], model_dir: str) -> dict[str, Any]:
    text = payload.get("text")
    output_path = payload.get("output_path")
    voice = payload.get("voice")

    if not isinstance(text, str) or not text.strip():
        raise ValueError("Text cannot be empty.")
    if not isinstance(output_path, str) or not output_path.strip():
        raise ValueError("Output path cannot be empty.")
    if not isinstance(voice, str) or not voice.strip():
        raise ValueError("Voice cannot be empty.")

    if shutil.which("piper") is None:
        raise RuntimeError(
            "piper CLI not found on PATH. Install it from https://github.com/rhasspy/piper/releases "
            "and ensure the piper executable is on your system PATH."
        )

    model_path = find_model(voice, model_dir)
    if model_path is None:
        raise FileNotFoundError(
            f"Piper voice model not found: {voice}. Download the .onnx file to the Piper voices directory."
        )

    output_dir = os.path.dirname(output_path)
    if output_dir:
        os.makedirs(output_dir, exist_ok=True)

    result = subprocess.run(
        ["piper", "--model", model_path, "--output_file", output_path],
        input=text,
        text=True,
        capture_output=True,
    )
    if result.returncode != 0:
        detail = result.stderr.strip() or result.stdout.strip() or "unknown error"
        raise RuntimeError(f"Piper failed: {detail}")
    if not os.path.exists(output_path):
        raise RuntimeError(f"Piper did not create output file: {output_path}")

    return {
        "output_path": output_path,
        "voice": voice,
        "file_size_bytes": os.path.getsize(output_path),
    }


def main() -> int:
    model_dir = sys.argv[1] if len(sys.argv) > 1 else ""
    log_worker_start()

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue

        request_id: str | None = None
        try:
            envelope = json.loads(line)
            request_id = envelope.get("id")
            payload = envelope.get("payload")
            if not isinstance(payload, dict):
                raise ValueError("Request payload must be an object.")

            response_payload = handle_request(payload, model_dir)
            emit(
                {
                    "id": request_id,
                    "success": True,
                    "payload": response_payload,
                    "error": None,
                }
            )
        except Exception as exc:  # noqa: BLE001
            emit(
                {
                    "id": request_id,
                    "success": False,
                    "payload": None,
                    "error": str(exc),
                }
            )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())

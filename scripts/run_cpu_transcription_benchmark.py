#!/usr/bin/env python3
"""
CPU transcription benchmark runner for Babel Player.

This script turns the manual benchmark runbook into an executable tool for the
transcription stage. It currently supports:

- local faster-whisper benchmarking on CPU
- containerized-service benchmarking on CPU/warm path
- naming-compliant dataset/matrix/run identifiers
- environment snapshot capture
- structured JSON output for later comparison

Examples:
  python scripts/run_cpu_transcription_benchmark.py \
    --source test-assets/video/sample.mp4 \
    --provider faster-whisper \
    --model base \
    --dataset-source local \
    --dataset-content dialogue \
    --source-lang es \
    --target-lang en \
    --duration-bucket s \
    --variant cpu-auto \
    --dry-run
"""

from __future__ import annotations

import argparse
import ctypes
import gc
import importlib.metadata
import json
import os
import platform
import re
import statistics
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any


REPO_ROOT = Path(__file__).resolve().parent.parent
VIDEO_EXTENSIONS = {".mp4", ".avi", ".mkv", ".mov"}
ALLOWED_SOURCES = {"local", "public"}
ALLOWED_CONTENT = {"dialogue", "mixed", "narration"}
ALLOWED_DURATION_BUCKETS = {"s", "m", "l", "xl"}
ALLOWED_STAGE = "transcription"
PROVIDER_ID_PATTERN = re.compile(r"^[a-z0-9]+(-[a-z0-9]+)*$")
LANGPAIR_PATTERN = re.compile(r"^[a-z]{2,3}-[a-z]{2,3}$")
SEMVER_PATTERN = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+$")


@dataclass(frozen=True)
class Variant:
    label: str
    compute_type: str
    threads: int
    workers: int


PRESET_VARIANTS: dict[str, Variant] = {
    "cpu-auto": Variant("cpu-auto", "int8", 0, 1),
    "cpu-4t": Variant("cpu-4t", "int8", 4, 1),
    "cpu-8t": Variant("cpu-8t", "int8", 8, 1),
    "cpu-16t": Variant("cpu-16t", "int8", 16, 1),
    "cpu-8t-2w": Variant("cpu-8t-2w", "int8", 8, 2),
    "cpu-auto-int8f16": Variant("cpu-auto-int8f16", "int8_float16", 0, 1),
    "cpu-auto-fp32": Variant("cpu-auto-fp32", "float32", 0, 1),
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run CPU transcription benchmarks for Babel Player.")
    parser.add_argument("--source", required=True, help="Input media path (audio or video).")
    parser.add_argument("--provider", required=True, choices=["faster-whisper", "containerized-service"])
    parser.add_argument("--model", required=True, help="Model name, e.g. tiny/base/small.")
    parser.add_argument("--dataset-source", required=True, choices=sorted(ALLOWED_SOURCES))
    parser.add_argument("--dataset-content", required=True, choices=sorted(ALLOWED_CONTENT))
    parser.add_argument("--source-lang", required=True, help="Source language code, e.g. es.")
    parser.add_argument("--target-lang", required=True, help="Target language code, e.g. en.")
    parser.add_argument("--duration-bucket", required=True, choices=sorted(ALLOWED_DURATION_BUCKETS))
    parser.add_argument("--version", default="1.0.0", help="Semantic version without leading v. Default: 1.0.0")
    parser.add_argument("--variant", action="append", dest="variants", help="Variant label from preset matrix. Repeatable.")
    parser.add_argument("--warmup-runs", type=int, default=1)
    parser.add_argument("--measured-runs", type=int, default=5)
    parser.add_argument("--cache-mode", action="append", dest="cache_modes", choices=["cold", "warm"], help="Repeatable cache mode selector.")
    parser.add_argument("--container-url", default="http://localhost:8000")
    parser.add_argument("--output-dir", default=str(REPO_ROOT / "benchmarks"))
    parser.add_argument("--machine-id", help="Override machine identifier used in environment snapshot.")
    parser.add_argument("--dry-run", action="store_true", help="Validate inputs and write a plan JSON without executing benchmarks.")
    return parser.parse_args()


def fail(message: str) -> None:
    raise SystemExit(message)


def validate_inputs(args: argparse.Namespace) -> None:
    if not LANGPAIR_PATTERN.match(f"{args.source_lang}-{args.target_lang}"):
        fail("Invalid langpair: must be two lowercase ISO 639 codes (2–3 alpha chars) separated by '-'")
    if not PROVIDER_ID_PATTERN.match(args.provider):
        fail("Invalid provider: must match pattern '^[a-z0-9]+(-[a-z0-9]+)*$'")
    if not PROVIDER_ID_PATTERN.match(args.model):
        fail("Invalid model: must match pattern '^[a-z0-9]+(-[a-z0-9]+)*$'")
    if not SEMVER_PATTERN.match(args.version):
        fail("Invalid version: must follow semver format '<major>.<minor>.<patch>'")
    if args.warmup_runs < 0:
        fail("Invalid warmup-runs: must be zero or a positive integer")
    if args.measured_runs <= 0:
        fail("Invalid measured-runs: must be a positive integer")
    if not Path(args.source).exists():
        fail(f"Source media not found: {args.source}")
    if not args.variants:
        args.variants = ["cpu-auto"]
    unknown = [v for v in args.variants if v not in PRESET_VARIANTS]
    if unknown:
        fail(f"Unknown variant(s): {', '.join(unknown)}")
    if not args.cache_modes:
        args.cache_modes = ["cold", "warm"]


def build_dataset_id(args: argparse.Namespace) -> str:
    langpair = f"{args.source_lang}-{args.target_lang}"
    return (
        f"bp.dataset.{args.dataset_source}.{args.dataset_content}."
        f"{langpair}.{args.duration_bucket}.v{args.version}"
    )


def collect_environment_snapshot(machine_id_override: str | None = None) -> dict[str, Any]:
    machine_id = machine_id_override or os.environ.get("COMPUTERNAME") or platform.node() or "unknown-machine"
    os_name, os_version = detect_os_info()
    cpu_name = detect_cpu_name()
    physical_cores = detect_physical_cores()
    logical_threads = os.cpu_count() or 0
    ram_total_gb, ram_available_gb = detect_ram_gb()
    gpu_name, gpu_vram_gb = detect_gpu()
    dotnet_version = run_and_capture(["dotnet", "--version"]) or "unknown"
    python_version = platform.python_version()
    runtimes = {
        pkg: package_version(pkg)
        for pkg in ["faster-whisper", "googletrans", "edge-tts", "torch"]
    }
    return {
        "MachineId": sanitize_token(machine_id.lower()),
        "OS": os_name,
        "OSVersion": os_version,
        "CPU": cpu_name,
        "PhysicalCores": physical_cores,
        "LogicalThreads": logical_threads,
        "RAM_GB": ram_total_gb,
        "AvailableRAM_GB": ram_available_gb,
        "GPU": gpu_name,
        "GPU_RAM_GB": gpu_vram_gb,
        "DiskPath": str(REPO_ROOT),
        "DotNet": dotnet_version,
        "Python": python_version,
        "ProviderRuntimes": runtimes,
    }


def build_hw_profile(env: dict[str, Any], precision: str = "int8") -> str:
    cores = env.get("PhysicalCores") or env.get("LogicalThreads") or "unknown"
    threads = env.get("LogicalThreads") or "unknown"
    ram = env.get("RAM_GB")
    ram_token = "unknown" if ram == "unknown" else str(int(round(float(ram))))
    return f"{precision}_{cores}c{threads}t_{ram_token}g"


def build_matrix_id(provider: str, model: str, hw_profile: str, version: str) -> str:
    return f"bp.matrix.transcription.{provider}.{model}.{hw_profile}.v{version}"


def build_run_batch_id(dataset_id: str, matrix_id: str, measured_runs: int) -> str:
    timestamp = time.strftime("%Y%m%dT%H%M%SZ", time.gmtime())
    return f"bp.run.{timestamp}.{dataset_id}.{matrix_id}.r{measured_runs}"


def detect_cpu_name() -> str:
    if platform.system() == "Windows":
        output = run_and_capture([
            "powershell",
            "-NoProfile",
            "-Command",
            "(Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name)"
        ])
        if output:
            return output.strip()
    return platform.processor() or "unknown"


def detect_os_info() -> tuple[str, str]:
    if platform.system() == "Windows":
        output = run_and_capture([
            "powershell",
            "-NoProfile",
            "-Command",
            "$os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version; "
            "Write-Output ($os.Caption + '||' + $os.Version)",
        ])
        if output and "||" in output:
            name, version = output.split("||", 1)
            return name.strip(), version.strip()
    return f"{platform.system()} {platform.release()}", platform.version()


def detect_physical_cores() -> int | str:
    try:
        import psutil  # type: ignore
        value = psutil.cpu_count(logical=False)
        if value:
            return int(value)
    except Exception:
        pass
    if platform.system() == "Windows":
        output = run_and_capture([
            "powershell",
            "-NoProfile",
            "-Command",
            "(Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty NumberOfCores)"
        ])
        if output and output.strip().isdigit():
            return int(output.strip())
    return "unknown"


def detect_ram_gb() -> tuple[float | str, float | str]:
    if platform.system() == "Windows":
        class MEMORYSTATUSEX(ctypes.Structure):
            _fields_ = [
                ("dwLength", ctypes.c_ulong),
                ("dwMemoryLoad", ctypes.c_ulong),
                ("ullTotalPhys", ctypes.c_ulonglong),
                ("ullAvailPhys", ctypes.c_ulonglong),
                ("ullTotalPageFile", ctypes.c_ulonglong),
                ("ullAvailPageFile", ctypes.c_ulonglong),
                ("ullTotalVirtual", ctypes.c_ulonglong),
                ("ullAvailVirtual", ctypes.c_ulonglong),
                ("sullAvailExtendedVirtual", ctypes.c_ulonglong),
            ]

        stat = MEMORYSTATUSEX()
        stat.dwLength = ctypes.sizeof(MEMORYSTATUSEX)
        if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(stat)):  # type: ignore[attr-defined]
            return (
                round(stat.ullTotalPhys / (1024 ** 3), 1),
                round(stat.ullAvailPhys / (1024 ** 3), 1),
            )
    return "unknown", "unknown"


def detect_gpu() -> tuple[str, str]:
    output = run_and_capture([
        "nvidia-smi",
        "--query-gpu=name,memory.total",
        "--format=csv,noheader,nounits",
    ])
    if output:
        first = output.splitlines()[0]
        parts = [p.strip() for p in first.split(",")]
        if len(parts) >= 2:
            try:
                vram_gb = round(float(parts[1]) / 1024.0, 1)
                return parts[0], str(vram_gb)
            except ValueError:
                return parts[0], "unknown"
        return first.strip(), "unknown"
    return "none", "none"


def package_version(name: str) -> str:
    try:
        return importlib.metadata.version(name)
    except importlib.metadata.PackageNotFoundError:
        return "unknown"


def sanitize_token(value: str) -> str:
    sanitized = re.sub(r"[^a-z0-9-]+", "-", value.lower()).strip("-")
    return sanitized or "unknown"


def run_and_capture(command: list[str]) -> str | None:
    try:
        proc = subprocess.run(command, capture_output=True, text=True, check=False)
        if proc.returncode == 0:
            return proc.stdout.strip() or proc.stderr.strip()
    except Exception:
        return None
    return None


def resolve_ffmpeg() -> str:
    candidates = [
        REPO_ROOT / "ffmpeg.exe",
        REPO_ROOT / "tools" / "ffmpeg.exe",
        Path("ffmpeg"),
    ]
    for candidate in candidates:
        probe = [str(candidate), "-version"]
        if run_and_capture(probe):
            return str(candidate)
    fail("ffmpeg not found. Expected ffmpeg.exe in repo root, tools/, or on PATH.")
    raise AssertionError("unreachable")


def maybe_extract_audio(source: Path) -> tuple[Path, Path | None]:
    if source.suffix.lower() not in VIDEO_EXTENSIONS:
        return source, None
    ffmpeg = resolve_ffmpeg()
    temp_audio = Path(tempfile.gettempdir()) / f"bp_bench_{uuid.uuid4().hex}.wav"
    command = [
        ffmpeg,
        "-i", str(source),
        "-vn",
        "-acodec", "pcm_s16le",
        "-ar", "16000",
        "-ac", "1",
        "-af", "loudnorm=I=-16:LRA=11:TP=-1.5",
        "-y",
        str(temp_audio),
    ]
    proc = subprocess.run(command, capture_output=True, text=True, check=False)
    if proc.returncode != 0 or not temp_audio.exists():
        fail(f"Audio extraction failed: {proc.stderr.strip()}")
    return temp_audio, temp_audio


def local_transcribe_once(audio_path: Path, model: str, variant: Variant) -> dict[str, Any]:
    try:
        from faster_whisper import WhisperModel  # type: ignore
    except Exception as ex:
        fail(f"faster-whisper import failed: {ex}")

    kwargs: dict[str, Any] = {
        "device": "cpu",
        "compute_type": variant.compute_type,
        "num_workers": max(1, variant.workers),
    }
    if variant.threads > 0:
        kwargs["cpu_threads"] = variant.threads

    start = time.perf_counter()
    model_instance = WhisperModel(model, **kwargs)
    segments, info = model_instance.transcribe(str(audio_path))
    segment_list = list(segments)
    elapsed = time.perf_counter() - start
    result = {
        "elapsed_seconds": elapsed,
        "segment_count": len(segment_list),
        "language": getattr(info, "language", "unknown") or "unknown",
        "language_probability": getattr(info, "language_probability", 0.0) or 0.0,
    }
    del model_instance
    gc.collect()
    return result


def container_transcribe_once(audio_path: Path, model: str, variant: Variant, container_url: str) -> dict[str, Any]:
    boundary = f"----bpbench{uuid.uuid4().hex}"
    body = build_multipart_body(
        boundary,
        {
            "model": model,
            "cpu_compute_type": variant.compute_type,
            "num_workers": str(max(1, variant.workers)),
            **({"cpu_threads": str(variant.threads)} if variant.threads > 0 else {}),
        },
        audio_path,
    )
    request = urllib.request.Request(
        f"{container_url.rstrip('/')}/transcribe",
        data=body,
        method="POST",
        headers={"Content-Type": f"multipart/form-data; boundary={boundary}"},
    )
    start = time.perf_counter()
    try:
        with urllib.request.urlopen(request, timeout=600) as response:
            payload = json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as ex:
        fail(f"Containerized transcription failed: {ex.read().decode('utf-8', errors='replace')}")
    except Exception as ex:
        fail(f"Containerized transcription failed: {ex}")
    elapsed = time.perf_counter() - start
    return {
        "elapsed_seconds": elapsed,
        "segment_count": len(payload.get("segments", [])),
        "language": payload.get("language", "unknown"),
        "language_probability": payload.get("language_probability", 0.0),
    }


def build_multipart_body(boundary: str, fields: dict[str, str], audio_path: Path) -> bytes:
    lines: list[bytes] = []
    for key, value in fields.items():
        lines.extend([
            f"--{boundary}".encode(),
            f'Content-Disposition: form-data; name="{key}"'.encode(),
            b"",
            str(value).encode(),
        ])
    lines.extend([
        f"--{boundary}".encode(),
        f'Content-Disposition: form-data; name="file"; filename="{audio_path.name}"'.encode(),
        b"Content-Type: application/octet-stream",
        b"",
        audio_path.read_bytes(),
        f"--{boundary}--".encode(),
        b"",
    ])
    return b"\r\n".join(lines)


def percentile95(values: list[float]) -> float:
    if not values:
        return 0.0
    if len(values) == 1:
        return values[0]
    return statistics.quantiles(values, n=100, method="inclusive")[94]


def run_variant(provider: str, audio_path: Path, model: str, variant: Variant, cache_mode: str, args: argparse.Namespace) -> dict[str, Any]:
    if provider == "containerized-service" and cache_mode == "cold":
        fail("Containerized cold-cache benchmarking is not yet supported by this runner. Use --cache-mode warm or extend the service restart flow.")

    runner = local_transcribe_once if provider == "faster-whisper" else (
        lambda path, mdl, var: container_transcribe_once(path, mdl, var, args.container_url)
    )

    timings: list[float] = []
    segment_count: int | None = None
    language: str | None = None
    language_probability: float | None = None
    notes: list[str] = []

    for _ in range(args.warmup_runs):
        runner(audio_path, model, variant)

    for _ in range(args.measured_runs):
        result = runner(audio_path, model, variant)
        timings.append(result["elapsed_seconds"])
        segment_count = result["segment_count"]
        language = result["language"]
        language_probability = result["language_probability"]

    if cache_mode == "warm" and provider == "faster-whisper":
        notes.append("Warm mode reuses OS/process state only; app-local provider still loads a new model per run in production path.")

    return {
        "variant": asdict(variant),
        "cache_mode": cache_mode,
        "measured_runs": args.measured_runs,
        "warmup_runs": args.warmup_runs,
        "timings_seconds": timings,
        "p50_seconds": statistics.median(timings),
        "p95_seconds": percentile95(timings),
        "mean_seconds": statistics.mean(timings),
        "segment_count": segment_count,
        "language": language,
        "language_probability": language_probability,
        "notes": notes,
    }


def write_output(report: dict[str, Any], output_dir: Path, dataset_id: str, matrix_id: str) -> Path:
    output_dir.mkdir(parents=True, exist_ok=True)
    timestamp = time.strftime("%Y%m%dT%H%M%SZ", time.gmtime())
    filename = f"{timestamp}_{dataset_id}_{matrix_id}.json".replace("/", "-")
    path = output_dir / filename
    path.write_text(json.dumps(report, indent=2), encoding="utf-8")
    return path


def main() -> int:
    args = parse_args()
    validate_inputs(args)

    env = collect_environment_snapshot(args.machine_id)
    hw_profile = build_hw_profile(env, precision="int8")
    dataset_id = build_dataset_id(args)
    matrix_id = build_matrix_id(args.provider, args.model, hw_profile, args.version)
    run_batch_id = build_run_batch_id(dataset_id, matrix_id, args.measured_runs)

    report: dict[str, Any] = {
        "environment_snapshot": env,
        "normalized_inputs": {
            "provider": args.provider,
            "model": args.model,
            "dataset_id": dataset_id,
            "matrix_id": matrix_id,
            "run_batch_id": run_batch_id,
            "cache_modes": args.cache_modes,
            "variants": args.variants,
            "warmup_runs": args.warmup_runs,
            "measured_runs": args.measured_runs,
            "source": str(Path(args.source).resolve()),
        },
        "results": [],
        "limitations": [],
    }

    if args.provider == "containerized-service":
        report["limitations"].append(
            "Cold-cache containerized benchmarking is not supported unless the service restart path is automated."
        )

    if args.dry_run:
        output_path = write_output(report, Path(args.output_dir), dataset_id, matrix_id)
        print(f"Dry-run plan written to {output_path}")
        return 0

    source_path = Path(args.source).resolve()
    audio_path, temp_artifact = maybe_extract_audio(source_path)
    try:
        for variant_name in args.variants:
            variant = PRESET_VARIANTS[variant_name]
            for cache_mode in args.cache_modes:
                result = run_variant(args.provider, audio_path, args.model, variant, cache_mode, args)
                report["results"].append(result)
        output_path = write_output(report, Path(args.output_dir), dataset_id, matrix_id)
        print(f"Benchmark results written to {output_path}")
        return 0
    finally:
        if temp_artifact and temp_artifact.exists():
            temp_artifact.unlink(missing_ok=True)


if __name__ == "__main__":
    raise SystemExit(main())
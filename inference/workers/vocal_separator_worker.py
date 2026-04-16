# pyright: reportMissingImports=false
# pylint: disable=missing-module-docstring,missing-class-docstring,missing-function-docstring,invalid-name,broad-exception-caught

import logging
import os
import shutil
import subprocess
import tempfile
import uuid
from pathlib import Path
from typing import Optional

logger = logging.getLogger(__name__)

DEFAULT_VOCALS_MODEL = "UVR-MDX-NET-Voc_FT.onnx"
# None = single-pass separation using vocals_model only (avoids requiring a second ONNX that may
# not ship with audio-separator's bundled model list, e.g. MDX23C-8KFFT-InstVoc_HQ.onnx).
DEFAULT_INSTRUMENTAL_MODEL: Optional[str] = None

# Stable model cache directory — shared across requests, never inside TEMP_DIR
_default_model_dir = Path(tempfile.gettempdir()) / "babel_separator_models"
SEPARATOR_MODEL_DIR = Path(os.environ.get("SEPARATOR_MODEL_DIR", str(_default_model_dir)))
SEPARATOR_MODEL_DIR.mkdir(parents=True, exist_ok=True)

_MDX_HOP_LENGTH = 1024
_MDX_SEGMENT_SIZE = 256
_MDX_OVERLAP = 0.25
_MDX_BATCH_SIZE = 1

# Paths with spaces or > ~220 chars often trigger OSError errno 22 on Windows in native
# loaders (soundfile/torchaudio). Decode containers to PCM WAV; copy other inputs to a
# short work path when needed.
_MAX_INPUT_PATH_LEN = 220


def _normalize_input_for_separator(src: Path, work_dir: Path) -> tuple[Path, Optional[Path]]:
    """
    Return (path_for_separator, temp_file_to_delete_or_none).
    """
    work_dir = work_dir.resolve()
    work_dir.mkdir(parents=True, exist_ok=True)

    resolved = src.resolve()
    suffix = resolved.suffix.lower()
    str_path = str(resolved)
    unsafe_path = " " in str_path or len(str_path) > _MAX_INPUT_PATH_LEN

    video_like = suffix in {
        ".mp4",
        ".mkv",
        ".webm",
        ".avi",
        ".mov",
        ".mpg",
        ".mpeg",
        ".wmv",
    }

    def _ffmpeg_to_wav(out: Path) -> None:
        ffmpeg = shutil.which("ffmpeg")
        if not ffmpeg:
            raise RuntimeError(
                "ffmpeg not found on PATH; required to decode this media for vocal separation."
            )
        proc = subprocess.run(
            [
                ffmpeg,
                "-y",
                "-i",
                str(resolved),
                "-vn",
                "-acodec",
                "pcm_s16le",
                "-ar",
                "44100",
                "-ac",
                "2",
                str(out),
            ],
            capture_output=True,
            text=True,
        )
        if proc.returncode != 0:
            tail = (proc.stderr or "")[-800:]
            raise RuntimeError(f"ffmpeg decode for vocal separation failed: {tail}")

    uid = uuid.uuid4().hex

    if video_like or suffix == ".m4a":
        out = work_dir / f"sep_in_{uid}.wav"
        _ffmpeg_to_wav(out)
        return out, out

    if suffix in {".wav", ".flac", ".mp3", ".ogg"}:
        if not unsafe_path:
            return resolved, None
        if suffix == ".wav":
            out = work_dir / f"sep_in_{uid}.wav"
            shutil.copy2(resolved, out)
            return out, out
        out = work_dir / f"sep_in_{uid}{suffix}"
        shutil.copy2(resolved, out)
        return out, out

    out = work_dir / f"sep_in_{uid}.wav"
    _ffmpeg_to_wav(out)
    return out, out


def _resolve_stem_path(raw: str | Path, output_dir: Path) -> Path:
    """
    audio-separator may return basename-only or cwd-relative paths. Always resolve against output_dir.
    (See audio_separator.separator: chunked path uses join(temp_dir, stem_path) for the same reason.)
    """
    p = Path(raw)
    if p.is_file():
        return p.resolve()
    alt = output_dir / p.name
    if alt.is_file():
        return alt.resolve()
    raise FileNotFoundError(
        f"Stem output not found after separation: {raw!r} (tried {p.resolve()!s} and {alt!s})"
    )


def run_vocal_separation(
    audio_path: Path,
    output_dir: Path,
    vocals_model: str = DEFAULT_VOCALS_MODEL,
    instrumental_model: Optional[str] = DEFAULT_INSTRUMENTAL_MODEL,
    *,
    use_gpu: bool = False,
    output_format: str = "wav",
) -> tuple[Path, Path]:
    """
    Split audio_path into (vocals_path, instrumental_path) stems.

    Runs two passes if vocals_model != instrumental_model, giving each stem
    the best model for its downstream use. Runs one pass if they are the same.

    Returns:
        vocals_path:       WAV containing isolated speech — feed to /transcribe.
        instrumental_path: WAV containing ambient audio — mix under TTS dub.
    """
    from audio_separator.separator import Separator

    work_audio, temp_input = _normalize_input_for_separator(Path(audio_path), output_dir)

    mdx_params = {
        "hop_length": _MDX_HOP_LENGTH,
        "segment_size": _MDX_SEGMENT_SIZE,
        "overlap": _MDX_OVERLAP,
        "batch_size": _MDX_BATCH_SIZE,
    }

    def make_separator(model_name: str) -> Separator:
        sep = Separator(
            model_file_dir=str(SEPARATOR_MODEL_DIR),
            output_dir=str(output_dir),
            output_format=output_format,
            use_autocast=use_gpu,
            mdx_params=mdx_params,
        )
        sep.load_model(model_filename=model_name)
        return sep

    def pick_vocals(stems: list[Path]) -> Path:
        hit = next((p for p in stems if "Vocals" in p.name and "No" not in p.name), None)
        return hit if hit is not None else stems[-1]

    def pick_instrumental(stems: list[Path]) -> Path:
        hit = next(
            (p for p in stems if "Instrumental" in p.name or "No Vocals" in p.name), None
        )
        return hit if hit is not None else stems[0]

    effective_instrumental_model = instrumental_model or vocals_model

    try:
        if vocals_model == effective_instrumental_model:
            # Single pass — both stems come from the same model run
            stems = [
                _resolve_stem_path(p, output_dir)
                for p in make_separator(vocals_model).separate(str(work_audio))
            ]
            if len(stems) < 2:
                raise RuntimeError(
                    f"Expected 2 stems from {vocals_model}, got {len(stems)}: {stems}"
                )
            vocals_path = pick_vocals(stems)
            instrumental_path = pick_instrumental(stems)
        else:
            # Two-pass — optimise each stem independently
            vocals_stems = [
                _resolve_stem_path(p, output_dir)
                for p in make_separator(vocals_model).separate(str(work_audio))
            ]
            instrumental_stems = [
                _resolve_stem_path(p, output_dir)
                for p in make_separator(effective_instrumental_model).separate(str(work_audio))
            ]
            if len(vocals_stems) < 2 or len(instrumental_stems) < 2:
                raise RuntimeError(
                    f"Expected 2 stems per pass. "
                    f"Got vocals={len(vocals_stems)}, instrumental={len(instrumental_stems)}"
                )
            vocals_path = pick_vocals(vocals_stems)
            instrumental_path = pick_instrumental(instrumental_stems)
            # Discard the stems we don't use — each pass produces 2, we keep 1 from each
            for rejected in vocals_stems:
                if rejected != vocals_path:
                    rejected.unlink(missing_ok=True)
            for rejected in instrumental_stems:
                if rejected != instrumental_path:
                    rejected.unlink(missing_ok=True)

        logger.info(
            "Vocal separation complete — vocals=%s (%d bytes), instrumental=%s (%d bytes)",
            vocals_path.name,
            vocals_path.stat().st_size,
            instrumental_path.name,
            instrumental_path.stat().st_size,
        )
        return vocals_path, instrumental_path
    finally:
        if temp_input is not None:
            try:
                temp_input.unlink(missing_ok=True)
            except (PermissionError, OSError) as exc:
                logger.warning(
                    "Failed to clean up temporary normalized input %s: %s",
                    temp_input,
                    exc,
                )

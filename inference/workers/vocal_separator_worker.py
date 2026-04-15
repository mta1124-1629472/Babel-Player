# pyright: reportMissingImports=false
# pylint: disable=missing-module-docstring,missing-class-docstring,missing-function-docstring,invalid-name,broad-exception-caught

import logging
import os
import tempfile
from pathlib import Path
from typing import Optional

logger = logging.getLogger(__name__)

DEFAULT_VOCALS_MODEL = "UVR-MDX-NET-Voc_FT.onnx"
DEFAULT_INSTRUMENTAL_MODEL = "MDX23C-8KFFT-InstVoc_HQ.onnx"

# Stable model cache directory — shared across requests, never inside TEMP_DIR
_default_model_dir = Path(tempfile.gettempdir()) / "babel_separator_models"
SEPARATOR_MODEL_DIR = Path(os.environ.get("SEPARATOR_MODEL_DIR", str(_default_model_dir)))
SEPARATOR_MODEL_DIR.mkdir(parents=True, exist_ok=True)

_MDX_HOP_LENGTH = 1024
_MDX_SEGMENT_SIZE = 256
_MDX_OVERLAP = 0.25
_MDX_BATCH_SIZE = 1


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

    if vocals_model == effective_instrumental_model:
        # Single pass — both stems come from the same model run
        stems = [Path(p) for p in make_separator(vocals_model).separate(str(audio_path))]
        if len(stems) < 2:
            raise RuntimeError(
                f"Expected 2 stems from {vocals_model}, got {len(stems)}: {stems}"
            )
        vocals_path = pick_vocals(stems)
        instrumental_path = pick_instrumental(stems)
    else:
        # Two-pass — optimise each stem independently
        vocals_stems = [
            Path(p) for p in make_separator(vocals_model).separate(str(audio_path))
        ]
        instrumental_stems = [
            Path(p)
            for p in make_separator(effective_instrumental_model).separate(str(audio_path))
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

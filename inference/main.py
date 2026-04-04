# pyright: reportMissingImports=false
# pylint: disable=missing-module-docstring,missing-class-docstring,missing-function-docstring,invalid-name,global-statement,line-too-long,broad-exception-caught

import argparse
import asyncio
import builtins
import importlib.util
import json
import logging
import os
import shutil
import subprocess
import sys
import tempfile
import wave
from importlib import import_module
from pathlib import Path
from datetime import datetime
from typing import Optional
from uuid import uuid4

try:
    import torch
    TORCH_IMPORT_ERROR: Optional[str] = None
except ModuleNotFoundError as torch_import_exc:
    TORCH_IMPORT_ERROR = str(torch_import_exc)

    class _TorchVersionShim:
        cuda = None

    class _TorchCudaShim:
        @staticmethod
        def is_available() -> bool:
            return False

        @staticmethod
        def empty_cache() -> None:
            return None

        @staticmethod
        def get_device_name(_index: int) -> str:
            return "unavailable"

        @staticmethod
        def get_device_capability(_index: int) -> tuple[int, int]:
            return (0, 0)

    class _TorchShim:
        cuda = _TorchCudaShim()
        version = _TorchVersionShim()
        float16 = "float16"
        bfloat16 = "bfloat16"

    torch = _TorchShim()  # type: ignore[assignment]
from fastapi import FastAPI, File, Form, UploadFile, HTTPException, BackgroundTasks
from fastapi.responses import FileResponse
from pydantic import BaseModel

def _ensure_transformers_beamsearch_compat() -> None:
    """
    Ensure `from transformers import BeamSearchScorer` succeeds for TTS 0.22.0.

    transformers>=4.46 removed BeamSearchScorer from the public API; in 4.57.x
    the original module is gone. XTTS in TTS 0.22.0 still imports that symbol.
    XTTS synthesis does not rely on beam search, so a sentinel stub is safe.
    """
    try:
        import transformers as _t_compat

        if not hasattr(_t_compat, "BeamSearchScorer"):
            class _BeamSearchScorerStub:  # type: ignore[no-redef]
                """Sentinel stub: beam-search symbol removed in newer transformers."""

                def __init__(self, *_unused_args, **_unused_kwargs):
                    raise RuntimeError(
                        "BeamSearchScorer has been removed from transformers >= 4.46.0; "
                        "beam-search path in TTS is unavailable."
                    )

            _t_compat.BeamSearchScorer = _BeamSearchScorerStub
            logging.getLogger(__name__).warning(
                "transformers compat: injected BeamSearchScorer stub for TTS 0.22.0 import compatibility."
            )

        # transformers is a lazy module that can consult __getattr__ during
        # `from transformers import ...`; ensure BeamSearchScorer resolves even
        # if internal import structures are recomputed.
        if callable(getattr(_t_compat, "__getattr__", None)) and not getattr(_t_compat, "_bp_beamsearch_patch", False):
            _original_getattr = _t_compat.__getattr__

            def _patched_getattr(name):
                if name == "BeamSearchScorer":
                    return _t_compat.__dict__["BeamSearchScorer"]
                return _original_getattr(name)

            _t_compat.__getattr__ = _patched_getattr
            _t_compat._bp_beamsearch_patch = True

        # Validate importability exactly as XTTS does it.
        from transformers import BeamSearchScorer as _unused_beam_search_scorer  # noqa: F401
    except Exception as _compat_exc:  # pragma: no cover
        logging.getLogger(__name__).warning("transformers compat shim failed: %s", _compat_exc)


_ensure_transformers_beamsearch_compat()


def _with_transformers_compat_import_hook(action):
    """Run an action while enforcing BeamSearchScorer compatibility on imports."""
    original_import = builtins.__import__

    def _compat_import(name, globals=None, locals=None, fromlist=(), level=0):
        module = original_import(name, globals, locals, fromlist, level)
        if name == "transformers" or name.startswith("transformers."):
            _ensure_transformers_beamsearch_compat()
        return module

    builtins.__import__ = _compat_import
    try:
        return action()
    finally:
        builtins.__import__ = original_import

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger(__name__)
uvicorn_logger = logging.getLogger("uvicorn.error")

# Initialize FastAPI
app = FastAPI(
    title="Babel Player Inference Service",
    description="GPU-accelerated inference service for transcription, translation, and TTS",
    version="1.0.0"
)

# Global model instances (loaded once)
whisper_model = None
whisper_model_key = None
nllb_tokenizer = None
nllb_model = None
nllb_model_key = None
xtts_model = None
xtts_model_key = None
xtts_reference_registry: dict[str, dict[str, str | None]] = {}
qwen_model = None
qwen_model_key = None
qwen_model_dtype: Optional[str] = None
qwen_model_attn_impl: Optional[str] = None
qwen_forced_model: Optional[str] = None
# Qwen voice-clone prompt cache: maps hash(ref_audio_bytes + ref_text) → prompt items
qwen_voice_clone_prompt_cache: dict[str, object] = {}
# Qwen3-TTS startup pre-warm state
qwen_warmup_complete: bool = False
qwen_warmup_error: Optional[str] = None

HOST_DEVICE = "cuda" if torch.cuda.is_available() else "cpu"
HOST_COMPUTE_TYPE = "float16" if HOST_DEVICE == "cuda" else "int8"
# Tracks effective compute type after per-stage validation and potential downgrades
EFFECTIVE_HOST_COMPUTE_TYPE = HOST_COMPUTE_TYPE
# Tracks downgrade reasons per stage for UI/logging projection
COMPUTE_DOWNGRADE_REASONS: dict[str, str] = {}
XTTS_MODEL_NAME = "tts_models/multilingual/multi-dataset/xtts_v2"
MANAGED_HOST_DRY_RUN = False

# Temporary directory for artifacts
TEMP_DIR = Path(tempfile.gettempdir()) / "babel_inference"
TEMP_DIR.mkdir(exist_ok=True)

# Qwen3-TTS language mapping: BCP-47 / common codes → Qwen language label
QWEN_LANGUAGE_MAP: dict[str, str] = {
    "en": "English",
    "es": "Spanish",
    "fr": "French",
    "de": "German",
    "it": "Italian",
    "pt": "Portuguese",
    "pt-br": "Portuguese",
    "pt-pt": "Portuguese",
    "ru": "Russian",
    "zh": "Chinese",
    "zh-cn": "Chinese",
    "zh-hans": "Chinese",
    "zh-tw": "Chinese",
    "zh-hant": "Chinese",
    "ja": "Japanese",
    "ko": "Korean",
}

QWEN_MODEL_PRIMARY = "Qwen/Qwen3-TTS-12Hz-1.7B-Base"
QWEN_MODEL_FALLBACK = "Qwen/Qwen3-TTS-12Hz-0.6B-Base"

FLORES = {
    # Latin-script European
    "en": "eng_Latn",
    "es": "spa_Latn",
    "fr": "fra_Latn",
    "de": "deu_Latn",
    "it": "ita_Latn",
    "pt": "por_Latn",
    "nl": "nld_Latn",
    "pl": "pol_Latn",
    "sv": "swe_Latn",
    "tr": "tur_Latn",
    "ro": "ron_Latn",
    "cs": "ces_Latn",
    "da": "dan_Latn",
    "fi": "fin_Latn",
    "hu": "hun_Latn",
    "nb": "nob_Latn",
    "sk": "slk_Latn",
    "hr": "hrv_Latn",
    "uk": "ukr_Cyrl",
    "ca": "cat_Latn",
    "id": "ind_Latn",
    "ms": "zsm_Latn",
    "vi": "vie_Latn",
    "sw": "swh_Latn",
    "af": "afr_Latn",
    # Cyrillic
    "ru": "rus_Cyrl",
    "bg": "bul_Cyrl",
    "sr": "srp_Cyrl",
    # CJK
    "zh": "zho_Hans",
    "zh-cn": "zho_Hans",
    "zh-tw": "zho_Hant",
    "ja": "jpn_Jpan",
    "ko": "kor_Hang",
    # Arabic / Devanagari / other
    "ar": "arb_Arab",
    "hi": "hin_Deva",
    "bn": "ben_Beng",
    "ta": "tam_Taml",
    "te": "tel_Telu",
    "he": "heb_Hebr",
    "fa": "pes_Arab",
    "ur": "urd_Arab",
    "th": "tha_Thai",
}

# ============================================================================
# Pydantic Models
# ============================================================================

class TranscriptionRequest(BaseModel):
    model: str = "base"
    language: Optional[str] = None
    cpu_compute_type: str = "int8"
    cpu_threads: Optional[int] = None
    num_workers: int = 1

class TranscriptSegment(BaseModel):
    start: float
    end: float
    text: str

class TranscriptionResponse(BaseModel):
    success: bool
    language: str
    language_probability: float
    segments: list[TranscriptSegment]
    error_message: Optional[str] = None

class TranslationRequest(BaseModel):
    source_language: str
    target_language: str

class TranslatedSegment(BaseModel):
    start: float
    end: float
    text: str
    translated_text: str

class TranslationResponse(BaseModel):
    success: bool
    source_language: str
    target_language: str
    segments: list[TranslatedSegment]
    error_message: Optional[str] = None

class TtsRequest(BaseModel):
    voice: str = "en-US-AriaNeural"

class TtsResponse(BaseModel):
    success: bool
    voice: str
    audio_path: str
    file_size_bytes: int
    error_message: Optional[str] = None

class XttsReferenceResponse(BaseModel):
    success: bool
    reference_id: Optional[str] = None
    error_message: Optional[str] = None

class QwenTtsRequest(BaseModel):
    text: str
    model: str = "Qwen/Qwen3-TTS-12Hz-1.7B-Base"
    language: Optional[str] = None
    reference_text: Optional[str] = None

class HealthResponse(BaseModel):
    status: str
    timestamp: str
    cuda_available: bool
    cuda_version: Optional[str] = None

class StageCapability(BaseModel):
    ready: bool
    detail: Optional[str] = None
    providers: Optional[dict[str, bool]] = None
    provider_details: Optional[dict[str, str]] = None

class CapabilitiesResponse(BaseModel):
    transcription: StageCapability
    translation: StageCapability
    tts: StageCapability

def _resolve_ffmpeg_path() -> str:
    script_dir = Path(__file__).resolve().parent
    candidates = [
        script_dir / "ffmpeg.exe",
        script_dir / "tools" / "win-x64" / "ffmpeg.exe",
        script_dir.parent / "ffmpeg.exe",
        script_dir.parent / "tools" / "win-x64" / "ffmpeg.exe",
    ]

    for candidate in candidates:
        if candidate.exists():
            return str(candidate)

    resolved = shutil.which("ffmpeg")
    if resolved:
        return resolved

    raise RuntimeError("ffmpeg not found. XTTS output conversion requires ffmpeg.")

def _wav_to_mp3(wav_path: Path, mp3_path: Path) -> None:
    """Convert WAV to MP3 via ffmpeg. Raises RuntimeError on failure."""
    cmd = [
        _resolve_ffmpeg_path(), "-y", "-i", str(wav_path),
        "-codec:a", "libmp3lame", "-q:a", "3",
        str(mp3_path),
    ]
    try:
        subprocess.check_output(cmd, stderr=subprocess.STDOUT)
    except subprocess.CalledProcessError as exc:
        raise RuntimeError(
            f"ffmpeg conversion failed: {exc.output.decode(errors='replace')}"
        ) from exc

def _temp_artifact_path(prefix: str, suffix: str) -> Path:
    return TEMP_DIR / f"{prefix}_{datetime.now().timestamp()}_{uuid4().hex}{suffix}"


def _is_dry_run_enabled() -> bool:
    return MANAGED_HOST_DRY_RUN


def _write_silent_mp3(output_path: Path, seconds: float = 0.35, sample_rate: int = 22050) -> None:
    temp_wav = _temp_artifact_path("dryrun_tts", ".wav")
    try:
        frame_count = max(1, int(sample_rate * seconds))
        with wave.open(str(temp_wav), "wb") as wav_file:
            wav_file.setnchannels(1)
            wav_file.setsampwidth(2)
            wav_file.setframerate(sample_rate)
            wav_file.writeframes(b"\x00\x00" * frame_count)
        _wav_to_mp3(temp_wav, output_path)
    finally:
        temp_wav.unlink(missing_ok=True)


def _log_host_runtime_state(context: str) -> None:
    logger.info(
        "Managed host runtime state (%s): device=%s compute_type=%s cuda_available=%s cuda_version=%s temp_dir=%s",
        context,
        HOST_DEVICE,
        HOST_COMPUTE_TYPE,
        torch.cuda.is_available(),
        torch.version.cuda,
        TEMP_DIR,
    )
    if torch.cuda.is_available():
        try:
            logger.info(
                "Managed host CUDA device (%s): index=0 name=%s capability=%s",
                context,
                torch.cuda.get_device_name(0),
                ".".join(str(part) for part in torch.cuda.get_device_capability(0)),
            )
        except Exception as exc:
            logger.warning("Failed to query CUDA device details during %s: %s", context, exc)

def _get_effective_compute_type(stage: str) -> str:
    """
    Returns the effective compute type for a given stage (transcription, translation, tts).
    If FP8 is requested but unsupported, downgrades to float16 and logs the reason.
    """
    if HOST_COMPUTE_TYPE != "float8":
        return HOST_COMPUTE_TYPE
    
    # FP8 is requested; check if stage supports it
    # For now, stages require float16; FP8 support will be validated per-model/stage
    if stage in ("transcription", "translation", "tts"):
        # NLLB and XTTS currently require float16; Whisper can work with float8 but we default to float16 for safety
        downgrade_reason = f"{stage} does not support float8 in this release; downgrading to float16"
        if stage not in COMPUTE_DOWNGRADE_REASONS:
            COMPUTE_DOWNGRADE_REASONS[stage] = downgrade_reason
            logger.warning(
                "Compute type downgrade for %s: requested float8 but using float16 (%s)",
                stage,
                downgrade_reason
            )
        return "float16"
    
    return HOST_COMPUTE_TYPE

def _normalize_xtts_model(model_name: Optional[str]) -> str:
    if model_name is None:
        return XTTS_MODEL_NAME

    normalized = model_name.strip().lower()
    if normalized in ("", "xtts-v2", XTTS_MODEL_NAME.lower()):
        return XTTS_MODEL_NAME

    raise RuntimeError(f"Unsupported XTTS model '{model_name}'. Only xtts-v2 is currently hosted.")

def _normalize_xtts_voice_label(model_name: Optional[str]) -> str:
    normalized = _normalize_xtts_model(model_name)
    return "xtts-v2" if normalized == XTTS_MODEL_NAME else normalized

def _normalize_xtts_language(language: Optional[str]) -> str:
    if not language:
        return "en"

    normalized = language.strip().lower()
    if normalized in ("zh", "zh-cn", "zh-hans"):
        return "zh-cn"
    if normalized in ("pt-br", "pt-pt"):
        return "pt"
    if "-" in normalized:
        return normalized.split("-", 1)[0]
    return normalized

def _probe_xtts_available() -> bool:
    if HOST_DEVICE != "cuda":
        return False
    
    effective_type = _get_effective_compute_type("tts")
    if effective_type != "float16":
        return False

    if importlib.util.find_spec("TTS.api") is None:
        logger.warning("XTTS capability probe failed: TTS.api is unavailable")
        return False

    return True


def _xtts_capability_detail() -> str:
    if HOST_DEVICE != "cuda":
        return "CUDA is unavailable; managed GPU XTTS is not ready"
    
    effective_type = _get_effective_compute_type("tts")
    if effective_type != "float16":
        downgrade_reason = COMPUTE_DOWNGRADE_REASONS.get("tts", "compute type mismatch")
        return f"XTTS requires compute_type=float16 on CUDA; downgraded to {effective_type} ({downgrade_reason})"
    
    return "XTTS dependencies are unavailable"


def _probe_qwen_available() -> bool:
    if HOST_DEVICE != "cuda":
        return False
    if importlib.util.find_spec("qwen_tts") is None:
        logger.warning("Qwen capability probe failed: qwen_tts package is unavailable")
        return False
    return True


def _qwen_capability_detail() -> str:
    if HOST_DEVICE != "cuda":
        return "CUDA is unavailable; Qwen3-TTS requires CUDA"
    return "qwen_tts package is unavailable; install qwen-tts"

# ============================================================================
# Capabilities / Health
# ============================================================================

def get_stage_capabilities() -> CapabilitiesResponse:
    if _is_dry_run_enabled():
        return CapabilitiesResponse(
            transcription=StageCapability(
                ready=True,
                detail="Dry-run mode: transcription capability simulated",
            ),
            translation=StageCapability(
                ready=True,
                detail="Dry-run mode: translation capability simulated",
            ),
            tts=StageCapability(
                ready=True,
                detail="Dry-run mode: TTS capability simulated",
                providers={"qwen-tts": True, "xtts-container": True},
                provider_details={
                    "qwen-tts": "Dry-run mode: Qwen3-TTS simulated",
                    "xtts-container": "Dry-run mode: XTTS simulated",
                },
            ),
        )

    transcription_ready = HOST_DEVICE == "cuda"
    transcription_effective_type = _get_effective_compute_type("transcription")
    
    translation_ready = _probe_nllb_available()
    translation_effective_type = _get_effective_compute_type("translation")
    
    xtts_ready = _probe_xtts_available()
    qwen_ready = _probe_qwen_available()
    qwen_provider_ready = qwen_ready and qwen_warmup_complete

    if qwen_provider_ready:
        dtype_label = qwen_model_dtype or "unknown"
        attn_label = qwen_model_attn_impl or "unknown-attn"
        fallback_note = (
            f"; model fallback active ({QWEN_MODEL_PRIMARY} -> {qwen_forced_model})"
            if qwen_forced_model and qwen_forced_model != QWEN_MODEL_PRIMARY
            else ""
        )
        qwen_detail = f"Qwen3-TTS ready on {HOST_DEVICE} ({dtype_label}, attn={attn_label}){fallback_note}; reference audio required"
    elif qwen_warmup_error:
        qwen_detail = f"Qwen3-TTS warmup failed: {qwen_warmup_error}"
    elif qwen_ready:
        qwen_detail = "Qwen3-TTS warming up"
    else:
        qwen_detail = _qwen_capability_detail()

    if xtts_ready:
        xtts_detail = f"XTTS v2 ready on {HOST_DEVICE}; reference audio required"
    else:
        xtts_detail = _xtts_capability_detail()

    tts_provider_ready = {
        "qwen-tts": qwen_provider_ready,
        "xtts-container": xtts_ready,
    }
    tts_provider_detail = {
        "qwen-tts": qwen_detail,
        "xtts-container": xtts_detail,
    }
    tts_ready = any(tts_provider_ready.values())

    tts_providers = []
    if qwen_provider_ready:
        tts_providers.append("Qwen3-TTS")
    elif qwen_ready:
        tts_providers.append(qwen_detail)
    if xtts_ready:
        tts_providers.append("XTTS v2")
    elif not tts_ready:
        tts_providers.append(xtts_detail)

    tts_detail = f"{', '.join(tts_providers)} ready on {HOST_DEVICE}; reference audio required" if tts_ready else "; ".join(tts_provider_detail.values())

    return CapabilitiesResponse(
        transcription=StageCapability(
            ready=transcription_ready,
            detail=(
                f"faster-whisper ready on {HOST_DEVICE} with compute_type={transcription_effective_type}"
                if transcription_ready
                else "CUDA is unavailable; managed GPU transcription is not ready"
            ),
        ),
        translation=StageCapability(
            ready=translation_ready,
            detail=(
                f"NLLB-200 ready on {HOST_DEVICE} with compute_type={translation_effective_type}"
                if translation_ready
                else _nllb_capability_detail()
            ),
        ),
        tts=StageCapability(
            ready=tts_ready,
            detail=tts_detail,
            providers=tts_provider_ready,
            provider_details=tts_provider_detail,
        ),
    )

@app.get("/health/live", response_model=HealthResponse)
async def health_live():
    """Liveness endpoint with basic CUDA info."""
    try:
        cuda_available = torch.cuda.is_available()
        cuda_version = torch.version.cuda if cuda_available else None
        logger.info(
            "Health probe served: status=healthy cuda_available=%s cuda_version=%s",
            cuda_available,
            cuda_version,
        )
        return HealthResponse(
            status="healthy",
            timestamp=datetime.utcnow().isoformat(),
            cuda_available=cuda_available,
            cuda_version=cuda_version,
        )
    except Exception as exc:
        logger.error("Health check failed: %s", exc)
        raise HTTPException(status_code=500, detail=str(exc)) from exc

@app.get("/health", response_model=HealthResponse)
async def health_check():
    """Backward-compatible health alias."""
    return await health_live()

@app.get("/capabilities", response_model=CapabilitiesResponse)
async def capabilities():
    """Stage-specific readiness details for the desktop app."""
    try:
        capability_snapshot = get_stage_capabilities()
        logger.info(
            "Capabilities probe served: transcription_ready=%s translation_ready=%s tts_ready=%s",
            capability_snapshot.transcription.ready,
            capability_snapshot.translation.ready,
            capability_snapshot.tts.ready,
        )
        return capability_snapshot
    except Exception as exc:
        logger.error("Capability probe failed: %s", exc, exc_info=True)
        raise HTTPException(status_code=500, detail=str(exc)) from exc

# ============================================================================
# Transcription
# ============================================================================

def load_whisper_model(
    model_name: str,
    cpu_compute_type: str = "int8",
    cpu_threads: Optional[int] = None,
    num_workers: int = 1,
):
    global whisper_model, whisper_model_key

    device = HOST_DEVICE
    compute_type = _get_effective_compute_type("transcription")
    requested_cpu_compute_type = cpu_compute_type or "int8"
    effective_num_workers = max(1, int(num_workers or 1))
    effective_cpu_threads = int(cpu_threads) if cpu_threads is not None else None
    if effective_cpu_threads is not None and effective_cpu_threads <= 0:
        effective_cpu_threads = None

    desired_key = (model_name, device, compute_type, effective_cpu_threads, effective_num_workers)

    if whisper_model is None or whisper_model_key != desired_key:
        whisper_module = import_module("faster_whisper")
        init_kwargs = {
            "device": device,
            "compute_type": compute_type,
            "num_workers": effective_num_workers,
        }
        if device == "cpu" and effective_cpu_threads is not None:
            init_kwargs["cpu_threads"] = effective_cpu_threads

        logger.info(
            "Loading Whisper model '%s' on device '%s' with host_compute_type='%s', "
            "requested_cpu_compute_type='%s', cpu_threads='%s', num_workers='%s'",
            model_name,
            device,
            compute_type,
            requested_cpu_compute_type,
            effective_cpu_threads if effective_cpu_threads is not None else "auto",
            effective_num_workers,
        )
        whisper_model = whisper_module.WhisperModel(model_name, **init_kwargs)
        whisper_model_key = desired_key
        logger.info("Whisper model loaded successfully")

    return whisper_model


def _probe_nllb_available() -> bool:
    if HOST_DEVICE != "cuda":
        return False

    effective_type = _get_effective_compute_type("translation")
    if effective_type != "float16":
        return False

    missing_modules = [
        module_name
        for module_name in ("transformers", "sentencepiece")
        if importlib.util.find_spec(module_name) is None
    ]
    if missing_modules:
        logger.warning("NLLB capability probe failed: missing dependencies %s", ", ".join(missing_modules))
        return False

    return True


def _nllb_capability_detail() -> str:
    if HOST_DEVICE != "cuda":
        return "CUDA is unavailable; managed GPU translation is not ready"
    
    effective_type = _get_effective_compute_type("translation")
    if effective_type != "float16":
        downgrade_reason = COMPUTE_DOWNGRADE_REASONS.get("translation", "compute type mismatch")
        return f"NLLB-200 requires compute_type=float16 on CUDA; downgraded to {effective_type} ({downgrade_reason})"
    
    return f"NLLB-200 ready on {HOST_DEVICE} with compute_type={effective_type}"


def load_nllb_model(model_name: str):
    global nllb_tokenizer, nllb_model, nllb_model_key

    if HOST_DEVICE != "cuda":
        raise RuntimeError("NLLB-200 GPU host requires CUDA.")
    
    effective_compute_type = _get_effective_compute_type("translation")
    if effective_compute_type != "float16":
        raise RuntimeError(
            f"NLLB-200 does not support compute_type={effective_compute_type} in the managed host. Use float16."
        )

    desired_key = (model_name, HOST_DEVICE, effective_compute_type)
    if nllb_model is None or nllb_model_key != desired_key:
        transformers_module = import_module("transformers")
        auto_model = transformers_module.AutoModelForSeq2SeqLM
        auto_tokenizer = transformers_module.AutoTokenizer

        model_id = f"facebook/{model_name}"
        logger.info(
            "Loading NLLB model '%s' on device '%s' with compute_type '%s'",
            model_id,
            HOST_DEVICE,
            effective_compute_type,
        )
        nllb_tokenizer = auto_tokenizer.from_pretrained(model_id)
        nllb_model = auto_model.from_pretrained(
            model_id,
            dtype=torch.float16,
            device_map=HOST_DEVICE,
        )
        nllb_model_key = desired_key
        logger.info("NLLB model loaded successfully")

    return nllb_tokenizer, nllb_model


def _find_xtts_hf_snapshot() -> Optional[str]:
    """Locate the XTTS v2 model in the HuggingFace local cache.

    Returns the path to the most-recently-written snapshot directory that
    contains both ``config.json`` and ``model.pth``, or ``None`` when no
    valid snapshot is found.
    """
    hf_home = os.environ.get(
        "HF_HOME",
        str(Path.home() / ".cache" / "huggingface"),
    )
    snapshots_dir = Path(hf_home) / "hub" / "models--coqui--XTTS-v2" / "snapshots"
    if not snapshots_dir.exists():
        return None
    candidates = sorted(
        snapshots_dir.iterdir(),
        key=lambda p: p.stat().st_mtime,
        reverse=True,
    )
    for snapshot in candidates:
        if (snapshot / "config.json").exists() and (snapshot / "model.pth").exists():
            return str(snapshot)
    return None


def load_xtts_model(model_name: Optional[str]):
    global xtts_model, xtts_model_key

    if HOST_DEVICE != "cuda":
        raise RuntimeError("XTTS GPU host requires CUDA.")

    effective_compute_type = _get_effective_compute_type("tts")
    if effective_compute_type != "float16":
        raise RuntimeError(
            f"XTTS does not support compute_type={effective_compute_type} in the managed GPU host. Use float16."
        )

    resolved_model = _normalize_xtts_model(model_name)
    desired_key = (resolved_model, HOST_DEVICE, effective_compute_type)
    if xtts_model is None or xtts_model_key != desired_key:
        _ensure_transformers_beamsearch_compat()
        tts_module = import_module("TTS.api")
        tts_factory = tts_module.TTS

        snapshot_path = _find_xtts_hf_snapshot()
        if snapshot_path:
            # Pass the model checkpoint (.pth) as model_path and the config.json
            # as config_path explicitly.  Some TTS versions treat a .json model_path
            # as a config reference and fall back to config.model_args paths (which
            # are null in HuggingFace snapshots) for lazily-loaded components such as
            # the DVAE and speaker encoder.  That fallback ultimately calls
            # torch.load(None) → open(None) → TypeError.  Using the actual .pth file
            # avoids all path-inference ambiguity across TTS library versions.
            model_pth_path = str(Path(snapshot_path) / "model.pth")
            config_json_path = str(Path(snapshot_path) / "config.json")
            logger.info(
                "Loading XTTS model from HuggingFace snapshot: %s (device=%s, compute_type=%s)",
                snapshot_path,
                HOST_DEVICE,
                effective_compute_type,
            )
            try:
                xtts_model = _with_transformers_compat_import_hook(
                    lambda: tts_factory(
                        model_path=model_pth_path,
                        config_path=config_json_path,
                        progress_bar=False,
                        gpu=(HOST_DEVICE == "cuda"),
                    )
                )
            except FileNotFoundError as ex:
                # Older/newer TTS internals disagree on whether `model_path` is the
                # checkpoint file path or the snapshot directory. If passing
                # model.pth leads to a nested lookup like model.pth/model.pth,
                # retry with the snapshot directory.
                if "model.pth/model.pth" not in str(ex).replace("\\", "/"):
                    raise
                logger.warning(
                    "XTTS checkpoint path fallback triggered (%s); retrying with snapshot directory.",
                    ex,
                )
                xtts_model = _with_transformers_compat_import_hook(
                    lambda: tts_factory(
                        model_path=str(snapshot_path),
                        config_path=config_json_path,
                        progress_bar=False,
                        gpu=(HOST_DEVICE == "cuda"),
                    )
                )
        else:
            logger.warning(
                "XTTS HuggingFace snapshot not found; falling back to Coqui registry for '%s' "
                "(this may trigger a slow network download)",
                resolved_model,
            )
            logger.info(
                "Loading XTTS model '%s' on device '%s' with compute_type '%s'",
                resolved_model,
                HOST_DEVICE,
                effective_compute_type,
            )
            xtts_model = tts_factory(resolved_model).to(HOST_DEVICE)

        xtts_model_key = desired_key
        logger.info("XTTS model loaded successfully")

    return xtts_model


_QWEN_FLASH_ATTN_AVAILABLE: Optional[bool] = None


def _qwen_attn_implementation() -> str:
    global _QWEN_FLASH_ATTN_AVAILABLE
    if _QWEN_FLASH_ATTN_AVAILABLE is None:
        _QWEN_FLASH_ATTN_AVAILABLE = importlib.util.find_spec("flash_attn") is not None
    return "flash_attention_2" if _QWEN_FLASH_ATTN_AVAILABLE else "eager"


def _normalize_qwen_model(model_name: Optional[str]) -> str:
    valid = {
        QWEN_MODEL_FALLBACK,
        QWEN_MODEL_PRIMARY,
    }
    requested = model_name.strip() if model_name and model_name.strip() in valid else QWEN_MODEL_PRIMARY

    # If the primary model has already been forced down due to runtime incompatibility,
    # keep future requests on the proven working fallback to avoid repeated hard failures.
    if qwen_forced_model and requested == QWEN_MODEL_PRIMARY:
        return qwen_forced_model

    return requested


def _normalize_qwen_language(language: Optional[str]) -> str:
    if not language:
        return "auto"
    key = language.strip().lower()
    return QWEN_LANGUAGE_MAP.get(key, "auto")


def _build_qwen_load_kwargs(attn_impl: str) -> dict[str, object]:
    load_kwargs: dict[str, object] = {
        "device_map": f"{HOST_DEVICE}:0",
        "dtype": torch.bfloat16,
        "attn_implementation": attn_impl,
        # Keep state-dict loading conservative to reduce peak host commit usage.
        "low_cpu_mem_usage": True,
        "offload_state_dict": True,
    }
    return load_kwargs


def _is_qwen_dtype_retryable_error(exc: Exception) -> bool:
    message = str(exc).lower()
    return (
        "invalid argument" in message
        or "bfloat16" in message
        or "bf16" in message
        or "not implemented for" in message
        or "unsupported" in message and "dtype" in message
    )


def _is_qwen_attn_retryable_error(exc: Exception) -> bool:
    message = str(exc).lower()
    return (
        "invalid argument" in message
        or "flash" in message and "attn" in message
        or "flash_attention" in message
        or "cutlass" in message
        or "xformers" in message
    )


def _format_qwen_load_error(exc: Exception) -> str:
    message = str(exc)
    if os.name == "nt" and ("os error 1455" in message.lower() or "paging file is too small" in message.lower()):
        return (
            "Windows paging file is too small to load the selected Qwen3-TTS model. "
            "Increase virtual memory or use the smaller Qwen model. "
            f"Original error: {message}"
        )
    return message


# Minimum available commit headroom (bytes) required to safely load the 1.7B model.
# safetensors safe_open() maps the entire weight file into committed virtual address
# space; the 1.7B model is ~3.4 GiB and peak commit during from_pretrained is roughly
# 2× that. 8 GiB is deliberately conservative to leave headroom for CUDA runtime init.
_QWEN_LARGE_MODEL_MIN_COMMIT_BYTES: int = 8 * 1024 ** 3  # 8 GiB


def _resolve_qwen_warmup_model() -> Optional[str]:
    """
    Return the Qwen3-TTS model name to use for startup pre-warm.

    On Windows, queries GlobalMemoryStatusEx to measure available commit-charge
    headroom (ullAvailPageFile = commit limit − current commit charge). If that
    headroom is below _QWEN_LARGE_MODEL_MIN_COMMIT_BYTES the 0.6B model is chosen
    to avoid ERROR_COMMITMENT_LIMIT (os error 1455) during safetensors mmap load.
    On all other platforms, or if the query fails, returns None so that
    _normalize_qwen_model(None) picks the default 1.7B model.
    """
    if os.name != "nt":
        return None

    try:
        import ctypes
        import ctypes.wintypes

        class MEMORYSTATUSEX(ctypes.Structure):
            _fields_ = [
                ("dwLength",                  ctypes.c_ulong),
                ("dwMemoryLoad",              ctypes.c_ulong),
                ("ullTotalPhys",              ctypes.c_ulonglong),
                ("ullAvailPhys",              ctypes.c_ulonglong),
                ("ullTotalPageFile",          ctypes.c_ulonglong),
                ("ullAvailPageFile",          ctypes.c_ulonglong),   # commit limit − commit charge
                ("ullTotalVirtual",           ctypes.c_ulonglong),
                ("ullAvailVirtual",           ctypes.c_ulonglong),
                ("ullAvailExtendedVirtual",   ctypes.c_ulonglong),
            ]

        stat = MEMORYSTATUSEX()
        stat.dwLength = ctypes.sizeof(stat)
        if not ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(stat)):
            logger.warning("GlobalMemoryStatusEx returned False; using default Qwen warmup model")
            return None

        available_commit_gib = stat.ullAvailPageFile / (1024 ** 3)
        threshold_gib = _QWEN_LARGE_MODEL_MIN_COMMIT_BYTES / (1024 ** 3)
        logger.info(
            "Windows commit headroom: available=%.1f GiB, threshold=%.1f GiB",
            available_commit_gib,
            threshold_gib,
        )

        if stat.ullAvailPageFile < _QWEN_LARGE_MODEL_MIN_COMMIT_BYTES:
            small_model = "Qwen/Qwen3-TTS-12Hz-0.6B-Base"
            logger.info(
                "Commit headroom %.1f GiB < %.1f GiB threshold; "
                "selecting smaller Qwen model '%s' for warmup to avoid os error 1455",
                available_commit_gib,
                threshold_gib,
                small_model,
            )
            return small_model

        return None  # sufficient headroom — _normalize_qwen_model(None) → 1.7B

    except Exception as exc:
        logger.warning("Windows commit headroom query failed (%s); using default Qwen warmup model", exc)
        return None


def load_qwen_model(model_name: Optional[str]):
    global qwen_model, qwen_model_key, qwen_model_dtype, qwen_model_attn_impl, qwen_forced_model

    if HOST_DEVICE != "cuda":
        raise RuntimeError("Qwen3-TTS managed host requires CUDA.")

    requested_model = _normalize_qwen_model(model_name)
    desired_key = (requested_model, HOST_DEVICE)
    if qwen_model is None or qwen_model_key != desired_key:
        qwen_tts_module = import_module("qwen_tts")
        Qwen3TTSModel = qwen_tts_module.Qwen3TTSModel
        attn_impl = _qwen_attn_implementation()
        load_kwargs = _build_qwen_load_kwargs(attn_impl)
        logger.info(
            "Loading Qwen3-TTS model '%s' on device '%s' (attn=%s, dtype=bfloat16, low_cpu_mem_usage=%s, offload_state_dict=%s)",
            requested_model,
            HOST_DEVICE,
            attn_impl,
            load_kwargs.get("low_cpu_mem_usage"),
            load_kwargs.get("offload_state_dict"),
        )

        # Attempt order:
        # 1) preferred attn + bfloat16
        # 2) preferred attn + float16 (dtype fallback)
        # 3) eager + float16 (kernel fallback)
        attempts: list[tuple[str, object]] = [
            (attn_impl, torch.bfloat16),
            (attn_impl, torch.float16),
        ]
        if attn_impl != "eager":
            attempts.append(("eager", torch.float16))

        def _attempt_load(target_model: str) -> Optional[Exception]:
            last_error: Optional[Exception] = None
            for index, (candidate_attn, candidate_dtype) in enumerate(attempts):
                candidate_kwargs = _build_qwen_load_kwargs(candidate_attn)
                candidate_kwargs["dtype"] = candidate_dtype
                try:
                    nonlocal requested_model
                    qwen_model_local = Qwen3TTSModel.from_pretrained(target_model, **candidate_kwargs)
                    # Promote successful load into module globals.
                    globals()["qwen_model"] = qwen_model_local
                    globals()["qwen_model_dtype"] = "bfloat16" if candidate_dtype == torch.bfloat16 else "float16"
                    globals()["qwen_model_attn_impl"] = candidate_attn
                    requested_model = target_model
                    if index > 0:
                        logger.info(
                            "Qwen3-TTS model '%s' loaded successfully via fallback (dtype=%s, attn=%s)",
                            target_model,
                            qwen_model_dtype,
                            candidate_attn,
                        )
                    return None
                except Exception as exc:
                    last_error = exc
                    is_last = index == len(attempts) - 1
                    if is_last:
                        return exc

                    next_attn, next_dtype = attempts[index + 1]
                    can_retry = _is_qwen_dtype_retryable_error(exc) or _is_qwen_attn_retryable_error(exc)
                    if not can_retry:
                        return exc

                    next_dtype_label = "bfloat16" if next_dtype == torch.bfloat16 else "float16"
                    logger.warning(
                        "Qwen3-TTS load attempt failed for '%s' (dtype=%s, attn=%s): %s; retrying with dtype=%s, attn=%s",
                        target_model,
                        "bfloat16" if candidate_dtype == torch.bfloat16 else "float16",
                        candidate_attn,
                        exc,
                        next_dtype_label,
                        next_attn,
                    )

            return last_error

        last_exc = _attempt_load(requested_model)
        if qwen_model is None and last_exc is not None:
            retryable = _is_qwen_dtype_retryable_error(last_exc) or _is_qwen_attn_retryable_error(last_exc)
            if requested_model == QWEN_MODEL_PRIMARY and retryable:
                logger.warning(
                    "Qwen3-TTS primary model '%s' failed with retryable error (%s); falling back to '%s'",
                    QWEN_MODEL_PRIMARY,
                    last_exc,
                    QWEN_MODEL_FALLBACK,
                )
                fallback_exc = _attempt_load(QWEN_MODEL_FALLBACK)
                if qwen_model is None and fallback_exc is not None:
                    formatted_error = _format_qwen_load_error(fallback_exc)
                    logger.error(
                        "Qwen3-TTS fallback model '%s' load failed: %s",
                        QWEN_MODEL_FALLBACK,
                        formatted_error,
                        exc_info=True,
                    )
                    raise RuntimeError(formatted_error) from fallback_exc
                qwen_forced_model = QWEN_MODEL_FALLBACK
            else:
                formatted_error = _format_qwen_load_error(last_exc)
                logger.error(
                    "Qwen3-TTS model load failed for '%s' after all fallback attempts: %s",
                    requested_model,
                    formatted_error,
                    exc_info=True,
                )
                raise RuntimeError(formatted_error) from last_exc

        qwen_model_key = (requested_model, HOST_DEVICE)
        logger.info(
            "Qwen3-TTS model '%s' loaded successfully (dtype=%s, attn=%s)",
            requested_model,
            qwen_model_dtype or "unknown",
            qwen_model_attn_impl or "unknown",
        )

    return qwen_model


def _qwen_prompt_cache_key(audio_bytes: bytes, reference_text: str) -> str:
    import hashlib
    digest = hashlib.md5(audio_bytes + reference_text.encode("utf-8")).hexdigest()
    return digest


def translate_with_nllb(model_name: str, source_language: str, target_language: str, text: str) -> str:

    tokenizer, model = load_nllb_model(model_name)
    src_flores = FLORES.get(source_language)
    tgt_flores = FLORES.get(target_language)

    if src_flores is None:
        raise RuntimeError(
            f"Source language '{source_language}' is not in the FLORES-200 map used by NLLB. "
            "Add the language code to the FLORES dict in inference/main.py."
        )
    if tgt_flores is None:
        raise RuntimeError(
            f"Target language '{target_language}' is not in the FLORES-200 map used by NLLB. "
            "Add the language code to the FLORES dict in inference/main.py."
        )

    tokenizer.src_lang = src_flores
    inputs = tokenizer(
        text,
        return_tensors="pt",
        padding=True,
        truncation=True,
        max_length=512,
    ).to(HOST_DEVICE)
    target_token_id = tokenizer.convert_tokens_to_ids(tgt_flores)

    with torch.no_grad():
        tokens = model.generate(
            **inputs,
            forced_bos_token_id=target_token_id,
            max_length=512,
        )

    return tokenizer.batch_decode(tokens, skip_special_tokens=True)[0]

@app.post("/transcribe", response_model=TranscriptionResponse)
async def transcribe(
    file: UploadFile = File(...),
    model: str = "base",
    language: Optional[str] = None,
    cpu_compute_type: str = "int8",
    cpu_threads: Optional[int] = None,
    num_workers: int = 1,
    background_tasks: BackgroundTasks = BackgroundTasks(),
):
    temp_audio_path = None
    try:
        if _is_dry_run_enabled():
            logger.info("Dry-run transcription request received (model=%s, language=%s)", model, language or "auto")
            return TranscriptionResponse(
                success=True,
                language=language or "es",
                language_probability=1.0,
                segments=[TranscriptSegment(start=0.0, end=1.5, text="dry-run transcript segment")],
            )

        temp_audio_path = TEMP_DIR / f"audio_{datetime.now().timestamp()}.wav"
        contents = await file.read()
        temp_audio_path.write_bytes(contents)

        requested_cpu_threads = cpu_threads if (cpu_threads is not None and cpu_threads > 0) else "auto"
        requested_num_workers = max(1, int(num_workers or 1))
        requested_cpu_compute = cpu_compute_type or "int8"

        logger.info(
            "Transcribing file '%s' (model=%s, cpu_compute=%s, cpu_threads=%s, cpu_workers=%s)",
            file.filename,
            model,
            requested_cpu_compute,
            requested_cpu_threads,
            requested_num_workers,
        )
        logger.info("Transcription temp audio path: %s", temp_audio_path)

        whisper = load_whisper_model(model, cpu_compute_type, cpu_threads, num_workers)
        segments, info = whisper.transcribe(str(temp_audio_path), language=language)

        transcript_segments = [
            TranscriptSegment(start=seg.start, end=seg.end, text=seg.text.strip())
            for seg in segments
        ]

        logger.info(
            "Transcription complete: segments=%s language=%s",
            len(transcript_segments),
            info.language,
        )
        background_tasks.add_task(lambda p=temp_audio_path: p.unlink(missing_ok=True))

        return TranscriptionResponse(
            success=True,
            language=info.language or "unknown",
            language_probability=info.language_probability or 0.0,
            segments=transcript_segments,
        )
    except Exception as exc:
        logger.error("Transcription failed: %s", exc, exc_info=True)
        if temp_audio_path:
            background_tasks.add_task(lambda p=temp_audio_path: p.unlink(missing_ok=True))
        raise HTTPException(status_code=400, detail=str(exc)) from exc

# ============================================================================
# Translation (NLLB-200 GPU host)
# ============================================================================

@app.post("/translate", response_model=TranslationResponse)
async def translate(
    transcript_json: str = Form(...),
    source_language: str = Form(...),
    target_language: str = Form(...),
    model: str = Form(...),
):
    try:
        if _is_dry_run_enabled():
            transcript_data = json.loads(transcript_json)
            segments = transcript_data.get("segments", [])
            translated_segments = [
                TranslatedSegment(
                    start=seg.get("start", 0.0),
                    end=seg.get("end", 0.0),
                    text=seg.get("text", ""),
                    translated_text=f"{seg.get('text', '')} [dry-run {target_language}]".strip(),
                )
                for seg in segments
            ]
            logger.info(
                "Dry-run translation request served: source=%s target=%s segments=%s",
                source_language,
                target_language,
                len(translated_segments),
            )
            return TranslationResponse(
                success=True,
                source_language=source_language,
                target_language=target_language,
                segments=translated_segments,
            )

        logger.info(
            "Translating transcript: source=%s target=%s model=%s",
            source_language,
            target_language,
            model,
        )
        transcript_data = json.loads(transcript_json)
        segments = transcript_data.get("segments", [])
        logger.info("Translation request contains %s segments", len(segments))

        translated_segments = []
        failures = []

        for index, seg in enumerate(segments):
            text = seg.get("text", "")
            translated_text = ""
            if text:
                try:
                    translated_text = translate_with_nllb(
                        model,
                        source_language,
                        target_language,
                        text,
                    )
                except Exception as exc:
                    segment_label = seg.get("start", index)
                    logger.error("Failed to translate segment %s: %s", segment_label, exc)
                    failures.append(f"segment {segment_label}: {exc}")
                    continue
            translated_segments.append(TranslatedSegment(
                start=seg.get("start", 0),
                end=seg.get("end", 0),
                text=text,
                translated_text=translated_text,
            ))

        if failures:
            raise RuntimeError(
                "Translation failed; no fallback was applied. " + "; ".join(failures)
            )

        logger.info(
            "Translation complete: translated_segments=%s",
            len(translated_segments),
        )
        return TranslationResponse(
            success=True,
            source_language=source_language,
            target_language=target_language,
            segments=translated_segments,
        )
    except Exception as exc:
        logger.error("Translation failed: %s", exc, exc_info=True)
        raise HTTPException(status_code=400, detail=str(exc)) from exc

# ============================================================================
# TTS
# ============================================================================

@app.post("/tts", response_model=TtsResponse)
async def text_to_speech(
    text: str = Form(...),
    voice: str = Form("en-US-AriaNeural"),
    background_tasks: BackgroundTasks = BackgroundTasks(),
):
    raise HTTPException(status_code=501, detail="Generic local GPU TTS is not enabled in this host build.")

@app.post("/tts/xtts/references", response_model=XttsReferenceResponse)
async def register_xtts_reference(
    speaker_id: str = Form(...),
    transcript: Optional[str] = Form(None),
    file: UploadFile = File(...),
):
    try:
        suffix = Path(file.filename or "reference.wav").suffix or ".wav"
        stored_path = _temp_artifact_path(f"xtts_ref_{speaker_id}", suffix)
        contents = await file.read()
        stored_path.write_bytes(contents)

        reference_id = uuid4().hex
        xtts_reference_registry[reference_id] = {
            "speaker_id": speaker_id,
            "path": str(stored_path),
            "transcript": transcript,
        }

        logger.info(
            "Registered XTTS reference '%s' for speaker '%s' at path=%s",
            reference_id,
            speaker_id,
            stored_path,
        )
        return XttsReferenceResponse(success=True, reference_id=reference_id)
    except Exception as exc:
        logger.error("Failed to register XTTS reference: %s", exc, exc_info=True)
        raise HTTPException(status_code=400, detail=str(exc)) from exc

@app.post("/tts/xtts/segment", response_model=TtsResponse)
async def xtts_segment(
    text: str = Form(...),
    model: str = Form("xtts-v2"),
    language: Optional[str] = Form("en"),
    speaker_id: Optional[str] = Form(None),
    reference_id: Optional[str] = Form(None),
    reference_transcript: Optional[str] = Form(None),
    reference_file: Optional[UploadFile] = File(None),
    background_tasks: BackgroundTasks = BackgroundTasks(),
):
    temp_reference_path = None
    temp_wav_path = None
    temp_mp3_path = None
    try:
        if _is_dry_run_enabled():
            if not text.strip():
                raise HTTPException(status_code=400, detail="XTTS text cannot be empty.")
            temp_mp3_path = _temp_artifact_path("xtts_segment", ".mp3")
            _write_silent_mp3(temp_mp3_path)
            logger.info("Dry-run XTTS segment request served")
            return TtsResponse(
                success=True,
                voice=_normalize_xtts_voice_label(model),
                audio_path=str(temp_mp3_path),
                file_size_bytes=temp_mp3_path.stat().st_size,
            )

        if not text.strip():
            raise HTTPException(status_code=400, detail="XTTS text cannot be empty.")

        resolved_reference_path = None
        resolved_reference_transcript = reference_transcript

        if reference_id:
            reference_entry = xtts_reference_registry.get(reference_id)
            if reference_entry is None:
                raise HTTPException(status_code=404, detail=f"Unknown XTTS reference_id '{reference_id}'.")
            resolved_reference_path = reference_entry.get("path")
            if not resolved_reference_transcript:
                resolved_reference_transcript = reference_entry.get("transcript")

        if reference_file is not None:
            suffix = Path(reference_file.filename or "reference.wav").suffix or ".wav"
            temp_reference_path = _temp_artifact_path("xtts_inline_ref", suffix)
            temp_reference_path.write_bytes(await reference_file.read())
            resolved_reference_path = str(temp_reference_path)

        if not resolved_reference_path:
            raise HTTPException(
                status_code=400,
                detail="XTTS requires reference audio. Configure a speaker reference clip before generating GPU TTS.",
            )

        normalized_language = _normalize_xtts_language(language)
        temp_wav_path = _temp_artifact_path("xtts_segment", ".wav")
        temp_mp3_path = _temp_artifact_path("xtts_segment", ".mp3")

        logger.info(
            "Generating XTTS segment: speaker=%s model=%s language=%s reference_id=%s",
            speaker_id or "<none>",
            _normalize_xtts_voice_label(model),
            normalized_language,
            reference_id or "<inline>",
        )
        logger.info(
            "XTTS artifact paths: reference_path=%s wav_path=%s mp3_path=%s",
            resolved_reference_path,
            temp_wav_path,
            temp_mp3_path,
        )

        _resolved_ref = str(resolved_reference_path)
        _wav = temp_wav_path
        _mp3 = temp_mp3_path

        def _run_synthesis() -> None:
            loaded = load_xtts_model(model)
            loaded.tts_to_file(
                text=text,
                speaker_wav=_resolved_ref,
                language=normalized_language,
                file_path=str(_wav),
            )
            _wav_to_mp3(_wav, _mp3)

        await asyncio.to_thread(_run_synthesis)

        if temp_reference_path is not None:
            background_tasks.add_task(lambda p=temp_reference_path: p.unlink(missing_ok=True))
        if temp_wav_path is not None:
            background_tasks.add_task(lambda p=temp_wav_path: p.unlink(missing_ok=True))

        return TtsResponse(
            success=True,
            voice=_normalize_xtts_voice_label(model),
            audio_path=str(temp_mp3_path),
            file_size_bytes=temp_mp3_path.stat().st_size,
        )
    except HTTPException:
        if temp_reference_path is not None:
            background_tasks.add_task(lambda p=temp_reference_path: p.unlink(missing_ok=True))
        if temp_wav_path is not None:
            background_tasks.add_task(lambda p=temp_wav_path: p.unlink(missing_ok=True))
        if temp_mp3_path is not None:
            background_tasks.add_task(lambda p=temp_mp3_path: p.unlink(missing_ok=True))
        raise
    except Exception as exc:
        logger.error("XTTS segment synthesis failed: %s", exc, exc_info=True)
        if temp_reference_path is not None:
            background_tasks.add_task(lambda p=temp_reference_path: p.unlink(missing_ok=True))
        if temp_wav_path is not None:
            background_tasks.add_task(lambda p=temp_wav_path: p.unlink(missing_ok=True))
        if temp_mp3_path is not None:
            background_tasks.add_task(lambda p=temp_mp3_path: p.unlink(missing_ok=True))
        raise HTTPException(status_code=400, detail=str(exc)) from exc

@app.post("/tts/qwen/segment", response_model=TtsResponse)
async def qwen_segment(
    text: str = Form(...),
    model: str = Form("Qwen/Qwen3-TTS-12Hz-1.7B-Base"),
    language: Optional[str] = Form(None),
    reference_text: Optional[str] = Form(None),
    reference_file: Optional[UploadFile] = File(None),
    background_tasks: BackgroundTasks = BackgroundTasks(),
):
    temp_reference_path = None
    temp_wav_path = None
    temp_mp3_path = None
    try:
        if _is_dry_run_enabled():
            if not text.strip():
                raise HTTPException(status_code=400, detail="Qwen TTS text cannot be empty.")
            resolved_model = _normalize_qwen_model(model)
            temp_mp3_path = _temp_artifact_path("qwen_segment", ".mp3")
            _write_silent_mp3(temp_mp3_path)
            logger.info("Dry-run Qwen3-TTS segment request served (model=%s)", resolved_model)
            return TtsResponse(
                success=True,
                voice=resolved_model,
                audio_path=str(temp_mp3_path),
                file_size_bytes=temp_mp3_path.stat().st_size,
            )

        if not text.strip():
            raise HTTPException(status_code=400, detail="Qwen TTS text cannot be empty.")

        if reference_file is None:
            raise HTTPException(
                status_code=400,
                detail="Qwen3-TTS requires reference audio. Provide a speaker reference clip.",
            )

        suffix = Path(reference_file.filename or "reference.wav").suffix or ".wav"
        temp_reference_path = _temp_artifact_path("qwen_ref", suffix)
        ref_bytes = await reference_file.read()
        temp_reference_path.write_bytes(ref_bytes)

        resolved_model = _normalize_qwen_model(model)
        resolved_language = _normalize_qwen_language(language)
        resolved_reference_text = (reference_text or "").strip()

        temp_wav_path = _temp_artifact_path("qwen_segment", ".wav")
        temp_mp3_path = _temp_artifact_path("qwen_segment", ".mp3")

        logger.info(
            "Generating Qwen3-TTS segment: model=%s language=%s has_ref_text=%s",
            resolved_model,
            resolved_language,
            bool(resolved_reference_text),
        )

        def _run_synthesis() -> None:
            model_instance = load_qwen_model(resolved_model)

            # Build a reusable voice-clone prompt, caching by content hash to avoid
            # recomputing per-segment token extraction when the same reference is reused.
            cache_key = _qwen_prompt_cache_key(ref_bytes, resolved_reference_text)
            prompt_items = qwen_voice_clone_prompt_cache.get(cache_key)
            if prompt_items is None:
                ref_audio_arg = str(temp_reference_path)
                prompt_kwargs: dict = {"ref_audio": ref_audio_arg}
                if resolved_reference_text:
                    prompt_kwargs["ref_text"] = resolved_reference_text
                else:
                    prompt_kwargs["x_vector_only_mode"] = True
                prompt_items = model_instance.create_voice_clone_prompt(**prompt_kwargs)
                qwen_voice_clone_prompt_cache[cache_key] = prompt_items

            lang_arg = resolved_language if resolved_language != "auto" else None
            generate_kwargs: dict = {
                "text": text,
                "voice_clone_prompt": prompt_items,
            }
            if lang_arg:
                generate_kwargs["language"] = lang_arg

            wavs, sr = model_instance.generate_voice_clone(**generate_kwargs)

            import scipy.io.wavfile as wavfile
            import numpy as np
            audio = wavs[0]
            if hasattr(audio, "dtype") and not hasattr(audio.dtype, "itemsize"):
                pass  # unexpected type; let scipy handle it
            elif hasattr(audio, "dtype") and str(audio.dtype).startswith("float"):
                # Convert float audio [-1, 1] to int16 PCM for broad WAV compatibility
                audio = np.clip(audio, -1.0, 1.0)
                audio = (audio * 32767).astype(np.int16)
            wavfile.write(str(temp_wav_path), sr, audio)
            _wav_to_mp3(temp_wav_path, temp_mp3_path)

        await asyncio.to_thread(_run_synthesis)

        if temp_reference_path is not None:
            background_tasks.add_task(lambda p=temp_reference_path: p.unlink(missing_ok=True))
        if temp_wav_path is not None:
            background_tasks.add_task(lambda p=temp_wav_path: p.unlink(missing_ok=True))

        return TtsResponse(
            success=True,
            voice=resolved_model,
            audio_path=str(temp_mp3_path),
            file_size_bytes=temp_mp3_path.stat().st_size,
        )
    except HTTPException:
        if temp_reference_path is not None:
            background_tasks.add_task(lambda p=temp_reference_path: p.unlink(missing_ok=True))
        if temp_wav_path is not None:
            background_tasks.add_task(lambda p=temp_wav_path: p.unlink(missing_ok=True))
        if temp_mp3_path is not None:
            background_tasks.add_task(lambda p=temp_mp3_path: p.unlink(missing_ok=True))
        raise
    except Exception as exc:
        logger.error("Qwen3-TTS segment synthesis failed: %s", exc, exc_info=True)
        if temp_reference_path is not None:
            background_tasks.add_task(lambda p=temp_reference_path: p.unlink(missing_ok=True))
        if temp_wav_path is not None:
            background_tasks.add_task(lambda p=temp_wav_path: p.unlink(missing_ok=True))
        if temp_mp3_path is not None:
            background_tasks.add_task(lambda p=temp_mp3_path: p.unlink(missing_ok=True))
        raise HTTPException(status_code=400, detail=str(exc)) from exc

@app.get("/tts/audio/{filename}")
async def get_tts_audio(filename: str, background_tasks: BackgroundTasks):
    try:
        file_path = TEMP_DIR / filename
        if not file_path.exists():
            raise HTTPException(status_code=404, detail="Audio file not found")
        logger.info("Serving TTS audio artifact: %s", file_path)
        background_tasks.add_task(lambda p=file_path: p.unlink(missing_ok=True))
        return FileResponse(file_path, media_type="audio/mpeg")
    except HTTPException:
        raise
    except Exception as exc:
        logger.error("Failed to retrieve TTS audio: %s", exc)
        raise HTTPException(status_code=400, detail=str(exc)) from exc

# ============================================================================
# Startup / Shutdown
# ============================================================================

@app.on_event("startup")
async def startup_event():
    logger.info("Inference service starting up")
    _log_host_runtime_state("startup")
    if _is_dry_run_enabled():
        global qwen_warmup_complete, qwen_warmup_error, qwen_model_dtype, qwen_model_attn_impl
        qwen_warmup_complete = True
        qwen_warmup_error = None
        qwen_model_dtype = "dry-run"
        qwen_model_attn_impl = "dry-run"
        logger.info("Managed inference host is running in dry-run mode; model warmups are skipped")
        return

    if _probe_qwen_available():
        import threading
        warmup_model = _resolve_qwen_warmup_model()
        def _warmup_qwen():
            global qwen_warmup_complete, qwen_warmup_error
            try:
                resolved = warmup_model or _normalize_qwen_model(None)
                logger.info("Pre-warming Qwen3-TTS model '%s' in background thread", resolved)
                load_qwen_model(warmup_model)
                qwen_warmup_complete = True
                logger.info("Qwen3-TTS pre-warm complete (model='%s')", resolved)
            except Exception as exc:
                qwen_warmup_error = str(exc)
                logger.warning("Qwen3-TTS pre-warm failed: %s", exc, exc_info=True)
        threading.Thread(target=_warmup_qwen, daemon=True, name="qwen-warmup").start()

@app.on_event("shutdown")
async def shutdown_event():
    logger.info("Inference service shutting down")
    _log_host_runtime_state("shutdown")
    if torch.cuda.is_available():
        torch.cuda.empty_cache()
        logger.info("Cleared CUDA cache during shutdown")

if __name__ == "__main__":
    import uvicorn
    parser = argparse.ArgumentParser(description="Babel Player managed inference host")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=18000)
    parser.add_argument("--compute-type", default="int8")
    parser.add_argument("--require-cuda", action="store_true")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    MANAGED_HOST_DRY_RUN = args.dry_run or os.environ.get("BABEL_INFERENCE_DRY_RUN", "0").lower() in ("1", "true", "yes", "on")

    if TORCH_IMPORT_ERROR and not MANAGED_HOST_DRY_RUN:
        raise RuntimeError(
            "PyTorch is required for managed inference host when dry-run mode is disabled. "
            f"Import error: {TORCH_IMPORT_ERROR}"
        )

    HOST_COMPUTE_TYPE = args.compute_type
    HOST_DEVICE = "cuda" if torch.cuda.is_available() else "cpu"
    logger.info(
        "Managed inference host launch args: host=%s port=%s compute_type=%s require_cuda=%s dry_run=%s python=%s",
        args.host,
        args.port,
        args.compute_type,
        args.require_cuda,
        MANAGED_HOST_DRY_RUN,
        sys.executable,
    )
    _log_host_runtime_state("preflight")

    if MANAGED_HOST_DRY_RUN:
        uvicorn_logger.info(
            "Starting uvicorn in dry-run mode on %s:%s",
            args.host,
            args.port,
        )
        uvicorn.run(app, host=args.host, port=args.port, access_log=True, log_level="info")
        sys.exit(0)

    if args.require_cuda and HOST_DEVICE != "cuda":
        raise RuntimeError(
            "Managed GPU host requested CUDA, but torch.cuda.is_available() returned false. "
            "Check the installed Torch CUDA build, NVIDIA driver, and whether the managed runtime can access the GPU."
        )
    
    # Validate compute type is supported by the host device
    valid_compute_types = {"float8", "float16", "int8"}
    if HOST_COMPUTE_TYPE not in valid_compute_types:
        raise RuntimeError(
            f"Invalid compute type '{HOST_COMPUTE_TYPE}'. Must be one of: {', '.join(valid_compute_types)}"
        )
    
    if HOST_DEVICE == "cuda":
        # CUDA host supports float8, float16
        if HOST_COMPUTE_TYPE not in ("float8", "float16"):
            raise RuntimeError(
                f"CUDA host requires --compute-type float8 or float16. Received '{HOST_COMPUTE_TYPE}'."
            )
    else:
        # CPU host only supports int8
        if HOST_COMPUTE_TYPE != "int8":
            raise RuntimeError(
                f"CPU host requires --compute-type int8. Received '{HOST_COMPUTE_TYPE}'."
            )
    
    # Initialize effective compute type (may be downgraded per-stage)
    EFFECTIVE_HOST_COMPUTE_TYPE = HOST_COMPUTE_TYPE

    uvicorn_logger.info(
        "Starting uvicorn for managed inference host on %s:%s",
        args.host,
        args.port,
    )
    uvicorn.run(app, host=args.host, port=args.port, access_log=True, log_level="info")

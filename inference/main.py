# pyright: reportMissingImports=false
# pylint: disable=missing-module-docstring,missing-class-docstring,missing-function-docstring,invalid-name,global-statement,line-too-long,broad-exception-caught

import argparse
import asyncio
from collections import deque
from contextlib import asynccontextmanager, nullcontext
import importlib.util
import json
import logging
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import traceback
import wave
from time import perf_counter
from pathlib import Path
from datetime import datetime, timezone
from typing import Optional
from uuid import uuid4

import numpy as np
import torch
from fastapi import FastAPI, File, Form, UploadFile, HTTPException, BackgroundTasks
from fastapi.responses import FileResponse, StreamingResponse
from pydantic import BaseModel, Field

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
qwen_reference_registry: dict[str, dict[str, str | None]] = {}
qwen_model = None
qwen_model_key = None
HOST_DEVICE = "cuda" if torch.cuda.is_available() else "cpu"
HOST_COMPUTE_TYPE = "float16" if HOST_DEVICE == "cuda" else "int8"
# Tracks effective compute type after per-stage validation and potential downgrades
EFFECTIVE_HOST_COMPUTE_TYPE = HOST_COMPUTE_TYPE
# Tracks downgrade reasons per stage for UI/logging projection
COMPUTE_DOWNGRADE_REASONS: dict[str, str] = {}
DEFAULT_QWEN_MODEL_NAME = "Qwen/Qwen3-TTS-12Hz-1.7B-Base"
DEFAULT_MINIMAL_QWEN_MODEL = "Qwen/Qwen3-TTS-12Hz-0.6B-Base"
PROVIDER_HEALTH_REFRESH_STALE_SECONDS = 60.0

_provider_health_cache: dict[str, dict[str, object]] = {
    "qwen": {
        "ready": False,
        "state": "pending",
        "detail": "Qwen3-TTS capability check pending",
        "checked_at": None,
        "is_stale": True,
        "failure_category": None,
        "metrics": {},
        "history": deque(maxlen=8),
    },
    "nemo": {
        "ready": False,
        "state": "pending",
        "detail": "NeMo capability check pending",
        "checked_at": None,
        "is_stale": True,
        "failure_category": None,
        "metrics": {},
        "history": deque(maxlen=8),
    },
}
_provider_health_refresh_lock: asyncio.Lock | None = None
_provider_health_refresh_task: asyncio.Task | None = None
_qwen_model_load_lock: asyncio.Lock | None = None
_qwen_segment_semaphore: asyncio.Semaphore | None = None
_qwen_segment_waiters = 0
_active_request_count = 0
_active_qwen_request_count = 0
_active_diarization_request_count = 0
_flash_attn_available: bool | None = None
_qwen_max_concurrency = 1
_nemo_diarizer_construction_lock = threading.Lock()
_nemo_restore_meta_patch_applied = False
_parakeet_model = None
_parakeet_model_lock = threading.Lock()

# Temporary directory for artifacts
TEMP_DIR = Path(tempfile.gettempdir()) / "babel_inference"
TEMP_DIR.mkdir(exist_ok=True)


def _ensure_ffprobe_on_path_for_pydub() -> None:
    """
    NeMo's clustering diarizer loads audio via pydub, which shells out to ffprobe.
    If ffmpeg is discoverable but ffprobe is not on PATH (common when tools are bundled),
    prepend ffmpeg's directory so ffprobe.exe is found on Windows.
    """
    if shutil.which("ffprobe") is not None:
        return
    ffmpeg = shutil.which("ffmpeg")
    if ffmpeg:
        os.environ["PATH"] = (
            str(Path(ffmpeg).resolve().parent)
            + os.pathsep
            + os.environ.get("PATH", "")
        )


_ensure_ffprobe_on_path_for_pydub()


def _resolve_debug_log_path() -> Path:
    env_path = os.environ.get("BABEL_DEBUG_LOG_PATH")
    if env_path:
        return Path(env_path)

    for candidate in Path(__file__).resolve().parents:
        if (candidate / "Babel-Player.sln").exists():
            return candidate / "debug-f76224.log"

    return Path.cwd() / "debug-f76224.log"


DEBUG_LOG_PATH = _resolve_debug_log_path()
DEBUG_SESSION_ID = "f76224"
NEMO_DIARIZATION_DEFAULT_PROVIDER = "nemo"
NEMO_VAD_MODEL = "vad_multilingual_marblenet"
NEMO_SPEAKER_EMBEDDING_MODEL = "titanet_large"
NEMO_SAMPLE_RATE = 16000
NEMO_BATCH_SIZE = 64
NEMO_VERBOSE = False
NEMO_COLLAR = 0.25
NEMO_IGNORE_OVERLAP = True
NEMO_SPEAKER_WINDOW_LENGTHS = (1.5, 1.25, 1.0, 0.75, 0.5)
NEMO_SPEAKER_SHIFT_LENGTHS = (0.75, 0.625, 0.5, 0.375, 0.25)
NEMO_SPEAKER_MULTISCALE_WEIGHTS = (1, 1, 1, 1, 1)
NEMO_VAD_PARAMETERS = {
    "window_length_in_sec": 0.15,
    "shift_length_in_sec": 0.01,
    "smoothing": "median",
    "overlap": 0.5,
    "onset": 0.1,
    "offset": 0.1,
    "pad_onset": 0.1,
    "pad_offset": 0.0,
    "min_duration_on": 0.0,
    "min_duration_off": 0.2,
    "filter_speech_first": True,
}


def _write_debug_log(run_id: str, hypothesis_id: str, location: str, message: str, data: dict) -> None:
    payload = {
        "sessionId": DEBUG_SESSION_ID,
        "runId": run_id,
        "hypothesisId": hypothesis_id,
        "location": location,
        "message": message,
        "data": data,
        "timestamp": int(time.time() * 1000),
    }
    try:
        with DEBUG_LOG_PATH.open("a", encoding="utf-8") as handle:
            handle.write(json.dumps(payload) + "\n")
    except Exception:
        pass


def _normalize_torch_device(map_location):
    if map_location is None:
        return torch.device("cuda") if torch.cuda.is_available() else torch.device("cpu")
    if isinstance(map_location, torch.device):
        return map_location
    return torch.device(map_location)


def _module_has_meta_tensors(module: torch.nn.Module) -> bool:
    for parameter in module.parameters(recurse=True):
        if getattr(parameter, "is_meta", False):
            return True
    for buffer in module.buffers(recurse=True):
        if getattr(buffer, "is_meta", False):
            return True
    return False


def _move_module_for_restore(instance: torch.nn.Module, map_location):
    normalized_map_location = _normalize_torch_device(map_location)
    try:
        return instance.to(normalized_map_location)
    except NotImplementedError as exc:
        if "meta tensor" not in str(exc).lower() or not _module_has_meta_tensors(instance):
            raise

        logger.warning(
            "Applying NeMo meta-tensor restore compatibility fallback for %s on %s",
            type(instance).__name__,
            normalized_map_location,
        )
        return instance.to_empty(device=normalized_map_location)


def _load_config_and_state_dict_with_meta_tensor_fallback(
    connector_module,
    connector,
    calling_cls,
    restore_path: str,
    override_config_path=None,
    map_location=None,
    strict: bool = True,
    return_config: bool = False,
    trainer=None,
):
    cwd = os.getcwd()
    normalized_map_location = _normalize_torch_device(map_location)
    app_state = connector_module.AppState()

    use_extracted_dir = connector.model_extracted_dir is not None and os.path.isdir(connector.model_extracted_dir)
    if use_extracted_dir:
        connector_module.logging.info(
            f"Restoration will occur within pre-extracted directory : `{connector.model_extracted_dir}`."
        )

    dir_context = nullcontext(connector.model_extracted_dir) if use_extracted_dir else tempfile.TemporaryDirectory()
    with dir_context as tmpdir:
        try:
            if not use_extracted_dir:
                filter_fn = None
                if return_config:
                    filter_fn = lambda name: ".yaml" in name
                members = connector._filtered_tar_info(restore_path, filter_fn=filter_fn)
                connector._unpack_nemo_file(path2file=restore_path, out_folder=tmpdir, members=members)

            os.chdir(tmpdir)
            if override_config_path is None:
                config_yaml = connector.model_config_yaml
            else:
                config_yaml = override_config_path

            if not isinstance(config_yaml, (connector_module.OmegaConf, connector_module.DictConfig)):
                conf = connector_module.OmegaConf.load(config_yaml)
            else:
                conf = config_yaml
                if override_config_path is not None:
                    conf = connector_module.OmegaConf.to_container(conf, resolve=True)
                    conf = connector_module.OmegaConf.create(conf)

            if "model" in conf:
                conf = conf.model

            if return_config:
                return conf

            if app_state.model_parallel_size is not None and app_state.model_parallel_size > 1:
                model_weights = connector._inject_model_parallel_rank_for_ckpt(tmpdir, connector.model_weights_ckpt)
            else:
                model_weights = os.path.join(tmpdir, connector.model_weights_ckpt)

            connector_module.OmegaConf.set_struct(conf, True)
            os.chdir(cwd)
            calling_cls._set_model_restore_state(is_being_restored=True, folder=tmpdir)
            instance = calling_cls.from_config_dict(config=conf, trainer=trainer)
            instance = _move_module_for_restore(instance, normalized_map_location)

            if app_state.model_parallel_size is not None and app_state.model_parallel_size > 1:
                model_weights = connector._inject_model_parallel_rank_for_ckpt(tmpdir, connector.model_weights_ckpt)
            state_dict = connector._load_state_dict_from_disk(model_weights, map_location=normalized_map_location)
        finally:
            os.chdir(cwd)

    return conf, instance, state_dict


def _apply_nemo_meta_tensor_restore_patch() -> None:
    global _nemo_restore_meta_patch_applied
    if _nemo_restore_meta_patch_applied:
        return

    import nemo.core.connectors.save_restore_connector as save_restore_connector_module

    original_load_config_and_state_dict = (
        save_restore_connector_module.SaveRestoreConnector.load_config_and_state_dict
    )

    if getattr(original_load_config_and_state_dict, "__babel_meta_tensor_patch__", False):
        _nemo_restore_meta_patch_applied = True
        return

    def _patched_load_config_and_state_dict(
        self,
        calling_cls,
        restore_path: str,
        override_config_path=None,
        map_location=None,
        strict: bool = True,
        return_config: bool = False,
        trainer=None,
        validate_access_integrity: bool = True,
    ):
        try:
            return original_load_config_and_state_dict(
                self,
                calling_cls,
                restore_path,
                override_config_path,
                map_location,
                strict,
                return_config,
                trainer,
                validate_access_integrity,
            )
        except NotImplementedError as exc:
            if "meta tensor" not in str(exc).lower():
                raise

            logger.warning(
                "NeMo restore hit Torch meta-tensor move incompatibility for %s; retrying with to_empty fallback.",
                restore_path,
            )
            return _load_config_and_state_dict_with_meta_tensor_fallback(
                save_restore_connector_module,
                self,
                calling_cls,
                restore_path,
                override_config_path,
                map_location,
                strict,
                return_config,
                trainer,
            )

    _patched_load_config_and_state_dict.__babel_meta_tensor_patch__ = True
    save_restore_connector_module.SaveRestoreConnector.load_config_and_state_dict = (
        _patched_load_config_and_state_dict
    )
    _nemo_restore_meta_patch_applied = True

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
    "ja": "jpn_Jpan",
    "ko": "kor_Hang",
    # South/Southeast Asian
    "hi": "hin_Deva",
    "bn": "ben_Beng",
    "ta": "tam_Taml",
    "te": "tel_Telu",
    "mr": "mar_Deva",
    "ur": "urd_Arab",
    "th": "tha_Thai",
    # Middle Eastern
    "ar": "arb_Arab",
    "fa": "pes_Arab",
    "he": "heb_Hebr",
    # African
    "am": "amh_Ethi",
    "yo": "yor_Latn",
    "ig": "ibo_Latn",
    "ha": "hau_Latn",
    "zu": "zul_Latn",
}


# ============================================================================
# Pydantic Models
# ============================================================================

class ProviderHealthHistoryEntry(BaseModel):
    timestamp: str
    state: str
    ready: bool
    detail: str
    failure_category: Optional[str] = None


class ProviderHealthSnapshot(BaseModel):
    ready: bool
    state: str
    detail: str
    checked_at: Optional[str] = None
    is_stale: bool = False
    failure_category: Optional[str] = None
    metrics: dict[str, object] = Field(default_factory=dict)
    history: list[ProviderHealthHistoryEntry] = Field(default_factory=list)


class HealthLiveResponse(BaseModel):
    status: str
    timestamp: str
    cuda_available: bool
    cuda_version: Optional[str] = None
    active_requests: int = 0
    active_qwen_requests: int = 0
    active_diarization_requests: int = 0
    busy: bool = False
    busy_reason: Optional[str] = None
    qwen_max_concurrency: int = 1
    qwen_queue_depth: int = 0
    qwen_last_queue_wait_ms: Optional[float] = None
    qwen_last_generation_ms: Optional[float] = None
    qwen_last_reference_prep_ms: Optional[float] = None
    qwen_last_warmup_ms: Optional[float] = None
    provider_health: Optional[dict[str, ProviderHealthSnapshot]] = None


class StageCapability(BaseModel):
    ready: bool
    detail: Optional[str] = None
    providers: Optional[dict[str, bool]] = None
    provider_details: Optional[dict[str, str]] = None
    provider_health: Optional[dict[str, ProviderHealthSnapshot]] = None
    default_provider: Optional[str] = None
    engines: Optional[list[str]] = None


class CapabilitiesResponse(BaseModel):
    transcription: StageCapability
    translation: StageCapability
    tts: StageCapability
    diarization: StageCapability
    vocal_separation: StageCapability


class WordTimestampResponse(BaseModel):
    text: str
    start: float
    end: float


class TranscriptSegmentResponse(BaseModel):
    start: float
    end: float
    text: str
    words: list[WordTimestampResponse] = Field(default_factory=list)


class TranscriptionResponse(BaseModel):
    success: bool
    language: str
    language_probability: float
    segments: list[TranscriptSegmentResponse]
    error_message: Optional[str] = None


class TranslatedSegmentResponse(BaseModel):
    start: float
    end: float
    text: str
    translated_text: str
    speaker_id: Optional[str] = None


class TranslationResponse(BaseModel):
    success: bool
    source_language: str
    target_language: str
    segments: list[TranslatedSegmentResponse]
    error_message: Optional[str] = None


class TtsResponse(BaseModel):
    success: bool
    voice: str
    audio_path: str
    file_size_bytes: int
    error_message: Optional[str] = None


class QwenBatchSegmentRequest(BaseModel):
    segment_id: str
    text: str
    language: Optional[str] = None
    reference_id: str


class QwenBatchRequest(BaseModel):
    model: str = "Qwen/Qwen3-TTS-12Hz-1.7B-Base"
    segments: list[QwenBatchSegmentRequest]


class QwenBatchSegmentResponse(BaseModel):
    segment_id: str
    voice: str
    audio_path: str
    file_size_bytes: int


class QwenBatchResponse(BaseModel):
    success: bool
    voice: str
    segments: list[QwenBatchSegmentResponse]
    error_message: Optional[str] = None


class QwenReferenceResponse(BaseModel):
    success: bool
    reference_id: Optional[str] = None
    error_message: Optional[str] = None


class VocalSeparationResponse(BaseModel):
    success: bool
    vocals_filename: str           # retrieve via /tts/audio/{filename}
    instrumental_filename: str     # retrieve via /tts/audio/{filename}
    vocals_file_size_bytes: int
    instrumental_file_size_bytes: int
    vocals_model: str
    instrumental_model: str
    error_message: Optional[str] = None


# ============================================================================
# Diarization models
# ============================================================================

class DiarizationSegment(BaseModel):
    start: float
    end: float
    speaker_id: str


class DiarizationResponse(BaseModel):
    success: bool
    segments: list[DiarizationSegment]
    speaker_count: int
    error_message: Optional[str] = None


class SpeakerSegmentInput(BaseModel):
    start: float
    end: float


class SpeakerReferenceResult(BaseModel):
    speaker_id: str
    reference_id: str  # registered in xtts_reference_registry for reuse
    duration_seconds: float


class SpeakerReferenceResponse(BaseModel):
    success: bool
    speakers: list[SpeakerReferenceResult]
    error_message: Optional[str] = None


# ============================================================================
# Capability probes
# ============================================================================

def _probe_whisper_available() -> tuple[bool, str]:
    try:
        import faster_whisper  # noqa: F401
        return True, "faster-whisper available"
    except Exception as exc:
        return False, str(exc)


def _probe_nllb_available() -> tuple[bool, str]:
    try:
        import transformers  # noqa: F401
        import ctranslate2  # noqa: F401
        return True, "ctranslate2 + transformers available"
    except Exception as exc:
        return False, str(exc)


def _probe_vocal_separator_available() -> tuple[bool, str]:
    try:
        from audio_separator.separator import Separator  # noqa: F401
        return True, "audio-separator available"
    except Exception as exc:
        return False, str(exc)


def _find_module(module_name: str):
    """
    Locate the import specification for a given module name.
    
    Parameters:
        module_name (str): The dotted module or package name to look up.
    
    Returns:
        importlib.machinery.ModuleSpec | None: The module's import spec if found, `None` if the module cannot be located or the lookup fails due to import-related or value errors.
    """
    try:
        return importlib.util.find_spec(module_name)
    except (ImportError, ModuleNotFoundError, ValueError):
        return None


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


def _resolve_qwen_max_concurrency() -> int:
    raw_value = os.getenv("BABEL_QWEN_MAX_CONCURRENCY", "1").strip()
    try:
        requested = int(raw_value)
    except ValueError:
        logger.warning("Invalid BABEL_QWEN_MAX_CONCURRENCY=%r; defaulting to 1", raw_value)
        return 1
    return max(1, min(2, requested))


def _probe_flash_attn_available() -> bool:
    global _flash_attn_available
    if _flash_attn_available is None:
        _flash_attn_available = _find_module("flash_attn") is not None
    return _flash_attn_available


def _ensure_provider_health_defaults(provider: str) -> dict[str, object]:
    """
    Ensure a provider health cache entry exists for the given provider and return it.
    
    If no entry exists, creates and stores a default health dictionary for the provider with these keys:
    - `ready`: readiness boolean (initially False)
    - `state`: lifecycle state string (initially "pending")
    - `detail`: human-readable detail message
    - `checked_at`: timestamp of last check (initially None)
    - `is_stale`: boolean indicating staleness (initially True)
    
    Parameters:
        provider (str): Provider identifier used as the cache key.
    
    Returns:
        dict[str, object]: The provider's health entry from the global cache.
    """
    entry = _provider_health_cache.get(provider)
    if entry is None:
        entry = {
            "ready": False,
            "state": "pending",
            "detail": f"{provider} capability check pending",
            "checked_at": None,
            "is_stale": True,
            "failure_category": None,
            "metrics": {},
            "history": deque(maxlen=8),
        }
        _provider_health_cache[provider] = entry
    if not isinstance(entry.get("history"), deque):
        entry["history"] = deque(entry.get("history", []), maxlen=8)
    if not isinstance(entry.get("metrics"), dict):
        entry["metrics"] = {}
    return entry


def _provider_health_is_stale(provider: str) -> bool:
    """
    Determine whether a provider's cached health entry is considered stale and update its `is_stale` flag accordingly.
    
    Checks whether the entry has a valid `checked_at` timestamp, whether its `state` is `"pending"` or `"refreshing"`, and whether the timestamp age exceeds the configured stale threshold; sets `entry["is_stale"]` to reflect the result.
    
    Parameters:
        provider (str): The provider key whose health entry to evaluate (e.g., "qwen" or "nemo").
    
    Returns:
        bool: `True` if the provider's health entry is stale, `False` otherwise.
    """
    entry = _ensure_provider_health_defaults(provider)
    checked_at = entry.get("checked_at")
    if checked_at is None:
        entry["is_stale"] = True
        return True
    if entry.get("state") in {"pending", "refreshing"}:
        entry["is_stale"] = True
        return True
    if not isinstance(checked_at, datetime):
        entry["is_stale"] = True
        return True
    age_seconds = (_utc_now() - checked_at).total_seconds()
    is_stale = age_seconds >= PROVIDER_HEALTH_REFRESH_STALE_SECONDS
    entry["is_stale"] = is_stale
    return is_stale


def _record_provider_health(
    provider: str,
    ready: bool,
    state: str,
    detail: str,
    *,
    checked_at: Optional[datetime] = None,
    failure_category: Optional[str] = None,
    metrics: Optional[dict[str, object]] = None,
) -> None:
    """
    Record the readiness and state details for a health provider in the provider health cache.
    
    Updates the provider's cached entry with the given readiness (`ready`), lifecycle `state`,
    human-readable `detail`, and `checked_at` timestamp (uses current UTC time when omitted).
    Also clears any staleness flag so the entry is considered fresh.
    
    Parameters:
        provider (str): Provider key/name to update (e.g., "qwen", "nemo").
        ready (bool): Whether the provider is currently considered ready.
        state (str): Short state string describing the provider lifecycle (e.g., "ready", "refreshing", "failed").
        detail (str): Human-readable detail or diagnostic message about the provider state.
        checked_at (Optional[datetime]): Timestamp of the health check; when None, current UTC time is used.
    """
    entry = _ensure_provider_health_defaults(provider)
    entry["ready"] = ready
    entry["state"] = state
    entry["detail"] = detail
    entry["checked_at"] = checked_at or _utc_now()
    entry["is_stale"] = False
    entry["failure_category"] = failure_category
    if metrics:
        entry.setdefault("metrics", {}).update(metrics)

    history = entry.setdefault("history", deque(maxlen=8))
    history.appendleft(
        {
            "timestamp": entry["checked_at"].isoformat(),
            "state": state,
            "ready": ready,
            "detail": detail,
            "failure_category": failure_category,
        }
    )


def _mark_provider_health_refreshing(
    provider: str,
    detail: str,
    *,
    failure_category: Optional[str] = None,
) -> None:
    _record_provider_health(
        provider,
        False,
        "refreshing",
        detail,
        failure_category=failure_category,
    )
    _provider_health_cache[provider]["is_stale"] = True


def _mark_provider_health_warming(
    provider: str,
    detail: str,
    *,
    failure_category: Optional[str] = None,
) -> None:
    _record_provider_health(
        provider,
        False,
        "warming",
        detail,
        failure_category=failure_category,
    )
    _provider_health_cache[provider]["is_stale"] = True


def _build_provider_health_snapshot(provider: str) -> ProviderHealthSnapshot:
    entry = _ensure_provider_health_defaults(provider)
    history = [
        ProviderHealthHistoryEntry(**item)
        for item in list(entry.get("history", []))
    ]
    checked_at = entry.get("checked_at")
    checked_at_text = checked_at.isoformat() if isinstance(checked_at, datetime) else None
    return ProviderHealthSnapshot(
        ready=bool(entry.get("ready")),
        state=str(entry.get("state", "pending")),
        detail=str(entry.get("detail", "")),
        checked_at=checked_at_text,
        is_stale=bool(entry.get("is_stale")),
        failure_category=entry.get("failure_category"),
        metrics=dict(entry.get("metrics", {})),
        history=history,
    )


def _update_provider_health_metrics(provider: str, **metrics: object) -> None:
    entry = _ensure_provider_health_defaults(provider)
    entry.setdefault("metrics", {}).update(metrics)


def _get_provider_health_metrics(provider: str) -> dict[str, object]:
    entry = _ensure_provider_health_defaults(provider)
    return dict(entry.get("metrics", {}))


def _get_provider_activity_reasons() -> list[str]:
    reasons: list[str] = []
    for provider in ("qwen", "nemo"):
        entry = _ensure_provider_health_defaults(provider)
        state = str(entry.get("state", "")).strip().lower()
        detail = str(entry.get("detail", "")).strip()
        if state == "warming":
            reasons.append(detail or f"{provider} warming up")
            continue
        if state == "refreshing" and detail:
            reasons.append(detail)
    return reasons


def _nemo_background_health_enabled() -> bool:
    """When false (default), skip NeMo import/construction during scheduled provider-health refresh."""
    raw = os.environ.get("BABEL_ENABLE_NEMO_BACKGROUND_HEALTH", "").strip().lower()
    return raw in ("1", "true", "yes", "on")


def _seed_nemo_idle_when_background_disabled() -> None:
    if _nemo_background_health_enabled():
        return
    _record_provider_health(
        "nemo",
        True,
        "idle",
        "NeMo /diarize is available on demand; background warm-up disabled "
        "(set BABEL_ENABLE_NEMO_BACKGROUND_HEALTH=1 for periodic checks).",
        failure_category=None,
    )


async def _ensure_provider_health_primitives() -> None:
    global _provider_health_refresh_lock, _qwen_model_load_lock, _qwen_segment_semaphore
    if _provider_health_refresh_lock is None:
        _provider_health_refresh_lock = asyncio.Lock()
    if _qwen_model_load_lock is None:
        _qwen_model_load_lock = asyncio.Lock()
    if _qwen_segment_semaphore is None:
        _qwen_max_concurrency = _resolve_qwen_max_concurrency()
        _qwen_segment_semaphore = asyncio.Semaphore(_qwen_max_concurrency)
        _update_provider_health_metrics(
            "qwen",
            max_concurrency=_qwen_max_concurrency,
            queue_depth=_qwen_segment_waiters,
        )
    _seed_nemo_idle_when_background_disabled()


def _schedule_provider_health_refresh(force: bool = False) -> None:
    """
    Schedule a background refresh of provider health status if needed.
    
    If `force` is True or either the "qwen" or "nemo" provider health entries are stale, create an asynchronous task to refresh the provider health cache; do nothing if a refresh task is already in progress.
    """
    global _provider_health_refresh_task
    if _provider_health_refresh_task is not None and not _provider_health_refresh_task.done():
        return
    nemo_stale = _nemo_background_health_enabled() and _provider_health_is_stale("nemo")
    if not force and not (_provider_health_is_stale("qwen") or nemo_stale):
        return
    _provider_health_refresh_task = asyncio.create_task(_refresh_provider_health_cache(force=force))


def _check_nemo_import_viability() -> tuple[bool, str]:
    """
    Check that the NeMo diarization import stack is available without constructing the diarizer.

    This stays lightweight enough for background health refresh and startup probing.
    """
    missing: list[str] = []
    if _find_module("nemo.collections.asr") is None:
        missing.append("nemo.collections.asr")
    if _find_module("omegaconf") is None:
        missing.append("omegaconf")
    if _find_module("lightning.pytorch") is None:
        missing.append("lightning.pytorch")

    if missing:
        return False, "Missing diarization dependency: " + ", ".join(missing)

    try:
        import nemo.collections.asr  # noqa: F401
        import lightning.pytorch  # noqa: F401
        import omegaconf  # noqa: F401
        _apply_nemo_meta_tensor_restore_patch()
    except Exception as exc:
        return False, f"NeMo import failed: {exc}"

    return True, "NeMo import dependencies available"


def _require_config_attr(config_node, attr_name: str, display_name: str):
    try:
        return getattr(config_node, attr_name)
    except Exception as exc:
        raise RuntimeError(
            f"{display_name} missing from {type(config_node).__name__}: {exc}"
        ) from exc


def _check_nemo_diarization_contract() -> tuple[bool, str]:
    """
    Check the NeMo diarization config contract without constructing the diarizer.

    This validates the config keys that the background refresh and the startup
    probe rely on, while keeping the public capabilities endpoint lightweight.
    """
    try:
        config = _build_nemo_diarization_config(
            Path("probe-manifest.json"),
            Path("probe-out"),
            None,
            None,
        )
        _ = (
            _require_config_attr(config, "device", "device"),
            _require_config_attr(config, "sample_rate", "sample_rate"),
            _require_config_attr(config, "verbose", "verbose"),
            _require_config_attr(config, "batch_size", "batch_size"),
            _require_config_attr(config, "num_workers", "num_workers"),
            _require_config_attr(config.diarizer, "collar", "diarizer.collar"),
            _require_config_attr(config.diarizer, "ignore_overlap", "diarizer.ignore_overlap"),
            _require_config_attr(config.diarizer.vad, "model_path", "diarizer.vad.model_path"),
            _require_config_attr(
                config.diarizer.vad.parameters,
                "window_length_in_sec",
                "diarizer.vad.parameters.window_length_in_sec",
            ),
            _require_config_attr(
                config.diarizer.vad.parameters,
                "shift_length_in_sec",
                "diarizer.vad.parameters.shift_length_in_sec",
            ),
            _require_config_attr(
                config.diarizer.speaker_embeddings,
                "model_path",
                "diarizer.speaker_embeddings.model_path",
            ),
            _require_config_attr(
                config.diarizer.speaker_embeddings.parameters,
                "window_length_in_sec",
                "diarizer.speaker_embeddings.parameters.window_length_in_sec",
            ),
            _require_config_attr(
                config.diarizer.speaker_embeddings.parameters,
                "shift_length_in_sec",
                "diarizer.speaker_embeddings.parameters.shift_length_in_sec",
            ),
            _require_config_attr(
                config.diarizer.speaker_embeddings.parameters,
                "multiscale_weights",
                "diarizer.speaker_embeddings.parameters.multiscale_weights",
            ),
            _require_config_attr(
                config.diarizer.speaker_embeddings.parameters,
                "save_embeddings",
                "diarizer.speaker_embeddings.parameters.save_embeddings",
            ),
            _require_config_attr(
                config.diarizer.clustering.parameters,
                "oracle_num_speakers",
                "diarizer.clustering.parameters.oracle_num_speakers",
            ),
            _require_config_attr(
                config.diarizer.clustering.parameters,
                "max_num_speakers",
                "diarizer.clustering.parameters.max_num_speakers",
            ),
        )
    except Exception as exc:
        return False, f"NeMo diarization config contract invalid: {exc}"

    return True, "NeMo diarization config contract ready"


def _check_nemo_diarizer_construction() -> tuple[bool, str]:
    if _find_module("nemo.collections.asr") is None:
        return False, "Missing diarization dependency: nemo.collections.asr"

    _apply_nemo_meta_tensor_restore_patch()
    import nemo.collections.asr as nemo_asr

    with tempfile.TemporaryDirectory(dir=TEMP_DIR, prefix="nemo_probe_") as work_dir_name:
        work_dir = Path(work_dir_name)
        out_dir = work_dir / "out"
        manifest_path = work_dir / "manifest.json"
        manifest_path.write_text(
            json.dumps(
                {
                    "audio_filepath": str(work_dir / "probe.wav"),
                    "offset": 0.0,
                    "duration": None,
                    "label": "infer",
                    "text": "-",
                    "rttm_filepath": None,
                    "uem_filepath": None,
                }
            )
            + "\n",
            encoding="utf-8",
        )
        config = _build_nemo_diarization_config(manifest_path, out_dir, None, None)
        try:
            with _nemo_diarizer_construction_lock:
                diarizer = nemo_asr.models.ClusteringDiarizer(cfg=config)
        except Exception as exc:
            logger.warning(
                "NeMo runtime construction failed: %s",
                exc,
                exc_info=True,
            )
            return False, f"NeMo diarizer construction failed: {exc}"
        _ = diarizer

    return True, "NeMo ClusteringDiarizer construction ready"


async def _refresh_qwen_provider_health(force: bool = False) -> None:
    """
    Refresh the cached readiness state for the Qwen TTS provider when stale or requested.

    Attempts to ensure the currently loaded or default minimal Qwen model is ready so the provider health cache is updated. If `force` is True, performs the refresh regardless of cached staleness. Failures during the refresh are suppressed.

    Parameters:
        force (bool): If True, force a refresh even when the provider health cache is not stale.
    """
    if not force and not _provider_health_is_stale("qwen"):
        return
    try:
        model_to_warm = qwen_model_key if qwen_model_key is not None else DEFAULT_MINIMAL_QWEN_MODEL
        await _ensure_qwen_model_ready(model_to_warm)
    except Exception:
        pass


async def _refresh_nemo_provider_health(force: bool = False) -> None:
    """
    Update cached health state for the NeMo diarization provider.
    
    Performs a capability/contract check for NeMo diarization and records the resulting readiness, state, and detail into the provider health cache. If `force` is False and the cached entry is not stale, the function returns immediately. On error the failure is recorded and a warning is logged.
    
    Parameters:
    	force (bool): If True, run the check even when the cached health entry is not stale.
    """
    if not force and not _provider_health_is_stale("nemo"):
        return
    _mark_provider_health_refreshing("nemo", "NeMo import check in progress", failure_category="import")
    try:
        import_ready, import_detail = _check_nemo_import_viability()
        _record_provider_health(
            "nemo",
            import_ready,
            "import-ready" if import_ready else "failed",
            import_detail,
            failure_category=None if import_ready else "import",
        )
        if not import_ready:
            return

        _mark_provider_health_refreshing(
            "nemo",
            "NeMo config contract check in progress",
            failure_category="config-contract",
        )
        contract_ready, contract_detail = _check_nemo_diarization_contract()
        _record_provider_health(
            "nemo",
            contract_ready,
            "config-ready" if contract_ready else "failed",
            contract_detail,
            failure_category=None if contract_ready else "config-contract",
        )
        if not contract_ready:
            return

        _mark_provider_health_refreshing(
            "nemo",
            "NeMo diarizer construction check in progress",
            failure_category="construction",
        )
        construction_ready, construction_detail = await asyncio.to_thread(_check_nemo_diarizer_construction)
        _record_provider_health(
            "nemo",
            construction_ready,
            "ready" if construction_ready else "failed",
            construction_detail,
            failure_category=None if construction_ready else "construction",
        )
    except Exception as exc:
        _record_provider_health(
            "nemo",
            False,
            "failed",
            f"NeMo capability check failed: {exc}",
            failure_category="construction",
        )
        logger.warning(f"NeMo capability check failed: {exc}", exc_info=True)


async def _refresh_provider_health_cache(force: bool = False) -> None:
    """
    Refresh cached provider health information for Qwen and NeMo.

    Ensures provider-health primitives are initialized and updates the cached readiness and detail for both providers concurrently. Qwen warmup runs in the background while NeMo contract check completes promptly. When `force` is True, performs a refresh regardless of cache staleness.

    Parameters:
        force (bool): If True, force a refresh even if cached entries are not marked stale.
    """
    global _provider_health_refresh_task
    await _ensure_provider_health_primitives()
    lock = _provider_health_refresh_lock
    if lock is None:
        return

    async with lock:
        try:
            # Start Qwen warmup in background; NeMo optionally (see BABEL_ENABLE_NEMO_BACKGROUND_HEALTH)
            qwen_task = asyncio.create_task(_refresh_qwen_provider_health(force=force))
            if _nemo_background_health_enabled():
                await _refresh_nemo_provider_health(force=force)
            else:
                _record_provider_health(
                    "nemo",
                    True,
                    "idle",
                    "NeMo /diarize is available on demand; background warm-up disabled "
                    "(set BABEL_ENABLE_NEMO_BACKGROUND_HEALTH=1 for periodic checks).",
                    failure_category=None,
                )
            # Ensure Qwen task completes and log any errors
            try:
                await qwen_task
            except Exception as exc:
                logger.warning(f"Background Qwen provider health refresh failed: {exc}", exc_info=True)
        finally:
            _provider_health_refresh_task = None


@app.middleware("http")
async def _count_active_requests(request, call_next):
    """
    Middleware that tracks active in-flight requests and categorizes them for health metrics.
    
    Increments global counters for total active requests and, when applicable, the Qwen TTS or diarization counters for the duration of the request; decrements the same counters after the request completes. Health and capabilities paths are not tracked.
    
    Parameters:
        request: The incoming ASGI/Starlette request.
        call_next: Callable that invokes the next request handler and returns a response.
    
    Returns:
        The response produced by the next handler.
    """
    path = request.url.path
    track = path not in {"/health", "/health/live", "/capabilities"}
    category: str | None = None
    global _active_request_count, _active_qwen_request_count, _active_diarization_request_count

    if track:
        _active_request_count += 1
        if path.startswith("/tts/qwen/"):
            _active_qwen_request_count += 1
            category = "qwen"
        elif path.startswith("/diarize"):
            _active_diarization_request_count += 1
            category = "diarization"
        else:
            category = "general"

    try:
        return await call_next(request)
    finally:
        if track:
            _active_request_count = max(0, _active_request_count - 1)
            if category == "qwen":
                _active_qwen_request_count = max(0, _active_qwen_request_count - 1)
            elif category == "diarization":
                _active_diarization_request_count = max(0, _active_diarization_request_count - 1)


def _build_busy_reason() -> Optional[str]:
    provider_reasons = _get_provider_activity_reasons()
    provider_reason = "; ".join(provider_reasons) if provider_reasons else None

    if _active_qwen_request_count > 0 and _active_diarization_request_count > 0:
        request_reason = (
            f"qwen requests active ({_active_qwen_request_count}); "
            f"diarization requests active ({_active_diarization_request_count})"
        )
    elif _active_qwen_request_count > 0:
        request_reason = f"qwen requests active ({_active_qwen_request_count})"
    elif _active_diarization_request_count > 0:
        request_reason = f"diarization requests active ({_active_diarization_request_count})"
    elif _active_request_count > 0:
        request_reason = f"{_active_request_count} request(s) active"
    else:
        request_reason = None

    if request_reason and provider_reason:
        return f"{request_reason}; {provider_reason}"
    if request_reason:
        return request_reason
    return provider_reason


def _probe_qwen_available() -> tuple[bool, str]:
    """
    Return the cached readiness and detail for the Qwen TTS provider, scheduling a background health refresh if the cached entry is stale.
    
    Returns:
        (ready, detail) (tuple[bool, str]): `true` if Qwen is marked ready, `false` otherwise; `detail` contains a human-readable status or error message.
    """
    entry = _ensure_provider_health_defaults("qwen")
    if _provider_health_is_stale("qwen"):
        _schedule_provider_health_refresh()
    return bool(entry["ready"]), str(entry["detail"])


def _probe_nemo_diarization_available() -> tuple[bool, str]:
    """
    Return the cached readiness and diagnostic detail for the NeMo diarization provider and schedule a background refresh if the cached entry is stale.
    
    Returns:
        tuple: `(ready, detail)` where `ready` is `True` if the last cached NeMo contract check succeeded, `False` otherwise; `detail` is the cached human-readable diagnostic message.
    """
    entry = _ensure_provider_health_defaults("nemo")
    if _provider_health_is_stale("nemo"):
        _schedule_provider_health_refresh()
    return bool(entry["ready"]), str(entry["detail"])


def _normalize_native_speaker_label(raw_label, assigned_labels: dict[str, str]) -> str:
    """
    Normalize a raw speaker label into a stable internal speaker ID and cache the mapping.
    
    If `raw_label` is empty or None, a default native key of the form `speaker_<n>` is used. If a mapping for the native key already exists in `assigned_labels`, the existing normalized ID is returned. Otherwise a new normalized ID of the form `spk_XX` (zero-padded two digits) is created, stored in `assigned_labels` under the native key, and returned.
    
    Parameters:
        raw_label: The original speaker label (any value convertible to string) or None.
        assigned_labels (dict[str, str]): Mutable mapping from native labels to normalized speaker IDs; this function will add a new entry when creating a normalization.
    
    Returns:
        str: The normalized speaker ID (e.g., `spk_00`, `spk_01`, ...).
    """
    key = str(raw_label).strip() if raw_label is not None else ""
    if not key:
        key = f"speaker_{len(assigned_labels)}"
    if key in assigned_labels:
        return assigned_labels[key]

    used_labels = set(assigned_labels.values())
    speaker_index = len(assigned_labels)
    while True:
        normalized = f"spk_{speaker_index:02d}"
        if normalized not in used_labels:
            break
        speaker_index += 1
    assigned_labels[key] = normalized
    return normalized


def _parse_rttm_file(rttm_path: Path) -> tuple[list[DiarizationSegment], int]:
    """
    Parse an RTTM file into diarization segments with normalized speaker labels.
    
    Parameters:
        rttm_path (Path): Path to an RTTM-format file.
    
    Returns:
        tuple[list[DiarizationSegment], int]: A pair where the first element is a list of diarization
        segments (each containing start time, end time, and a normalized `speaker_id`), and the
        second element is the count of unique normalized speakers found.
    """
    segments: list[DiarizationSegment] = []
    seen_speakers: set[str] = set()
    assigned_labels: dict[str, str] = {}

    for line in rttm_path.read_text(encoding="utf-8").splitlines():
        parts = line.split()
        if len(parts) < 8 or parts[0] != "SPEAKER":
            continue

        start = round(float(parts[3]), 3)
        end = round(start + float(parts[4]), 3)
        speaker_id = _normalize_native_speaker_label(parts[7], assigned_labels)
        segments.append(DiarizationSegment(start=start, end=end, speaker_id=speaker_id))
        seen_speakers.add(speaker_id)

    return segments, len(seen_speakers)


def _build_nemo_diarization_config(
    manifest_path: Path,
    out_dir: Path,
    min_speakers: Optional[int],
    max_speakers: Optional[int],
):
    """
    Build the minimal NeMo ClusteringDiarizer config required by the current runtime contract.

    The config intentionally stays close to NeMo's documented clustering diarization example and
    includes the `diarizer.vad.parameters` mapping that newer NeMo releases read during model
    construction.
    """
    from nemo.collections.asr.models.configs.diarizer_config import NeuralDiarizerInferenceConfig
    from omegaconf import OmegaConf

    clustering_parameters: dict[str, object] = {
        "oracle_num_speakers": False,
        "max_num_speakers": max_speakers or 8,
    }
    if min_speakers is not None and max_speakers is not None and min_speakers == max_speakers:
        clustering_parameters["oracle_num_speakers"] = True

    config = OmegaConf.structured(NeuralDiarizerInferenceConfig())
    config.device = HOST_DEVICE
    config.verbose = NEMO_VERBOSE
    config.sample_rate = NEMO_SAMPLE_RATE
    config.batch_size = NEMO_BATCH_SIZE
    config.num_workers = 0

    config.diarizer.manifest_filepath = str(manifest_path)
    config.diarizer.out_dir = str(out_dir)
    config.diarizer.oracle_vad = False
    config.diarizer.collar = NEMO_COLLAR
    config.diarizer.ignore_overlap = NEMO_IGNORE_OVERLAP

    config.diarizer.vad.model_path = NEMO_VAD_MODEL
    config.diarizer.vad.external_vad_manifest = None
    for key, value in NEMO_VAD_PARAMETERS.items():
        setattr(config.diarizer.vad.parameters, key, value)

    config.diarizer.speaker_embeddings.model_path = NEMO_SPEAKER_EMBEDDING_MODEL
    config.diarizer.speaker_embeddings.parameters.window_length_in_sec = NEMO_SPEAKER_WINDOW_LENGTHS
    config.diarizer.speaker_embeddings.parameters.shift_length_in_sec = NEMO_SPEAKER_SHIFT_LENGTHS
    config.diarizer.speaker_embeddings.parameters.multiscale_weights = NEMO_SPEAKER_MULTISCALE_WEIGHTS
    config.diarizer.speaker_embeddings.parameters.save_embeddings = False

    config.diarizer.clustering.parameters.oracle_num_speakers = clustering_parameters["oracle_num_speakers"]
    config.diarizer.clustering.parameters.max_num_speakers = clustering_parameters["max_num_speakers"]
    return config


def _run_nemo_diarization(
    audio_path: Path,
    min_speakers: Optional[int],
    max_speakers: Optional[int],
) -> tuple[list[DiarizationSegment], int]:
    """
    Run NeMo's ClusteringDiarizer on a staged audio file and produce parsed diarization segments.
    
    Parameters:
        audio_path (Path): Path to the input audio file to diarize.
        min_speakers (Optional[int]): Minimum speaker count hint; when equal to `max_speakers`, the diarizer is instructed to use that exact speaker count.
        max_speakers (Optional[int]): Maximum speaker count hint to bound clustering when provided.
    
    Returns:
        tuple[list[DiarizationSegment], int]: A tuple where the first element is a list of diarization segments with normalized speaker IDs and rounded start/end times, and the second element is the number of distinct speakers detected.
    
    Raises:
        RuntimeError: If NeMo produces no RTTM output file.
    """
    _apply_nemo_meta_tensor_restore_patch()
    import nemo.collections.asr as nemo_asr
    import nemo.collections.asr.parts.utils.vad_utils as nemo_vad_utils

    # NeMo VAD uses tqdm progress bars that may flush to an invalid stderr handle
    # under managed host execution on Windows ([Errno 22]). Force quiet tqdm in that path.
    if not getattr(nemo_vad_utils, "_babel_tqdm_patched", False):
        original_tqdm = nemo_vad_utils.tqdm

        def _quiet_tqdm(*args, **kwargs):
            kwargs["disable"] = True
            kwargs.setdefault("file", sys.stdout)
            return original_tqdm(*args, **kwargs)

        nemo_vad_utils.tqdm = _quiet_tqdm
        nemo_vad_utils._babel_tqdm_patched = True

    with tempfile.TemporaryDirectory(dir=TEMP_DIR, prefix="nemo_diar_") as work_dir_name:
        work_dir = Path(work_dir_name)
        out_dir = work_dir / "out"
        manifest_path = work_dir / "manifest.json"
        signal_stats = _measure_wav_signal_stats(audio_path)

        manifest_entry = {
            "audio_filepath": str(audio_path),
            "offset": 0.0,
            "duration": None,
            "label": "infer",
            "text": "-",
            "rttm_filepath": None,
            "uem_filepath": None,
        }
        if min_speakers is not None and max_speakers is not None and min_speakers == max_speakers:
            manifest_entry["num_speakers"] = min_speakers

        manifest_path.write_text(json.dumps(manifest_entry) + "\n", encoding="utf-8")
        config = _build_nemo_diarization_config(
            manifest_path,
            out_dir,
            min_speakers,
            max_speakers,
        )

        try:
            with _nemo_diarizer_construction_lock:
                diarizer = nemo_asr.models.ClusteringDiarizer(cfg=config)
        except Exception as exc:
            logger.error(
                "NeMo diarizer initialization failed: %s",
                exc,
                exc_info=True,
            )
            raise
        try:
            diarize_started = time.perf_counter()
            diarizer.diarize()
            diarize_elapsed = time.perf_counter() - diarize_started
        except Exception as exc:
            raise

        rttm_files = sorted(out_dir.rglob("*.rttm"))
        if not rttm_files:
            raise RuntimeError("NeMo diarization did not produce an RTTM file")
        rttm_path = rttm_files[0]
        raw_labels: list[str] = []
        for line in rttm_path.read_text(encoding="utf-8").splitlines():
            parts = line.split()
            if len(parts) >= 8 and parts[0] == "SPEAKER":
                raw_labels.append(parts[7].strip())

        return _parse_rttm_file(rttm_path)


def _validate_diarization_speaker_bounds(
    min_speakers: Optional[int],
    max_speakers: Optional[int],
) -> None:
    """
    Validate optional diarization speaker bounds and raise an HTTP 400 error for invalid values.
    
    Parameters:
        min_speakers (Optional[int]): Minimum number of speakers; when provided it must be greater than or equal to 1.
        max_speakers (Optional[int]): Maximum number of speakers; when provided it must be greater than or equal to 1.
    
    Raises:
        HTTPException: Raised with status code 400 if:
            - `min_speakers` is provided and is less than 1 (detail: "min_speakers must be at least 1"),
            - `max_speakers` is provided and is less than 1 (detail: "max_speakers must be at least 1"),
            - both are provided and `min_speakers` is greater than `max_speakers` (detail: "min_speakers cannot be greater than max_speakers").
    """
    if min_speakers is not None and min_speakers < 1:
        raise HTTPException(status_code=400, detail="min_speakers must be at least 1")
    if max_speakers is not None and max_speakers < 1:
        raise HTTPException(status_code=400, detail="max_speakers must be at least 1")
    if min_speakers is not None and max_speakers is not None and min_speakers > max_speakers:
        raise HTTPException(status_code=400, detail="min_speakers cannot be greater than max_speakers")


def _probe_diarization_providers() -> tuple[dict[str, bool], dict[str, str], bool, str]:
    """
    Probe availability of the managed-GPU NeMo diarization provider and summarize overall readiness.
    
    Returns:
        provider_ready (dict[str, bool]): Mapping of provider name to availability (currently only {'nemo': True/False}).
        provider_details (dict[str, str]): Mapping of provider name to a human-readable availability/detail message.
        stage_ready (bool): `True` if NeMo is available, `False` otherwise.
        detail (str): Human-readable stage-level status:
            - "Diarization available" when NeMo is available,
            - otherwise the NeMo detail string.
    """
    provider_ready: dict[str, bool] = {}
    provider_details: dict[str, str] = {}

    nemo_ready, nemo_detail = _probe_nemo_diarization_available()
    provider_ready["nemo"] = nemo_ready
    provider_details["nemo"] = nemo_detail

    logger.info(
        "Diarization capability probe: nemo=%s (%s)",
        nemo_ready,
        nemo_detail,
    )

    stage_ready = nemo_ready
    if stage_ready:
        detail = "Diarization available"
    else:
        detail = nemo_detail

    return provider_ready, provider_details, stage_ready, detail


def _stage_audio_upload_to_temp(audio: UploadFile, prefix: str) -> Path:
    """
    Create a safe temporary file path for an uploaded audio file.
    
    Parameters:
    	audio (UploadFile): Uploaded file; its base filename (basename) is used when constructing the temp name.
    	prefix (str): Short prefix to include in the generated filename.
    
    Returns:
    	Path: Path inside TEMP_DIR combining the prefix, a random UUID, and the uploaded file's base name.
    """
    safe_name = Path(audio.filename or "").name or "audio"
    temp_audio_path = TEMP_DIR / f"{prefix}_{uuid4().hex}_{safe_name}"
    return temp_audio_path


def _build_diarization_response(
    segments: list[DiarizationSegment],
    speaker_count: int,
) -> DiarizationResponse:
    """
    Builds a successful DiarizationResponse containing the provided segments and speaker count.
    
    Parameters:
        segments (list[DiarizationSegment]): Ordered list of diarization segments.
        speaker_count (int): Number of distinct speakers detected.
    
    Returns:
        DiarizationResponse: A response object with `success=True`, the given `segments`, and `speaker_count`.
    """
    return DiarizationResponse(
        success=True,
        segments=segments,
        speaker_count=speaker_count,
    )


# ============================================================================
# Health / capabilities endpoints
# ============================================================================

@app.get("/health/live", response_model=HealthLiveResponse)
async def health_live():
    """
    Report current liveness and runtime health metrics for the service.
    
    Returns:
        HealthLiveResponse: Contains service status, UTC ISO-8601 timestamp, CUDA availability and CUDA version (when available), current active request counters (total, Qwen TTS, diarization), a `busy` flag indicating whether there are in-flight requests, and a human-readable `busy_reason`.
    """
    cuda_available = torch.cuda.is_available()
    qwen_metrics = _get_provider_health_metrics("qwen")
    busy_reason = _build_busy_reason()
    return HealthLiveResponse(
        status="healthy",
        timestamp=_utc_now().isoformat(),
        cuda_available=cuda_available,
        cuda_version=torch.version.cuda if cuda_available else None,
        active_requests=_active_request_count,
        active_qwen_requests=_active_qwen_request_count,
        active_diarization_requests=_active_diarization_request_count,
        busy=not not busy_reason,
        busy_reason=busy_reason,
        qwen_max_concurrency=_qwen_max_concurrency,
        qwen_queue_depth=int(qwen_metrics.get("queue_depth", _qwen_segment_waiters) or 0),
        qwen_last_queue_wait_ms=qwen_metrics.get("last_queue_wait_ms"),
        qwen_last_generation_ms=qwen_metrics.get("last_generation_ms"),
        qwen_last_reference_prep_ms=qwen_metrics.get("last_reference_prep_ms"),
        qwen_last_warmup_ms=qwen_metrics.get("last_warmup_ms"),
        provider_health={
            "qwen-tts": _build_provider_health_snapshot("qwen"),
            "nemo-local": _build_provider_health_snapshot("nemo"),
        },
    )


@app.get("/health", response_model=HealthLiveResponse)
async def health_check():
    return await health_live()


@app.get("/capabilities", response_model=CapabilitiesResponse)
async def get_stage_capabilities():
    """
    Constructs the service capability summary for transcription, translation, TTS, and diarization.
    
    Probes available backends and assembles a CapabilitiesResponse describing per-stage readiness, human-readable detail strings, provider readiness maps and provider detail maps, the diarization default provider, and supported diarization engines.
    
    Returns:
        CapabilitiesResponse: Aggregated capability information with these fields populated:
            - transcription: readiness/detail for Whisper.
            - translation: readiness/detail for NLLB.
            - tts: readiness/detail for Qwen3-TTS and provider maps for "qwen-tts".
            - diarization: overall readiness/detail, per-provider readiness/details, `default_provider` set to NEMO_DIARIZATION_DEFAULT_PROVIDER, and `engines` set to ["nemo"].
    """
    tx_ready, tx_detail = _probe_whisper_available()
    tl_ready, tl_detail = _probe_nllb_available()
    sep_ready, sep_detail = _probe_vocal_separator_available()

    qwen_ready, qwen_detail = _probe_qwen_available()
    tts_ready = qwen_ready
    if tts_ready:
        tts_detail = "TTS available"
    else:
        tts_detail = f"qwen: {qwen_detail}"

    diar_providers, diar_provider_details, diar_ready, diar_detail = _probe_diarization_providers()

    return CapabilitiesResponse(
        transcription=StageCapability(ready=tx_ready, detail=tx_detail),
        translation=StageCapability(ready=tl_ready, detail=tl_detail),
        tts=StageCapability(
            ready=tts_ready,
            detail=tts_detail,
            providers={
                "qwen-tts": qwen_ready,
            },
            provider_details={
                "qwen-tts": qwen_detail,
            },
            provider_health={
                "qwen-tts": _build_provider_health_snapshot("qwen"),
            },
        ),
        diarization=StageCapability(
            ready=diar_ready,
            detail=diar_detail,
            providers=diar_providers,
            provider_details=diar_provider_details,
            provider_health={
                "nemo": _build_provider_health_snapshot("nemo"),
            },
            default_provider=NEMO_DIARIZATION_DEFAULT_PROVIDER,
            engines=["nemo"],
        ),
        vocal_separation=StageCapability(
            ready=sep_ready,
            detail=sep_detail,
            engines=["audio-separator"],
        ),
    )


# ============================================================================
# Transcription
# ============================================================================

def _clear_whisper_model_cache(model_name: str) -> None:
    """Delete the HF hub cache entry for a faster-whisper model.

    Called when the model load fails with a file-open error, which indicates a
    broken or incomplete cache left by a prior interrupted download or a
    huggingface_hub version upgrade that invalidated the cached layout.
    Removing the cache dir forces a clean re-download on the next load attempt.
    """
    hub_cache = os.environ.get("HUGGINGFACE_HUB_CACHE") or os.path.join(
        os.path.expanduser("~"), ".cache", "huggingface", "hub"
    )
    # Standard faster-whisper model names ("base", "small", …) resolve to the
    # Systran org on HuggingFace Hub.
    if "/" not in model_name:
        repo_id = f"Systran/faster-whisper-{model_name}"
    else:
        repo_id = model_name
    cache_dir_name = "models--" + repo_id.replace("/", "--")
    model_cache_path = os.path.join(hub_cache, cache_dir_name)
    if os.path.isdir(model_cache_path):
        logger.warning(f"Clearing stale model cache at {model_cache_path}")
        shutil.rmtree(model_cache_path, ignore_errors=True)
    else:
        logger.warning(f"Model cache dir not found for clearing: {model_cache_path}")


def load_whisper_model(
    model_name: str,
    cpu_compute_type: str = "int8",
    cpu_threads: int = 0,
    num_workers: int = 1,
):
    global whisper_model, whisper_model_key
    effective_compute = EFFECTIVE_HOST_COMPUTE_TYPE if HOST_DEVICE == "cuda" else cpu_compute_type
    cache_key = f"{model_name}:{effective_compute}:{cpu_threads}:{num_workers}"
    if whisper_model is None or whisper_model_key != cache_key:
        from faster_whisper import WhisperModel

        def _do_load():
            return WhisperModel(
                model_name,
                device=HOST_DEVICE,
                compute_type=effective_compute,
                cpu_threads=cpu_threads,
                num_workers=num_workers,
            )

        logger.info(f"Loading Whisper '{model_name}' on {HOST_DEVICE} ({effective_compute})")
        try:
            whisper_model = _do_load()
        except Exception as first_err:
            err_str = str(first_err)
            if "model.bin" in err_str or "Unable to open file" in err_str or "No such file" in err_str:
                # Stale or incomplete HF hub cache — clear it and retry once.
                logger.warning(
                    f"Whisper model load failed ({first_err}); "
                    "clearing stale cache and retrying…"
                )
                _clear_whisper_model_cache(model_name)
                whisper_model = _do_load()
            else:
                raise

        whisper_model_key = cache_key
        logger.info("Whisper loaded")
    return whisper_model


def load_parakeet_model():
    global _parakeet_model
    if _parakeet_model is not None:
        return _parakeet_model
    with _parakeet_model_lock:
        if _parakeet_model is not None:
            return _parakeet_model
        import nemo.collections.asr as nemo_asr
        _apply_nemo_meta_tensor_restore_patch()
        logger.info("Loading Parakeet TDT 0.6B-v3")
        model = nemo_asr.models.EncDecRNNTBPEModel.from_pretrained(
            "nvidia/parakeet-tdt-0.6b-v3"
        )
        import torch
        if torch.cuda.is_available():
            model = model.cuda()
        model.train(False)
        _parakeet_model = model
        logger.info("Parakeet loaded")
        return _parakeet_model


def _is_parakeet_manifest_lock_error(exc: Exception) -> bool:
    message = str(exc)
    return "WinError 32" in message and "manifest.json" in message


def _exception_chain_contains(exc: Exception, needle: str) -> bool:
    seen: set[int] = set()
    current: Exception | None = exc
    while current is not None and id(current) not in seen:
        seen.add(id(current))
        if needle in str(current):
            return True
        next_exc = current.__cause__ or current.__context__
        current = next_exc if isinstance(next_exc, Exception) else None
    return False


def _is_non_retryable_parakeet_input_error(exc: Exception) -> bool:
    return (
        _exception_chain_contains(exc, "Input shape mismatch")
        or _exception_chain_contains(exc, "expected = (batch, time)")
        or _exception_chain_contains(exc, "torch.Size([1, 2,")
    )


def _transcribe_parakeet_with_retry(model, temp_audio_path: Path):
    retry_delays_seconds = (0.2, 0.5)
    max_attempts = len(retry_delays_seconds) + 1

    for attempt in range(1, max_attempts + 1):
        try:
            with torch.inference_mode():
                hypotheses = model.transcribe([str(temp_audio_path)], timestamps=True)
            return hypotheses[0]
        except Exception as exc:
            if _is_non_retryable_parakeet_input_error(exc):
                raise
            if not _is_parakeet_manifest_lock_error(exc) or attempt == max_attempts:
                raise
            delay_seconds = retry_delays_seconds[attempt - 1]
            logger.warning(
                "Parakeet manifest lock detected; retrying transcribe "
                "(attempt %s/%s, delay=%.1fs): %s",
                attempt,
                max_attempts,
                delay_seconds,
                exc,
            )
            time.sleep(delay_seconds)


def _extract_parakeet_word_timestamps(hypothesis) -> list:
    for attr_name in ("timestep", "timestamp", "timestamps"):
        attr_value = getattr(hypothesis, attr_name, None)
        if isinstance(attr_value, dict):
            for key in ("word", "words"):
                candidate = attr_value.get(key)
                if isinstance(candidate, list) and candidate:
                    return candidate
        elif attr_value is not None:
            for key in ("word", "words"):
                candidate = getattr(attr_value, key, None)
                if isinstance(candidate, list) and candidate:
                    return candidate

    direct = getattr(hypothesis, "word_timestamps", None)
    if isinstance(direct, list) and direct:
        return direct

    return []


def _estimate_wav_duration_seconds(audio_path: Path) -> float:
    try:
        with wave.open(str(audio_path), "rb") as wav_reader:
            frames = wav_reader.getnframes()
            sample_rate = wav_reader.getframerate()
            if sample_rate <= 0:
                return 0.0
            return float(frames) / float(sample_rate)
    except (wave.Error, EOFError, OSError):
        return 0.0


def _segment_text_without_word_timestamps(
    fallback_text: str,
    duration_seconds: float | None,
) -> list:
    text = " ".join((fallback_text or "").split()).strip()
    if not text:
        return []

    sentence_chunks = [chunk.strip() for chunk in re.split(r"(?<=[.!?])\s+", text) if chunk.strip()]
    if len(sentence_chunks) <= 1:
        words = text.split()
        chunk_size = 12
        sentence_chunks = [
            " ".join(words[index:index + chunk_size])
            for index in range(0, len(words), chunk_size)
        ] or [text]

    word_counts = [max(1, len(chunk.split())) for chunk in sentence_chunks]
    total_words = max(1, sum(word_counts))
    total_duration = duration_seconds or 0.0
    if total_duration <= 0:
        total_duration = max(total_words / 2.6, len(sentence_chunks) * 1.0)

    segments: list = []
    cursor = 0.0
    for index, chunk in enumerate(sentence_chunks):
        chunk_duration = total_duration * (word_counts[index] / total_words)
        end = total_duration if index == len(sentence_chunks) - 1 else min(total_duration, cursor + chunk_duration)
        segments.append(
            TranscriptSegmentResponse(
                start=round(cursor, 3),
                end=round(end, 3),
                text=chunk,
                words=[],
            )
        )
        cursor = end

    return segments


def _words_to_segments(
    word_timestamps: list,
    fallback_text: str,
    duration_seconds: float | None = None,
) -> list:
    """Group NeMo word-timestamp objects into TranscriptSegmentResponse segments.

    Splits on sentence-ending punctuation or an inter-word gap > 0.5 s.
    Falls back to a single segment when no word timestamps are available.
    """
    if not word_timestamps:
        return _segment_text_without_word_timestamps(fallback_text, duration_seconds)

    segments: list = []
    seg_words: list = []
    seg_start: float = 0.0
    prev_end: float = 0.0

    for w in word_timestamps:
        # NeMo word-timestamp items may be dicts or objects with attributes
        if isinstance(w, dict):
            w_text = w.get("word") or w.get("text") or ""
            w_start = float(w.get("start_time") or w.get("start") or 0.0)
            w_end = float(w.get("end_time") or w.get("end") or 0.0)
        else:
            w_text = getattr(w, "word", None) or getattr(w, "text", "")
            w_start = float(getattr(w, "start_time", None) or getattr(w, "start", 0.0))
            w_end = float(getattr(w, "end_time", None) or getattr(w, "end", 0.0))

        if not w_text:
            continue

        gap = w_start - prev_end if seg_words else 0.0
        ends_sentence = prev_end > 0 and any(
            seg_words[-1].text.rstrip().endswith(p) for p in (".", "?", "!")
        ) if seg_words and hasattr(seg_words[-1], "text") else False

        if seg_words and (gap > 0.5 or ends_sentence):
            segments.append(
                TranscriptSegmentResponse(
                    start=seg_start,
                    end=prev_end,
                    text=" ".join(
                        (s.text if hasattr(s, "text") else s.get("text", ""))
                        for s in seg_words
                    ).strip(),
                    words=[
                        WordTimestampResponse(
                            text=(s.text if hasattr(s, "text") else s.get("text", "")),
                            start=(s.start if hasattr(s, "start") else s.get("start", 0.0)),
                            end=(s.end if hasattr(s, "end") else s.get("end", 0.0)),
                        )
                        for s in seg_words
                    ],
                )
            )
            seg_words = []
            seg_start = w_start

        word_item = type("W", (), {"text": w_text, "start": w_start, "end": w_end})()
        seg_words.append(word_item)
        if not seg_words or len(seg_words) == 1:
            seg_start = w_start
        prev_end = w_end

    if seg_words:
        segments.append(
            TranscriptSegmentResponse(
                start=seg_start,
                end=prev_end,
                text=" ".join(
                    (s.text if hasattr(s, "text") else s.get("text", ""))
                    for s in seg_words
                ).strip(),
                words=[
                    WordTimestampResponse(
                        text=(s.text if hasattr(s, "text") else s.get("text", "")),
                        start=(s.start if hasattr(s, "start") else s.get("start", 0.0)),
                        end=(s.end if hasattr(s, "end") else s.get("end", 0.0)),
                    )
                    for s in seg_words
                ],
            )
        )

    return segments


@app.post("/transcribe/parakeet", response_model=TranscriptionResponse)
async def transcribe_parakeet(
    file: UploadFile = File(...),
    language: Optional[str] = Form(None),
    background_tasks: BackgroundTasks = BackgroundTasks(),
):
    temp_audio_path = None
    try:
        temp_audio_path = _stage_audio_upload_to_temp(file, "parakeet")
        contents = await file.read()
        temp_audio_path.write_bytes(contents)
        temp_audio_path = _ensure_wav_audio(temp_audio_path, force_mono_16k=True)
        duration_seconds = _estimate_wav_duration_seconds(temp_audio_path)
        model = load_parakeet_model()
        hypothesis = _transcribe_parakeet_with_retry(model, temp_audio_path)
        word_ts = _extract_parakeet_word_timestamps(hypothesis)
        segments = _words_to_segments(word_ts, hypothesis.text, duration_seconds)
        background_tasks.add_task(_deferred_cleanup_temp, temp_audio_path)
        return TranscriptionResponse(
            success=True,
            language="en",
            language_probability=1.0,
            segments=segments,
        )
    except Exception as exc:
        logger.error(f"Parakeet transcription failed: {exc}", exc_info=True)
        if temp_audio_path:
            background_tasks.add_task(_deferred_cleanup_temp, temp_audio_path)
        raise HTTPException(status_code=400, detail=str(exc)) from exc


@app.post("/transcribe", response_model=TranscriptionResponse)
async def transcribe(
    file: UploadFile = File(...),
    model: str = Form("base"),
    language: Optional[str] = Form(None),
    cpu_compute_type: str = Form("int8"),
    cpu_threads: int = Form(0),
    num_workers: int = Form(1),
    background_tasks: BackgroundTasks = BackgroundTasks(),
):
    temp_audio_path = None
    try:
        temp_audio_path = TEMP_DIR / f"audio_{uuid4().hex}.wav"
        contents = await file.read()
        temp_audio_path.write_bytes(contents)
        whisper = load_whisper_model(model, cpu_compute_type=cpu_compute_type, cpu_threads=cpu_threads, num_workers=num_workers)
        segments_gen, info = whisper.transcribe(
            str(temp_audio_path), language=language or None, word_timestamps=True)
        segments = [
            TranscriptSegmentResponse(
                start=s.start, end=s.end, text=s.text.strip(),
                words=[WordTimestampResponse(text=w.word, start=w.start, end=w.end)
                       for w in (s.words or [])])
            for s in segments_gen if s.text.strip()
        ]
        background_tasks.add_task(lambda p=temp_audio_path: p.unlink(missing_ok=True))
        return TranscriptionResponse(
            success=True,
            language=info.language or "unknown",
            language_probability=info.language_probability or 0.0,
            segments=segments,
        )
    except Exception as exc:
        logger.error(f"Transcription failed: {exc}", exc_info=True)
        if temp_audio_path:
            background_tasks.add_task(lambda p=temp_audio_path: p.unlink(missing_ok=True))
        raise HTTPException(status_code=400, detail=str(exc)) from exc


@app.post("/transcribe/stream")
async def transcribe_stream(
    file: UploadFile = File(...),
    model: str = Form("base"),
    language: Optional[str] = Form(None),
    cpu_compute_type: str = Form("int8"),
    cpu_threads: int = Form(0),
    num_workers: int = Form(1),
):
    temp_audio_path = None
    try:
        temp_audio_path = TEMP_DIR / f"audio_stream_{uuid4().hex}.wav"
        contents = await file.read()
        temp_audio_path.write_bytes(contents)
        whisper = load_whisper_model(model, cpu_compute_type=cpu_compute_type, cpu_threads=cpu_threads, num_workers=num_workers)
        segments_gen, info = whisper.transcribe(
            str(temp_audio_path), language=language or None, word_timestamps=True)

        def _sample_ram_mb() -> float:
            try:
                import psutil
                return psutil.Process(os.getpid()).memory_info().rss / (1024 * 1024)
            except Exception:
                return -1.0

        def _sample_vram_mb() -> float:
            try:
                import pynvml
                pynvml.nvmlInit()
                try:
                    handle = pynvml.nvmlDeviceGetHandleByIndex(0)
                    info_obj = pynvml.nvmlDeviceGetMemoryInfo(handle)
                    return info_obj.used / (1024 * 1024)
                finally:
                    pynvml.nvmlShutdown()
            except Exception:
                return -1.0

        ram_before = _sample_ram_mb()
        vram_before = _sample_vram_mb()

        async def _generate():
            try:
                yield json.dumps({
                    "type": "metadata",
                    "language": info.language or "unknown",
                    "language_probability": info.language_probability or 0.0,
                }, ensure_ascii=False) + "\n"

                segment_count = 0
                for seg in segments_gen:
                    text = (seg.text or "").strip()
                    if not text:
                        continue

                    payload = {
                        "type": "segment",
                        "segment": {
                            "start": seg.start,
                            "end": seg.end,
                            "text": text,
                            "words": [
                                {
                                    "text": word.word,
                                    "start": word.start,
                                    "end": word.end,
                                }
                                for word in (seg.words or [])
                            ],
                        },
                    }
                    yield json.dumps(payload, ensure_ascii=False) + "\n"
                    segment_count += 1
                    await asyncio.sleep(0)

                ram_after = _sample_ram_mb()
                vram_after = _sample_vram_mb()
                yield json.dumps({
                    "type": "complete",
                    "segment_count": segment_count,
                    "peak_ram_mb": max(ram_before, ram_after),
                    "peak_vram_mb": max(vram_before, vram_after),
                }, ensure_ascii=False) + "\n"
            finally:
                if temp_audio_path:
                    temp_audio_path.unlink(missing_ok=True)

        return StreamingResponse(_generate(), media_type="application/x-ndjson")
    except Exception as exc:
        logger.error(f"Streaming transcription failed: {exc}", exc_info=True)
        if temp_audio_path:
            temp_audio_path.unlink(missing_ok=True)
        raise HTTPException(status_code=400, detail=str(exc)) from exc


# ============================================================================
# Translation
# ============================================================================

def normalize_nllb_model_id(model_name: str) -> str:
    normalized = model_name.strip()
    if not normalized:
        raise ValueError("Model name must not be empty")

    if "\\" in normalized or normalized.startswith("/") or normalized.endswith("/"):
        raise ValueError(f"Invalid model name: {model_name}")

    if "/" not in normalized:
        normalized = f"facebook/{normalized}"

    namespace, repo_name = normalized.split("/", 1)
    if not namespace or not repo_name or "/" in repo_name:
        raise ValueError(f"Invalid model name: {model_name}")

    return normalized


def load_nllb_model(model_name: str):
    global nllb_tokenizer, nllb_model, nllb_model_key
    normalized_model_name = normalize_nllb_model_id(model_name)
    if nllb_model is None or nllb_model_key != normalized_model_name:
        from transformers import AutoTokenizer, AutoModelForSeq2SeqLM
        logger.info(f"Loading NLLB '{normalized_model_name}' on {HOST_DEVICE}")
        nllb_tokenizer = AutoTokenizer.from_pretrained(normalized_model_name)
        model_kwargs: dict = {}
        if HOST_DEVICE == "cuda":
            cuda_dtype = torch.bfloat16 if torch.cuda.is_bf16_supported() else torch.float16
            model_kwargs["torch_dtype"] = cuda_dtype
            model_kwargs["device_map"] = "cuda"
        nllb_model = AutoModelForSeq2SeqLM.from_pretrained(normalized_model_name, **model_kwargs)
        nllb_model.eval()
        if HOST_DEVICE != "cuda":
            nllb_model = nllb_model.to(HOST_DEVICE)
        nllb_model_key = normalized_model_name
        logger.info("NLLB loaded")
    return nllb_tokenizer, nllb_model


@app.post("/translate", response_model=TranslationResponse)
async def translate(
    transcript_json: str = Form(...),
    source_language: str = Form(...),
    target_language: str = Form(...),
    model: str = Form("facebook/nllb-200-distilled-600M"),
):
    try:
        data = json.loads(transcript_json)
        segments = data.get("segments", [])
        if source_language not in FLORES:
            raise HTTPException(
                status_code=400,
                detail=f"Source language '{source_language}' is not a supported NLLB language code.",
            )
        if target_language not in FLORES:
            raise HTTPException(
                status_code=400,
                detail=f"Target language '{target_language}' is not a supported NLLB language code.",
            )
        src_flores = FLORES[source_language]
        tgt_flores = FLORES[target_language]
        tokenizer, nllb = load_nllb_model(model)
        translated: list[TranslatedSegmentResponse] = []
        for seg in segments:
            text = seg.get("text", "")
            t_text = ""
            if text:
                tokenizer.src_lang = src_flores
                inputs = tokenizer(text, return_tensors="pt").to(HOST_DEVICE)
                forced = tokenizer.convert_tokens_to_ids([tgt_flores])
                with torch.no_grad():
                    out = nllb.generate(**inputs, forced_bos_token_id=forced[0], max_length=512)
                t_text = tokenizer.batch_decode(out, skip_special_tokens=True)[0]
            translated.append(TranslatedSegmentResponse(
                start=seg.get("start", 0.0),
                end=seg.get("end", 0.0),
                text=text,
                translated_text=t_text,
                speaker_id=seg.get("speaker_id") or None,
            ))
        return TranslationResponse(
            success=True,
            source_language=source_language,
            target_language=target_language,
            segments=translated,
        )
    except Exception as exc:
        logger.error(f"Translation failed: {exc}", exc_info=True)
        raise HTTPException(status_code=400, detail=str(exc))


# ============================================================================
# Diarization
# ============================================================================


def _requires_wav_normalization(audio_path: Path) -> bool:
    if audio_path.suffix.lower() != ".wav":
        return True
    try:
        with wave.open(str(audio_path), "rb") as wav_reader:
            return not (
                wav_reader.getcomptype() == "NONE"
                and wav_reader.getnchannels() == 1
                and wav_reader.getframerate() == 16000
                and wav_reader.getsampwidth() == 2
            )
    except (wave.Error, EOFError, OSError):
        return True


def _ensure_wav_audio(audio_path: Path, *, force_mono_16k: bool = False) -> Path:
    """Convert media to PCM16 mono 16 kHz WAV for NeMo-compatible inference paths."""
    if not force_mono_16k and audio_path.suffix.lower() == ".wav":
        return audio_path
    if force_mono_16k and not _requires_wav_normalization(audio_path):
        return audio_path

    if audio_path.suffix.lower() == ".wav":
        wav_path = audio_path.with_name(f"{audio_path.stem}_mono16k.wav")
    else:
        wav_path = audio_path.with_suffix(".wav")

    proc = subprocess.run(
        [
            "ffmpeg", "-y",
            "-i", str(audio_path),
            "-vn",
            "-acodec", "pcm_s16le",
            "-ar", "16000",
            "-ac", "1",
            str(wav_path),
        ],
        capture_output=True,
        text=True,
    )
    if proc.returncode != 0:
        raise RuntimeError(f"ffmpeg audio conversion failed: {proc.stderr[-500:]}")
    if wav_path != audio_path:
        audio_path.unlink(missing_ok=True)
    return wav_path


def _measure_wav_signal_stats(audio_path: Path) -> dict[str, float | int]:
    with wave.open(str(audio_path), "rb") as wav_reader:
        sample_width = wav_reader.getsampwidth()
        frame_count = wav_reader.getnframes()
        channel_count = wav_reader.getnchannels()
        sample_rate = wav_reader.getframerate()
        raw = wav_reader.readframes(frame_count)

    if sample_width != 2 or frame_count <= 0:
        return {
            "sample_width_bytes": sample_width,
            "frame_count": frame_count,
            "channel_count": channel_count,
            "sample_rate_hz": sample_rate,
            "peak_abs": 0.0,
            "rms": 0.0,
            "non_silent_ratio": 0.0,
        }

    samples = np.frombuffer(raw, dtype=np.int16)
    if samples.size == 0:
        return {
            "sample_width_bytes": sample_width,
            "frame_count": frame_count,
            "channel_count": channel_count,
            "sample_rate_hz": sample_rate,
            "peak_abs": 0.0,
            "rms": 0.0,
            "non_silent_ratio": 0.0,
        }

    normalized = samples.astype(np.float32) / 32768.0
    peak_abs = float(np.max(np.abs(normalized)))
    rms = float(np.sqrt(np.mean(np.square(normalized))))
    non_silent_ratio = float(np.mean(np.abs(normalized) > 0.01))

    return {
        "sample_width_bytes": sample_width,
        "frame_count": frame_count,
        "channel_count": channel_count,
        "sample_rate_hz": sample_rate,
        "peak_abs": peak_abs,
        "rms": rms,
        "non_silent_ratio": non_silent_ratio,
    }


def _deferred_cleanup_temp(path: Path) -> None:
    """Retry-based temp file cleanup for Windows file-locking resilience."""
    for attempt in range(3):
        try:
            path.unlink(missing_ok=True)
            return
        except PermissionError:
            if attempt < 2:
                time.sleep(0.5)
    logger.warning("Could not delete temp file after retries: %s", path)


async def _deferred_cleanup_after_delay(path: Path, delay_seconds: int = 1800) -> None:
    """Remove a file after a delay, allowing the coordinator time to retrieve it."""
    await asyncio.sleep(delay_seconds)
    _deferred_cleanup_temp(path)


@app.post("/diarize", response_model=DiarizationResponse)
async def diarize(
    audio: UploadFile = File(...),
    min_speakers: Optional[int] = Form(None),
    max_speakers: Optional[int] = Form(None),
    background_tasks: BackgroundTasks = BackgroundTasks(),
):
    """
    Perform speaker diarization on uploaded audio and return a structured diarization response.

    Parameters:
        min_speakers (Optional[int]): Minimum number of speakers to detect; used as a hint when provided.
        max_speakers (Optional[int]): Maximum number of speakers to detect; used as a hint when provided.

    Returns:
        DiarizationResponse: Object containing the list of diarization segments and the detected speaker count.

    Raises:
        HTTPException: If diarization fails or the uploaded audio cannot be processed.
    """
    _validate_diarization_speaker_bounds(min_speakers, max_speakers)
    temp_audio_path: Optional[Path] = None
    try:
        temp_audio_path = _stage_audio_upload_to_temp(audio, "diar")
        temp_audio_path.write_bytes(await audio.read())
        temp_audio_path = _ensure_wav_audio(temp_audio_path)
        segments, speaker_count = await asyncio.to_thread(
            _run_nemo_diarization,
            temp_audio_path,
            min_speakers,
            max_speakers,
        )
        response = _build_diarization_response(segments, speaker_count)
        background_tasks.add_task(_deferred_cleanup_temp, temp_audio_path)
        return response

    except HTTPException:
        raise
    except Exception as exc:
        logger.error(f"Diarization failed: {exc}", exc_info=True)
        if temp_audio_path:
            background_tasks.add_task(_deferred_cleanup_temp, temp_audio_path)
        raise HTTPException(status_code=400, detail=str(exc))


@app.post("/diarize/wespeaker", response_model=DiarizationResponse)
async def diarize_wespeaker(
    audio: UploadFile = File(...),
    min_speakers: Optional[int] = Form(None),
    max_speakers: Optional[int] = Form(None),
):
    """
    Handle the legacy WeSpeaker diarization endpoint by rejecting requests with a clear permanent-deprecation response.
    
    Raises:
        HTTPException: Always raised with status_code=410 and detail
        "WeSpeaker moved to the managed CPU runtime and is no longer served by the managed GPU host."
    """
    _ = audio
    _ = min_speakers
    _ = max_speakers
    raise HTTPException(
        status_code=410,
        detail="WeSpeaker moved to the managed CPU runtime and is no longer served by the managed GPU host.",
    )


# ============================================================================
# Vocal separation
# ============================================================================

@app.post("/separate/vocals", response_model=VocalSeparationResponse)
async def separate_vocals(
    background_tasks: BackgroundTasks,
    file: UploadFile = File(...),
    vocals_model: str = Form("UVR-MDX-NET-Voc_FT.onnx"),
    instrumental_model: Optional[str] = Form(None),
):
    """
    Split uploaded audio or video into a vocals stem and an instrumental stem.

    vocals_filename    → download via /tts/audio/{filename}, feed to /transcribe
    instrumental_filename → download via /tts/audio/{filename}, mix under TTS dub in final assembly
    """
    temp_src: Optional[Path] = None
    try:
        safe_name = Path(file.filename or "upload").name or "upload"
        temp_src = TEMP_DIR / f"sep_src_{uuid4().hex}_{safe_name}"
        temp_src.write_bytes(await file.read())

        from workers.vocal_separator_worker import run_vocal_separation

        inst = instrumental_model.strip() if instrumental_model else None
        effective_instrumental_model = inst or vocals_model

        vocals_path, instrumental_path = await asyncio.to_thread(
            run_vocal_separation,
            temp_src,
            TEMP_DIR,
            vocals_model,
            inst,
            use_gpu=(HOST_DEVICE == "cuda"),
        )

        background_tasks.add_task(_deferred_cleanup_temp, temp_src)
        # Guard against never-downloaded stems; /tts/audio handler auto-cleans on download
        background_tasks.add_task(_deferred_cleanup_after_delay, vocals_path, 1800)
        background_tasks.add_task(_deferred_cleanup_after_delay, instrumental_path, 1800)

        return VocalSeparationResponse(
            success=True,
            vocals_filename=vocals_path.name,
            instrumental_filename=instrumental_path.name,
            vocals_file_size_bytes=vocals_path.stat().st_size,
            instrumental_file_size_bytes=instrumental_path.stat().st_size,
            vocals_model=vocals_model,
            instrumental_model=effective_instrumental_model,
        )
    except HTTPException:
        raise
    except Exception as exc:
        logger.error("Vocal separation failed: %s", exc, exc_info=True)
        if temp_src and temp_src.exists():
            background_tasks.add_task(_deferred_cleanup_temp, temp_src)
        raise HTTPException(status_code=400, detail=str(exc))


# ============================================================================
# Per-speaker reference clip extraction
# ============================================================================

def _resolve_reference_path_in_temp_dir(path_value: str) -> Optional[Path]:
    """Resolve a reference file path and ensure it stays within TEMP_DIR."""
    try:
        candidate = Path(path_value).resolve(strict=False)
        temp_dir = Path(TEMP_DIR).resolve(strict=False)
        candidate.relative_to(temp_dir)
        return candidate
    except Exception:
        return None




def _evict_qwen_reference(ref_id: str) -> bool:
    """Remove a Qwen reference from the registry and delete its backing file."""
    entry = qwen_reference_registry.pop(ref_id, None)
    if entry is None:
        return False
    old_path = entry.get("path")
    if old_path:
        safe_path = _resolve_reference_path_in_temp_dir(old_path)
        if safe_path is None:
            logger.warning(
                f"Skipping deletion for Qwen reference file outside TEMP_DIR: {old_path}"
            )
        else:
            try:
                if safe_path.is_file() or not safe_path.exists():
                    safe_path.unlink(missing_ok=True)
                else:
                    logger.warning(f"Skipping deletion for non-file Qwen reference path: {safe_path}")
            except Exception as exc:
                logger.warning(f"Could not delete Qwen reference file {safe_path}: {exc}")
    return True


@app.delete("/speakers/references/{ref_id}")
async def delete_speaker_reference(ref_id: str):
    """Explicitly release a speaker reference clip and free its temp file.

    The C# coordinator should call this when a dubbing session ends so that
    extracted reference WAVs do not accumulate in TEMP_DIR.
    """
    if not _evict_qwen_reference(ref_id):
        raise HTTPException(status_code=404, detail=f"reference_id '{ref_id}' not found")
    return {"success": True, "deleted": ref_id}


@app.post("/speakers/extract-reference", response_model=SpeakerReferenceResponse)
async def extract_speaker_references(
    file: UploadFile = File(...),
    speakers_json: str = Form(...),
    target_duration_seconds: float = Form(10.0),
    session_id: Optional[str] = Form(None),
    background_tasks: BackgroundTasks = BackgroundTasks(),
):
    """Extract a reference audio clip for each speaker from the source audio.

    For each speaker, this endpoint:
      1. Validates the diarization segment list via Pydantic.
      2. Finds the single longest segment for that speaker.
      3. Trims it to `target_duration_seconds` if it is longer.
      4. Extracts the clip via ffmpeg (frame-accurate seek).
      5. Registers it in the Qwen reference registry so subsequent TTS calls
         can pass `reference_id` instead of re-uploading the file.
      6. Evicts any pre-existing registry entry for the same speaker_id AND
         session_id so temp files do not accumulate across repeated calls from
         the same session, without invalidating references from other sessions
         that happen to share the same diarization label (e.g. SPEAKER_00).

    Parameters
    ----------
    file                    : full source audio (same file used for diarization)
    speakers_json           : JSON object mapping speaker_id -> list of
                              {start, end} diarization segments for that speaker.
                              e.g. {"SPEAKER_00": [{"start": 1.2, "end": 8.7}]}
    target_duration_seconds : max clip length to extract (default 10 s)
    session_id              : caller-supplied session/job identifier. Pass the
                              same value on repeated calls within the same dubbing
                              job so that prior refs for the same speaker are
                              evicted and temp files do not accumulate. When
                              omitted, a new UUID is generated (no eviction will
                              occur for prior calls that also omitted it).
    """
    if shutil.which("ffmpeg") is None:
        raise HTTPException(status_code=500, detail="ffmpeg not found on PATH — required for reference extraction")

    # Use caller-supplied session_id when provided; fall back to a fresh UUID
    # (backwards-compatible: old callers that don't send session_id never evict
    # each other, but also never accumulate duplicates within a single call).
    effective_session_id = (session_id or "").strip() or uuid4().hex

    temp_source_path: Optional[Path] = None
    try:
        safe_name = Path(file.filename or "").name or "source_audio"
        temp_source_path = TEMP_DIR / f"src_{uuid4().hex}_{safe_name}"
        temp_source_path.write_bytes(await file.read())

        # --- Parse + validate speakers_json via Pydantic ---
        try:
            raw = json.loads(speakers_json)
        except json.JSONDecodeError as exc:
            raise HTTPException(status_code=400, detail=f"Invalid speakers_json: {exc}") from exc

        if not isinstance(raw, dict) or not raw:
            raise HTTPException(status_code=400, detail="speakers_json must be a non-empty JSON object")

        # Validate each speaker's segment list through SpeakerSegmentInput
        try:
            validated_speakers: dict[str, list[SpeakerSegmentInput]] = {
                spk: [SpeakerSegmentInput(**seg) for seg in segs]
                for spk, segs in raw.items()
            }
        except Exception as exc:
            raise HTTPException(
                status_code=400,
                detail=f"speakers_json has invalid segment format: {exc}",
            ) from exc

        results: list[SpeakerReferenceResult] = []

        for speaker_id, segments in validated_speakers.items():
            if not segments:
                logger.warning(f"No segments for speaker {speaker_id}, skipping")
                continue

            # Find the longest segment for this speaker
            best_seg = max(segments, key=lambda s: s.end - s.start)
            seg_start = best_seg.start
            seg_end = best_seg.end
            seg_duration = seg_end - seg_start

            extract_duration = min(seg_duration, target_duration_seconds)
            if extract_duration < 1.0:
                logger.warning(
                    f"Longest segment for {speaker_id} is only {extract_duration:.2f}s — "
                    "voice clone quality may be poor"
                )

            out_path = TEMP_DIR / f"ref_{speaker_id}_{uuid4().hex}.wav"

            # Frame-accurate seek: -i before -ss ensures we don't land on a
            # keyframe boundary and clip the wrong speaker's audio.
            cmd = [
                "ffmpeg", "-y",
                "-i", str(temp_source_path),
                "-ss", str(seg_start),
                "-t", str(extract_duration),
                "-ar", "22050",
                "-ac", "1",
                "-f", "wav",
                str(out_path),
            ]
            proc = await asyncio.to_thread(
                subprocess.run,
                cmd,
                capture_output=True,
                text=True,
            )
            if proc.returncode != 0:
                logger.error(f"ffmpeg failed for {speaker_id}: {proc.stderr}")
                raise HTTPException(
                    status_code=500,
                    detail=f"ffmpeg extraction failed for speaker {speaker_id}: {proc.stderr[-200:]}",
                )

            ref_id = f"{speaker_id}_{uuid4().hex}"
            qwen_reference_registry[ref_id] = {
                "speaker_id": speaker_id,
                "session_id": effective_session_id,
                "path": str(out_path),
                "transcript": None,
            }

            results.append(SpeakerReferenceResult(
                speaker_id=speaker_id,
                reference_id=ref_id,
                duration_seconds=round(extract_duration, 3),
            ))
            logger.info(
                f"Extracted {extract_duration:.2f}s reference for {speaker_id} "
                f"-> ref_id={ref_id} session={effective_session_id}"
            )

        background_tasks.add_task(lambda p=temp_source_path: p.unlink(missing_ok=True))
        return SpeakerReferenceResponse(success=True, speakers=results)

    except HTTPException:
        raise
    except Exception as exc:
        logger.error(f"Reference extraction failed: {exc}", exc_info=True)
        if temp_source_path:
            background_tasks.add_task(lambda p=temp_source_path: p.unlink(missing_ok=True))
        raise HTTPException(status_code=400, detail=str(exc))







@app.post("/tts/qwen/references", response_model=QwenReferenceResponse)
async def register_qwen_reference(
    speaker_id: str = Form(...),
    file: UploadFile = File(...),
    transcript: Optional[str] = Form(None),
    background_tasks: BackgroundTasks = BackgroundTasks(),
):
    temp_path = None
    try:
        safe_filename = Path(file.filename or "").name or "ref_audio"
        temp_path = TEMP_DIR / f"qwenref_{uuid4().hex}_{safe_filename}"
        temp_path.write_bytes(await file.read())
        ref_id = f"{speaker_id}_{uuid4().hex}"
        qwen_reference_registry[ref_id] = {
            "speaker_id": speaker_id,
            "session_id": None,
            "path": str(temp_path),
            "transcript": transcript,
        }
        return QwenReferenceResponse(success=True, reference_id=ref_id)
    except Exception as exc:
        if temp_path:
            background_tasks.add_task(lambda p=temp_path: p.unlink(missing_ok=True))
        raise HTTPException(status_code=400, detail=str(exc))




# ============================================================================
# Qwen3-TTS
# ============================================================================

def load_qwen_model(model_name: str = "Qwen/Qwen3-TTS-12Hz-1.7B-Base"):
    """
    Ensure the specified Qwen3-TTS model is loaded and cached globally.
    
    Loads the Qwen3-TTS model identified by `model_name` if it is not already loaded or if a different model is currently cached, and updates the global cache keys used by the module.
    
    Parameters:
        model_name (str): Hugging Face repository ID or model identifier for Qwen3-TTS. Defaults to "Qwen/Qwen3-TTS-12Hz-1.7B-Base".
    
    Returns:
        The loaded Qwen3-TTS model instance.
    """
    global qwen_model, qwen_model_key
    if qwen_model is None or qwen_model_key != model_name:
        from qwen_tts import Qwen3TTSModel
        logger.info(f"Loading Qwen3-TTS '{model_name}' on {HOST_DEVICE}")
        if HOST_DEVICE == "cuda" and not _probe_flash_attn_available():
            logger.info("flash-attn is not installed; Qwen3-TTS will use sdpa attention")
        qwen_model = Qwen3TTSModel.from_pretrained(
            model_name,
            device_map={"": "cuda:0"} if HOST_DEVICE == "cuda" else HOST_DEVICE,
            dtype=torch.bfloat16 if HOST_DEVICE == "cuda" else torch.float32,
            attn_implementation="sdpa"
        )
        qwen_model_key = model_name
        logger.info("Qwen3-TTS loaded")
    return qwen_model


async def _ensure_qwen_model_ready(model_name: str) -> object:
    """
    Ensure the specified Qwen3-TTS model is loaded and update the provider health state accordingly.
    
    Parameters:
        model_name (str): Identifier of the Qwen3-TTS model to ensure is loaded.
    
    Returns:
        object: The loaded Qwen3-TTS model instance.
    
    Raises:
        RuntimeError: If the Qwen model load lock was not initialized.
        OSError: If the underlying model load fails due to low-level I/O or memory errors.
        Exception: For other failures encountered while loading the model.
    """
    await _ensure_provider_health_primitives()
    entry = _ensure_provider_health_defaults("qwen")

    if qwen_model is not None and qwen_model_key == model_name and bool(entry["ready"]):
        _record_provider_health(
            "qwen",
            True,
            "ready",
            f"Qwen3-TTS model loaded on {HOST_DEVICE}",
            metrics={"max_concurrency": _qwen_max_concurrency},
        )
        return qwen_model

    _mark_provider_health_warming("qwen", "Qwen3-TTS warming up", failure_category="warmup")
    lock = _qwen_model_load_lock
    if lock is None:
        raise RuntimeError("Qwen3-TTS model load lock was not initialized")

    async with lock:
        if qwen_model is not None and qwen_model_key == model_name:
            _record_provider_health(
                "qwen",
                True,
                "ready",
                f"Qwen3-TTS model loaded on {HOST_DEVICE}",
                metrics={"max_concurrency": _qwen_max_concurrency},
            )
            return qwen_model

        try:
            warmup_start = perf_counter()
            model = await asyncio.to_thread(load_qwen_model, model_name)
            warmup_ms = (perf_counter() - warmup_start) * 1000.0
        except OSError as exc:
            reason = str(exc)
            if "Errno 22" in reason or "Invalid argument" in reason:
                reason = (
                    f"Windows memory-mapping failure ({reason}). "
                    "Try increasing your Windows page file size or use the smaller 0.6B model."
                )
            elif "paging file" in reason.lower():
                reason = (
                    f"Insufficient virtual memory ({reason}). "
                    "Increase your Windows page file size or use the smaller 0.6B model."
                )
            _record_provider_health(
                "qwen",
                False,
                "failed",
                f"failed: {reason}",
                failure_category="warmup",
            )
            logger.warning(f"Qwen3-TTS warmup failed: {reason}", exc_info=True)
            raise
        except Exception as exc:
            _record_provider_health(
                "qwen",
                False,
                "failed",
                f"failed: {exc}",
                failure_category="warmup",
            )
            logger.warning(f"Qwen3-TTS warmup failed: {exc}", exc_info=True)
            raise

        metrics = {
            "max_concurrency": _qwen_max_concurrency,
            "last_warmup_ms": round(warmup_ms, 2),
            "flash_attn_available": _probe_flash_attn_available() if HOST_DEVICE == "cuda" else None,
        }
        _update_provider_health_metrics("qwen", **metrics)
        detail = f"Qwen3-TTS model loaded on {HOST_DEVICE}"
        if HOST_DEVICE == "cuda" and not _probe_flash_attn_available():
            detail += " (flash-attn unavailable; using sdpa)"
        _record_provider_health("qwen", True, "ready", detail, metrics=metrics)
        logger.info(
            "Qwen3-TTS warmup completed in %.1f ms on %s (max_concurrency=%s, flash_attn=%s)",
            warmup_ms,
            HOST_DEVICE,
            _qwen_max_concurrency,
            _probe_flash_attn_available() if HOST_DEVICE == "cuda" else None,
        )
        return model


@app.get("/tts/qwen/warmup")
async def qwen_warmup(model: str = "Qwen/Qwen3-TTS-12Hz-1.7B-Base"):
    """
    Ensure the specified Qwen TTS model is loaded into memory/VRAM and marked ready.
    
    Parameters:
        model (str): HuggingFace repository identifier of the Qwen TTS model to warm up (default "Qwen/Qwen3-TTS-12Hz-1.7B-Base").
    
    Returns:
        dict: `{"success": True, "model": <model>}` when the model is ready.
    
    Raises:
        HTTPException: If warmup fails (status 500 with the underlying error message).
    """
    try:
        await _ensure_qwen_model_ready(model)
        return {"success": True, "model": model}
    except Exception as exc:
        logger.error(f"Qwen warmup failed: {exc}", exc_info=True)
        raise HTTPException(status_code=500, detail=str(exc))


@app.post("/tts/qwen/segment", response_model=TtsResponse)
async def qwen_segment(
    text: str = Form(...),
    model: str = Form("Qwen/Qwen3-TTS-12Hz-1.7B-Base"),
    language: Optional[str] = Form(None),
    reference_text: Optional[str] = Form(None),
    reference_id: Optional[str] = Form(None),
    reference_file: Optional[UploadFile] = File(None),
    background_tasks: BackgroundTasks = BackgroundTasks(),
):
    """
    Synthesize speech for the given text using a Qwen3-TTS model and a reference audio clip.
    
    Parameters:
    	text (str): The text to synthesize; must not be empty or whitespace.
    	model (str): Qwen3-TTS model identifier to use (e.g., "Qwen/Qwen3-TTS-12Hz-1.7B-Base").
    	language (Optional[str]): Language hint; defaults to "english" when omitted.
    	reference_text (Optional[str]): Unused by the endpoint (accepted but not processed).
    	reference_id (Optional[str]): ID of a previously registered reference audio in the Qwen reference registry.
    	reference_file (Optional[UploadFile]): Uploaded reference audio file to use for voice cloning; takes precedence over reference_id.
    	background_tasks (BackgroundTasks): FastAPI background tasks manager used to schedule temporary-file cleanup.
    
    Returns:
    	TtsResponse: On success contains fields `voice` (model used), `audio_path` (temporary WAV file path), and `file_size_bytes`.
    
    Raises:
    	HTTPException: 400 for validation or synthesis errors; 500 if internal server resources (semaphore) are uninitialized.
    """
    await _ensure_provider_health_primitives()
    semaphore = _qwen_segment_semaphore
    if semaphore is None:
        raise HTTPException(status_code=500, detail="Qwen3-TTS execution semaphore was not initialized")

    global _qwen_segment_waiters
    temp_ref_path: Optional[Path] = None
    out_path = TEMP_DIR / f"qwen_{uuid4().hex}.wav"
    qwen_request_started = perf_counter()
    _qwen_segment_waiters += 1
    queue_wait_ms: Optional[float] = None
    reference_prep_ms: Optional[float] = None
    generation_ms: Optional[float] = None
    qwen_queue_depth = _qwen_segment_waiters
    waiter_counted = True

    try:
        async with semaphore:
            queue_wait_ms = (perf_counter() - qwen_request_started) * 1000.0
            _qwen_segment_waiters = max(0, _qwen_segment_waiters - 1)
            waiter_counted = False
            qwen_queue_depth = _qwen_segment_waiters
            _update_provider_health_metrics(
                "qwen",
                max_concurrency=_qwen_max_concurrency,
                queue_depth=qwen_queue_depth,
                last_queue_wait_ms=round(queue_wait_ms, 2),
            )
            if not text.strip():
                raise HTTPException(status_code=400, detail="text cannot be empty")

            tts = await _ensure_qwen_model_ready(model)

            reference_prep_started = perf_counter()
            ref_audio_path: Optional[str] = None
            if reference_file is not None:
                safe_reference_name = os.path.basename(reference_file.filename or "")
                if not safe_reference_name:
                    safe_reference_name = "reference_audio"
                temp_ref_path = TEMP_DIR / f"qwenref_{uuid4().hex}_{safe_reference_name}"
                temp_ref_path.write_bytes(await reference_file.read())
                ref_audio_path = str(temp_ref_path)
            elif reference_id and reference_id in qwen_reference_registry:
                ref_audio_path = qwen_reference_registry[reference_id]["path"]

            lang = (language or "english").strip().lower()

            if ref_audio_path is None:
                raise HTTPException(
                    status_code=400,
                    detail="Qwen3-TTS requires reference audio: provide reference_file or a valid reference_id")
            reference_prep_ms = (perf_counter() - reference_prep_started) * 1000.0

            generation_started = perf_counter()

            def _synth_and_write() -> None:
                """
                Generate a voice-cloned waveform from the surrounding context and write the first output to out_path as a 16-bit PCM WAV.
                
                Uses the available `tts` object with the current `text`, `lang`, and `ref_audio_path` to produce one or more waveforms, then writes the first waveform to the file path referenced by `out_path` with subtype "PCM_16".
                """
                import soundfile as sf
                wavs, sample_rate = tts.generate_voice_clone(
                    text=text,
                    language=lang,
                    ref_audio=ref_audio_path,
                    x_vector_only_mode=True,
                    non_streaming_mode=True,
                )
                sf.write(str(out_path), wavs[0], sample_rate, subtype="PCM_16")

            await asyncio.to_thread(_synth_and_write)
            generation_ms = (perf_counter() - generation_started) * 1000.0

            if temp_ref_path:
                background_tasks.add_task(lambda p=temp_ref_path: p.unlink(missing_ok=True))

            total_ms = (perf_counter() - qwen_request_started) * 1000.0
            _update_provider_health_metrics(
                "qwen",
                max_concurrency=_qwen_max_concurrency,
                queue_depth=qwen_queue_depth,
                last_queue_wait_ms=round(queue_wait_ms or 0.0, 2),
                last_reference_prep_ms=round(reference_prep_ms or 0.0, 2),
                last_generation_ms=round(generation_ms or 0.0, 2),
                last_segment_ms=round(total_ms, 2),
            )
            logger.info(
                "Qwen3-TTS segment timing: queue_wait_ms=%.1f reference_prep_ms=%.1f generation_ms=%.1f total_ms=%.1f queue_depth=%s max_concurrency=%s",
                queue_wait_ms or 0.0,
                reference_prep_ms or 0.0,
                generation_ms or 0.0,
                total_ms,
                qwen_queue_depth,
                _qwen_max_concurrency,
            )
            logger.info(f"Qwen3-TTS segment written: {out_path} ({out_path.stat().st_size} bytes)")
            return TtsResponse(
                success=True,
                voice=model,
                audio_path=str(out_path),
                file_size_bytes=out_path.stat().st_size,
            )
    except HTTPException:
        raise
    except Exception as exc:
        logger.error(f"Qwen3-TTS segment failed: {exc}", exc_info=True)
        if temp_ref_path:
            background_tasks.add_task(lambda p=temp_ref_path: p.unlink(missing_ok=True))
        if out_path.exists():
            background_tasks.add_task(lambda p=out_path: p.unlink(missing_ok=True))
        raise HTTPException(status_code=400, detail=str(exc))
    finally:
        if waiter_counted and _qwen_segment_waiters > 0:
            _qwen_segment_waiters -= 1


@app.post("/tts/qwen/batch", response_model=QwenBatchResponse)
async def qwen_batch(
    request: QwenBatchRequest,
    background_tasks: BackgroundTasks = BackgroundTasks(),
):
    await _ensure_provider_health_primitives()
    semaphore = _qwen_segment_semaphore
    if semaphore is None:
        raise HTTPException(status_code=500, detail="Qwen3-TTS execution semaphore was not initialized")

    if not request.segments:
        raise HTTPException(status_code=400, detail="segments cannot be empty")

    global _qwen_segment_waiters
    qwen_request_started = perf_counter()
    _qwen_segment_waiters += 1
    queue_wait_ms: Optional[float] = None
    generation_ms_total = 0.0
    qwen_queue_depth = _qwen_segment_waiters
    waiter_counted = True
    response_segments: list[QwenBatchSegmentResponse] = []

    try:
        async with semaphore:
            queue_wait_ms = (perf_counter() - qwen_request_started) * 1000.0
            _qwen_segment_waiters = max(0, _qwen_segment_waiters - 1)
            waiter_counted = False
            qwen_queue_depth = _qwen_segment_waiters
            _update_provider_health_metrics(
                "qwen",
                max_concurrency=_qwen_max_concurrency,
                queue_depth=qwen_queue_depth,
                last_queue_wait_ms=round(queue_wait_ms, 2),
            )

            tts = await _ensure_qwen_model_ready(request.model)
            for segment in request.segments:
                if not segment.text.strip():
                    raise HTTPException(status_code=400, detail=f"segment '{segment.segment_id}' text cannot be empty")

                ref_entry = qwen_reference_registry.get(segment.reference_id)
                if ref_entry is None:
                    raise HTTPException(
                        status_code=400,
                        detail=f"segment '{segment.segment_id}' has invalid reference_id '{segment.reference_id}'")

                out_path = TEMP_DIR / f"qwen_{uuid4().hex}.wav"
                lang = (segment.language or "english").strip().lower()
                generation_started = perf_counter()

                def _synth_and_write_batch() -> None:
                    import soundfile as sf
                    wavs, sample_rate = tts.generate_voice_clone(
                        text=segment.text,
                        language=lang,
                        ref_audio=ref_entry["path"],
                        x_vector_only_mode=True,
                        non_streaming_mode=True,
                    )
                    sf.write(str(out_path), wavs[0], sample_rate, subtype="PCM_16")

                await asyncio.to_thread(_synth_and_write_batch)
                generation_ms_total += (perf_counter() - generation_started) * 1000.0
                response_segments.append(
                    QwenBatchSegmentResponse(
                        segment_id=segment.segment_id,
                        voice=request.model,
                        audio_path=str(out_path),
                        file_size_bytes=out_path.stat().st_size,
                    )
                )

            total_ms = (perf_counter() - qwen_request_started) * 1000.0
            _update_provider_health_metrics(
                "qwen",
                max_concurrency=_qwen_max_concurrency,
                queue_depth=qwen_queue_depth,
                last_queue_wait_ms=round(queue_wait_ms or 0.0, 2),
                last_generation_ms=round(generation_ms_total, 2),
                last_segment_ms=round(total_ms, 2),
            )
            logger.info(
                "Qwen3-TTS batch timing: queue_wait_ms=%.1f generation_ms=%.1f total_ms=%.1f segments=%s queue_depth=%s max_concurrency=%s",
                queue_wait_ms or 0.0,
                generation_ms_total,
                total_ms,
                len(response_segments),
                qwen_queue_depth,
                _qwen_max_concurrency,
            )
            return QwenBatchResponse(
                success=True,
                voice=request.model,
                segments=response_segments,
            )
    except HTTPException:
        raise
    except Exception as exc:
        logger.error(f"Qwen3-TTS batch failed: {exc}", exc_info=True)
        for segment in response_segments:
            background_tasks.add_task(lambda p=Path(segment.audio_path): p.unlink(missing_ok=True))
        raise HTTPException(status_code=400, detail=str(exc))
    finally:
        if waiter_counted and _qwen_segment_waiters > 0:
            _qwen_segment_waiters -= 1


# ============================================================================
# Generic TTS audio download
# ============================================================================

@app.get("/tts/audio/{filename}")
async def get_tts_audio(filename: str, background_tasks: BackgroundTasks):
    safe_filename = os.path.basename(filename)
    if not safe_filename or safe_filename in (".", ".."):
        raise HTTPException(status_code=400, detail="Invalid audio filename")

    temp_dir_resolved = TEMP_DIR.resolve()
    file_path = (TEMP_DIR / safe_filename).resolve()

    try:
        file_path.relative_to(temp_dir_resolved)
    except ValueError as exc:
        raise HTTPException(status_code=400, detail="Invalid audio filename") from exc

    if not file_path.exists():
        raise HTTPException(status_code=404, detail="Audio file not found")

    media_type = "audio/wav" if safe_filename.endswith(".wav") else "audio/mpeg"
    background_tasks.add_task(lambda p=file_path: p.unlink(missing_ok=True))
    return FileResponse(file_path, media_type=media_type)


# ============================================================================
# Model warmup (background tasks launched at startup)
# ============================================================================



# ============================================================================
# Startup / shutdown
# ============================================================================

@asynccontextmanager
async def lifespan(_: FastAPI):
    logger.info("Babel Player inference service starting")
    logger.info(f"CUDA available: {torch.cuda.is_available()}")
    if torch.cuda.is_available():
        logger.info(f"CUDA device: {torch.cuda.get_device_name(0)}")
        logger.info(f"CUDA version: {torch.version.cuda}")
    await _ensure_provider_health_primitives()
    _schedule_provider_health_refresh(force=True)
    try:
        yield
    finally:
        logger.info("Babel Player inference service shutting down")
        if torch.cuda.is_available():
            torch.cuda.empty_cache()


app.router.lifespan_context = lifespan


if __name__ == "__main__":
    import uvicorn
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=8000)
    parser.add_argument("--compute-type", default=None,
                        help="Override compute type (float16, int8, etc.)")
    parser.add_argument("--require-cuda", action="store_true",
                        help="Exit non-zero if CUDA is not available")
    args = parser.parse_args()
    if args.require_cuda and not torch.cuda.is_available():
        logger.error("--require-cuda specified but CUDA is not available")
        sys.exit(1)
    if args.compute_type:
        HOST_COMPUTE_TYPE = args.compute_type
        EFFECTIVE_HOST_COMPUTE_TYPE = args.compute_type
        logger.info(f"Compute type override from CLI: {args.compute_type}")
    uvicorn.run(app, host=args.host, port=args.port)

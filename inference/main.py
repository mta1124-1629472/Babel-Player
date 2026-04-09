# pyright: reportMissingImports=false
# pylint: disable=missing-module-docstring,missing-class-docstring,missing-function-docstring,invalid-name,global-statement,line-too-long,broad-exception-caught

import argparse
import asyncio
import importlib.util
import json
import logging
import os
import shutil
import subprocess
import sys
import tempfile
import threading
from importlib import import_module
from pathlib import Path
from datetime import datetime
from typing import Optional
from uuid import uuid4

import torch
from fastapi import FastAPI, File, Form, UploadFile, HTTPException, BackgroundTasks
from fastapi.responses import FileResponse
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
wespeaker_model = None
wespeaker_model_key: str | None = None
_wespeaker_model_lock = threading.Lock()

# Warmup state: None = not started, "warming" = in progress,
# "ready" = model loaded, "failed: <reason>" = terminal failure
_qwen_warmup_status: str | None = None
HOST_DEVICE = "cuda" if torch.cuda.is_available() else "cpu"
HOST_COMPUTE_TYPE = "float16" if HOST_DEVICE == "cuda" else "int8"
# Tracks effective compute type after per-stage validation and potential downgrades
EFFECTIVE_HOST_COMPUTE_TYPE = HOST_COMPUTE_TYPE
# Tracks downgrade reasons per stage for UI/logging projection
COMPUTE_DOWNGRADE_REASONS: dict[str, str] = {}

# Temporary directory for artifacts
TEMP_DIR = Path(tempfile.gettempdir()) / "babel_inference"
TEMP_DIR.mkdir(exist_ok=True)
NEMO_DIARIZATION_DEFAULT_PROVIDER = "nemo"
NEMO_VAD_MODEL = "vad_multilingual_marblenet"
NEMO_SPEAKER_EMBEDDING_MODEL = "titanet_large"
WESPEAKER_MODEL = "english"

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

class HealthLiveResponse(BaseModel):
    status: str
    timestamp: str
    cuda_available: bool
    cuda_version: Optional[str] = None


class StageCapability(BaseModel):
    ready: bool
    detail: Optional[str] = None
    providers: Optional[dict[str, bool]] = None
    provider_details: Optional[dict[str, str]] = None
    default_provider: Optional[str] = None
    engines: Optional[list[str]] = None


class CapabilitiesResponse(BaseModel):
    transcription: StageCapability
    translation: StageCapability
    tts: StageCapability
    diarization: StageCapability


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


class QwenReferenceResponse(BaseModel):
    success: bool
    reference_id: Optional[str] = None
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




def _probe_qwen_available() -> tuple[bool, str]:
    """
    Report Qwen3-TTS warmup readiness and a human-readable status message.
    
    Returns:
        A tuple `(ready, detail)` where `ready` is `True` if the Qwen3-TTS model is loaded and ready, `False` otherwise, and `detail` is a short human-readable status message describing the warmup state or device.
    """
    status = _qwen_warmup_status
    if status is None or status == "warming":
        return False, "Qwen3-TTS warming up"
    if status == "ready":
        return True, f"Qwen3-TTS model loaded on {HOST_DEVICE}"
    return False, f"Qwen3-TTS warmup {status}"


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


def _probe_nemo_diarization_available() -> tuple[bool, str]:
    """
    Check whether the Python packages required for NeMo-based diarization are importable.
    
    Returns:
        tuple: First element is `True` if required NeMo packages are present, `False` otherwise; second element is a human-readable detail message describing availability or listing missing packages.
    """
    missing: list[str] = []
    if _find_module("nemo.collections.asr") is None:
        missing.append("nemo.collections.asr")
    if _find_module("omegaconf") is None:
        missing.append("omegaconf")
    if missing:
        return False, "Missing diarization dependency: " + ", ".join(missing)
    return True, "NeMo ClusteringDiarizer available"


def _probe_wespeaker_available() -> tuple[bool, str]:
    """
    Check whether the WeSpeaker diarization backend is importable and available for use.
    
    Returns:
        tuple[bool, str]: `True` and an availability message if WeSpeaker can be imported; `False` and an error message otherwise.
    """
    if _find_module("wespeaker") is None:
        return False, "Missing diarization dependency: wespeaker"

    try:
        import wespeaker  # noqa: F401
    except Exception as exc:
        return False, f"WeSpeaker import failed: {exc}"

    return True, "WeSpeaker available (CPU only; min/max speaker hints ignored)"


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


def _run_nemo_diarization(
    audio_path: Path,
    min_speakers: Optional[int],
    max_speakers: Optional[int],
) -> tuple[list[DiarizationSegment], int]:
    """
    Run NeMo's ClusteringDiarizer on a staged audio file and return parsed diarization results.
    
    Parameters:
        audio_path (Path): Path to the input audio file to diarize.
        min_speakers (Optional[int]): Minimum number of speakers hint; if equal to `max_speakers` the diarizer is instructed to use that exact speaker count.
        max_speakers (Optional[int]): Maximum number of speakers hint; used to limit clustering if provided.
    
    Returns:
        tuple[list[DiarizationSegment], int]: A tuple where the first element is a list of diarization segments with normalized speaker IDs and rounded start/end times, and the second element is the number of distinct speakers detected.
    
    Raises:
        RuntimeError: If NeMo produces no RTTM output file.
    """
    import nemo.collections.asr as nemo_asr
    from omegaconf import OmegaConf

    with tempfile.TemporaryDirectory(dir=TEMP_DIR, prefix="nemo_diar_") as work_dir_name:
        work_dir = Path(work_dir_name)
        out_dir = work_dir / "out"
        manifest_path = work_dir / "manifest.json"

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

        clustering_parameters: dict[str, object] = {
            "oracle_num_speakers": False,
            "max_num_speakers": max_speakers or 8,
        }
        if min_speakers is not None and max_speakers is not None and min_speakers == max_speakers:
            clustering_parameters["oracle_num_speakers"] = True

        config = OmegaConf.create(
            {
                "num_workers": 0,
                "diarizer": {
                    "manifest_filepath": str(manifest_path),
                    "out_dir": str(out_dir),
                    "oracle_vad": False,
                    "clustering": {
                        "parameters": clustering_parameters,
                    },
                    "vad": {
                        "model_path": NEMO_VAD_MODEL,
                    },
                    "speaker_embeddings": {
                        "model_path": NEMO_SPEAKER_EMBEDDING_MODEL,
                        "parameters": {
                            "save_embeddings": False,
                        },
                    },
                },
            }
        )

        diarizer = nemo_asr.models.ClusteringDiarizer(cfg=config)
        diarizer.diarize()

        rttm_files = sorted(out_dir.rglob("*.rttm"))
        if not rttm_files:
            raise RuntimeError("NeMo diarization did not produce an RTTM file")

        return _parse_rttm_file(rttm_files[0])


def load_wespeaker_model(model_name: str = WESPEAKER_MODEL):
    """
    Load and cache a WeSpeaker model and ensure the model is placed on the CPU.
    
    If a different model name is requested than the currently cached one, the function loads the named model, sets it to use the CPU, and updates the module cache.
    
    Parameters:
        model_name (str): Identifier or path of the WeSpeaker model to load (defaults to the module's WESPEAKER_MODEL).
    
    Returns:
        wespeaker_model: The loaded WeSpeaker model instance (cached for subsequent calls).
    """
    global wespeaker_model, wespeaker_model_key
    if wespeaker_model is not None and wespeaker_model_key == model_name:
        return wespeaker_model

    with _wespeaker_model_lock:
        if wespeaker_model is None or wespeaker_model_key != model_name:
            import wespeaker

            logger.info(f"Loading WeSpeaker '{model_name}' on cpu")
            wespeaker_model = wespeaker.load_model(model_name)
            wespeaker_model.set_device("cpu")
            wespeaker_model_key = model_name
            logger.info("WeSpeaker loaded")
    return wespeaker_model


def _validate_diarization_speaker_bounds(
    min_speakers: Optional[int],
    max_speakers: Optional[int],
) -> None:
    if min_speakers is not None and min_speakers < 1:
        raise HTTPException(status_code=400, detail="min_speakers must be at least 1")
    if max_speakers is not None and max_speakers < 1:
        raise HTTPException(status_code=400, detail="max_speakers must be at least 1")
    if min_speakers is not None and max_speakers is not None and min_speakers > max_speakers:
        raise HTTPException(status_code=400, detail="min_speakers cannot be greater than max_speakers")


def _run_wespeaker_diarization(audio_path: Path) -> tuple[list[DiarizationSegment], int]:
    """
    Run speaker diarization on an audio file using the WeSpeaker backend and return normalized segments.
    
    Parameters:
        audio_path (Path): Path to the audio file to be diarized.
    
    Returns:
        segments (list[DiarizationSegment]): Ordered list of diarization segments with start/end times and normalized speaker IDs.
        speaker_count (int): Number of distinct normalized speakers detected.
    """
    model = load_wespeaker_model()
    diar_result = model.diarize(str(audio_path))

    segments: list[DiarizationSegment] = []
    seen_speakers: set[str] = set()
    assigned_labels: dict[str, str] = {}
    for item in diar_result:
        _, start, end, raw_speaker_id = item
        speaker_id = _normalize_native_speaker_label(raw_speaker_id, assigned_labels)
        segments.append(
            DiarizationSegment(
                start=round(float(start), 3),
                end=round(float(end), 3),
                speaker_id=speaker_id,
            )
        )
        seen_speakers.add(speaker_id)

    return segments, len(seen_speakers)


def _probe_diarization_providers() -> tuple[dict[str, bool], dict[str, str], bool, str]:
    """
    Probe availability of NeMo and WeSpeaker diarization providers and summarize overall readiness.
    
    Returns:
        provider_ready (dict[str, bool]): Mapping of provider name to availability (e.g., {'nemo': True, 'wespeaker': False}).
        provider_details (dict[str, str]): Mapping of provider name to a human-readable availability/detail message.
        stage_ready (bool): `True` if at least one provider is available, `False` otherwise.
        detail (str): Human-readable stage-level status:
            - "Diarization available" when the default provider (NeMo) is available,
            - "Default diarization provider unavailable; CPU fallback available" when some provider is available but the default is not,
            - otherwise a concatenation of provider-specific detail messages.
    """
    provider_ready: dict[str, bool] = {}
    provider_details: dict[str, str] = {}

    nemo_ready, nemo_detail = _probe_nemo_diarization_available()
    wespeaker_ready, wespeaker_detail = _probe_wespeaker_available()

    provider_ready["nemo"] = nemo_ready
    provider_ready["wespeaker"] = wespeaker_ready
    provider_details["nemo"] = nemo_detail
    provider_details["wespeaker"] = wespeaker_detail

    logger.info(
        "Diarization capability probe: nemo=%s (%s); wespeaker=%s (%s)",
        nemo_ready,
        nemo_detail,
        wespeaker_ready,
        wespeaker_detail,
    )

    stage_ready = any(provider_ready.values())
    if stage_ready and provider_ready[NEMO_DIARIZATION_DEFAULT_PROVIDER]:
        detail = "Diarization available"
    elif stage_ready:
        detail = "Default diarization provider unavailable; CPU fallback available"
    else:
        detail = f"nemo: {nemo_detail}; wespeaker: {wespeaker_detail}"

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
    cuda_available = torch.cuda.is_available()
    return HealthLiveResponse(
        status="healthy",
        timestamp=datetime.utcnow().isoformat(),
        cuda_available=cuda_available,
        cuda_version=torch.version.cuda if cuda_available else None,
    )


@app.get("/health", response_model=HealthLiveResponse)
async def health_check():
    return await health_live()


@app.get("/capabilities", response_model=CapabilitiesResponse)
async def get_stage_capabilities():
    """
    Builds and returns the service capabilities for transcription, translation, TTS, and diarization.
    
    Queries available backends (Whisper, NLLB, Qwen3-TTS, NeMo/WeSpeaker) and assembles a CapabilitiesResponse summarizing readiness, human-readable details, per-stage provider availability, provider-specific details, the diarization default provider, and supported diarization engines.
    
    Returns:
        CapabilitiesResponse: Summary of stage capabilities with the following fields populated:
            - transcription: StageCapability for Whisper readiness and detail.
            - translation: StageCapability for NLLB readiness and detail.
            - tts: StageCapability indicating Qwen3-TTS readiness, detail, and provider map/details for "qwen-tts".
            - diarization: StageCapability indicating overall diarization readiness, detail, provider readiness map, provider detail map, `default_provider` (NeMo), and `engines` (["nemo", "wespeaker"]).
    """
    tx_ready, tx_detail = _probe_whisper_available()
    tl_ready, tl_detail = _probe_nllb_available()

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
        ),
        diarization=StageCapability(
            ready=diar_ready,
            detail=diar_detail,
            providers=diar_providers,
            provider_details=diar_provider_details,
            default_provider=NEMO_DIARIZATION_DEFAULT_PROVIDER,
            engines=["nemo", "wespeaker"],
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

@app.post("/diarize", response_model=DiarizationResponse)
async def diarize(
    audio: UploadFile = File(...),
    min_speakers: Optional[int] = Form(None),
    max_speakers: Optional[int] = Form(None),
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
        segments, speaker_count = await asyncio.to_thread(
            _run_nemo_diarization,
            temp_audio_path,
            min_speakers,
            max_speakers,
        )
        return _build_diarization_response(segments, speaker_count)

    except HTTPException:
        raise
    except Exception as exc:
        logger.error(f"Diarization failed: {exc}", exc_info=True)
        raise HTTPException(status_code=400, detail=str(exc))
    finally:
        if temp_audio_path:
            temp_audio_path.unlink(missing_ok=True)


@app.post("/diarize/wespeaker", response_model=DiarizationResponse)
async def diarize_wespeaker(
    audio: UploadFile = File(...),
    min_speakers: Optional[int] = Form(None),
    max_speakers: Optional[int] = Form(None),
):
    """
    Perform speaker diarization on uploaded audio using the WeSpeaker backend.
    
    This endpoint accepts optional speaker-count hints for parity with the NeMo endpoint but ignores them; it writes the uploaded audio to a temporary file, runs WeSpeaker diarization in a background thread, cleans up the temp file in a finally block, and returns the diarization result.
    
    Parameters:
        audio (UploadFile): Uploaded audio file to diarize.
        min_speakers (Optional[int]): Hint for minimum number of speakers (accepted but ignored).
        max_speakers (Optional[int]): Hint for maximum number of speakers (accepted but ignored).
    
    Returns:
        DiarizationResponse: Structured response containing diarization segments and the detected speaker count.
    
    Raises:
        HTTPException: Raised with status 400 when diarization fails for reasons other than pre-existing HTTP errors.
    """
    _validate_diarization_speaker_bounds(min_speakers, max_speakers)
    _ = min_speakers
    _ = max_speakers

    temp_audio_path: Optional[Path] = None
    try:
        temp_audio_path = _stage_audio_upload_to_temp(audio, "diar_wespeaker")
        temp_audio_path.write_bytes(await audio.read())
        segments, speaker_count = await asyncio.to_thread(
            _run_wespeaker_diarization,
            temp_audio_path,
        )
        return _build_diarization_response(segments, speaker_count)

    except HTTPException:
        raise
    except Exception as exc:
        logger.error(f"WeSpeaker diarization failed: {exc}", exc_info=True)
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    finally:
        if temp_audio_path:
            temp_audio_path.unlink(missing_ok=True)


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
    """Lazy-load Qwen3-TTS pipeline; cached globally per model name."""
    global qwen_model, qwen_model_key
    if qwen_model is None or qwen_model_key != model_name:
        from qwen_tts import Qwen3TTSModel
        logger.info(f"Loading Qwen3-TTS '{model_name}' on {HOST_DEVICE}")
        qwen_model = Qwen3TTSModel.from_pretrained(
            model_name,
            device_map={"": "cuda:0"} if HOST_DEVICE == "cuda" else HOST_DEVICE,
            dtype=torch.bfloat16 if HOST_DEVICE == "cuda" else torch.float32,
            attn_implementation="sdpa"
        )
        qwen_model_key = model_name
        logger.info("Qwen3-TTS loaded")
    return qwen_model


@app.get("/tts/qwen/warmup")
async def qwen_warmup(model: str = "Qwen/Qwen3-TTS-12Hz-1.7B-Base"):
    """Pre-load model weights into memory / VRAM."""
    try:
        load_qwen_model(model)
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
    if _qwen_warmup_status == "warming":
        raise HTTPException(status_code=503, detail="Qwen3-TTS model is still loading, please wait")
    if _qwen_warmup_status is not None and _qwen_warmup_status.startswith("failed"):
        raise HTTPException(status_code=503, detail=f"Qwen3-TTS model not available: {_qwen_warmup_status}")

    temp_ref_path: Optional[Path] = None
    out_path = TEMP_DIR / f"qwen_{uuid4().hex}.wav"

    try:
        if not text.strip():
            raise HTTPException(status_code=400, detail="text cannot be empty")

        tts = load_qwen_model(model)

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

        def _synth_and_write() -> None:
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

        if temp_ref_path:
            background_tasks.add_task(lambda p=temp_ref_path: p.unlink(missing_ok=True))

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



async def _warmup_qwen():
    global _qwen_warmup_status
    _qwen_warmup_status = "warming"
    try:
        logger.info("Qwen3-TTS warmup starting")
        await asyncio.to_thread(load_qwen_model)
        _qwen_warmup_status = "ready"
        logger.info("Qwen3-TTS warmup complete")
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
        _qwen_warmup_status = f"failed: {reason}"
        logger.warning(f"Qwen3-TTS warmup failed: {reason}", exc_info=True)
    except Exception as exc:
        _qwen_warmup_status = f"failed: {exc}"
        logger.warning(f"Qwen3-TTS warmup failed: {exc}", exc_info=True)


# ============================================================================
# Startup / shutdown
# ============================================================================

@app.on_event("startup")
async def startup_event():
    logger.info("Babel Player inference service starting")
    logger.info(f"CUDA available: {torch.cuda.is_available()}")
    if torch.cuda.is_available():
        logger.info(f"CUDA device: {torch.cuda.get_device_name(0)}")
        logger.info(f"CUDA version: {torch.version.cuda}")
    asyncio.create_task(_warmup_qwen())


@app.on_event("shutdown")
async def shutdown_event():
    logger.info("Babel Player inference service shutting down")
    if torch.cuda.is_available():
        torch.cuda.empty_cache()


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

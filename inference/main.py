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
from importlib import import_module
from pathlib import Path
from datetime import datetime
from typing import Optional
from uuid import uuid4

import torch
from fastapi import FastAPI, File, Form, UploadFile, HTTPException, BackgroundTasks
from fastapi.responses import FileResponse
from pydantic import BaseModel

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

HOST_DEVICE = "cuda" if torch.cuda.is_available() else "cpu"
HOST_COMPUTE_TYPE = "float16" if HOST_DEVICE == "cuda" else "int8"
# Tracks effective compute type after per-stage validation and potential downgrades
EFFECTIVE_HOST_COMPUTE_TYPE = HOST_COMPUTE_TYPE
# Tracks downgrade reasons per stage for UI/logging projection
COMPUTE_DOWNGRADE_REASONS: dict[str, str] = {}
XTTS_MODEL_NAME = "tts_models/multilingual/multi-dataset/xtts_v2"

# Temporary directory for artifacts
TEMP_DIR = Path(tempfile.gettempdir()) / "babel_inference"
TEMP_DIR.mkdir(exist_ok=True)

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

class HealthResponse(BaseModel):
    status: str
    timestamp: str
    cuda_available: bool
    cuda_version: Optional[str] = None

class StageCapability(BaseModel):
    ready: bool
    detail: Optional[str] = None

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

# ============================================================================
# Capabilities / Health
# ============================================================================

def get_stage_capabilities() -> CapabilitiesResponse:
    transcription_ready = HOST_DEVICE == "cuda"
    transcription_effective_type = _get_effective_compute_type("transcription")
    
    translation_ready = _probe_nllb_available()
    translation_effective_type = _get_effective_compute_type("translation")
    
    tts_ready = _probe_xtts_available()
    tts_effective_type = _get_effective_compute_type("tts")

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
            detail=(
                f"XTTS v2 ready on {HOST_DEVICE} with compute_type={tts_effective_type}; reference audio required"
                if tts_ready
                else _xtts_capability_detail()
            ),
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
            xtts_model = tts_factory(
                model_path=model_pth_path,
                config_path=config_json_path,
                progress_bar=False,
                gpu=(HOST_DEVICE == "cuda"),
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


def translate_with_nllb(model_name: str, source_language: str, target_language: str, text: str) -> str:
    if not text.strip():
        return ""

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

@app.get("/tts/audio/{filename}")
async def get_tts_audio(filename: str, background_tasks: BackgroundTasks):
    """Retrieve a generated TTS audio file by its basename."""
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
    args = parser.parse_args()

    HOST_COMPUTE_TYPE = args.compute_type
    HOST_DEVICE = "cuda" if torch.cuda.is_available() else "cpu"
    logger.info(
        "Managed inference host launch args: host=%s port=%s compute_type=%s require_cuda=%s python=%s",
        args.host,
        args.port,
        args.compute_type,
        args.require_cuda,
        sys.executable,
    )
    _log_host_runtime_state("preflight")

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

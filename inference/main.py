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
qwen_model = None
qwen_model_key = None

# Warmup state: None = not started, "warming" = in progress,
# "ready" = model loaded, "failed: <reason>" = terminal failure
_xtts_warmup_status: str | None = None
_qwen_warmup_status: str | None = None

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


class CapabilitiesResponse(BaseModel):
    transcription: StageCapability
    translation: StageCapability
    tts: StageCapability


class TranscriptSegmentResponse(BaseModel):
    start: float
    end: float
    text: str


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


class XttsReferenceResponse(BaseModel):
    success: bool
    reference_id: Optional[str] = None
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


def _probe_xtts_available() -> tuple[bool, str]:
    status = _xtts_warmup_status
    if status is None or status == "warming":
        return False, "XTTS model warming up"
    if status == "ready":
        return True, f"XTTS model loaded on {HOST_DEVICE}"
    # status starts with "failed: ..."
    return False, f"XTTS warmup {status}"


def _probe_qwen_available() -> tuple[bool, str]:
    status = _qwen_warmup_status
    if status is None or status == "warming":
        return False, "Qwen3-TTS warming up"
    if status == "ready":
        return True, f"Qwen3-TTS model loaded on {HOST_DEVICE}"
    return False, f"Qwen3-TTS warmup {status}"


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
    tx_ready, tx_detail = _probe_whisper_available()
    tl_ready, tl_detail = _probe_nllb_available()

    xtts_ready, xtts_detail = _probe_xtts_available()
    qwen_ready, qwen_detail = _probe_qwen_available()
    tts_ready = xtts_ready or qwen_ready
    if tts_ready:
        tts_detail = "TTS available"
    else:
        tts_detail = f"xtts: {xtts_detail}; qwen: {qwen_detail}"

    return CapabilitiesResponse(
        transcription=StageCapability(ready=tx_ready, detail=tx_detail),
        translation=StageCapability(ready=tl_ready, detail=tl_detail),
        tts=StageCapability(
            ready=tts_ready,
            detail=tts_detail,
            # FIX: use "xtts-container" (C# provider ID) not "xtts-v2" (model name)
            # so ContainerCapabilitiesSnapshot.TryGetTtsProviderReadiness() finds it
            providers={
                "xtts-container": xtts_ready,
                "qwen-tts": qwen_ready,
            },
            provider_details={
                "xtts-container": xtts_detail,
                "qwen-tts": qwen_detail,
            },
        ),
    )


# ============================================================================
# Transcription
# ============================================================================

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
        logger.info(f"Loading Whisper '{model_name}' on {HOST_DEVICE} ({effective_compute})")
        whisper_model = WhisperModel(
            model_name,
            device=HOST_DEVICE,
            compute_type=effective_compute,
            cpu_threads=cpu_threads,
            num_workers=num_workers,
        )
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
        segments_gen, info = whisper.transcribe(str(temp_audio_path), language=language or None)
        segments = [
            TranscriptSegmentResponse(start=s.start, end=s.end, text=s.text.strip())
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
        raise HTTPException(status_code=400, detail=str(exc))


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
        # FIX: load with correct dtype directly to avoid float32 OOM on CUDA
        model_kwargs: dict = {}
        if HOST_DEVICE == "cuda":
            cuda_dtype = torch.bfloat16 if torch.cuda.is_bf16_supported() else torch.float16
            model_kwargs["torch_dtype"] = cuda_dtype
            model_kwargs["device_map"] = "cuda"
        nllb_model = AutoModelForSeq2SeqLM.from_pretrained(normalized_model_name, **model_kwargs)
        # FIX: set eval mode after loading to avoid tracking gradients at inference time
        nllb_model.eval()
        if HOST_DEVICE != "cuda":
            # device_map handles GPU placement above; only needed for CPU
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
                # FIX: set src_lang so NLLB encodes the source language token correctly
                tokenizer.src_lang = src_flores
                inputs = tokenizer(text, return_tensors="pt").to(HOST_DEVICE)
                forced = tokenizer.convert_tokens_to_ids([tgt_flores])
                # FIX: wrap in no_grad to avoid unnecessary gradient tracking in production
                with torch.no_grad():
                    out = nllb.generate(**inputs, forced_bos_token_id=forced[0], max_length=512)
                t_text = tokenizer.batch_decode(out, skip_special_tokens=True)[0]
            translated.append(TranslatedSegmentResponse(
                start=seg.get("start", 0.0),
                end=seg.get("end", 0.0),
                text=text,
                translated_text=t_text,
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
# XTTS
# ============================================================================

def load_xtts_model(model_name: str = XTTS_MODEL_NAME):
    global xtts_model, xtts_model_key
    if xtts_model is None or xtts_model_key != model_name:
        # Compatibility shim: TTS==0.22.0 references transformers.BeamSearchScorer which
        # was moved in newer transformers versions. Patch before importing TTS.api.
        try:
            import transformers as _transformers
            if not hasattr(_transformers, "BeamSearchScorer"):
                from transformers.generation.beam_search import BeamSearchScorer as _bsc
                _transformers.BeamSearchScorer = _bsc
        except Exception:
            pass
        from TTS.api import TTS
        logger.info(f"Loading XTTS '{model_name}'")
        xtts_model = TTS(model_name).to(HOST_DEVICE)
        xtts_model_key = model_name
        logger.info("XTTS loaded")
    return xtts_model


@app.post("/tts/xtts/references", response_model=XttsReferenceResponse)
async def register_xtts_reference(
    speaker_id: str = Form(...),
    file: UploadFile = File(...),
    transcript: Optional[str] = Form(None),
    background_tasks: BackgroundTasks = BackgroundTasks(),
):
    temp_path = None
    try:
        # FIX: sanitize filename to prevent path traversal
        safe_filename = Path(file.filename or "").name
        temp_path = TEMP_DIR / f"ref_{uuid4().hex}_{safe_filename}"
        temp_path.write_bytes(await file.read())
        ref_id = f"{speaker_id}_{uuid4().hex}"
        xtts_reference_registry[ref_id] = {
            "speaker_id": speaker_id,
            "path": str(temp_path),
            "transcript": transcript,
        }
        return XttsReferenceResponse(success=True, reference_id=ref_id)
    except Exception as exc:
        if temp_path:
            background_tasks.add_task(lambda p=temp_path: p.unlink(missing_ok=True))
        raise HTTPException(status_code=400, detail=str(exc))


@app.post("/tts/xtts/segment", response_model=TtsResponse)
async def xtts_segment(
    text: str = Form(...),
    model: str = Form("xtts-v2"),
    language: Optional[str] = Form(None),
    speaker_id: Optional[str] = Form(None),
    reference_id: Optional[str] = Form(None),
    reference_transcript: Optional[str] = Form(None),
    reference_file: Optional[UploadFile] = File(None),
    background_tasks: BackgroundTasks = BackgroundTasks(),
):
    if _xtts_warmup_status == "warming":
        raise HTTPException(status_code=503, detail="XTTS model is still loading, please wait")
    if _xtts_warmup_status is not None and _xtts_warmup_status.startswith("failed"):
        raise HTTPException(status_code=503, detail=f"XTTS model not available: {_xtts_warmup_status}")

    temp_ref_path = None
    # FIX: write .wav not .mp3 — TTS.tts_to_file() writes PCM, not MP3
    out_path = TEMP_DIR / f"xtts_{uuid4().hex}.wav"
    try:
        tts = load_xtts_model(XTTS_MODEL_NAME)

        ref_audio_path: Optional[str] = None
        if reference_file is not None:
            # FIX: sanitize filename to prevent path traversal
            safe_ref_name = Path(reference_file.filename or "").name
            temp_ref_path = TEMP_DIR / f"ref_{uuid4().hex}_{safe_ref_name}"
            temp_ref_path.write_bytes(await reference_file.read())
            ref_audio_path = str(temp_ref_path)
        elif reference_id and reference_id in xtts_reference_registry:
            ref_audio_path = xtts_reference_registry[reference_id]["path"]

        if not ref_audio_path:
            raise HTTPException(status_code=400, detail="XTTS requires a reference audio file or valid reference_id.")

        await asyncio.to_thread(
            tts.tts_to_file,
            text=text,
            speaker_wav=ref_audio_path,
            language=language or "en",
            file_path=str(out_path),
        )

        if temp_ref_path:
            background_tasks.add_task(lambda p=temp_ref_path: p.unlink(missing_ok=True))

        return TtsResponse(
            success=True,
            voice=model,
            audio_path=str(out_path),
            file_size_bytes=out_path.stat().st_size,
        )
    except HTTPException:
        raise
    except Exception as exc:
        logger.error(f"XTTS segment failed: {exc}", exc_info=True)
        if temp_ref_path:
            background_tasks.add_task(lambda p=temp_ref_path: p.unlink(missing_ok=True))
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
            device_map=HOST_DEVICE,
            torch_dtype=torch.float16 if HOST_DEVICE == "cuda" else torch.float32,
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
            # FIX: sanitize filename to prevent path traversal
            safe_reference_name = os.path.basename(reference_file.filename or "")
            if not safe_reference_name:
                safe_reference_name = "reference_audio"
            temp_ref_path = TEMP_DIR / f"qwenref_{uuid4().hex}_{safe_reference_name}"
            temp_ref_path.write_bytes(await reference_file.read())
            ref_audio_path = str(temp_ref_path)

        lang = (language or "en").strip().lower()

        if ref_audio_path is None:
            raise HTTPException(
                status_code=400,
                detail="Qwen3-TTS Base model requires reference audio (reference_file)")

        def _synth_and_write() -> None:
            import soundfile as sf
            wavs, sample_rate = tts.generate_voice_clone(
                text=text,
                language=lang,
                ref_audio=ref_audio_path,
                ref_text=reference_text or None,
                x_vector_only_mode=not bool(reference_text),
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
    # FIX: sanitize filename + enforce path stays under TEMP_DIR to prevent traversal
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

async def _warmup_xtts():
    global _xtts_warmup_status
    _xtts_warmup_status = "warming"
    try:
        logger.info("XTTS warmup starting")
        await asyncio.to_thread(load_xtts_model)
        _xtts_warmup_status = "ready"
        logger.info("XTTS warmup complete")
    except Exception as exc:
        _xtts_warmup_status = f"failed: {exc}"
        logger.warning(f"XTTS warmup failed: {exc}", exc_info=True)


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
    asyncio.create_task(_warmup_xtts())
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
    # FIX: restore flags expected by ManagedVenvHostManager.CreateHostProcessStartInfo()
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

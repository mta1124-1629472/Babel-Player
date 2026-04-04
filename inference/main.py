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
    try:
        from TTS.api import TTS  # noqa: F401
        return True, "Coqui TTS available"
    except Exception as exc:
        return False, str(exc)


def _probe_qwen_available() -> tuple[bool, str]:
    try:
        import qwen_tts  # noqa: F401
        return True, "qwen-tts available"
    except Exception as exc:
        return False, str(exc)


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
            providers={
                "xtts-v2": xtts_ready,
                "qwen-tts": qwen_ready,
            },
            provider_details={
                "xtts-v2": xtts_detail,
                "qwen-tts": qwen_detail,
            },
        ),
    )


# ============================================================================
# Transcription
# ============================================================================

def load_whisper_model(model_name: str):
    global whisper_model, whisper_model_key
    if whisper_model is None or whisper_model_key != model_name:
        from faster_whisper import WhisperModel
        compute_type = "float16" if HOST_DEVICE == "cuda" else "int8"
        logger.info(f"Loading Whisper '{model_name}' on {HOST_DEVICE} ({compute_type})")
        whisper_model = WhisperModel(model_name, device=HOST_DEVICE, compute_type=compute_type)
        whisper_model_key = model_name
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
        whisper = load_whisper_model(model)
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

def load_nllb_model(model_name: str):
    global nllb_tokenizer, nllb_model, nllb_model_key
    if nllb_model is None or nllb_model_key != model_name:
        from transformers import AutoTokenizer, AutoModelForSeq2SeqLM
        logger.info(f"Loading NLLB '{model_name}' on {HOST_DEVICE}")
        nllb_tokenizer = AutoTokenizer.from_pretrained(model_name)
        nllb_model = AutoModelForSeq2SeqLM.from_pretrained(model_name)
        if HOST_DEVICE == "cuda":
            nllb_model = nllb_model.to("cuda")
        nllb_model_key = model_name
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
        src_flores = FLORES.get(source_language, source_language)
        tgt_flores = FLORES.get(target_language, target_language)
        tokenizer, nllb = load_nllb_model(model)
        translated: list[TranslatedSegmentResponse] = []
        for seg in segments:
            text = seg.get("text", "")
            t_text = ""
            if text:
                inputs = tokenizer(text, return_tensors="pt").to(HOST_DEVICE)
                forced = tokenizer.convert_tokens_to_ids([tgt_flores])
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
        temp_path = TEMP_DIR / f"ref_{uuid4().hex}_{file.filename}"
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
    temp_ref_path = None
    out_path = TEMP_DIR / f"xtts_{uuid4().hex}.mp3"
    try:
        tts = load_xtts_model(XTTS_MODEL_NAME)

        ref_audio_path: Optional[str] = None
        if reference_file is not None:
            temp_ref_path = TEMP_DIR / f"ref_{uuid4().hex}_{reference_file.filename}"
            temp_ref_path.write_bytes(await reference_file.read())
            ref_audio_path = str(temp_ref_path)
        elif reference_id and reference_id in xtts_reference_registry:
            ref_audio_path = xtts_reference_registry[reference_id]["path"]

        if not ref_audio_path:
            raise HTTPException(status_code=400, detail="XTTS requires a reference audio file or valid reference_id.")

        tts.tts_to_file(
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
        import qwen_tts  # noqa: F401  -- confirms package present
        from qwen_tts import QwenTTS
        logger.info(f"Loading Qwen3-TTS '{model_name}' on {HOST_DEVICE}")
        qwen_model = QwenTTS.from_pretrained(
            model_name,
            device=HOST_DEVICE,
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
    """
    Synthesise one TTS segment with Qwen3-TTS voice cloning.

    Multipart fields
    ----------------
    text              : str  – text to synthesise
    model             : str  – HF model id (default Qwen/Qwen3-TTS-12Hz-1.7B-Base)
    language          : str  – BCP-47 language code (default "en")
    reference_text    : str  – transcript of the reference clip (optional)
    reference_file    : file – WAV/MP3 speaker reference audio

    Response
    --------
    TtsResponse JSON with audio_path pointing to a temp file the client
    can download via GET /tts/audio/{filename}.
    """
    temp_ref_path: Optional[Path] = None
    out_path = TEMP_DIR / f"qwen_{uuid4().hex}.wav"

    try:
        if not text.strip():
            raise HTTPException(status_code=400, detail="text cannot be empty")

        tts = load_qwen_model(model)

        ref_audio_path: Optional[str] = None
        if reference_file is not None:
            temp_ref_path = TEMP_DIR / f"qwenref_{uuid4().hex}_{reference_file.filename}"
            temp_ref_path.write_bytes(await reference_file.read())
            ref_audio_path = str(temp_ref_path)

        lang = (language or "en").strip().lower()

        # qwen-tts 0.1.x API: synthesise(text, reference_audio, reference_text, language)
        # Returns (sample_rate, audio_ndarray) – 24 kHz mono float32
        result = tts.synthesise(
            text=text,
            reference_audio=ref_audio_path,
            reference_text=reference_text or "",
            language=lang,
        )

        # Persist as WAV (24 kHz, mono, 16-bit PCM)
        import soundfile as sf
        sample_rate, audio_data = result
        sf.write(str(out_path), audio_data, sample_rate, subtype="PCM_16")

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
    file_path = TEMP_DIR / filename
    if not file_path.exists():
        raise HTTPException(status_code=404, detail="Audio file not found")
    media_type = "audio/wav" if filename.endswith(".wav") else "audio/mpeg"
    background_tasks.add_task(lambda p=file_path: p.unlink(missing_ok=True))
    return FileResponse(file_path, media_type=media_type)


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
    args = parser.parse_args()
    uvicorn.run(app, host=args.host, port=args.port)

import os
import json
import logging
import tempfile
import uuid
from pathlib import Path
from typing import Optional
from datetime import datetime

import torch
from fastapi import FastAPI, File, Form, UploadFile, HTTPException, BackgroundTasks
from fastapi.responses import FileResponse
from pydantic import BaseModel
from faster_whisper import WhisperModel
from googletrans import Translator
import edge_tts
import asyncio

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger(__name__)

# Initialize FastAPI
app = FastAPI(
    title="Babel Player Inference Service",
    description="GPU-accelerated inference service for transcription, translation, and TTS",
    version="1.0.0"
)

# Global model instances (loaded once)
whisper_model = None
whisper_model_key = None
translator = None
xtts_model = None
xtts_available = None
xtts_error = None
xtts_reference_store: dict[str, dict] = {}

# Temporary directory for artifacts
TEMP_DIR = Path(tempfile.gettempdir()) / "babel_inference"
TEMP_DIR.mkdir(exist_ok=True)

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

# ============================================================================
# Health Check
# ============================================================================

def get_stage_capabilities() -> CapabilitiesResponse:
    global xtts_available, xtts_error

    if xtts_available is None:
        try:
            from TTS.api import TTS  # noqa: F401
            xtts_available = True
            xtts_error = None
        except Exception as e:
            xtts_available = False
            xtts_error = str(e)

    tts_detail = "edge-tts available"
    if xtts_available:
        tts_detail += "; xtts-v2 available"
    else:
        tts_detail += f"; xtts-v2 unavailable ({xtts_error})"

    return CapabilitiesResponse(
        transcription=StageCapability(
            ready=True,
            detail="faster-whisper available; model loads on demand",
        ),
        translation=StageCapability(
            ready=True,
            detail="googletrans available",
        ),
        tts=StageCapability(
            ready=True,
            detail=tts_detail,
        ),
    )

@app.get("/health/live", response_model=HealthResponse)
async def health_live():
    """Liveness endpoint with basic CUDA info."""
    try:
        cuda_available = torch.cuda.is_available()
        cuda_version = torch.version.cuda if cuda_available else None
        
        return HealthResponse(
            status="healthy",
            timestamp=datetime.utcnow().isoformat(),
            cuda_available=cuda_available,
            cuda_version=cuda_version
        )
    except Exception as e:
        logger.error(f"Health check failed: {e}")
        raise HTTPException(status_code=500, detail=str(e))

@app.get("/health", response_model=HealthResponse)
async def health_check():
    """Backward-compatible health alias."""
    return await health_live()

@app.get("/capabilities", response_model=CapabilitiesResponse)
async def capabilities():
    """Stage-specific readiness details for the desktop app."""
    try:
        return get_stage_capabilities()
    except Exception as e:
        logger.error(f"Capability probe failed: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=str(e))

# ============================================================================
# Transcription Endpoint
# ============================================================================

def load_whisper_model(model_name: str, cpu_compute_type: str = "int8", cpu_threads: Optional[int] = None, num_workers: int = 1):
    """Load Whisper model with GPU support."""
    global whisper_model
    global whisper_model_key

    device = "cuda" if torch.cuda.is_available() else "cpu"
    compute_type = "float16" if device == "cuda" else (cpu_compute_type or "int8")
    effective_num_workers = max(1, int(num_workers or 1))
    effective_cpu_threads = int(cpu_threads) if cpu_threads is not None else None
    if effective_cpu_threads is not None and effective_cpu_threads <= 0:
        effective_cpu_threads = None

    desired_key = (model_name, device, compute_type, effective_cpu_threads, effective_num_workers)

    if whisper_model is None or whisper_model_key != desired_key:
        init_kwargs = {
            "device": device,
            "compute_type": compute_type,
            "num_workers": effective_num_workers,
        }
        if device == "cpu" and effective_cpu_threads is not None:
            init_kwargs["cpu_threads"] = effective_cpu_threads

        logger.info(
            f"Loading Whisper model '{model_name}' on device '{device}' "
            f"with compute_type '{compute_type}', "
            f"cpu_threads='{effective_cpu_threads if effective_cpu_threads is not None else 'auto'}', "
            f"num_workers='{effective_num_workers}'"
        )
        whisper_model = WhisperModel(model_name, **init_kwargs)
        whisper_model_key = desired_key
        logger.info(f"Whisper model loaded successfully")
    
    return whisper_model

@app.post("/transcribe", response_model=TranscriptionResponse)
async def transcribe(
    file: UploadFile = File(...),
    model: str = "base",
    language: Optional[str] = None,
    cpu_compute_type: str = "int8",
    cpu_threads: Optional[int] = None,
    num_workers: int = 1,
    background_tasks: BackgroundTasks = BackgroundTasks()
):
    """Transcribe audio file using Whisper."""
    temp_audio_path = None
    try:
        # Save uploaded file
        temp_audio_path = TEMP_DIR / f"audio_{datetime.now().timestamp()}.wav"
        contents = await file.read()
        temp_audio_path.write_bytes(contents)

        requested_cpu_threads = cpu_threads if (cpu_threads is not None and cpu_threads > 0) else "auto"
        requested_num_workers = max(1, int(num_workers or 1))
        requested_cpu_compute = cpu_compute_type or "int8"
        
        logger.info(
            f"Transcribing file: {file.filename} "
            f"(model={model}, cpu_compute={requested_cpu_compute}, "
            f"cpu_threads={requested_cpu_threads}, cpu_workers={requested_num_workers})"
        )
        
        # Load model
        whisper = load_whisper_model(model, cpu_compute_type, cpu_threads, num_workers)
        
        # Transcribe
        segments, info = whisper.transcribe(str(temp_audio_path), language=language)
        
        # Convert to response format
        transcript_segments = []
        for seg in segments:
            transcript_segments.append(TranscriptSegment(
                start=seg.start,
                end=seg.end,
                text=seg.text.strip()
            ))
        
        logger.info(f"Transcription complete: {len(transcript_segments)} segments, language: {info.language}")
        
        # Schedule cleanup
        background_tasks.add_task(lambda p=temp_audio_path: p.unlink(missing_ok=True))
        
        return TranscriptionResponse(
            success=True,
            language=info.language or "unknown",
            language_probability=info.language_probability or 0.0,
            segments=transcript_segments
        )
    except Exception as e:
        logger.error(f"Transcription failed: {e}", exc_info=True)
        if temp_audio_path:
            background_tasks.add_task(lambda p=temp_audio_path: p.unlink(missing_ok=True))
        raise HTTPException(status_code=400, detail=str(e))

# ============================================================================
# Translation Endpoint
# ============================================================================

def get_translator():
    """Get or create Translator instance."""
    global translator
    if translator is None:
        logger.info("Initializing Translator")
        translator = Translator()
    return translator

@app.post("/translate", response_model=TranslationResponse)
async def translate(
    transcript_json: str = Form(...),
    source_language: str = Form(...),
    target_language: str = Form(...)
):
    """Translate transcript segments."""
    try:
        logger.info(f"Translating {source_language} -> {target_language}")
        
        # Parse transcript JSON
        transcript_data = json.loads(transcript_json)
        segments = transcript_data.get("segments", [])
        
        # Translate each segment
        trans = get_translator()
        translated_segments = []
        failures = []
        
        for index, seg in enumerate(segments):
            text = seg.get("text", "")
            translated_text = ""
            
            if text:
                try:
                    result = trans.translate(text, src=source_language, dest=target_language)
                    translated_text = result.text if result else ""
                except Exception as e:
                    segment_label = seg.get("start", index)
                    logger.error(f"Failed to translate segment {segment_label}: {e}")
                    failures.append(f"segment {segment_label}: {e}")
                    continue
            
            translated_segments.append(TranslatedSegment(
                start=seg.get("start", 0),
                end=seg.get("end", 0),
                text=text,
                translated_text=translated_text
            ))

        if failures:
            raise RuntimeError(
                "Translation failed; no fallback was applied. " + "; ".join(failures)
            )
        
        logger.info(f"Translation complete: {len(translated_segments)} segments translated")
        
        return TranslationResponse(
            success=True,
            source_language=source_language,
            target_language=target_language,
            segments=translated_segments
        )
    except Exception as e:
        logger.error(f"Translation failed: {e}", exc_info=True)
        raise HTTPException(status_code=400, detail=str(e))

# ============================================================================
# TTS Endpoint
# ============================================================================

def get_xtts_model():
    global xtts_model, xtts_available, xtts_error

    if xtts_model is not None:
        return xtts_model

    try:
        from TTS.api import TTS

        logger.info("Loading XTTS model (tts_models/multilingual/multi-dataset/xtts_v2)")
        xtts_model = TTS(model_name="tts_models/multilingual/multi-dataset/xtts_v2")
        xtts_available = True
        xtts_error = None
        return xtts_model
    except Exception as e:
        xtts_available = False
        xtts_error = str(e)
        raise


def save_xtts_wav_to_mp3(wav_path: Path, mp3_path: Path):
    import subprocess

    cmd = [
        "ffmpeg",
        "-y",
        "-i",
        str(wav_path),
        "-codec:a",
        "libmp3lame",
        "-q:a",
        "3",
        str(mp3_path),
    ]

    try:
        subprocess.check_output(cmd, stderr=subprocess.STDOUT)
    except Exception as e:
        raise RuntimeError(f"ffmpeg conversion failed: {e}")


@app.post("/tts", response_model=TtsResponse)
async def text_to_speech(
    text: str = Form(...),
    voice: str = Form("en-US-AriaNeural"),
    background_tasks: BackgroundTasks = BackgroundTasks()
):
    """Generate speech from text using edge-tts."""
    temp_audio_path = None
    try:
        logger.info(f"Generating TTS for voice: {voice}")
        
        temp_audio_path = TEMP_DIR / f"tts_{datetime.now().timestamp()}.mp3"
        
        # Generate speech
        communicate = edge_tts.Communicate(text, voice)
        await communicate.save(str(temp_audio_path))
        
        file_size = temp_audio_path.stat().st_size
        logger.info(f"TTS generation complete: {file_size} bytes")
        
        # Note: Client must retrieve the file before cleanup happens
        # In production, you'd stream the file or use a more robust cleanup strategy
        
        return TtsResponse(
            success=True,
            voice=voice,
            audio_path=str(temp_audio_path),
            file_size_bytes=file_size
        )
    except Exception as e:
        logger.error(f"TTS generation failed: {e}", exc_info=True)
        if temp_audio_path:
            background_tasks.add_task(lambda p=temp_audio_path: p.unlink(missing_ok=True))
        raise HTTPException(status_code=400, detail=str(e))


@app.post("/tts/xtts/references")
async def register_xtts_reference(
    speaker_id: str = Form(...),
    file: UploadFile = File(...),
    transcript: Optional[str] = Form(None)
):
    """Register a reusable XTTS reference clip and return a reference_id."""
    temp_ref_path = None
    try:
        temp_ref_path = TEMP_DIR / f"xtts_ref_{uuid.uuid4().hex}.wav"
        temp_ref_path.write_bytes(await file.read())

        reference_id = f"ref_{uuid.uuid4().hex}"
        xtts_reference_store[reference_id] = {
            "speaker_id": speaker_id,
            "path": str(temp_ref_path),
            "transcript": transcript,
            "created_at": datetime.utcnow().isoformat(),
        }

        return {
            "success": True,
            "reference_id": reference_id,
            "speaker_id": speaker_id,
            "error_message": None,
        }
    except Exception as e:
        logger.error(f"XTTS reference registration failed: {e}", exc_info=True)
        if temp_ref_path:
            temp_ref_path.unlink(missing_ok=True)
        raise HTTPException(status_code=400, detail=str(e))


@app.post("/tts/xtts/segment", response_model=TtsResponse)
async def xtts_segment(
    text: str = Form(...),
    speaker_id: Optional[str] = Form(None),
    reference_id: Optional[str] = Form(None),
    reference_file: Optional[UploadFile] = File(None),
    reference_transcript: Optional[str] = Form(None),
    language: Optional[str] = Form(None),
    background_tasks: BackgroundTasks = BackgroundTasks()
):
    """Generate XTTS segment from text with optional voice-clone reference."""
    wav_path = None
    mp3_path = None
    temp_ref_path = None

    try:
        model = get_xtts_model()

        if reference_id:
            ref = xtts_reference_store.get(reference_id)
            if not ref:
                raise HTTPException(status_code=404, detail=f"Unknown reference_id '{reference_id}'")
            speaker_wav = ref["path"]
            if not os.path.exists(speaker_wav):
                raise HTTPException(status_code=404, detail=f"Reference audio missing for '{reference_id}'")
        elif reference_file is not None:
            temp_ref_path = TEMP_DIR / f"xtts_inline_ref_{uuid.uuid4().hex}.wav"
            temp_ref_path.write_bytes(await reference_file.read())
            speaker_wav = str(temp_ref_path)
        else:
            raise HTTPException(status_code=400, detail="XTTS requires reference_id or reference_file")

        wav_path = TEMP_DIR / f"xtts_{datetime.now().timestamp()}_{uuid.uuid4().hex}.wav"
        mp3_path = TEMP_DIR / f"xtts_{datetime.now().timestamp()}_{uuid.uuid4().hex}.mp3"

        # Use Coqui XTTS API
        tts_kwargs = {
            "text": text,
            "speaker_wav": speaker_wav,
            "language": language or "en",
            "file_path": str(wav_path),
        }
        model.tts_to_file(**tts_kwargs)

        save_xtts_wav_to_mp3(wav_path, mp3_path)

        file_size = mp3_path.stat().st_size
        background_tasks.add_task(lambda p=wav_path: p.unlink(missing_ok=True))

        if temp_ref_path is not None:
            background_tasks.add_task(lambda p=temp_ref_path: p.unlink(missing_ok=True))

        return TtsResponse(
            success=True,
            voice="xtts-v2",
            audio_path=str(mp3_path),
            file_size_bytes=file_size,
            error_message=None,
        )
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"XTTS segment synthesis failed: {e}", exc_info=True)
        if wav_path:
            wav_path.unlink(missing_ok=True)
        if mp3_path:
            mp3_path.unlink(missing_ok=True)
        if temp_ref_path:
            temp_ref_path.unlink(missing_ok=True)
        raise HTTPException(status_code=400, detail=str(e))


@app.get("/tts/audio/{filename}")
async def get_tts_audio(filename: str, background_tasks: BackgroundTasks):
    """Retrieve generated TTS audio file."""
    try:
        file_path = TEMP_DIR / filename
        if not file_path.exists():
            raise HTTPException(status_code=404, detail="Audio file not found")
        
        # Schedule cleanup after response
        background_tasks.add_task(lambda p=file_path: p.unlink(missing_ok=True))
        
        return FileResponse(file_path, media_type="audio/mpeg")
    except Exception as e:
        logger.error(f"Failed to retrieve TTS audio: {e}")
        raise HTTPException(status_code=400, detail=str(e))

# ============================================================================
# Startup/Shutdown Events
# ============================================================================

@app.on_event("startup")
async def startup_event():
    """Initialize resources on startup."""
    logger.info("Inference service starting up")
    logger.info(f"CUDA available: {torch.cuda.is_available()}")
    if torch.cuda.is_available():
        logger.info(f"CUDA device: {torch.cuda.get_device_name(0)}")
        logger.info(f"CUDA version: {torch.version.cuda}")

@app.on_event("shutdown")
async def shutdown_event():
    """Clean up on shutdown."""
    logger.info("Inference service shutting down")
    # Clear GPU cache if available
    if torch.cuda.is_available():
        torch.cuda.empty_cache()

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8000)

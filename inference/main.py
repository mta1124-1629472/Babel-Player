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

# XTTS availability probe — None = not yet checked, True/False = result
xtts_available: Optional[bool] = None
xtts_error: Optional[str] = None

# In-memory reference store: speaker_id -> {"audio_path": str, "transcript": str|None}
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

class XttsReferenceResponse(BaseModel):
    success: bool
    reference_id: str
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
# XTTS helpers
# ============================================================================

def _probe_xtts_available() -> bool:
    """Attempt to import TTS.api and cache the result. Never raises."""
    global xtts_available, xtts_error
    if xtts_available is not None:
        return xtts_available
    try:
        from TTS.api import TTS  # noqa: F401
        xtts_available = True
        xtts_error = None
        logger.info("XTTS-v2 (TTS.api) import probe: available")
    except Exception as e:
        xtts_available = False
        xtts_error = str(e)
        logger.warning(f"XTTS-v2 (TTS.api) import probe: unavailable — {e}")
    return xtts_available


def get_xtts_model():
    """Lazy-load XTTS-v2. Raises on failure."""
    global xtts_model, xtts_available, xtts_error
    if xtts_model is not None:
        return xtts_model
    try:
        from TTS.api import TTS
        device = "cuda" if torch.cuda.is_available() else "cpu"
        logger.info(f"Loading XTTS-v2 model on device '{device}'")
        xtts_model = TTS(model_name="tts_models/multilingual/multi-dataset/xtts_v2").to(device)
        xtts_available = True
        xtts_error = None
        logger.info("XTTS-v2 model loaded successfully")
        return xtts_model
    except Exception as e:
        xtts_available = False
        xtts_error = str(e)
        logger.error(f"Failed to load XTTS-v2 model: {e}", exc_info=True)
        raise


def _wav_to_mp3(wav_path: Path, mp3_path: Path) -> None:
    """Convert WAV to MP3 via ffmpeg. Raises RuntimeError on failure."""
    import subprocess
    cmd = [
        "ffmpeg", "-y", "-i", str(wav_path),
        "-codec:a", "libmp3lame", "-q:a", "3",
        str(mp3_path),
    ]
    try:
        subprocess.check_output(cmd, stderr=subprocess.STDOUT)
    except subprocess.CalledProcessError as e:
        raise RuntimeError(f"ffmpeg conversion failed: {e.output.decode(errors='replace')}")

# ============================================================================
# Capabilities / Health
# ============================================================================

def get_stage_capabilities() -> CapabilitiesResponse:
    xtts_ready = _probe_xtts_available()

    if xtts_ready:
        tts_detail = "edge-tts (cloud) available; xtts-v2 (GPU) available"
    else:
        tts_detail = f"edge-tts (cloud) available; xtts-v2 unavailable ({xtts_error})"

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
            # Truthful: ready only when XTTS-v2 is importable.
            # edge-tts alone is a cloud path and does not constitute local GPU TTS readiness.
            ready=xtts_ready,
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
            cuda_version=cuda_version,
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
# Transcription
# ============================================================================

def load_whisper_model(
    model_name: str,
    cpu_compute_type: str = "int8",
    cpu_threads: Optional[int] = None,
    num_workers: int = 1,
):
    global whisper_model, whisper_model_key

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
        logger.info("Whisper model loaded successfully")

    return whisper_model

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
            f"Transcribing file: {file.filename} "
            f"(model={model}, cpu_compute={requested_cpu_compute}, "
            f"cpu_threads={requested_cpu_threads}, cpu_workers={requested_num_workers})"
        )

        whisper = load_whisper_model(model, cpu_compute_type, cpu_threads, num_workers)
        segments, info = whisper.transcribe(str(temp_audio_path), language=language)

        transcript_segments = [
            TranscriptSegment(start=seg.start, end=seg.end, text=seg.text.strip())
            for seg in segments
        ]

        logger.info(f"Transcription complete: {len(transcript_segments)} segments, language: {info.language}")
        background_tasks.add_task(lambda p=temp_audio_path: p.unlink(missing_ok=True))

        return TranscriptionResponse(
            success=True,
            language=info.language or "unknown",
            language_probability=info.language_probability or 0.0,
            segments=transcript_segments,
        )
    except Exception as e:
        logger.error(f"Transcription failed: {e}", exc_info=True)
        if temp_audio_path:
            background_tasks.add_task(lambda p=temp_audio_path: p.unlink(missing_ok=True))
        raise HTTPException(status_code=400, detail=str(e))

# ============================================================================
# Translation
# ============================================================================

def get_translator():
    global translator
    if translator is None:
        logger.info("Initializing Translator")
        translator = Translator()
    return translator

@app.post("/translate", response_model=TranslationResponse)
async def translate(
    transcript_json: str = Form(...),
    source_language: str = Form(...),
    target_language: str = Form(...),
):
    try:
        logger.info(f"Translating {source_language} -> {target_language}")
        transcript_data = json.loads(transcript_json)
        segments = transcript_data.get("segments", [])

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
                translated_text=translated_text,
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
            segments=translated_segments,
        )
    except Exception as e:
        logger.error(f"Translation failed: {e}", exc_info=True)
        raise HTTPException(status_code=400, detail=str(e))

# ============================================================================
# TTS — edge-tts (cloud/CPU path, unchanged)
# ============================================================================

@app.post("/tts", response_model=TtsResponse)
async def text_to_speech(
    text: str = Form(...),
    voice: str = Form("en-US-AriaNeural"),
    background_tasks: BackgroundTasks = BackgroundTasks(),
):
    """Generate speech via edge-tts (cloud, no GPU required)."""
    temp_audio_path = None
    try:
        logger.info(f"Generating edge-tts for voice: {voice}")
        temp_audio_path = TEMP_DIR / f"tts_{datetime.now().timestamp()}.mp3"
        communicate = edge_tts.Communicate(text, voice)
        await communicate.save(str(temp_audio_path))
        file_size = temp_audio_path.stat().st_size
        logger.info(f"edge-tts generation complete: {file_size} bytes")
        return TtsResponse(
            success=True,
            voice=voice,
            audio_path=str(temp_audio_path),
            file_size_bytes=file_size,
        )
    except Exception as e:
        logger.error(f"TTS generation failed: {e}", exc_info=True)
        if temp_audio_path:
            background_tasks.add_task(lambda p=temp_audio_path: p.unlink(missing_ok=True))
        raise HTTPException(status_code=400, detail=str(e))

# ============================================================================
# TTS — XTTS-v2 GPU paths
# ============================================================================

@app.post("/tts/xtts/references", response_model=XttsReferenceResponse)
async def register_xtts_reference(
    speaker_id: str = Form(...),
    file: UploadFile = File(...),
    transcript: Optional[str] = Form(None),
):
    """
    Register a reference audio clip for a speaker.
    The uploaded audio is saved to TEMP_DIR and keyed by speaker_id.
    Subsequent /tts/xtts/segment calls can reference this speaker_id
    without re-uploading the audio.
    """
    if not _probe_xtts_available():
        raise HTTPException(
            status_code=503,
            detail=f"XTTS-v2 is not available on this host: {xtts_error}",
        )

    reference_id = str(uuid.uuid4())
    ref_audio_path = TEMP_DIR / f"xtts_ref_{reference_id}{Path(file.filename or 'ref.wav').suffix}"

    try:
        contents = await file.read()
        ref_audio_path.write_bytes(contents)
        logger.info(
            f"Registered XTTS reference: speaker_id={speaker_id}, "
            f"reference_id={reference_id}, file={ref_audio_path.name}"
        )
        xtts_reference_store[speaker_id] = {
            "reference_id": reference_id,
            "audio_path": str(ref_audio_path),
            "transcript": transcript,
        }
        return XttsReferenceResponse(success=True, reference_id=reference_id)
    except Exception as e:
        ref_audio_path.unlink(missing_ok=True)
        logger.error(f"Failed to register XTTS reference: {e}", exc_info=True)
        raise HTTPException(status_code=400, detail=str(e))


@app.post("/tts/xtts/segment", response_model=TtsResponse)
async def xtts_segment(
    text: str = Form(...),
    speaker_id: Optional[str] = Form(None),
    reference_file: Optional[UploadFile] = File(None),
    reference_transcript: Optional[str] = Form(None),
    background_tasks: BackgroundTasks = BackgroundTasks(),
):
    """
    Synthesize a single segment using XTTS-v2 voice cloning.

    Reference audio is resolved in this priority order:
      1. Inline reference_file upload (takes precedence, allows one-shot use)
      2. Previously registered speaker_id via /tts/xtts/references

    Returns an MP3 artifact at audio_path (same contract as /tts).
    """
    if not _probe_xtts_available():
        raise HTTPException(
            status_code=503,
            detail=f"XTTS-v2 is not available on this host: {xtts_error}",
        )

    # Resolve reference audio
    ref_audio_path: Optional[Path] = None
    ref_transcript: Optional[str] = reference_transcript
    inline_ref_path: Optional[Path] = None  # track for cleanup

    if reference_file is not None:
        # Inline upload — write to temp, clean up after synthesis
        suffix = Path(reference_file.filename or "ref.wav").suffix or ".wav"
        inline_ref_path = TEMP_DIR / f"xtts_inline_{uuid.uuid4()}{suffix}"
        contents = await reference_file.read()
        inline_ref_path.write_bytes(contents)
        ref_audio_path = inline_ref_path
        logger.info(f"XTTS segment using inline reference: {inline_ref_path.name}")
    elif speaker_id and speaker_id in xtts_reference_store:
        stored = xtts_reference_store[speaker_id]
        ref_audio_path = Path(stored["audio_path"])
        if not ref_audio_path.exists():
            raise HTTPException(
                status_code=404,
                detail=f"Registered reference audio for speaker '{speaker_id}' no longer exists. "
                       f"Re-register via /tts/xtts/references.",
            )
        ref_transcript = ref_transcript or stored.get("transcript")
        logger.info(f"XTTS segment using registered reference for speaker_id={speaker_id}")
    else:
        raise HTTPException(
            status_code=400,
            detail="Provide either reference_file or a pre-registered speaker_id.",
        )

    wav_path: Optional[Path] = None
    mp3_path: Optional[Path] = None

    try:
        tts = get_xtts_model()
        stamp = datetime.now().timestamp()
        wav_path = TEMP_DIR / f"xtts_{stamp}.wav"
        mp3_path = TEMP_DIR / f"xtts_{stamp}.mp3"

        language = "en"  # default; callers may extend this via a form param later

        logger.info(
            f"Synthesizing XTTS-v2 segment: speaker_id={speaker_id or 'inline'}, "
            f"text_len={len(text)}, ref={ref_audio_path.name}"
        )

        # XTTS synthesis is CPU/GPU-bound — run in thread pool to avoid blocking the event loop
        loop = asyncio.get_event_loop()
        await loop.run_in_executor(
            None,
            lambda: tts.tts_to_file(
                text=text,
                speaker_wav=str(ref_audio_path),
                language=language,
                file_path=str(wav_path),
            ),
        )

        _wav_to_mp3(wav_path, mp3_path)
        file_size = mp3_path.stat().st_size
        logger.info(f"XTTS-v2 synthesis complete: {file_size} bytes")

        # Schedule cleanup of WAV and any inline reference
        background_tasks.add_task(lambda p=wav_path: p.unlink(missing_ok=True))
        if inline_ref_path is not None:
            background_tasks.add_task(lambda p=inline_ref_path: p.unlink(missing_ok=True))

        return TtsResponse(
            success=True,
            voice="xtts-v2",
            audio_path=str(mp3_path),
            file_size_bytes=file_size,
        )
    except Exception as e:
        logger.error(f"XTTS-v2 synthesis failed: {e}", exc_info=True)
        if wav_path:
            background_tasks.add_task(lambda p=wav_path: p.unlink(missing_ok=True))
        if mp3_path:
            background_tasks.add_task(lambda p=mp3_path: p.unlink(missing_ok=True))
        if inline_ref_path:
            background_tasks.add_task(lambda p=inline_ref_path: p.unlink(missing_ok=True))
        raise HTTPException(status_code=500, detail=str(e))

# ============================================================================
# TTS audio retrieval (shared by edge-tts and XTTS paths)
# ============================================================================

@app.get("/tts/audio/{filename}")
async def get_tts_audio(filename: str, background_tasks: BackgroundTasks):
    """Retrieve a generated TTS audio file by its basename."""
    try:
        file_path = TEMP_DIR / filename
        if not file_path.exists():
            raise HTTPException(status_code=404, detail="Audio file not found")
        background_tasks.add_task(lambda p=file_path: p.unlink(missing_ok=True))
        return FileResponse(file_path, media_type="audio/mpeg")
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Failed to retrieve TTS audio: {e}")
        raise HTTPException(status_code=400, detail=str(e))

# ============================================================================
# Startup / Shutdown
# ============================================================================

@app.on_event("startup")
async def startup_event():
    logger.info("Inference service starting up")
    cuda_available = torch.cuda.is_available()
    logger.info(f"CUDA available: {cuda_available}")
    if cuda_available:
        logger.info(f"CUDA device: {torch.cuda.get_device_name(0)}")
        logger.info(f"CUDA version: {torch.version.cuda}")
    # Eagerly probe XTTS so /capabilities is accurate from the first request
    _probe_xtts_available()

@app.on_event("shutdown")
async def shutdown_event():
    logger.info("Inference service shutting down")
    if torch.cuda.is_available():
        torch.cuda.empty_cache()

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8000)

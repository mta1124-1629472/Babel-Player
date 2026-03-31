import os
import json
import logging
import tempfile
from pathlib import Path
from typing import Optional
from datetime import datetime

import torch
from fastapi import FastAPI, File, UploadFile, HTTPException, BackgroundTasks
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
translator = None

# Temporary directory for artifacts
TEMP_DIR = Path(tempfile.gettempdir()) / "babel_inference"
TEMP_DIR.mkdir(exist_ok=True)

# ============================================================================
# Pydantic Models
# ============================================================================

class TranscriptionRequest(BaseModel):
    model: str = "base"
    language: Optional[str] = None

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

# ============================================================================
# Health Check
# ============================================================================

@app.get("/health", response_model=HealthResponse)
async def health_check():
    """Health check endpoint with GPU info."""
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

# ============================================================================
# Transcription Endpoint
# ============================================================================

def load_whisper_model(model_name: str):
    """Load Whisper model with GPU support."""
    global whisper_model
    
    if whisper_model is None or whisper_model.model.device != ("cuda" if torch.cuda.is_available() else "cpu"):
        device = "cuda" if torch.cuda.is_available() else "cpu"
        compute_type = "float16" if device == "cuda" else "int8"
        logger.info(f"Loading Whisper model '{model_name}' on device '{device}' with compute_type '{compute_type}'")
        whisper_model = WhisperModel(model_name, device=device, compute_type=compute_type)
        logger.info(f"Whisper model loaded successfully")
    
    return whisper_model

@app.post("/transcribe", response_model=TranscriptionResponse)
async def transcribe(
    file: UploadFile = File(...),
    model: str = "base",
    language: Optional[str] = None,
    background_tasks: BackgroundTasks = BackgroundTasks()
):
    """Transcribe audio file using Whisper."""
    temp_audio_path = None
    try:
        # Save uploaded file
        temp_audio_path = TEMP_DIR / f"audio_{datetime.now().timestamp()}.wav"
        contents = await file.read()
        temp_audio_path.write_bytes(contents)
        
        logger.info(f"Transcribing file: {file.filename} (model: {model})")
        
        # Load model
        whisper = load_whisper_model(model)
        
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
    transcript_json: str,
    source_language: str,
    target_language: str
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
        
        for seg in segments:
            text = seg.get("text", "")
            translated_text = ""
            
            if text:
                try:
                    result = trans.translate(text, src_lang=source_language, dest_lang=target_language)
                    translated_text = result.text if result else ""
                except Exception as e:
                    logger.warning(f"Failed to translate segment: {e}")
                    translated_text = text  # Fallback to original
            
            translated_segments.append(TranslatedSegment(
                start=seg.get("start", 0),
                end=seg.get("end", 0),
                text=text,
                translated_text=translated_text
            ))
        
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

@app.post("/tts", response_model=TtsResponse)
async def text_to_speech(
    text: str,
    voice: str = "en-US-AriaNeural",
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

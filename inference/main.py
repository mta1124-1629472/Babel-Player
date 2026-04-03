import argparse
import json
import logging
import shutil
import subprocess
import sys
import tempfile
from collections.abc import Iterable
from pathlib import Path
from typing import Optional
from datetime import datetime
from uuid import uuid4

import torch
from fastapi import FastAPI, File, Form, UploadFile, HTTPException, BackgroundTasks
from fastapi.responses import FileResponse
from pydantic import BaseModel
from faster_whisper import WhisperModel

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
nllb_tokenizer = None
nllb_model = None
nllb_model_key = None
xtts_model = None
xtts_model_key = None
xtts_reference_registry: dict[str, dict[str, str | None]] = {}

HOST_DEVICE = "cuda" if torch.cuda.is_available() else "cpu"
HOST_COMPUTE_TYPE = "float16" if HOST_DEVICE == "cuda" else "int8"
XTTS_MODEL_NAME = "tts_models/multilingual/multi-dataset/xtts_v2"

# Temporary directory for artifacts
TEMP_DIR = Path(tempfile.gettempdir()) / "babel_inference"
TEMP_DIR.mkdir(exist_ok=True)

FLORES = {
    "en": "eng_Latn",
    "es": "spa_Latn",
    "fr": "fra_Latn",
    "de": "deu_Latn",
    "it": "ita_Latn",
    "pt": "por_Latn",
    "ru": "rus_Cyrl",
    "zh": "zho_Hans",
    "ja": "jpn_Jpan",
    "ko": "kor_Hang",
    "ar": "arb_Arab",
    "hi": "hin_Deva",
    "nl": "nld_Latn",
    "pl": "pol_Latn",
    "sv": "swe_Latn",
    "tr": "tur_Latn",
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
    except subprocess.CalledProcessError as e:
        raise RuntimeError(f"ffmpeg conversion failed: {e.output.decode(errors='replace')}")

def _temp_artifact_path(prefix: str, suffix: str) -> Path:
    return TEMP_DIR / f"{prefix}_{datetime.now().timestamp()}_{uuid4().hex}{suffix}"

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
    if HOST_COMPUTE_TYPE != "float16":
        return False

    try:
        from TTS.api import TTS  # noqa: F401
        return True
    except Exception as exc:
        logger.warning(f"XTTS capability probe failed: {exc}")
        return False

def _xtts_capability_detail() -> str:
    if HOST_DEVICE != "cuda":
        return "CUDA is unavailable; managed GPU XTTS is not ready"
    if HOST_COMPUTE_TYPE != "float16":
        return f"XTTS requires compute_type=float16 on CUDA; current host compute_type={HOST_COMPUTE_TYPE}"
    return "XTTS dependencies are unavailable"

# ============================================================================
# Capabilities / Health
# ============================================================================

def get_stage_capabilities() -> CapabilitiesResponse:
    transcription_ready = HOST_DEVICE == "cuda"
    translation_ready = _probe_nllb_available()
    tts_ready = _probe_xtts_available()

    return CapabilitiesResponse(
        transcription=StageCapability(
            ready=transcription_ready,
            detail=(
                f"faster-whisper ready on {HOST_DEVICE} with compute_type={HOST_COMPUTE_TYPE}"
                if transcription_ready
                else "CUDA is unavailable; managed GPU transcription is not ready"
            ),
        ),
        translation=StageCapability(
            ready=translation_ready,
            detail=(
                f"NLLB-200 ready on {HOST_DEVICE} with compute_type={HOST_COMPUTE_TYPE}"
                if translation_ready
                else _nllb_capability_detail()
            ),
        ),
        tts=StageCapability(
            ready=tts_ready,
            detail=(
                f"XTTS v2 ready on {HOST_DEVICE} with compute_type={HOST_COMPUTE_TYPE}; reference audio required"
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

    device = HOST_DEVICE
    compute_type = HOST_COMPUTE_TYPE
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


def _probe_nllb_available() -> bool:
    if HOST_DEVICE != "cuda":
        return False

    try:
        from transformers import AutoModelForSeq2SeqLM, AutoTokenizer  # noqa: F401
        import sentencepiece  # noqa: F401
        return HOST_COMPUTE_TYPE == "float16"
    except Exception as exc:
        logger.warning(f"NLLB capability probe failed: {exc}")
        return False


def _nllb_capability_detail() -> str:
    if HOST_DEVICE != "cuda":
        return "CUDA is unavailable; managed GPU translation is not ready"
    if HOST_COMPUTE_TYPE != "float16":
        return f"NLLB-200 requires compute_type=float16 on CUDA; current host compute_type={HOST_COMPUTE_TYPE}"
    return "NLLB-200 dependencies are unavailable"


def load_nllb_model(model_name: str):
    global nllb_tokenizer, nllb_model, nllb_model_key

    if HOST_DEVICE != "cuda":
        raise RuntimeError("NLLB-200 GPU host requires CUDA.")
    if HOST_COMPUTE_TYPE != "float16":
        raise RuntimeError(
            f"NLLB-200 does not support compute_type={HOST_COMPUTE_TYPE} in the managed host. Use float16."
        )

    desired_key = (model_name, HOST_DEVICE, HOST_COMPUTE_TYPE)
    if nllb_model is None or nllb_model_key != desired_key:
        from transformers import AutoModelForSeq2SeqLM, AutoTokenizer

        model_id = f"facebook/{model_name}"
        logger.info(
            f"Loading NLLB model '{model_id}' on device '{HOST_DEVICE}' with compute_type '{HOST_COMPUTE_TYPE}'"
        )
        nllb_tokenizer = AutoTokenizer.from_pretrained(model_id)
        nllb_model = AutoModelForSeq2SeqLM.from_pretrained(
            model_id,
            torch_dtype=torch.float16,
        ).to(HOST_DEVICE)
        nllb_model_key = desired_key
        logger.info("NLLB model loaded successfully")

    return nllb_tokenizer, nllb_model


def load_xtts_model(model_name: Optional[str]):
    global xtts_model, xtts_model_key

    if HOST_DEVICE != "cuda":
        raise RuntimeError("XTTS GPU host requires CUDA.")
    if HOST_COMPUTE_TYPE != "float16":
        raise RuntimeError(
            f"XTTS does not support compute_type={HOST_COMPUTE_TYPE} in the managed GPU host. Use float16."
        )

    resolved_model = _normalize_xtts_model(model_name)
    desired_key = (resolved_model, HOST_DEVICE, HOST_COMPUTE_TYPE)
    if xtts_model is None or xtts_model_key != desired_key:
        from TTS.api import TTS

        logger.info(
            f"Loading XTTS model '{resolved_model}' on device '{HOST_DEVICE}' with compute_type '{HOST_COMPUTE_TYPE}'"
        )
        xtts_model = TTS(resolved_model).to(HOST_DEVICE)
        xtts_model_key = desired_key
        logger.info("XTTS model loaded successfully")

    return xtts_model


def translate_with_nllb(model_name: str, source_language: str, target_language: str, text: str) -> str:
    if not text.strip():
        return ""

    tokenizer, model = load_nllb_model(model_name)
    src_flores = FLORES.get(source_language, source_language)
    tgt_flores = FLORES.get(target_language, target_language)
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
        logger.info(f"Translating {source_language} -> {target_language}")
        transcript_data = json.loads(transcript_json)
        segments = transcript_data.get("segments", [])

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

        logger.info(f"Registered XTTS reference '{reference_id}' for speaker '{speaker_id}'")
        return XttsReferenceResponse(success=True, reference_id=reference_id)
    except Exception as e:
        logger.error(f"Failed to register XTTS reference: {e}", exc_info=True)
        raise HTTPException(status_code=400, detail=str(e))

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

        xtts = load_xtts_model(model)
        normalized_language = _normalize_xtts_language(language)
        temp_wav_path = _temp_artifact_path("xtts_segment", ".wav")
        temp_mp3_path = _temp_artifact_path("xtts_segment", ".mp3")

        logger.info(
            f"Generating XTTS segment: speaker={speaker_id or '<none>'}, model={_normalize_xtts_voice_label(model)}, "
            f"language={normalized_language}, reference_id={reference_id or '<inline>'}"
        )

        xtts.tts_to_file(
            text=text,
            speaker_wav=str(resolved_reference_path),
            language=normalized_language,
            file_path=str(temp_wav_path),
        )
        _wav_to_mp3(temp_wav_path, temp_mp3_path)

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
    except Exception as e:
        logger.error(f"XTTS segment synthesis failed: {e}", exc_info=True)
        if temp_reference_path is not None:
            background_tasks.add_task(lambda p=temp_reference_path: p.unlink(missing_ok=True))
        if temp_wav_path is not None:
            background_tasks.add_task(lambda p=temp_wav_path: p.unlink(missing_ok=True))
        if temp_mp3_path is not None:
            background_tasks.add_task(lambda p=temp_mp3_path: p.unlink(missing_ok=True))
        raise HTTPException(status_code=400, detail=str(e))

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
    logger.info(f"Host device: {HOST_DEVICE}")
    logger.info(f"Host compute_type: {HOST_COMPUTE_TYPE}")
    logger.info(f"CUDA available: {HOST_DEVICE == 'cuda'}")
    if HOST_DEVICE == "cuda":
        logger.info(f"CUDA device: {torch.cuda.get_device_name(0)}")
        logger.info(f"CUDA version: {torch.version.cuda}")

@app.on_event("shutdown")
async def shutdown_event():
    logger.info("Inference service shutting down")
    if torch.cuda.is_available():
        torch.cuda.empty_cache()

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

    if args.require_cuda and HOST_DEVICE != "cuda":
        raise RuntimeError(
            "Managed GPU host requested CUDA, but torch.cuda.is_available() returned false. "
            "Check the installed Torch CUDA build, NVIDIA driver, and whether the managed runtime can access the GPU."
        )
    if HOST_DEVICE == "cuda" and HOST_COMPUTE_TYPE != "float16":
        raise RuntimeError(
            f"CUDA host requires --compute-type float16. Received '{HOST_COMPUTE_TYPE}'."
        )
    if HOST_DEVICE != "cuda" and HOST_COMPUTE_TYPE != "int8":
        raise RuntimeError(
            f"CPU host requires --compute-type int8 in phase 1. Received '{HOST_COMPUTE_TYPE}'."
        )

    uvicorn.run(app, host=args.host, port=args.port)

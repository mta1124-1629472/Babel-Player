# Dev/local build for the external inference service consumed by the desktop app.
FROM nvidia/cuda:12.6.3-cudnn-runtime-ubuntu22.04

ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    PIP_NO_CACHE_DIR=1 \
    CUDA_HOME=/usr/local/cuda \
    PATH=/usr/local/cuda/bin:$PATH \
    LD_LIBRARY_PATH=/usr/local/cuda/lib64:$LD_LIBRARY_PATH

RUN apt-get update && apt-get install -y --no-install-recommends \
    python3.10 \
    python3-pip \
    python3-dev \
    build-essential \
    ffmpeg \
    libsndfile1 \
    && rm -rf /var/lib/apt/lists/*

RUN pip install --upgrade pip setuptools wheel

WORKDIR /app

COPY gpu-constraints.txt ./

# Step 1: PyTorch installed from pre-downloaded wheels to avoid large SSL downloads
# inside Docker's network stack. Download the .whl files first on the host:
#   curl -L -o inference/torch-2.8.0+cu128.whl "https://download.pytorch.org/whl/cu128/torch-2.8.0%2Bcu128-cp310-cp310-manylinux_2_28_x86_64.whl"
#   curl -L -o inference/torchaudio-2.8.0+cu128.whl "https://download.pytorch.org/whl/cu128/torchaudio-2.8.0%2Bcu128-cp310-cp310-manylinux_2_28_x86_64.whl"
#   curl -L -o inference/torchvision-0.23.0+cu128.whl "https://download.pytorch.org/whl/cu128/torchvision-0.23.0%2Bcu128-cp310-cp310-manylinux_2_28_x86_64.whl"
COPY torch-2.8.0+cu128.whl torchaudio-2.8.0+cu128.whl torchvision-0.23.0+cu128.whl ./
RUN pip install --no-cache-dir \
    torch-2.8.0+cu128.whl \
    torchaudio-2.8.0+cu128.whl \
    torchvision-0.23.0+cu128.whl

# Step 2: Everything else from PyPI, pinned via constraints.
RUN pip install --no-cache-dir -c gpu-constraints.txt \
    fastapi==0.115.4 \
    uvicorn==0.30.6 \
    "pydantic>=2.10.6,<3" \
    python-multipart==0.0.9 \
    faster-whisper==1.2.1 \
    "numpy>=1.22.0,<2.0" \
    scipy==1.14.1 \
    transformers==4.57.3 \
    sentencepiece==0.2.0 \
    safetensors==0.6.2 \
    accelerate==1.12.0 \
    pydub==0.25.1 \
    "qwen-tts>=0.1.1" \
    "soundfile>=0.12.1" \
    "hf_xet>=0.1.0" \
    "nemo-toolkit[asr]==2.7.2"

COPY main.py ./
COPY workers/ ./workers/

HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD python3 -c "import sys; import urllib.request; urllib.request.urlopen('http://localhost:8000/health/live', timeout=5); sys.exit(0)" || exit 1

EXPOSE 8000

CMD ["python3", "-m", "uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000"]

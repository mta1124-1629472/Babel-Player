# Dev/local build for the external inference service consumed by the desktop app.
FROM nvidia/cuda:12.4.1-cudnn-runtime-ubuntu22.04

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

COPY requirements.txt .
RUN pip install --no-cache-dir \
    --index-url https://download.pytorch.org/whl/cu124 \
    torch==2.5.1 \
    torchaudio==2.5.1 && \
    pip install --no-cache-dir -r requirements.txt

COPY main.py .

HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD python3 -c "import sys; import urllib.request; urllib.request.urlopen('http://localhost:8000/health/live', timeout=5); sys.exit(0)" || exit 1

EXPOSE 8000

CMD ["python3", "-m", "uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000"]

# WSL PyTorch GPU - Smoke Note

## Metadata
- Type: `environment`
- Name: `WSL PyTorch GPU`
- Date: `2026-03-28`
- Status: `complete`

## What Was Verified
- `nvidia-smi` ran successfully in WSL.
- WSL detected the RTX 5070.
- `ffmpeg` is available.
- `uv` is available.
- PyTorch installed successfully in a fresh environment.
- `torch.cuda.is_available()` returned `True`.
- PyTorch resolved the device as `NVIDIA GeForce RTX 5070`.

## What Was Not Verified
- No app-level inference service was exercised in this smoke note.
- No Babel Player transcription, translation, or TTS workflow was exercised in this smoke note.
- No containerized or NVIDIA-managed serving path was exercised in this smoke note.

## Evidence

### Commands Run
```text
nvidia-smi
ffmpeg -version
python3 --version
uv python list
uv venv
uv pip install torch torchvision torchaudio
python -c "import torch; print('torch', torch.__version__); print('cuda?', torch.cuda.is_available()); print('device', torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'none')"
# pyright: reportMissingImports=false
"""
Tests for _safe_separation_upload_suffix added in inference/main.py.

The function strips file paths to a safe extension for Windows temp-file paths,
falling back to ".wav" for anything unrecognised.
"""

import sys
import types
import importlib
import pathlib
import pytest


# ---------------------------------------------------------------------------
# Minimal stub so that `import main` succeeds without the full FastAPI stack
# ---------------------------------------------------------------------------

def _build_main_module():
    """
    Import only the _safe_separation_upload_suffix symbol from inference/main.py
    by patching out every heavy dependency before the import.
    """
    heavy = [
        "fastapi",
        "fastapi.middleware",
        "fastapi.middleware.cors",
        "fastapi.responses",
        "fastapi.staticfiles",
        "pydantic",
        "uvicorn",
        "torch",
        "ctranslate2",
        "whisper",
        "faster_whisper",
        "pyannote",
        "pyannote.audio",
        "pyannote.audio.pipelines",
        "speechbrain",
        "torchaudio",
        "soundfile",
        "numpy",
        "omegaconf",
        "wespeaker",
        "nemo",
        "nemo.collections",
        "nemo.collections.asr",
        "nemo.collections.asr.models",
        "nemo.core",
    ]
    for name in heavy:
        if name not in sys.modules:
            sys.modules[name] = types.ModuleType(name)

    # Minimal FastAPI stubs so module-level decorators don't crash
    fastapi_mod = sys.modules.setdefault("fastapi", types.ModuleType("fastapi"))

    class _FakeApp:
        def post(self, *a, **kw):
            return lambda f: f
        def get(self, *a, **kw):
            return lambda f: f
        def on_event(self, *a, **kw):
            return lambda f: f
        def add_middleware(self, *a, **kw):
            pass

    class _FakeHTTPException(Exception):
        def __init__(self, status_code=500, detail=""):
            self.status_code = status_code
            self.detail = detail

    class _FakeUploadFile:
        pass

    class _FakeFile:
        pass

    class _FakeForm:
        def __call__(self, *a, **kw):
            return None

    class _FakeBackgroundTasks:
        pass

    for attr, val in [
        ("FastAPI", lambda **kw: _FakeApp()),
        ("HTTPException", _FakeHTTPException),
        ("UploadFile", _FakeUploadFile),
        ("File", _FakeFile()),
        ("Form", _FakeForm()),
        ("BackgroundTasks", _FakeBackgroundTasks),
        ("Request", object),
    ]:
        setattr(fastapi_mod, attr, val)

    # fastapi.responses stub
    responses_mod = sys.modules.setdefault("fastapi.responses", types.ModuleType("fastapi.responses"))
    responses_mod.FileResponse = object  # type: ignore[attr-defined]
    responses_mod.JSONResponse = object  # type: ignore[attr-defined]

    # fastapi.staticfiles stub
    static_mod = sys.modules.setdefault("fastapi.staticfiles", types.ModuleType("fastapi.staticfiles"))
    static_mod.StaticFiles = object  # type: ignore[attr-defined]

    # fastapi.middleware.cors stub
    cors_mod = sys.modules.setdefault("fastapi.middleware.cors", types.ModuleType("fastapi.middleware.cors"))
    cors_mod.CORSMiddleware = object  # type: ignore[attr-defined]

    # pydantic stub
    pydantic_mod = sys.modules.setdefault("pydantic", types.ModuleType("pydantic"))

    class _FakeBaseModel:
        def __init_subclass__(cls, **kwargs):
            pass

    pydantic_mod.BaseModel = _FakeBaseModel  # type: ignore[attr-defined]
    pydantic_mod.Field = lambda *a, **kw: None  # type: ignore[attr-defined]

    # Insert inference/ on sys.path so `import main` resolves
    inference_dir = str(pathlib.Path(__file__).parent.parent)
    if inference_dir not in sys.path:
        sys.path.insert(0, inference_dir)

    # Force a fresh import each time this helper is called in the same process
    if "main" in sys.modules:
        return sys.modules["main"]

    import importlib as _il
    mod = _il.import_module("main")
    return mod


@pytest.fixture(scope="module")
def main_module():
    return _build_main_module()


@pytest.fixture(scope="module")
def fn(main_module):
    return main_module._safe_separation_upload_suffix


# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------

class TestSafeSeparationUploadSuffix:
    """Tests for _safe_separation_upload_suffix (added in this PR)."""

    # -- allowed extensions ------------------------------------------------

    @pytest.mark.parametrize("filename,expected", [
        ("track.wav",  ".wav"),
        ("track.mp3",  ".mp3"),
        ("track.flac", ".flac"),
        ("track.m4a",  ".m4a"),
        ("track.mp4",  ".mp4"),
        ("track.mkv",  ".mkv"),
        ("track.webm", ".webm"),
        ("track.ogg",  ".ogg"),
        ("track.avi",  ".avi"),
        ("track.mov",  ".mov"),
        ("track.wmv",  ".wmv"),
    ])
    def test_allowed_extension_returned_as_is(self, fn, filename, expected):
        assert fn(filename) == expected

    # -- case normalisation (extensions are lowercased) --------------------

    @pytest.mark.parametrize("filename", [
        "track.WAV",
        "track.Mp3",
        "track.FLAC",
        "track.MP4",
    ])
    def test_uppercase_extension_is_accepted(self, fn, filename):
        ext = pathlib.Path(filename).suffix.lower()
        assert fn(filename) == ext

    # -- disallowed / unknown extensions fall back to .wav -----------------

    @pytest.mark.parametrize("filename", [
        "track.aac",
        "track.opus",
        "track.wma",
        "track.ra",
        "track.xyz",
        "track.exe",
        "track.txt",
    ])
    def test_unknown_extension_returns_wav(self, fn, filename):
        assert fn(filename) == ".wav"

    # -- edge cases --------------------------------------------------------

    def test_empty_string_returns_wav(self, fn):
        assert fn("") == ".wav"

    def test_no_extension_returns_wav(self, fn):
        assert fn("no_extension") == ".wav"

    def test_dotfile_no_extension_returns_wav(self, fn):
        # ".hidden" → suffix is ".hidden", not in allowed set
        assert fn(".hidden") == ".wav"

    def test_path_with_directory_component_uses_final_extension(self, fn):
        # Only the suffix of the basename matters
        assert fn("/some/path/to/file.mp3") == ".mp3"

    def test_multiple_dots_uses_final_extension(self, fn):
        assert fn("archive.tar.gz") == ".wav"  # .gz not in allowed set

    def test_mp3_with_dots_in_name(self, fn):
        assert fn("my.song.v2.mp3") == ".mp3"

    # -- boundary / regression --------------------------------------------

    def test_returns_string_type(self, fn):
        result = fn("file.wav")
        assert isinstance(result, str)

    def test_fallback_is_dot_wav_not_wav(self, fn):
        """Fallback must include the leading dot."""
        result = fn("file.unknown")
        assert result.startswith(".")

    def test_m4a_is_allowed(self, fn):
        """m4a is a container (needs ffmpeg) but is explicitly in the allowed set."""
        assert fn("audio.m4a") == ".m4a"
# pyright: reportMissingImports=false
"""
Tests for the new code added to inference/workers/vocal_separator_worker.py in this PR:

  • _normalize_input_for_separator — path safety / ffmpeg decode logic
  • run_vocal_separation — try/finally temp-file cleanup
"""

import shutil
import subprocess
import sys
import types
import pathlib
from pathlib import Path
from typing import Optional
from unittest.mock import MagicMock, patch, call

import pytest

# ---------------------------------------------------------------------------
# Import the worker module without its heavyweight optional dependencies
# ---------------------------------------------------------------------------

def _import_worker():
    """
    Return the vocal_separator_worker module, stubbing out audio_separator so
    that the module-level code that sets SEPARATOR_MODEL_DIR can run without
    the real package installed.
    """
    inference_dir = str(pathlib.Path(__file__).parent.parent)
    if inference_dir not in sys.path:
        sys.path.insert(0, inference_dir)

    # Stub audio_separator only when not already present
    if "audio_separator" not in sys.modules:
        pkg = types.ModuleType("audio_separator")
        sep_sub = types.ModuleType("audio_separator.separator")

        class _FakeSeparator:
            pass

        sep_sub.Separator = _FakeSeparator  # type: ignore[attr-defined]
        pkg.separator = sep_sub  # type: ignore[attr-defined]
        sys.modules["audio_separator"] = pkg
        sys.modules["audio_separator.separator"] = sep_sub

    import importlib
    mod_name = "workers.vocal_separator_worker"
    if mod_name in sys.modules:
        return sys.modules[mod_name]
    return importlib.import_module(mod_name)


@pytest.fixture(scope="module")
def worker():
    return _import_worker()


@pytest.fixture(scope="module")
def normalize_fn(worker):
    return worker._normalize_input_for_separator


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _make_file(directory: Path, name: str) -> Path:
    """Create an empty file at directory/name and return its Path."""
    p = directory / name
    p.write_bytes(b"")
    return p


# ---------------------------------------------------------------------------
# _normalize_input_for_separator — safe-path audio formats (no ffmpeg needed)
# ---------------------------------------------------------------------------

class TestNormalizeInputSafeAudioFormats:
    """WAV/FLAC/MP3/OGG files with no spaces in their resolved path are returned as-is."""

    @pytest.mark.parametrize("suffix", [".wav", ".flac", ".mp3", ".ogg"])
    def test_safe_path_returns_original_no_temp(self, normalize_fn, tmp_path, suffix):
        src = _make_file(tmp_path, f"track{suffix}")
        result_path, temp_file = normalize_fn(src, tmp_path / "work")
        assert result_path == src.resolve()
        assert temp_file is None

    def test_work_dir_is_created_if_missing(self, normalize_fn, tmp_path):
        src = _make_file(tmp_path, "track.wav")
        work = tmp_path / "new_work_dir"
        assert not work.exists()
        normalize_fn(src, work)
        assert work.exists()


class TestNormalizeInputUnsafePathCopy:
    """Audio files with spaces in path are copied to a safe work path."""

    @pytest.mark.parametrize("suffix", [".wav", ".flac", ".mp3", ".ogg"])
    def test_space_in_path_triggers_copy(self, normalize_fn, tmp_path, suffix):
        # Create a directory whose resolved path contains a space
        spaced = tmp_path / "my files"
        spaced.mkdir()
        src = _make_file(spaced, f"track{suffix}")
        work = tmp_path / "work"

        result_path, temp_file = normalize_fn(src, work)

        # A new file must have been created in work_dir
        assert result_path != src.resolve()
        assert result_path.parent.resolve() == work.resolve()
        # Instead, patch str(resolved) inside the function by controlling _MAX_INPUT_PATH_LEN

    def test_wav_copy_has_wav_extension(self, normalize_fn, tmp_path):
        spaced = tmp_path / "path with space"
        spaced.mkdir()
        src = _make_file(spaced, "audio.wav")
        work = tmp_path / "workw"
        result_path, _ = normalize_fn(src, work)
        assert result_path.suffix == ".wav"

    def test_path_too_long_triggers_copy(self, normalize_fn, tmp_path, monkeypatch):
        """Paths longer than _MAX_INPUT_PATH_LEN (220) should also be copied."""
        src = _make_file(tmp_path, "track.wav")
        # Monkey-patch the resolved str to look very long
        long_str = "/tmp/" + "a" * 221 + "/track.wav"

        orig_resolve = Path.resolve

        def _fake_resolve(self, strict=False):
            real = orig_resolve(self, strict=False)
            # Only fake the source file's resolution
            if real == src.resolve(strict=False):
                p = Path(long_str)
                # We still want the file to resolve to the real one for copy purposes,
                # so we patch str() indirectly via monkeypatching unsafe_path check.
                return real
            return real

        # Instead, patch str(resolved) inside the function by controlling _MAX_INPUT_PATH_LEN
        import workers.vocal_separator_worker as wmod
        original = wmod._MAX_INPUT_PATH_LEN
        try:
            # Make threshold tiny so current path is "too long"
            wmod._MAX_INPUT_PATH_LEN = 1
            work = tmp_path / "worklong"
            result_path, temp_file = normalize_fn(src, work)
            assert temp_file is not None
            assert result_path != src.resolve()
        finally:
            wmod._MAX_INPUT_PATH_LEN = original


# ---------------------------------------------------------------------------
# _normalize_input_for_separator — video / container formats need ffmpeg
# ---------------------------------------------------------------------------

class TestNormalizeInputVideoFormats:
    """Video-like files and .m4a must be decoded to WAV via ffmpeg."""

    VIDEO_SUFFIXES = [".mp4", ".mkv", ".webm", ".avi", ".mov", ".mpg", ".mpeg", ".wmv"]

    @pytest.mark.parametrize("suffix", VIDEO_SUFFIXES + [".m4a"])
    def test_video_and_m4a_call_ffmpeg(self, normalize_fn, tmp_path, suffix):
        src = _make_file(tmp_path, f"video{suffix}")
        work = tmp_path / "work_vid"

        mock_proc = MagicMock()
        mock_proc.returncode = 0

        with patch("shutil.which", return_value="/usr/bin/ffmpeg"), \
             patch("subprocess.run", return_value=mock_proc) as mock_run:
            result_path, temp_file = normalize_fn(src, work)

        assert mock_run.called
        cmd = mock_run.call_args[0][0]
        assert cmd[0] == "/usr/bin/ffmpeg"
        assert "-vn" in cmd
        assert "pcm_s16le" in cmd
        # result must be a wav
        assert result_path.suffix == ".wav"
        # temp_file == result_path (caller must clean up)
        assert temp_file == result_path

    @pytest.mark.parametrize("suffix", VIDEO_SUFFIXES + [".m4a"])
    def test_video_result_is_in_work_dir(self, normalize_fn, tmp_path, suffix):
        src = _make_file(tmp_path, f"movie{suffix}")
        work = tmp_path / "work_out"

        mock_proc = MagicMock()
        mock_proc.returncode = 0

        with patch("shutil.which", return_value="/usr/bin/ffmpeg"), \
             patch("subprocess.run", return_value=mock_proc):
            result_path, _ = normalize_fn(src, work)

        assert result_path.parent.resolve() == work.resolve()


class TestNormalizeInputUnknownExtension:
    """Files with unrecognised extensions are also decoded via ffmpeg."""

    @pytest.mark.parametrize("suffix", [".aac", ".wma", ".ra", ".xyz"])
    def test_unknown_extension_uses_ffmpeg(self, normalize_fn, tmp_path, suffix):
        src = _make_file(tmp_path, f"audio{suffix}")
        work = tmp_path / "work_unk"

        mock_proc = MagicMock()
        mock_proc.returncode = 0

        with patch("shutil.which", return_value="/usr/bin/ffmpeg"), \
             patch("subprocess.run", return_value=mock_proc) as mock_run:
            result_path, temp_file = normalize_fn(src, work)

        assert mock_run.called
        assert result_path.suffix == ".wav"
        assert temp_file == result_path


class TestNormalizeInputFfmpegErrors:
    """Proper RuntimeError is raised when ffmpeg is unavailable or fails."""

    def test_ffmpeg_not_on_path_raises(self, normalize_fn, tmp_path):
        src = _make_file(tmp_path, "video.mp4")
        work = tmp_path / "work_err"

        with patch("shutil.which", return_value=None):
            with pytest.raises(RuntimeError, match="ffmpeg not found on PATH"):
                normalize_fn(src, work)

    def test_ffmpeg_nonzero_exit_raises(self, normalize_fn, tmp_path):
        src = _make_file(tmp_path, "video.mp4")
        work = tmp_path / "work_fail"

        mock_proc = MagicMock()
        mock_proc.returncode = 1
        mock_proc.stderr = "Error: something went wrong"

        with patch("shutil.which", return_value="/usr/bin/ffmpeg"), \
             patch("subprocess.run", return_value=mock_proc):
            with pytest.raises(RuntimeError, match="ffmpeg decode for vocal separation failed"):
                normalize_fn(src, work)

    def test_ffmpeg_error_includes_stderr_tail(self, normalize_fn, tmp_path):
        src = _make_file(tmp_path, "video.mkv")
        work = tmp_path / "work_stderr"

        mock_proc = MagicMock()
        mock_proc.returncode = 2
        mock_proc.stderr = "fatal: codec not found"

        with patch("shutil.which", return_value="/usr/bin/ffmpeg"), \
             patch("subprocess.run", return_value=mock_proc):
            with pytest.raises(RuntimeError, match="codec not found"):
                normalize_fn(src, work)

    def test_ffmpeg_not_found_for_unknown_extension(self, normalize_fn, tmp_path):
        src = _make_file(tmp_path, "audio.aac")
        work = tmp_path / "work_aac"

        with patch("shutil.which", return_value=None):
            with pytest.raises(RuntimeError, match="ffmpeg not found on PATH"):
                normalize_fn(src, work)


# ---------------------------------------------------------------------------
# _normalize_input_for_separator — return type contracts
# ---------------------------------------------------------------------------

class TestNormalizeInputReturnContract:
    """The function must always return (Path, Optional[Path])."""

    @pytest.mark.parametrize("suffix,use_ffmpeg", [
        (".wav", False),
        (".mp3", False),
        (".mp4", True),
        (".aac", True),
    ])
    def test_return_is_two_tuple_of_paths(self, normalize_fn, tmp_path, suffix, use_ffmpeg):
        src = _make_file(tmp_path, f"f{suffix}")
        work = tmp_path / "contract_work"

        if use_ffmpeg:
            mock_proc = MagicMock()
            mock_proc.returncode = 0
            ctx = [
                patch("shutil.which", return_value="/usr/bin/ffmpeg"),
                patch("subprocess.run", return_value=mock_proc),
            ]
        else:
            ctx = []

        if ctx:
            with ctx[0], ctx[1]:
                result = normalize_fn(src, work)
        else:
            result = normalize_fn(src, work)

        assert isinstance(result, tuple)
        assert len(result) == 2
        path_for_sep, maybe_temp = result
        assert isinstance(path_for_sep, Path)
        assert maybe_temp is None or isinstance(maybe_temp, Path)

    def test_safe_audio_second_element_is_none(self, normalize_fn, tmp_path):
        src = _make_file(tmp_path, "safe.wav")
        _, temp_file = normalize_fn(src, tmp_path / "w")
        assert temp_file is None

    def test_video_second_element_equals_first(self, normalize_fn, tmp_path):
        """For video inputs the temp file IS the work audio — both elements are the same path."""
        src = _make_file(tmp_path, "clip.mp4")
        mock_proc = MagicMock()
        mock_proc.returncode = 0
        with patch("shutil.which", return_value="/usr/bin/ffmpeg"), \
             patch("subprocess.run", return_value=mock_proc):
            result_path, temp_file = normalize_fn(src, tmp_path / "wv")
        assert temp_file == result_path


# ---------------------------------------------------------------------------
# run_vocal_separation — try/finally cleanup of temp_input
# ---------------------------------------------------------------------------

class TestRunVocalSeparationCleanup:
    """
    The PR wraps the separation body in try/finally that deletes the
    normalised temp input.  Verify cleanup occurs both on success and on error.
    """

    def _make_fake_separator_class(self, stem_paths):
        """Return a Separator class whose .separate() returns stem_paths."""

        class _FakeSeparator:
            def __init__(self, **kwargs):
                pass

            def load_model(self, model_filename):
                pass

            def separate(self, audio_path):
                return [str(p) for p in stem_paths]

        return _FakeSeparator

    def test_temp_file_deleted_on_success(self, worker, tmp_path):
        # Create a fake audio source
        src = _make_file(tmp_path, "source.wav")
        work = tmp_path / "out"
        work.mkdir()

        # Pre-create the two stem files that the fake separator returns
        vocals_stem = _make_file(work, "track_(Vocals).wav")
        instrumental_stem = _make_file(work, "track_(Instrumental).wav")

        FakeSeparator = self._make_fake_separator_class([vocals_stem, instrumental_stem])

        # We'll track whether the temp file is deleted
        temp_deleted = []

        orig_normalize = worker._normalize_input_for_separator

        # Fake temp file
        fake_temp = _make_file(tmp_path, "sep_in_fake.wav")

        def _fake_normalize(s, w):
            return fake_temp, fake_temp

        with patch.object(worker, "_normalize_input_for_separator", side_effect=_fake_normalize):
            # Patch the audio_separator import inside run_vocal_separation
            fake_sep_mod = types.ModuleType("audio_separator.separator")
            fake_sep_mod.Separator = FakeSeparator  # type: ignore[attr-defined]

            with patch.dict(sys.modules, {"audio_separator.separator": fake_sep_mod}):
                vocals, instrumental = worker.run_vocal_separation(src, work)

        # fake_temp should have been unlinked
        assert not fake_temp.exists(), "Temp input must be deleted after successful separation"

    def test_temp_file_deleted_on_separator_error(self, worker, tmp_path):
        """Even when the separator raises, the temp file must be cleaned up."""
        src = _make_file(tmp_path, "source.wav")
        work = tmp_path / "out_err"
        work.mkdir()

        fake_temp = _make_file(tmp_path, "sep_in_err.wav")

        def _fake_normalize(s, w):
            return fake_temp, fake_temp

        class _BrokenSeparator:
            def __init__(self, **kwargs):
                pass
            def load_model(self, model_filename):
                pass
            def separate(self, path):
                raise RuntimeError("Separator exploded")

        fake_sep_mod = types.ModuleType("audio_separator.separator")
        fake_sep_mod.Separator = _BrokenSeparator  # type: ignore[attr-defined]

        with patch.object(worker, "_normalize_input_for_separator", side_effect=_fake_normalize):
            with patch.dict(sys.modules, {"audio_separator.separator": fake_sep_mod}):
                with pytest.raises(RuntimeError, match="Separator exploded"):
                    worker.run_vocal_separation(src, work)

        assert not fake_temp.exists(), "Temp input must be deleted even when separator raises"

    def test_no_temp_file_no_deletion_attempt(self, worker, tmp_path):
        """When _normalize returns None as temp, no unlink should be called."""
        src = _make_file(tmp_path, "source.wav")
        work = tmp_path / "out_notmp"
        work.mkdir()

        vocals_stem = _make_file(work, "x_(Vocals).wav")
        instrumental_stem = _make_file(work, "x_(Instrumental).wav")

        FakeSeparator = self._make_fake_separator_class([vocals_stem, instrumental_stem])

        def _fake_normalize(s, w):
            # Simulate safe path — no temp needed
            return src.resolve(), None

        fake_sep_mod = types.ModuleType("audio_separator.separator")
        fake_sep_mod.Separator = FakeSeparator  # type: ignore[attr-defined]

        with patch.object(worker, "_normalize_input_for_separator", side_effect=_fake_normalize):
            with patch.dict(sys.modules, {"audio_separator.separator": fake_sep_mod}):
                # Should succeed without errors
                vocals, instrumental = worker.run_vocal_separation(src, work)

        assert vocals == vocals_stem.resolve()
        assert instrumental == instrumental_stem.resolve()

    def test_work_audio_path_passed_to_separator_not_original(self, worker, tmp_path):
        """
        Regression: run_vocal_separation must pass work_audio (the normalised path)
        to Separator.separate(), not the original audio_path.
        """
        src = _make_file(tmp_path, "original source.wav")  # space in name
        work = tmp_path / "out_reg"
        work.mkdir()

        safe_copy = _make_file(work, "sep_in_safe.wav")
        vocals_stem = _make_file(work, "s_(Vocals).wav")
        instrumental_stem = _make_file(work, "s_(Instrumental).wav")

        separated_paths = []

        class _TrackingSeparator:
            def __init__(self, **kwargs):
                pass
            def load_model(self, model_filename):
                pass
            def separate(self, audio_path):
                separated_paths.append(audio_path)
                return [str(vocals_stem), str(instrumental_stem)]

        def _fake_normalize(s, w):
            return safe_copy, safe_copy

        fake_sep_mod = types.ModuleType("audio_separator.separator")
        fake_sep_mod.Separator = _TrackingSeparator  # type: ignore[attr-defined]

        with patch.object(worker, "_normalize_input_for_separator", side_effect=_fake_normalize):
            with patch.dict(sys.modules, {"audio_separator.separator": fake_sep_mod}):
                worker.run_vocal_separation(src, work)

        assert separated_paths, "Separator.separate() was never called"
        assert str(safe_copy) in separated_paths[0], (
            f"Expected safe copy path in separate() call, got {separated_paths[0]!r}"
        )
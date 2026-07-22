"""Synthesize the teacher's spoken line into a 16-bit PCM WAV.

The agent's STT (faster-whisper `base.en`) needs *speech*, not a tone, so the
synthetic teacher must publish real audio. We support three offline sources, in
priority order, and the whole media stage skips cleanly (with a clear reason) when
none is available — so the REST/orchestration assertions still run everywhere.

  1. E2E_TEACHER_WAV  — a ready-made WAV you provide (any rate/channels).
  2. piper            — neural TTS; set E2E_PIPER_MODEL to a `.onnx` voice.
  3. espeak-ng        — the `espeak-ng` CLI, if installed.

Returns the path to a mono 16-bit PCM WAV. Callers read its real sample rate with
the stdlib `wave` module, so any rate is fine.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import wave
from pathlib import Path

_ASSETS = Path(__file__).resolve().parent.parent / "assets"


class TtsUnavailable(RuntimeError):
    """No usable TTS source was found — the media stage should skip."""


def _is_valid_wav(path: Path) -> bool:
    try:
        with wave.open(str(path), "rb") as w:
            return w.getnframes() > 0
    except Exception:  # noqa: BLE001
        return False


def _discover_piper_model(model_path: str) -> str:
    """Prefer an explicit model path; otherwise use any `*.onnx` voice in assets/."""
    if model_path and Path(model_path).exists():
        return model_path
    candidates = sorted(_ASSETS.glob("*.onnx"))
    return str(candidates[0]) if candidates else ""


def _piper(text: str, out: Path, model_path: str) -> bool:
    piper_bin = shutil.which("piper")
    model_path = _discover_piper_model(model_path)
    if not piper_bin or not model_path:
        return False
    proc = subprocess.run(
        [piper_bin, "--model", model_path, "--output_file", str(out)],
        input=text.encode("utf-8"),
        capture_output=True,
    )
    return proc.returncode == 0 and _is_valid_wav(out)


def _espeak(text: str, out: Path) -> bool:
    espeak_bin = shutil.which("espeak-ng") or shutil.which("espeak")
    if not espeak_bin:
        return False
    # Slightly slower speech transcribes more reliably with whisper base.en.
    proc = subprocess.run(
        [espeak_bin, "-s", "150", "-w", str(out), text],
        capture_output=True,
    )
    return proc.returncode == 0 and _is_valid_wav(out)


def _gtts(text: str, out: Path) -> bool:
    """Google TTS: a tiny (~30KB) request, decoded to 16k mono WAV via ffmpeg.

    Needs network to translate.google.com and ffmpeg on PATH, but no big model
    download — the payload is the audio itself, not a voice model.
    """
    ffmpeg = shutil.which("ffmpeg")
    if not ffmpeg:
        return False
    try:
        from gtts import gTTS  # optional dependency
    except Exception:  # noqa: BLE001
        return False
    mp3 = out.with_suffix(".mp3")
    try:
        gTTS(text, lang="en").save(str(mp3))
    except Exception:  # noqa: BLE001 — offline / throttled
        return False
    proc = subprocess.run(
        [ffmpeg, "-y", "-i", str(mp3), "-ar", "16000", "-ac", "1", "-sample_fmt", "s16", str(out)],
        capture_output=True,
    )
    return proc.returncode == 0 and _is_valid_wav(out)


def synthesize_teacher_wav(text: str, *, teacher_wav_path: str, piper_model_path: str) -> Path:
    """Return a path to a WAV of ``text`` spoken aloud, or raise TtsUnavailable."""
    if teacher_wav_path:
        p = Path(teacher_wav_path)
        if not _is_valid_wav(p):
            raise TtsUnavailable(f"E2E_TEACHER_WAV is not a readable WAV: {p}")
        return p

    _ASSETS.mkdir(parents=True, exist_ok=True)
    # Cache by a stable name so repeat runs reuse the same synthesized clip.
    out = _ASSETS / "teacher_line.wav"
    if _is_valid_wav(out) and os.environ.get("E2E_TTS_NOCACHE") != "1":
        return out

    if _piper(text, out, piper_model_path):
        return out
    if _espeak(text, out):
        return out
    if _gtts(text, out):
        return out

    raise TtsUnavailable(
        "No TTS available. Provide a WAV via E2E_TEACHER_WAV, a piper voice in assets/ "
        "(or E2E_PIPER_MODEL), install espeak-ng, or `pip install gTTS` (needs ffmpeg + net)."
    )

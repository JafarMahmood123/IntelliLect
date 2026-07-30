"""In-memory WAV encoding, shared by every hosted STT engine.

Both hosted engines (Groq's file upload, Gemini's inline audio part) need the same thing: the
finished float32 window from ``StreamingTranscriber`` turned into a real audio container. Kept
here rather than in either engine so neither has to import the other.
"""

from __future__ import annotations

import io
import wave

import numpy as np


def to_wav_bytes(samples: np.ndarray, sample_rate: int) -> bytes:
    """Encode a mono float32 window as 16-bit PCM WAV, in memory.

    Hosted APIs need a real audio container; float32 in [-1, 1] is scaled to int16 with clipping
    so a hot mic cannot wrap around into noise.
    """
    clipped = np.clip(samples, -1.0, 1.0)
    pcm16 = (clipped * 32767.0).astype(np.int16)
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)  # 16-bit
        wav.setframerate(sample_rate)
        wav.writeframes(pcm16.tobytes())
    return buffer.getvalue()

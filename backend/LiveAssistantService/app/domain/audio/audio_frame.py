from __future__ import annotations

from dataclasses import dataclass

# Bytes per PCM sample. The whole service standardizes on 16-bit signed
# little-endian PCM (the format downstream STT expects); interleaved when
# multi-channel, though normalized frames are always mono.
_BYTES_PER_SAMPLE = 2


@dataclass(frozen=True)
class AudioFrame:
    """A single chunk of captured teacher audio.

    Pure domain object: no framework/SDK imports. `pcm` is signed 16-bit
    little-endian PCM; for a multi-channel frame the samples are interleaved.
    Frames produced by an ``AudioSource`` are already normalized (see
    ``TARGET_SAMPLE_RATE`` / ``TARGET_CHANNELS``), so downstream stages (STT,
    boundary detection — later phases) are decoupled from LiveKit's native format.

    `timestamp` is the stream time, in seconds, of the frame's first sample: a
    monotonically increasing offset from the start of capture (not wall-clock), so
    later phases can order frames and measure idea/pause durations deterministically.
    """

    pcm: bytes
    sample_rate: int
    channels: int
    timestamp: float  # seconds from start of capture, of this frame's first sample

    @property
    def num_samples(self) -> int:
        """Samples per channel in this frame."""
        return len(self.pcm) // (_BYTES_PER_SAMPLE * self.channels)

    @property
    def duration_seconds(self) -> float:
        """Wall-clock duration this frame represents."""
        if self.sample_rate <= 0:
            return 0.0
        return self.num_samples / self.sample_rate

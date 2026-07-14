# Test fixtures

Drop a short English WAV here (e.g. `english_sample.wav`) to enable the opt-in
real-model STT test in `tests/test_faster_whisper_real.py`. Any sample rate / channel
count works — `FakeAudioSource` normalizes it to 16kHz mono.

Alternatively point the test at a clip without committing it:

```bash
STT_TEST_WAV=/path/to/english.wav pytest tests/test_faster_whisper_real.py
```

No audio is committed to the repo, so this test skips cleanly by default.

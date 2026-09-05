# VoiceGuard Stage 6.1 — Per-Word Sound Replacements Implemented

This build adds a working per-word/per-phrase replacement sound workflow.

## How to use it

1. Build/run VoiceGuard.
2. Add a blocked word or phrase normally.
3. Right-click that entry in **Blocked words**.
4. Choose **Set replacement sound...**.
5. Select a WAV file.
6. The entry displays `[SOUND: filename.wav]`.
7. When that trigger is detected, its censored PCM region uses that sound instead of silence.
8. Right-click the entry and choose **Clear replacement sound** to return it to mute.

## Audio format

For deterministic low-latency playback, replacement files must currently be:
- WAV
- PCM
- 48,000 Hz
- mono
- 16-bit

Files that do not match are rejected and the offending region is muted instead.

The existing 3-second safety delay and detection engine are unchanged.

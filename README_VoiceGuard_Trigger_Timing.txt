VoiceGuard rollback base: 6.6.5 Soundboard Headset StopToggle BUILD FIX

This build intentionally uses the 6.6.5 AudioEngine/SpeechDetector censor pipeline as the baseline.

VoiceGuard Trigger timing change:
- Default VoiceGuard Trigger key: <
- Trigger-down immediately starts the proven delayed/censored AudioEngine path.
- The physical game PTT key is injected only after the configured delay.
- Trigger-up stops new microphone capture immediately.
- Game PTT is released at Trigger-up + configured delay.

Example with a 2.0 second delay:
- Hold < for 5.0 seconds
- Game PTT DOWN at +2.0s
- Game PTT UP at +7.0s
- Game PTT is therefore held for exactly 5.0 seconds.

Important: The audio/censor engine itself was not replaced with the experimental 6.6.7 timing architecture. This is deliberate so Whisper detection and replacement-sound playback remain on the previously working 6.6.5 path.

Build:
Run BUILD_INSTALLER.bat on Windows with the .NET 8 SDK and Inno Setup 6 installed.

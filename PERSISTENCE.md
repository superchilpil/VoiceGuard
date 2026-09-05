# VoiceGuard Stage 6.5.5 — Dark Purple Theme + Persistence

This build adds persistent application configuration.

Saved automatically to:
`%LOCALAPPDATA%\VoiceGuard\config.json`

Persisted:
- Blocked words
- Transcription aliases
- Per-word replacement WAV assignments
- Delay setting
- PTT key
- Selected input device name
- Selected output device name

The configuration is loaded at startup and saved whenever settings or word/alias/effect assignments change, and again on application close.

The existing dark/purple GUI and consecutive replacement-effect behavior are preserved.

Build:
Run `BUILD.bat` on a Windows machine with the .NET 8 SDK installed.

# VoiceGuard

VoiceGuard is a Windows voice-chat profanity filter built with C#/.NET 8 WinForms. It captures microphone audio, delays playback, uses local Whisper transcription to detect configured blocked words/phrases, and censors offending audio before it reaches the selected output device.

## Current version

6.5.5

## Highlights

- Local Whisper speech detection
- Configurable blocked words and phrases
- Transcription aliases
- Per-word replacement WAV effects
- Multiple repeated profanity occurrences handled independently
- Hard audio censoring with pre/post-roll
- Configurable audio delay
- Push-to-talk support
- Persistent settings stored under `%LOCALAPPDATA%\\VoiceGuard`
- CPU-only Whisper runtime for a smaller Windows deployment
- Dark WinForms interface with VoiceGuard branding

## Requirements

- Windows 10/11 x64
- .NET 8 SDK for building from source
- **VB-Audio Virtual Cable (VB-CABLE) is required for audio routing**
- FFmpeg is not required by the VoiceGuard audio engine

### VB-Audio Virtual Cable

VoiceGuard uses VB-Audio Virtual Cable to route the processed audio to applications such as Discord and other voice-chat software. Install the standard **VB-CABLE** package before using VoiceGuard.

**Official download:** https://vb-audio.com/Cable/

After installation, Windows should provide the `CABLE Input` playback device and `CABLE Output` recording device. VoiceGuard can then use the appropriate VB-CABLE device for its output/input routing.

## Build

Run `BUILD.bat` for a normal build or `BUILD_INSTALLER.bat` to publish the self-contained Windows build and create the installer with Inno Setup.

The published application intentionally preserves the Whisper native runtime directory under `runtimes\\win-x64` rather than using single-file publishing. This is required for reliable Whisper native-library loading.

## Model storage

Whisper models are stored in:

`%LOCALAPPDATA%\\VoiceGuard\\Models`

## License

No license has been selected for this repository yet.

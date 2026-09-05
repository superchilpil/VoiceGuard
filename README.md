# VoiceGuard

VoiceGuard is a Windows voice-chat profanity filter that captures microphone
audio, delays it for filtering, detects configured blocked words/phrases with
local Whisper speech recognition, and mutes or replaces offending audio before
sending it to the selected output device.

## Requirements

- Windows 10/11, 64-bit
- .NET 8 SDK
- A microphone/input device
- **VB-Audio Virtual Cable (VB-CABLE)**

VoiceGuard is designed to route its filtered audio through VB-CABLE.

Download VB-CABLE from the official VB-Audio page:
https://vb-audio.com/Cable/

## Features

- Local Whisper speech recognition
- Configurable blocked words and phrases
- Transcription aliases
- Per-word replacement sound effects
- Adjustable delayed filtering
- Push-to-talk support
- Input/output device selection
- Persistent settings stored in the user's local application data
- Windows taskbar/application icon
- CPU-only Whisper runtime for a smaller deployment

## Building

Open a Developer Command Prompt or PowerShell in the repository directory.

Build:

    BUILD.bat

For a self-contained Windows x64 publish and installer build:

    BUILD_INSTALLER.bat

The published application intentionally keeps Whisper's native runtime files
in the `runtimes\win-x64` directory. Do not convert the application to a
single-file publish, because Whisper's native runtime layout is required.

## Models

Whisper models are stored under:

    %LOCALAPPDATA%\VoiceGuard\Models

The application downloads/loads its configured local Whisper model there.

## Audio routing

A typical setup is:

    Microphone
        -> VoiceGuard
        -> VB-CABLE
        -> Voice chat application

Configure the voice-chat application to use the VB-CABLE recording/input side
as its microphone source.

## License

VoiceGuard is licensed under the **MIT License**.

See the [LICENSE](LICENSE) file for the complete license text.

Copyright (c) 2026 superchilpil.

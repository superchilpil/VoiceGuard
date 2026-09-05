# VoiceGuard

VoiceGuard is a Windows voice-chat profanity filter that captures microphone audio, delays it for filtering, detects configured blocked words/phrases with local Whisper speech recognition, and mutes or replaces offending audio before sending it to the selected output device.

## Requirements

- Windows 10/11, 64-bit
- .NET 8 SDK (only required if building from source)
- A microphone/input device
- **VB-Audio Virtual Cable (VB-CABLE)**

VoiceGuard is designed to route its filtered audio through VB-CABLE.

Download VB-CABLE from the official VB-Audio page:
https://vb-audio.com/Cable/

## Download

The latest Windows installer is available from the repository's GitHub Releases page.

## How to Use

1. Install and launch VoiceGuard.
2. Install VB-CABLE if you have not already done so.
3. Select your microphone under **Input Device**.
4. Select **CABLE Input** / the VB-CABLE playback side as the VoiceGuard output device.
5. Configure your voice-chat application to use **CABLE Output** / the VB-CABLE recording side as its microphone input.
6. Add the words or phrases you want VoiceGuard to block.
7. Hold the configured push-to-talk key while speaking. VoiceGuard processes the captured audio through the configured delay and filters detected blocked words before sending the audio to VB-CABLE.
8. Release the push-to-talk key when finished speaking.

### Blocked Words

The **Blocked Words** list contains the words and phrases VoiceGuard will look for in Whisper's transcription.

To add a blocked word or phrase:

1. Enter the word or phrase in the blocked-word field.
2. Click **Add**.
3. VoiceGuard will save the change automatically.

To remove one, select it and click **Remove**.

## Replacement Sound Effects

VoiceGuard can replace a detected blocked word with a WAV sound effect instead of simply muting that portion of audio.

To assign a sound effect:

1. Add the word or phrase to **Blocked Words**.
2. Right-click that word in the list.
3. Choose the replacement-sound option.
4. Select the `.wav` file you want to use.
5. The assignment is saved automatically.

Each blocked word can have its own replacement sound. If consecutive blocked words are detected, their replacement effects are played as separate events rather than being merged into one effect.

To remove a replacement sound assignment, use the same right-click menu for the word and choose the option to clear its replacement sound.

## Transcription Aliases

An **alias** tells VoiceGuard to treat a phrase that Whisper commonly transcribes incorrectly as a different blocked word or phrase.

For example, if Whisper frequently hears a blocked word as a similar-sounding phrase, you can create an alias so that VoiceGuard interprets that transcription as the intended blocked word.

To add an alias:

1. Right-click the relevant blocked word.
2. Choose the alias option.
3. Enter the phrase Whisper is actually producing.
4. Set it to map to the intended blocked word.
5. Save/confirm the alias.

For example:

    Whisper transcription: bag it
    Alias: bag it -> faggot

With that alias configured, a transcription of `bag it` can be handled as the corresponding blocked word.

Aliases are useful when pronunciation, background noise, microphone quality, or Whisper's speech recognition causes a blocked word to be transcribed differently from how it was actually spoken.

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

The published application intentionally keeps Whisper's native runtime files in the `runtimes\\win-x64` directory. Do not convert the application to a single-file publish, because Whisper's native runtime layout is required.

## Models

Whisper models are stored under:

    %LOCALAPPDATA%\\VoiceGuard\\Models

The application downloads/loads its configured local Whisper model there.

## Audio Routing

A typical setup is:

    Microphone
        -> VoiceGuard
        -> VB-CABLE
        -> Voice chat application

Configure the voice-chat application to use the VB-CABLE recording/input side as its microphone source.

## Settings and Persistence

VoiceGuard automatically saves its configuration, including blocked words, aliases, replacement-sound assignments, delay, push-to-talk key, and selected audio devices.

Settings are stored under:

    %LOCALAPPDATA%\\VoiceGuard\\config.json

## License

VoiceGuard is licensed under the **MIT License**.

See the [LICENSE](LICENSE) file for the complete license text.

Copyright (c) 2026 superchilpil.

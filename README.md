# VoiceGuard

VoiceGuard is a Windows voice-chat profanity filter designed primarily for **gaming and other voice-chat applications that use push-to-talk (PTT)**. It captures microphone audio, delays PTT speech for filtering, detects configured blocked words/phrases with local Whisper speech recognition, and mutes or replaces offending audio before sending the audio to the selected output device.

## How VoiceGuard Works

VoiceGuard has two audio paths depending on whether you are pressing your configured PTT key:

- **PTT not pressed:** VoiceGuard uses **live audio passthrough**. Your microphone audio is sent directly to the selected output without the profanity filter or Whisper speech recognition being applied.
- **PTT pressed:** VoiceGuard captures and delays your speech, analyzes it with Whisper, and filters detected blocked words before the delayed audio is sent to the selected output.
- **PTT released:** VoiceGuard finishes draining the delayed PTT audio and then automatically returns to live passthrough.

This design is intended for games where you normally communicate by holding a push-to-talk key. Audio that you are not intentionally transmitting through PTT is not filtered or transcribed by VoiceGuard.

### PTT and Elevated Applications

VoiceGuard's global PTT detection can be affected when a **higher-privilege/elevated application** is in the foreground. For example, Windows Task Manager runs elevated and may prevent VoiceGuard from receiving the PTT key while Task Manager has focus.

If PTT stops responding only when an elevated application is in the foreground, this is a Windows security/privilege boundary rather than a VoiceGuard audio-processing failure. Running VoiceGuard as **Administrator** allows PTT to work with elevated foreground applications, but VoiceGuard does not normally require administrator privileges.

## Requirements

- Windows 10/11, 64-bit
- .NET 8 SDK (only required if building from source)
- A microphone/input device
- **VB-Audio Virtual Cable (VB-CABLE)**
- Intel NPU support is optional; compatible Intel systems can use OpenVINO NPU acceleration for Whisper

VoiceGuard is designed to route its audio through VB-CABLE.

Download VB-CABLE from the official VB-Audio page:
https://vb-audio.com/Cable/

## Download

The latest Windows installer is available from the repository's GitHub Releases page.

## How to Use

1. Install and launch VoiceGuard.
2. Install VB-CABLE if you have not already done so.
3. Select your microphone under **Input Device**.
4. Select **CABLE Input** / the VB-CABLE playback side as the VoiceGuard output device.
5. Configure your game or voice-chat application to use **CABLE Output** / the VB-CABLE recording side as its microphone input.
6. Add the words or phrases you want VoiceGuard to block.
7. Configure your preferred push-to-talk key in VoiceGuard.
8. **Hold the push-to-talk key when you want to transmit.** VoiceGuard delays that audio long enough for Whisper to recognize speech and filters detected blocked words before sending the audio to VB-CABLE.
9. **When PTT is not pressed, VoiceGuard passes microphone audio through live without filtering or Whisper transcription.**
10. Release the push-to-talk key when finished speaking. VoiceGuard drains the remaining delayed audio and then returns to live passthrough.

### Delay

VoiceGuard uses a short audio delay during PTT transmission so Whisper has time to transcribe speech and detect blocked words before the audio reaches the output.

- **2.0 seconds is the minimum delay** supported by VoiceGuard and is intended for higher-end machines that can process Whisper quickly enough.
- If VoiceGuard is **missing words or frequently showing `MISSED` entries** in the log, increase the delay to give Whisper more time to recognize the speech before it reaches the output.
- Increasing the delay can improve filtering reliability, especially on slower systems or when processing more difficult audio.
- The delay applies to the **PTT transmission path**; microphone audio while PTT is idle remains live passthrough.

### Blocked Words

The **Blocked Words** list contains the words and phrases VoiceGuard will look for in Whisper's transcription while PTT is active.

To add a blocked word or phrase:

1. Enter the word or phrase in the blocked-word field.
2. Click **Add**.
3. VoiceGuard will save the change automatically.

To remove one, select it and click **Remove**.

## Replacement Sound Effects

VoiceGuard can replace a detected blocked word with a custom sound instead of muting it. Each blocked word can have its own replacement sound and playback duration.

### Assigning a Replacement Sound

1. Add the word or phrase to **Blocked Words**.
2. Right-click the word and choose the replacement-sound option.
3. Select the audio file. VoiceGuard converts a copy to its internal WAV/PCM format in the background; the original file is never modified.

Imported replacement audio is automatically trimmed to a maximum of **5 seconds**. A 5-second runtime safety limit also applies to existing WAV replacement files.

### Playback Duration

Each blocked word has its own playback-duration setting. Right-click the word and open **replacement playback settings** to choose:

- **Word length** — plays the replacement for the detected offending word/event duration.
- **Custom length** — plays the replacement for a selected duration from **0.1 to 5.0 seconds**, in 0.1-second increments.

The selected duration is saved separately for each blocked word. Replacement playback continues through the selected duration even if PTT is released, then VoiceGuard returns to live passthrough.

Replacement audio is converted and prepared before real-time censor playback, so codec conversion is not performed during live PTT processing. Consecutive blocked words remain separate events, allowing each replacement to use its own sound and playback setting.

To remove a replacement sound, use the word's right-click menu and choose the option to clear it.

## Transcription Aliases

An **alias** tells VoiceGuard to treat a phrase that Whisper commonly transcribes incorrectly as a different blocked word or phrase.

To add an alias:

1. Right-click the relevant blocked word.
2. Choose the alias option.
3. Enter the phrase Whisper is actually producing.
4. Set it to map to the intended blocked word.
5. Save/confirm the alias.

For example:

    Whisper transcription: bits
    Alias: bits -> bitch

With that alias configured, a transcription of `bits` can be handled as the corresponding blocked word.

Aliases are useful when pronunciation, background noise, microphone quality, or Whisper's speech recognition causes a blocked word to be transcribed differently from how it was actually spoken.

## Intel NPU / OpenVINO Acceleration

**VoiceGuard 6.6** adds optional Intel OpenVINO NPU acceleration for Whisper speech recognition during PTT processing.

- Compatible Intel systems can use the **Intel NPU** for Whisper's OpenVINO encoder.
- VoiceGuard automatically attempts the NPU path when the required Intel/OpenVINO runtime is available.
- Systems without compatible NPU support continue to use the CPU Whisper runtime.
- NPU acceleration is isolated to Whisper initialization and does not change VoiceGuard's audio routing, PTT, delay, or censor scheduling pipeline.
- The startup log reports whether OpenVINO is selected and whether the NPU encoder was requested.
- NPU acceleration only matters while VoiceGuard is analyzing PTT speech; idle live passthrough does not run Whisper.

The NPU path is optional. VoiceGuard remains usable on systems that do not have a compatible Intel NPU.

## Features

- Designed for gaming and PTT-based voice chat
- Live microphone passthrough when PTT is not pressed
- Delayed and filtered audio while PTT is pressed
- Local Whisper speech recognition
- Optional Intel OpenVINO/NPU Whisper acceleration
- CPU Whisper fallback
- Configurable blocked words and phrases
- Transcription aliases
- Per-word replacement sounds and playback-duration settings
- Replacement audio conversion to WAV/PCM
- 5-second maximum replacement-audio limit
- Adjustable PTT filtering delay
- Input/output device selection
- Persistent settings stored in the user's local application data
- Single-instance protection to prevent multiple VoiceGuard audio engines from running at once

## Building

For a self-contained Windows x64 publish and installer build:

    BUILD_INSTALLER.bat

`BUILD_INSTALLER.bat` is the project's single build script. The published application intentionally keeps Whisper's native runtime files in the `runtimes` directory. Do not convert the application to a single-file publish, because Whisper's native runtime layout is required.

## Models

Whisper models are stored under:

    %LOCALAPPDATA%\\VoiceGuard\\Models

The application downloads/loads its configured local Whisper model there.

## Audio Routing

A typical gaming setup is:

    Microphone
        -> VoiceGuard
        -> VB-CABLE
        -> Game / voice-chat application

Configure the game or voice-chat application to use the VB-CABLE recording/input side as its microphone source.

VoiceGuard is intended to sit between your microphone and the game's voice input. When PTT is idle, VoiceGuard passes the microphone audio through live. When you press PTT, the transmitted audio enters VoiceGuard's delayed filtering path.

Personally I use VoiceMeeter Banana in conjunction with this to switch from direct Mic input and VG depending on the game to conserve resources

## Settings and Persistence

VoiceGuard automatically saves its configuration, including blocked words, aliases, replacement-sound assignments, replacement playback-duration settings, delay, push-to-talk key, and selected audio devices.

Settings are stored under:

    %LOCALAPPDATA%\\VoiceGuard\\config.json

## License

VoiceGuard is licensed under the **MIT License**.

See the [LICENSE](LICENSE) file for the complete license text.

Copyright (c) 2026 superchilpil.

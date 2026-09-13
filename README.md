# VoiceGuard

VoiceGuard is a Windows voice-chat profanity filter designed primarily for **gaming and other voice-chat applications that use push-to-talk (PTT)**. It captures microphone audio, delays PTT speech for filtering, detects configured blocked words/phrases with local Whisper speech recognition, and mutes or replaces offending audio before sending the audio to the selected output device.

## How VoiceGuard Works

VoiceGuard adds a short delay to your voice so it has time to check what you are saying before sending it to your game.

When you press your **VoiceGuard Trigger Key**:

1. **Your microphone audio is captured** and held for the selected delay.
2. **VoiceGuard listens to what you say** using speech recognition.
3. If a blocked word is detected, VoiceGuard **removes it and plays your chosen replacement sound** instead.
4. After the delay, the **filtered audio is sent to your game** through your normal voice-chat input.
5. When you stop talking, VoiceGuard finishes sending the remaining delayed audio before releasing the game's **Push-to-Talk** key.

This means the other players hear the cleaned-up version of what you said rather than the original audio.

### In simple terms

**You speak → VoiceGuard listens → Bad words are replaced → Clean audio is sent to the game.**

### Configurable Transmission Keys

VoiceGuard provides separate settings for:

- **VoiceGuard Trigger Key** — the key you hold to capture and transmit filtered speech. Default: `<`.
- **Game PTT Key** — the key VoiceGuard automatically presses in the game while transmitting delayed audio.
- **Soundboard Listen Key** — the key used to begin the private soundboard phrase-listening window.

All three keys are configurable independently and are saved with the VoiceGuard configuration.

### PTT and Elevated Applications

VoiceGuard's global keyboard input and PTT injection can be affected when a **higher-privilege/elevated application** is in the foreground. For example, Windows Task Manager runs elevated and may prevent VoiceGuard from interacting with an elevated foreground application normally.

If PTT stops responding only when an elevated application is in the foreground, this can be a Windows security/privilege boundary rather than an audio-processing failure. Running VoiceGuard as **Administrator** can allow interaction with elevated foreground applications, but VoiceGuard does not normally require administrator privileges.

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
7. Configure the **VoiceGuard Trigger Key** and the **Game PTT Key** separately.
8. **Hold the VoiceGuard Trigger Key when you want to transmit.** VoiceGuard captures that speech privately, delays it long enough for Whisper to recognize speech, and filters detected blocked words before sending the delayed audio to VB-CABLE.
9. VoiceGuard automatically presses the configured **Game PTT Key** when the delayed transmission begins. You do not need to physically press the game's PTT key.
10. Release the VoiceGuard Trigger Key when finished speaking. VoiceGuard drains the remaining delayed audio and then releases the Game PTT Key.
11. When no filtered transmission is active, VoiceGuard returns to live microphone passthrough.

### Transmission Timing

The VoiceGuard Trigger workflow applies the configured VoiceGuard delay **once**. The Game PTT Key is engaged for the transmission without adding a second full delay before the delayed audio begins.

For example, with a 2.5-second delay:

    VoiceGuard Trigger held
        -> capture/filter privately
        -> approximately 2.5 seconds of configured delay
        -> Game PTT automatically pressed
        -> delayed filtered audio transmitted
        -> remaining queued audio drains
        -> Game PTT released

The Game PTT remains held long enough for the complete delayed transmission. This prevents the game from cutting off the beginning or end of the filtered audio because its PTT was released too early.

### Delay

VoiceGuard uses a short audio delay during filtered transmission so Whisper has time to transcribe speech and detect blocked words before the audio reaches the output.

- **2.0 seconds is the minimum delay** supported by VoiceGuard and is intended for higher-end machines that can process Whisper quickly enough.
- If VoiceGuard is **missing words or frequently showing `MISSED` entries** in the log, increase the delay to give Whisper more time to recognize the speech before it reaches the output.
- Increasing the delay can improve filtering reliability, especially on slower systems or when processing more difficult audio.
- The configured delay applies **once** to the filtered transmission path.
- When no filtered transmission is active, microphone audio remains live passthrough.

### Blocked Words

The **Blocked Words** list contains the words and phrases VoiceGuard will look for in Whisper's transcription during filtered transmission.

To add a blocked word or phrase:

1. Enter the word or phrase in the blocked-word field.
2. Click **Add**.
3. VoiceGuard will save the change automatically.

To remove one, select it and click **Remove**.

## Replacement Sound Effects

VoiceGuard can replace a detected blocked word with a custom sound instead of muting it. Each blocked word can have its own replacement sound, playback duration, and replacement volume.

### Assigning a Replacement Sound

1. Add the word or phrase to **Blocked Words**.
2. Right-click the word and choose the replacement-sound option.
3. Select the audio file. VoiceGuard converts a copy to its internal WAV/PCM format in the background; the original file is never modified.

Imported replacement audio is automatically trimmed to a maximum of **5 seconds**. A 5-second runtime safety limit also applies to existing WAV replacement files.

### Playback Duration

Each blocked word has its own playback-duration setting. Right-click the word and open **replacement playback settings** to choose:

- **Word length** — plays the replacement for the detected offending word/event duration.
- **Custom length** — plays the replacement for a selected duration from **0.1 to 5.0 seconds**, in 0.1-second increments.

The selected duration is saved separately for each blocked word. Replacement playback continues through the selected duration even if the VoiceGuard Trigger Key is released, then VoiceGuard returns to live passthrough after the transmission drains.

Replacement audio is converted and prepared before real-time censor playback, so codec conversion is not performed during live processing. Consecutive blocked words remain separate events, allowing each replacement to use its own sound and playback setting.

### Replacement Volume

Each blocked word can have its own replacement-sound volume from **0% to 150%**. This is useful when a replacement recording is quieter than the surrounding voice audio.

- **100%** is the normal playback level.
- Levels above 100% provide additional gain for quieter replacement sounds.
- The replacement volume is configured independently for each blocked word.
- Use the **Test** button beside a replacement volume control to preview the sound locally. Test playback uses the computer's normal Windows playback device and is **not sent through the selected VoiceGuard/VB-CABLE output**.

VoiceGuard also provides a **master output volume control from 0% to 150%** directly below the selected output device. This controls the volume of audio sent through VoiceGuard's output path.

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

**VoiceGuard 6.6** adds optional Intel OpenVINO NPU acceleration for Whisper speech recognition during filtered transmission.

- Compatible Intel systems can use the **Intel NPU** for Whisper's OpenVINO encoder.
- VoiceGuard automatically attempts the NPU path when the required Intel/OpenVINO runtime is available.
- Systems without compatible NPU support continue to use the CPU Whisper runtime.
- NPU acceleration is isolated to Whisper initialization and does not change VoiceGuard's audio routing, delay, or censor scheduling pipeline.
- The startup log reports whether OpenVINO is selected and whether the NPU encoder was requested.
- NPU acceleration only matters while VoiceGuard is analyzing speech; idle live passthrough does not run Whisper.

The NPU path is optional. VoiceGuard remains usable on systems that do not have a compatible Intel NPU.

## Soundboard

The Soundboard is an **additional feature** built on top of VoiceGuard's primary profanity-filtering and replacement system. It allows configured audio clips to be triggered by spoken phrases while using the same PTT/audio routing pipeline.

### Soundboard Phrases and Aliases

- Add soundboard phrases that VoiceGuard can recognize with local Whisper processing.
- Add aliases for phrases that Whisper commonly transcribes differently.
- Configure the Soundboard Listen Key used to begin a private phrase-listening window.
- The spoken trigger phrase itself is not transmitted to the game as PTT audio.
- Soundboard clips can be triggered repeatedly during normal use.

### Soundboard Audio Selection

Soundboard audio includes a waveform-based editor for selecting the portion of a source recording to use.

- Set precise **Start** and **End** positions.
- Select the full source with **Full**.
- Preview the selected portion with **Play Selection**.
- Preview the complete source with **Play Full**.
- Stop preview playback at any time.
- Use the selected range when saving the soundboard clip.
- Selection markers and the selected range can be adjusted directly on the waveform.

Soundboard clips are not subject to the 5-second replacement-sound limit. Longer soundboard clips are supported and remain queued long enough to finish before VoiceGuard returns to live passthrough.

### Soundboard Playback

When a soundboard clip is triggered, VoiceGuard plays the clip through the configured voice-chat/output path so the game can receive it, while also playing a local copy through the computer's normal Windows playback device so **you can hear the soundboard audio through your headset**.

The Game PTT Key remains held for the duration of the soundboard transmission, including the configured VoiceGuard delay and a small safety margin. This keeps the game's voice input active for the complete clip instead of releasing PTT early.

The configured Soundboard Listen Key acts as a **toggle while a soundboard clip is playing**. Pressing it a second time immediately:

- Stops the local headset playback.
- Cancels the remaining queued soundboard playback.
- Releases the PTT key immediately.
- Returns VoiceGuard to normal live mode without waiting for the clip to finish.

This allows a soundboard transmission to be interrupted at any point without having to wait for the remainder of a long clip.

### Soundboard Volume and Testing

Each soundboard clip can have its own playback volume from **0% to 150%**.

The **Test** button beside each soundboard volume control previews the clip locally through the computer's normal Windows playback device. Test playback is not sent through the VoiceGuard/VB-CABLE output path.

## Diagnostic Logging

VoiceGuard includes a separate detailed diagnostic logger intended to make troubleshooting easier when users encounter problems.

The normal on-screen log remains focused on useful user-facing events, while the diagnostic log records additional technical information such as:

- VoiceGuard startup and session information
- Windows/.NET and process architecture information
- Application and model paths
- Whisper runtime and acceleration information
- Audio device and engine information
- Game PTT and VoiceGuard Trigger activity
- Soundboard activity
- Recognition and filtering events
- Exceptions and unexpected application errors

Diagnostic logs are written to the VoiceGuard `Logs` directory when possible. If the installed application directory is not writable, VoiceGuard falls back to its local application-data location.

Diagnostic logs are rotated when they reach the configured size limit so they do not grow indefinitely.

## Features

- Designed primarily as a gaming profanity filter for PTT-based voice chat
- Dedicated configurable VoiceGuard Trigger Key
- Separate configurable Game PTT Key automatically controlled by VoiceGuard
- Filtered transmission with the configured delay applied once
- Automatic Game PTT hold through the complete delayed transmission
- Live microphone passthrough when no filtered transmission is active
- Local Whisper speech recognition
- Optional Intel OpenVINO/NPU Whisper acceleration
- CPU Whisper fallback
- Configurable blocked words and phrases
- Transcription aliases
- Per-word replacement sounds, playback-duration settings, and volume controls
- Replacement audio conversion to WAV/PCM
- 5-second maximum replacement-audio limit
- Per-word replacement volume up to 150%
- Master output volume control up to 150%
- Local replacement-sound Test playback
- Adjustable filtering delay
- Optional Soundboard feature with spoken triggers and aliases
- Soundboard waveform selection and long-form audio playback
- Soundboard audio playback through the user's headset while transmitting through the voice-chat output path
- Soundboard PTT remains held for the complete transmission
- Second-press Soundboard key cancellation with immediate playback stop and PTT release
- Soundboard volume control up to 150%
- Local Soundboard Test playback
- Global Start/Stop hotkey
- Optional minimize-to-system-tray support
- Optional start with Windows
- Single-instance protection to prevent multiple VoiceGuard audio engines from running at once
- Input/output device selection
- Persistent settings stored in the user's local application data

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

VoiceGuard is intended to sit between your microphone and the game's voice input. During a filtered transmission, the VoiceGuard Trigger Key starts private capture and VoiceGuard automatically controls the game's PTT key for the delayed, filtered output. When no filtered transmission is active, VoiceGuard passes the microphone audio through live.

Personally I use VoiceMeeter Banana in conjunction with this to switch from direct Mic input and VG depending on the game to conserve resources

## Settings and Persistence

VoiceGuard automatically saves its configuration, including blocked words, aliases, replacement-sound assignments, replacement playback-duration settings, replacement volumes, soundboard phrases and aliases, soundboard audio assignments, soundboard volumes, soundboard listen key, master output volume, delay, **VoiceGuard Trigger Key**, **Game PTT Key**, and selected audio devices.

Settings are stored under:

    %LOCALAPPDATA%\\VoiceGuard\\config.json

## License

VoiceGuard is licensed under the **MIT License**.

See the [LICENSE](LICENSE) file for the complete license text.

Copyright (c) 2026 superchilpil.

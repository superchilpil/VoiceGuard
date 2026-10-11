## Standby Recognition Gating
- Added a defensive gate so profanity-recognition windows are only queued during an active VoiceGuard PTT segment.
- Normal live microphone passthrough remains active in standby; the dedicated soundboard-listen path remains separate.
- Whisper stays loaded and ready to avoid model-load delay when the trigger is pressed; idle inference is not performed.

## Reduced CPU Contention
- Limited Whisper to four CPU threads so speech recognition is less likely to monopolize the processor during gaming.
- Intel NPU acceleration remains enabled where supported; CPU work is still required for parts of recognition.

## Soundboard Alias Persistence
- Fixed soundboard aliases not being restored when VoiceGuard starts.
- Saved aliases now load against their matching soundboard clips and remain available for speech detection.

## What's New

### In-App Updates
- Added a **Check for updates** button directly in VoiceGuard.
- VoiceGuard automatically checks for newer releases when it starts.
- When an update is available, the button changes to **Update to X.Y.Z**.
- The updater downloads the new installer and launches it automatically.
- VoiceGuard closes before the installer replaces the installed application.

### Soundboard Timing Fix
- Fixed the timing of **local soundboard playback**.
- The soundboard audio heard through your headset is now synchronized with the configured VoiceGuard delay.
- The local copy no longer starts immediately while the game receives the delayed version.
- Local and transmitted soundboard audio now start in sync.
- Soundboard PTT remains held for the complete delayed transmission.

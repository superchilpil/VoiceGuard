## UI Redesign (Testing Branch)
- Introduced a JTG-RGB-inspired sidebar navigation and focused content pages while retaining VoiceGuard's existing purple color palette.
- Grouped the existing controls, blocked-word list, soundboard, and logs into navigable views without changing audio-processing behavior.
- This redesign is being validated on the `ui/jtg-rgb-redesign` branch before it is merged or released.

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

## UI Redesign Follow-up (Testing Branch)
- Replaced the combined Dashboard & Audio view with a dedicated dashboard featuring a VoiceGuard status hero, Start/Stop action, readiness card, PTT key and audio-delay cards, and quick setup guidance.
- Added a separate Audio navigation page and kept Word Filter, Soundboard, and Logs as focused pages.
- Kept the existing audio engine and PTT passthrough logic unchanged.
- The redesign remains under validation on `ui/jtg-rgb-redesign`; it is not a stable release.

# Stage 6.5.5 — Whisper Warm-Up + Repeated Profanity Event Tracking

This revision keeps the Stage 6.5.5 GUI hard-boundary layout and adds two detection changes:

1. **Whisper warm-up**
   - After `base.en` is loaded, VoiceGuard runs one silent 0.75-second inference.
   - The first real PTT phrase therefore does not pay the normal native Whisper initialization cost.
   - The warm-up transcription is discarded.

2. **Occurrence-aware profanity events**
   - Every blocked-word occurrence found in a single Whisper phrase is turned into its own observation.
   - Adjacent repetitions are associated by temporal center rather than broad interval overlap.
   - Separate occurrences such as `bitch bitch bitch` therefore create separate candidate events instead of being collapsed into one event.
   - Overlapping Whisper windows can still reinforce the same spoken occurrence.
   - Candidate events are logged with an event number for easier testing.

The existing 60 ms censor pre-roll and 300 ms post-roll remain unchanged.

## Build note

The source package was inspected after modification. The available build environment does not contain the .NET SDK, so a local Windows build should be performed with `BUILD.bat` before deployment.

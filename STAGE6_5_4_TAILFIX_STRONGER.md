# VoiceGuard Stage 6.5.4 — Stronger Censor Tail Fix

This build increases the hard-censor safety envelope around detected profanity:
- Pre-roll: 60 ms
- Post-roll: 300 ms

The output path remains hard-censored by absolute source-time intervals. Replacement audio is written first, and any remaining portion of the censor interval is explicitly muted, so original speech cannot leak through the replacement.

This is a source package. The current environment does not have the .NET SDK installed, so the executable was not compiled here.

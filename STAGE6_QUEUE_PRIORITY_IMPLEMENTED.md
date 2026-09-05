# VoiceGuard Stage 6 — Queue Priority Implemented

Based on the working Stage 6 low-latency source.

Implemented:
- newest-first recognition scheduling
- queue depth reporting at dispatch/completion
- detection-headroom diagnostics against the delayed output cursor
- preserved 750 ms windows / 250 ms step
- preserved 3-second safety buffer
- preserved consensus/deduplication and aliases

The source was not compiled in this environment. Build on Windows with BUILD.bat.

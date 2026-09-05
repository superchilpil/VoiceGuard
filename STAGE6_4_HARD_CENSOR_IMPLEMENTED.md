# Stage 6.4 Hard Censor

The delayed output now applies censor regions by absolute PCM overlap for every WaveOut buffer. Any overlap is forcibly muted or overwritten with the assigned replacement sound. PTT remains a hard gate: non-PTT capture is represented only by silence in the delayed timeline.

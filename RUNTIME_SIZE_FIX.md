# Runtime Size Fix

The project previously used `Whisper.net.AllRuntimes`, which intentionally
bundles all available Whisper runtimes.

For the Windows x64 VoiceGuard release build, this has been changed to the
CPU-only combination:

- `Whisper.net` 1.9.1
- `Whisper.net.Runtime` 1.9.1

This avoids packaging CUDA/Vulkan/other platform runtimes that are not required
by the current VoiceGuard build.

The one-click build also enables single-file compression and refuses to continue
to Inno Setup if the generated EXE is over 500 MB.

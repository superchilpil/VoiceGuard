VoiceGuard 6.6.6 — VoiceGuard Trigger / Automatic Game PTT

Normal VoiceGuard transmission now uses two independent configurable keys:
- VoiceGuard Trigger Key: hold to privately capture and filter speech.
- Game PTT Key: VoiceGuard automatically presses this key after the configured delay and holds it while the delayed filtered audio drains.

The trigger key is not sent to the game. The Game PTT key is injected with the Windows SendInput keyboard API using physical scan-code events for better compatibility with games that read keyboard input at a lower level than normal window messages.

If the game is running elevated (Administrator), Windows may require VoiceGuard to run at the same elevation level before injected keyboard input can reach the game. Some games/anti-cheat systems can also intentionally reject synthetic keyboard input; in that case VoiceGuard cannot force the game to accept it.

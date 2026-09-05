# Stage 6.5.5 — Repeated Profanity / GUI Redesign

## Detection fix
The detector previously called `FindWordOccurrence`, which returned only the first regex match in a Whisper phrase. Stage 6.5.5 scans all occurrences of the blocked word and configured aliases, creates one acoustic observation per occurrence, and keeps repeated occurrences separated unless their acoustic ranges overlap or are within 100 ms.

## GUI redesign
The main window is now organized into three columns:
1. Left: Input → Output → Download/Load base.en → Start/Stop, followed by Delay/PTT settings and status.
2. Middle: Blocked words, Add/Remove, and right-click word actions.
3. Right: diagnostic logs with a dark, monospace display and horizontal scrolling.

## Testing note
The source package was inspected after modification. The current execution environment has no `dotnet` executable, so a local compile/publish could not be performed here.


## GUI bounds fix
- Replaced the middle blocked-word TableLayoutPanel with a docked Panel layout.
- The blocked-word list now fills only the available center area.
- Add/Remove controls and the right-click hint are permanently docked below the list.
- Set `IntegralHeight=false` and enabled horizontal scrolling so list items remain selectable and visible as the list grows.
- This specifically fixes the reported issue where a newly added word could appear outside the usable/interactable bounds.

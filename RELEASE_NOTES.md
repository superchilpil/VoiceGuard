## UI Layout Corrections (Testing Branch)
- Increased the dashboard status card height and repositioned Start/Stop inside its bounds, aligned to the right of the status copy.
- Changed the Audio page to a single full-width column so the microphone and virtual-cable output cards and dropdowns are not squeezed into a partial-width column; the output volume control remains included.
- Increased Settings card and row sizing so delay, PTT, trigger/listen keys, start/stop hotkey, and startup/tray controls have dedicated visible cards.
- Audio engine and PTT passthrough behavior are unchanged. These are layout corrections on `ui/jtg-rgb-redesign` and require visual verification in the Windows build.

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

## UI Feedback Pass
- Reduced the window's default and minimum dimensions to make the workspace more compact.
- Added icon-like glyphs to the sidebar navigation and a dedicated Settings page with overlay configuration.
- Removed the duplicate readiness card and hid the redundant top-right readiness badge; the dashboard status now reads "READY WHEN YOU ARE" in the main status card.
- Added rounded clipping and borders to dashboard cards while retaining the VoiceGuard purple palette.
- Kept "Check for updates" in the top-right header.

## Audio, Word Filter, and Soundboard Styling
- Rebuilt the Audio page into separate rounded cards for device selection and volume, transmission controls, hotkeys, and preferences/status.
- Added rounded clipping to the Word Filter and Soundboard page surfaces and their lists, keeping their existing behavior and controls.
- Reused existing audio and key controls rather than replacing their handlers or routing logic.

## Settings Consolidation and Compact Layout
- Reduced the default/minimum window width by another 25%, to 825 px.
- Moved delay, PTT key, VoiceGuard trigger key, soundboard listen key, start/stop hotkey, Windows startup, and minimize-to-tray controls into Settings.
- Added the Jack The Gooner logo to the bottom area of Settings.
- Removed the Overlay settings button from the top-right header; overlay configuration is now accessed from Settings.
- Rounded the Check for updates button and corrected the dashboard PTT value to initialize from the configured key and refresh after settings load.

## UI Cleanup Pass
- Reworked the dashboard status card to use responsive widths and smaller title typography so its headline/status no longer run outside the card at the compact window width.
- Simplified Audio to only microphone input, virtual-cable output, and output volume; transmission and hotkey controls remain in Settings.
- Applied dark purple owner-drawn styling to the audio device dropdowns.
- Rounded individual blocked-word and soundboard item cards, replacement/test surfaces, and their action buttons.
- Reorganized Settings sizing and startup/tray options to reduce overlap and keep the JTG logo at the bottom.
## Consistent Corner and Outline Pass
- Rounded the sidebar shell and every sidebar navigation item, and repositioned the sidebar footer to remain inside the available height.
- Standardized button outline strokes to the same VoiceGuard border color instead of accent-colored top/left and darker bottom/right effects.
- Kept the existing VoiceGuard purple palette and avoided changes to audio processing.
## UI Rendering Fixes
- Adjusted dashboard card outline drawing and headline sizing to avoid clipped text and partially rendered borders.
- Removed selected-item blue/purple fill and focus highlighting from Word Filter and Soundboard rows.
- Reduced Audio device selector minimum widths and applied rounded clipping to dropdown controls.
- Applied rounded clipping to Settings text/numeric fields and removed their native inset borders.
## Layout Refinement
- Expanded VoiceGuard status across the dashboard and restored the Start VoiceGuard button in that card; removed the redundant PTT overview card.
- Split Audio into separate microphone-input and virtual-cable-output cards, placed vertically, and removed the pointless page scrollbar.
- Converted Settings to a single-column stack of individual setting cards.
- Removed native list borders that were showing hover/focus outlines in Word Filter and Soundboard, and removed standard button outlines for a cleaner rounded style.
## Dashboard, Audio, and Settings Follow-up
- Moved the dashboard Start/Stop control to the right side of VoiceGuard Status and applied rounded corners.
- Made the Audio input/output cards fill their available rows, with proportional vertical sizing so both cards are visible.
- Explicitly assigned each Settings card to its own row, including startup/tray options, to prevent controls disappearing from the stack.

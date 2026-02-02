# Changelog

## v1.3.2
- **Fix:** Activation logic completely rewritten to prevent "false positives" where a point was marked activated but its state hadn't changed. The mod now waits and verifies a state change.
- **Fix:** Activation now prioritizes using the game's `StateSetRPC` to ensure in-game UI (map markers, etc.) are correctly updated for all players. Falls back to `ButtonPress` only if necessary.
- **Fix:** `IsPointActivatableNow` logic is now stricter, checking that a point's state is `Idle` or `None` before considering it for activation.
- **Fix:** Removed permanent `_activated` marking for points that are temporarily unavailable, allowing them to be reconsidered later.
- **Enhancement:** Added a file logger (`BepInEx/config/REPO_Active/logs/`) which is on by default to aid in troubleshooting.
- **Enhancement:** Plugin version and manifest version are now aligned.

## v1.3.1 (Previous Internal Build)
- **Fix:** Implemented a state machine to prevent logic from running during level loading, fixing the "stuck on black screen" bug.
- **Fix:** Changed `ActivationKey` config to a string to ensure it appears in in-game config UIs.
- **Feature:** Added a file-based logger for diagnostics.

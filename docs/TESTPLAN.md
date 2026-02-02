# REPO_Active Test Plan

## Environment
- Windows 10
- r2modman profile: Default
- Game: REPO (Steam)

## Scenarios
### S1: Singleplayer / Offline
- Start game
- Confirm config shows 3 options in-game
- Press X to activate next point
- Observe: point activates + in-game UI / map marker behaves like vanilla

### S2: Multiplayer (Host)
- Create room (host)
- Ensure not stuck at loading
- Auto activate ON: verify it activates when allowed
- Manual X: activates next
- Verify: activation triggers same broadcast / marker as vanilla

### S3: Multiplayer (Client)
- Join host
- Ensure client does not activate
- Discovery sync: client discovers point -> host marks discovered too (and vice versa)

## Evidence to capture
- BepInEx console excerpts (only relevant lines)
- A short “what I did / what happened”
# REPO_Active 4.5.0

A lightweight BepInEx plugin that remotely activates extraction points using the game’s native `ExtractionPoint.OnClick()` path. This preserves full in‑game feedback (broadcast, markers, money, etc.) while supporting manual activation and optional auto‑activation with discovery filtering.

## Features
- **Native activation**: calls `ExtractionPoint.OnClick()` via reflection (same chain as in‑game click).
- **Planned order**: the extraction point closest to spawn is always the first target; the rest follow a nearest‑neighbor plan.
- **Safe gating**: activates only when no other extraction point is currently active.
- **Discovery filter**: when `DiscoverAllPoints=false`, only discovered points are eligible.
- **Multiplayer (host)**: host uses all players’ positions (PhotonView objects) to mark discoveries.

## Keybinds
- **F3**: Activate next extraction point (planned list)

## Configuration
Generated at:
`BepInEx\config\angelcomilk.repo_active.cfg`

- `AutoActivate` (bool): Auto‑activate at a fixed interval.
- `ActivateNearest` (KeyCode): Manual activation key (default F3).
- `DiscoverAllPoints` (bool): If true, treat all points as discovered.

## Installation (r2modman)
1. Import the zip in r2modman.
2. Ensure the DLL is placed under:
   `BepInEx\plugins\REPO_Active\REPO_Active.dll`

## Notes
- Multiplayer discovery is **host‑side**; clients do not run the player‑position aggregation.
- Discovery interval is fixed once per round based on player count for performance.

## Credits
Author: **AngelcoMilk-天使棉**

# REPO_Active 3.5.3

## What changed
- Replace activation with: Reflection call to `ExtractionPoint.OnClick()` (F3-like native click path).
- Queue and nearest activation both support excluding spawn-nearest extraction point.
- Designed to preserve "far activation" + "broadcast" behavior (native OnClick path).

## Keybinds
- F3: Activate nearest EP (excluded spawn-nearest if enabled)
- F4: Build queue (sorted by distance) and activate sequentially

## Build
1. Open `src/REPO_Active/REPO_Active.csproj`
2. Set `UnityManagedPath` to:
   `...\REPO\REPO_Data\Managed`
3. Build `Release`
4. Put `REPO_Active.dll` into:
   `BepInEx/plugins/REPO_Active/REPO_Active.dll`

## Runtime Config
Generated at:
`BepInEx/config/angelcomilk.repo_active.cfg`

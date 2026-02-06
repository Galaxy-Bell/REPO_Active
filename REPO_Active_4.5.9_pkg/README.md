# REPO_Active v4.5.9

A lightweight BepInEx plugin for REPO that **remotely activates extraction points via the native `ExtractionPoint.OnClick()` chain**. This preserves the game鈥檚 full feedback path (broadcast + marker + money) while providing a stable, planned activation order, manual control, and optional auto mode.

## Why It Exists (Pain Points Solved)
- **Remote activation that still feels 鈥渘ative鈥?*: uses the same OnClick logic as in-game interaction.
- **Predictable order**: plans a path so you don鈥檛 waste time running back and forth.
- **Safety-first**: will not activate a new point if another is already active.
- **Multiplayer friendly (host)**: discovery can consider all players鈥?positions.

## Features
- **Native activation**: reflection call to `ExtractionPoint.OnClick()`.
- **Planned order**:
  - First target = extraction point closest to spawn.
  - Remaining targets follow a nearest-neighbor plan from current player position.
- **Dynamic planning**: plan is rebuilt when you trigger activation.
- **Safe gating**: activates only if **no other extraction point is currently active**.
- **Discovery filter**: when `DiscoverAllPoints=false`, only discovered points are eligible.
- **Multiplayer (host)**: discovery uses **all players鈥?positions** (host only).

## Keybind
- **F3**: Activate next extraction point (planned list)

## Configuration
Config file:
`BepInEx\config\angelcomilk.repo_active.cfg`

- `AutoActivate` (bool): Auto-activate at a fixed interval.
- `ActivateNearest` (KeyCode): Manual activation key (default F3).
- `DiscoverAllPoints` (bool): If true, treat all points as discovered.

## Manual vs Auto
- **Manual (F3)**: Runs the same planning + safety checks and activates one point.
- **Auto**: Periodically triggers the same F3 logic (no special activation path).

## How It Chooses the Next Point
1. Capture **spawn position** from the first valid reference position.
2. Build the list of eligible extraction points (respects discovery filter).
3. Fix the **spawn-nearest point as the first target**.
4. Sort the rest using **nearest-neighbor** from current player position.
5. If any extraction point is active, **do not activate**.

## Installation (r2modman)
1. Import the zip in r2modman.
2. Ensure the DLL is at:
   `BepInEx\plugins\REPO_Active\REPO_Active.dll`

## Notes
- Multiplayer discovery is **host-side only**. Clients do not aggregate positions.
- Discovery polling interval is fixed per round based on player count (performance-friendly).

## Credits
Author: **AngelcoMilk-澶╀娇妫?*

---

# REPO_Active v4.5.9锛堜腑鏂囪鏄庯級

杩欐槸涓€涓交閲忕殑 REPO 妯＄粍锛岄€氳繃 **鍘熺敓 `ExtractionPoint.OnClick()` 閾捐矾**杩滅▼婵€娲绘彁鍙栫偣锛屼繚鐣欐父鎴忓畬鏁村弽棣堬紙骞挎挱/鐧界偣/閲戦锛夛紝骞舵彁渚涚ǔ瀹氱殑瑙勫垝椤哄簭銆佹墜鍔ㄦ帶鍒朵笌鍙€夎嚜鍔ㄦā寮忋€?
## 瑙ｅ喅鐨勭棝鐐?- **杩滅▼婵€娲讳絾浠嶄繚鐣欏師鐢熷弽棣?*锛氫娇鐢ㄦ父鎴忓唴鍚屾牱鐨?OnClick 閫昏緫銆?- **鍙帶涓旂ǔ瀹氱殑婵€娲婚『搴?*锛氬噺灏戞潵鍥炶窇鍥剧殑鏃堕棿娴垂銆?- **瀹夊叏闃诲**锛氭湁婵€娲讳腑鐨勬彁鍙栫偣鏃朵笉浼氳Е鍙戞柊婵€娲汇€?- **澶氫汉鍙敤锛堜富鏈猴級**锛氬彂鐜伴€昏緫鍙€冭檻鎵€鏈夌帺瀹朵綅缃€?
## 鍔熻兘鐗规€?- **鍘熺敓婵€娲?*锛氬弽灏勮皟鐢?`ExtractionPoint.OnClick()`銆?- **瑙勫垝椤哄簭**锛?  - 绗竴涓洰鏍囷細绂诲嚭鐢熺偣鏈€杩戠殑鎻愬彇鐐广€?  - 鍏朵綑鐐癸細浠庣帺瀹跺綋鍓嶄綅缃繘琛屸€滄渶杩戦偦鈥濇帓搴忋€?- **鍔ㄦ€佽鍒?*锛氭瘡娆¤Е鍙戞縺娲婚兘浼氶噸寤鸿鍒掗『搴忋€?- **瀹夊叏闃诲**锛氬瓨鍦ㄦ縺娲讳腑鐨勬彁鍙栫偣鏃朵笉婵€娲绘柊鐐广€?- **鍙戠幇杩囨护**锛歚DiscoverAllPoints=false` 鏃讹紝浠呭凡鍙戠幇鐐瑰彲婵€娲汇€?- **澶氫汉锛堜富鏈猴級**锛氬彂鐜伴€昏緫浣跨敤鎵€鏈夌帺瀹朵綅缃€?
## 蹇嵎閿?- **F3**锛氭縺娲讳笅涓€鎻愬彇鐐癸紙鎸夎鍒掗『搴忥級

## 閰嶇疆鏂囦欢
`BepInEx\config\angelcomilk.repo_active.cfg`

- `AutoActivate`锛坆ool锛夛細鑷姩鎸夊浐瀹氶棿闅旀縺娲汇€?- `ActivateNearest`锛圞eyCode锛夛細鎵嬪姩婵€娲绘寜閿紙榛樿 F3锛夈€?- `DiscoverAllPoints`锛坆ool锛夛細鏄惁瑙嗕负鍏ㄥ浘宸插彂鐜般€?
## 鎵嬪姩涓庤嚜鍔?- **鎵嬪姩锛團3锛?*锛氳繍琛岃鍒?+ 瀹夊叏妫€鏌ワ紝浠呮縺娲讳竴涓偣銆?- **鑷姩**锛氬畾鏃惰Е鍙戝悓涓€濂?F3 閫昏緫锛堟棤鐗规畩璺緞锛夈€?
## 婵€娲婚『搴忚鏄?1. 棣栨鑾峰彇鍙傝€冧綅缃椂璁板綍 **鍑虹敓鐐?*銆?2. 鏋勫缓绗﹀悎鏉′欢鐨勬彁鍙栫偣鍒楄〃锛堥伒瀹堝彂鐜拌繃婊わ級銆?3. 鍥哄畾 **绂诲嚭鐢熺偣鏈€杩戠殑鐐逛负绗竴涓洰鏍?*銆?4. 鍏朵綑鎸夆€滄渶杩戦偦鈥濅粠鐜╁浣嶇疆鎺掑簭銆?5. 鑻ュ凡鏈夋縺娲荤偣锛屽垯涓嶅啀婵€娲绘柊鐐广€?
## 瀹夎锛坮2modman锛?1. 鍦?r2modman 涓鍏?zip銆?2. 纭繚 DLL 璺緞锛?   `BepInEx\plugins\REPO_Active\REPO_Active.dll`

## 璇存槑
- 澶氫汉鍙戠幇閫昏緫鐢?**涓绘満** 鎵ц锛屽鎴风涓嶅仛浣嶇疆鑱氬悎銆?- 鍙戠幇鎵弿闂撮殧鍦ㄦ瘡灞€寮€濮嬫椂鏍规嵁浜烘暟鍥哄畾锛屽噺灏戞€ц兘娑堣€椼€?
## 浣滆€?**AngelcoMilk-澶╀娇妫?*




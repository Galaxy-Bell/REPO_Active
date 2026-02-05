# REPO_Active 4.5.0

A lightweight BepInEx plugin that remotely activates extraction points using the game’s native `ExtractionPoint.OnClick()` path. This preserves full in‑game feedback (broadcast, markers, money, etc.) while supporting manual activation and optional auto‑activation with discovery filtering.

## Features
- **Native activation**: calls `ExtractionPoint.OnClick()` via reflection (same chain as in‑game click).
- **Planned order**: the extraction point closest to spawn is always the first target; the rest follow a nearest‑neighbor plan.
- **Dynamic planning**: the plan is rebuilt when activation is triggered, so the player’s current position is considered.
- **Safe gating**: activates only when no other extraction point is currently active.
- **Discovery filter**: when `DiscoverAllPoints=false`, only discovered points are eligible.
- **Multiplayer (host)**: host uses all players’ positions (PhotonView objects) to mark discoveries.

## How It Chooses the Next Point
1. Capture **spawn position** on the first valid reference position.
2. Build a list of eligible extraction points (respecting discovery).
3. Pick the **spawn‑nearest point as the first target**.
4. For the rest, use a **nearest‑neighbor** order from the current player position.
5. Before activating, ensure **no extraction point is currently active**.

## Keybinds
- **F3**: Activate next extraction point (planned list)

## Configuration
Generated at:
`BepInEx\config\angelcomilk.repo_active.cfg`

- `AutoActivate` (bool): Auto‑activate at a fixed interval.
- `ActivateNearest` (KeyCode): Manual activation key (default F3).
- `DiscoverAllPoints` (bool): If true, treat all points as discovered.

## Manual vs Auto
- **Manual**: pressing F3 runs the same planning + safety checks and activates one point.
- **Auto**: periodically triggers the same F3 logic (no special activation path).

## Installation (r2modman)
1. Import the zip in r2modman.
2. Ensure the DLL is placed under:
   `BepInEx\plugins\REPO_Active\REPO_Active.dll`

## Notes
- Multiplayer discovery is **host‑side**; clients do not run the player‑position aggregation.
- Discovery interval is fixed once per round based on player count for performance.

## Credits
Author: **AngelcoMilk-天使棉**

---

# REPO_Active 4.5.0（中文说明）

这是一个轻量级 BepInEx 插件，通过游戏原生 `ExtractionPoint.OnClick()` 逻辑远程激活提取点，保留完整反馈（广播、标记、金额等），支持手动激活与可选的自动激活，并可根据“已发现”过滤。

## 功能特点
- **原生激活链路**：反射调用 `ExtractionPoint.OnClick()`（等同游戏内点击）。
- **规划顺序**：离出生点最近的提取点始终作为第一个目标，其余按“最近邻”规划。
- **动态排序**：每次触发激活时重新规划，玩家当前位置会被纳入计算。
- **安全阻塞**：仅在没有其他提取点处于激活状态时才触发新激活。
- **发现过滤**：当 `DiscoverAllPoints=false` 时，仅已发现的点才会参与激活。
- **多人（主机）**：由主机汇总玩家位置（PhotonView）进行“发现”判定。

## 选择顺序说明
1. 首次获取到有效参考位置后记录 **出生点**。
2. 构建符合条件的提取点列表（遵守发现规则）。
3. **出生点最近的提取点**固定为第一个目标。
4. 其余点按“**从玩家当前位置出发的最近邻**”排序。
5. 激活前检查 **是否已有点处于激活状态**，有则阻塞。

## 快捷键
- **F3**：激活下一个提取点（按规划列表）

## 配置文件
生成路径：
`BepInEx\config\angelcomilk.repo_active.cfg`

- `AutoActivate`（bool）：是否自动按固定间隔激活。
- `ActivateNearest`（KeyCode）：手动激活按键（默认 F3）。
- `DiscoverAllPoints`（bool）：是否视为全图已发现。

## 手动与自动
- **手动**：按 F3 走完整规划 + 安全判断，只激活一个点。
- **自动**：定期触发同一套 F3 逻辑（没有额外特殊路径）。

## 安装（r2modman）
1. 在 r2modman 中导入 zip。
2. 确保 DLL 位于：
   `BepInEx\plugins\REPO_Active\REPO_Active.dll`

## 说明
- 多人模式下的发现逻辑由**主机**执行，客户端不参与位置聚合。
- 发现扫描间隔会根据玩家数量在每局开始时固定，以控制性能开销。

## 作者
**AngelcoMilk-天使棉**

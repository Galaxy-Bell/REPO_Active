# REPO_Active v4.5.5

A lightweight BepInEx plugin for REPO that **remotely activates extraction points via the native `ExtractionPoint.OnClick()` chain**. This preserves the game’s full feedback path (broadcast + marker + money) while providing a stable, planned activation order, manual control, and optional auto mode.

## Why It Exists (Pain Points Solved)
- **Remote activation that still feels “native”**: uses the same OnClick logic as in-game interaction.
- **Predictable order**: plans a path so you don’t waste time running back and forth.
- **Safety-first**: will not activate a new point if another is already active.
- **Multiplayer friendly (host)**: discovery can consider all players’ positions.

## Features
- **Native activation**: reflection call to `ExtractionPoint.OnClick()`.
- **Planned order**:
  - First target = extraction point closest to spawn.
  - Remaining targets follow a nearest-neighbor plan from current player position.
- **Dynamic planning**: plan is rebuilt when you trigger activation.
- **Safe gating**: activates only if **no other extraction point is currently active**.
- **Discovery filter**: when `DiscoverAllPoints=false`, only discovered points are eligible.
- **Multiplayer (host)**: discovery uses **all players’ positions** (host only).

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
Author: **AngelcoMilk-天使棉**

---

# REPO_Active v4.5.5（中文说明）

这是一个轻量的 REPO 模组，通过 **原生 `ExtractionPoint.OnClick()` 链路**远程激活提取点，保留游戏完整反馈（广播/白点/金额），并提供稳定的规划顺序、手动控制与可选自动模式。

## 解决的痛点
- **远程激活但仍保留原生反馈**：使用游戏内同样的 OnClick 逻辑。
- **可控且稳定的激活顺序**：减少来回跑图的时间浪费。
- **安全阻塞**：有激活中的提取点时不会触发新激活。
- **多人可用（主机）**：发现逻辑可考虑所有玩家位置。

## 功能特性
- **原生激活**：反射调用 `ExtractionPoint.OnClick()`。
- **规划顺序**：
  - 第一个目标：离出生点最近的提取点。
  - 其余点：从玩家当前位置进行“最近邻”排序。
- **动态规划**：每次触发激活都会重建规划顺序。
- **安全阻塞**：存在激活中的提取点时不激活新点。
- **发现过滤**：`DiscoverAllPoints=false` 时，仅已发现点可激活。
- **多人（主机）**：发现逻辑使用所有玩家位置。

## 快捷键
- **F3**：激活下一提取点（按规划顺序）

## 配置文件
`BepInEx\config\angelcomilk.repo_active.cfg`

- `AutoActivate`（bool）：自动按固定间隔激活。
- `ActivateNearest`（KeyCode）：手动激活按键（默认 F3）。
- `DiscoverAllPoints`（bool）：是否视为全图已发现。

## 手动与自动
- **手动（F3）**：运行规划 + 安全检查，仅激活一个点。
- **自动**：定时触发同一套 F3 逻辑（无特殊路径）。

## 激活顺序说明
1. 首次获取参考位置时记录 **出生点**。
2. 构建符合条件的提取点列表（遵守发现过滤）。
3. 固定 **离出生点最近的点为第一个目标**。
4. 其余按“最近邻”从玩家位置排序。
5. 若已有激活点，则不再激活新点。

## 安装（r2modman）
1. 在 r2modman 中导入 zip。
2. 确保 DLL 路径：
   `BepInEx\plugins\REPO_Active\REPO_Active.dll`

## 说明
- 多人发现逻辑由 **主机** 执行，客户端不做位置聚合。
- 发现扫描间隔在每局开始时根据人数固定，减少性能消耗。

## 作者
**AngelcoMilk-天使棉**
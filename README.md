# REPO_Active v4.6.3

A lightweight BepInEx plugin for REPO that **remotely activates extraction points via the native ExtractionPoint.OnClick() chain**. It preserves full in‑game feedback (broadcast + marker + reward) while providing a stable planned order, manual control, and optional auto mode.

Are you overwhelmed by R.E.P.O.’s complex maps and the large number of extraction points? This mod reduces unnecessary backtracking and noticeably improves the overall flow.

## Why It Exists (Pain Points Solved)
- **Remote activation but still native**: uses the same OnClick logic as in‑game interaction; the result matches manual button presses.
- **Predictable order**: dynamic route planning reduces backtracking; the plan fixes the **spawn‑nearest point as the first target**, orders the rest by **nearest‑neighbor from the player’s position**, and guarantees the **last target is the remaining point closest to spawn**.
- **Safety‑first**: will not activate a new point if another is already active.
- **Multiplayer friendly (host)**: discovery can consider all players’ positions and is host‑authoritative.

## Features
- **Native activation**: reflection call to ExtractionPoint.OnClick().
- **Planned order**: first target is the extraction point closest to spawn; remaining points follow a nearest‑neighbor order from current player position.
- **Dynamic planning**: the plan is rebuilt each time you trigger activation.
- **Safe gating**: activates only if **no extraction point is currently active**.
- **Discovery filter**: when DiscoverAllPoints=false, only discovered points are eligible.
- **Multiplayer (host)**: discovery uses **all players’ positions** (host only).

## Keybind
- **F3**: Activate next extraction point (planned list)

## Configuration
Config file:
BepInEx\\config\\angelcomilk.repo_active.cfg

- AutoActivate (bool): Auto‑activate at a fixed interval.
- ActivateNearest (KeyCode): Manual activation key (default F3).
- DiscoverAllPoints (bool): If true, treat all points as discovered.

## Manual vs Auto
- **Manual (F3)**: Runs the same planning + safety checks and activates one point.
- **Auto**: Periodically triggers the same F3 logic (no special activation path).

## How It Chooses the Next Point
1. Capture **spawn position** from the first valid reference position.
2. Build the list of eligible extraction points (respects discovery filter).
3. Fix the **spawn‑nearest point as the first target**.
4. Sort the rest using **nearest‑neighbor** from current player position.
5. If any extraction point is active, **do not activate**.

## Installation (r2modman)
1. Import the zip in r2modman.
2. Ensure the DLL is at:
   BepInEx\\plugins\\REPO_Active\\REPO_Active.dll

## Notes
- Multiplayer discovery is **host‑side only**. Clients do not aggregate positions.
- Discovery polling interval is fixed per round based on player count (performance‑friendly).

## Credits
Author: **AngelcoMilk-天使棉**

---

# REPO_Active v4.6.3（中文说明）

这是一个轻量的 REPO BepInEx 模组，通过 **原生 ExtractionPoint.OnClick() 链路**远程激活提取点，保留完整游戏反馈（广播 + 标记 + 奖励），并提供稳定的规划顺序、手动控制与可选自动模式。

# 前言

你是否因为 `R.E.P.O.` 复杂的地图与繁多的提取点而焦头烂额？这个模组可以帮助你减少跑图负担，并显著提升整体游戏体验。

## 解决的痛点

- **远程激活但仍保持原生体验**：使用与游戏内交互一致的 OnClick 逻辑，效果与手动按下按钮一致。
- **顺序可预期**：动态规划路径，减少反复折返；规划逻辑为：**出生点最近点固定为第一个**，其余点按玩家当前位置做**最近邻排序**，并确保**最后一个点为剩余点中最靠近出生点**。
- **安全优先**：当已有提取点处于激活中时，不会启动新的激活。
- **多人友好（主机）**：发现逻辑可结合所有玩家位置，由主机统一生效。

## 功能特性
- **原生激活**：反射调用 ExtractionPoint.OnClick()。
- **规划顺序**：第一个目标为出生点最近的提取点，其余点按玩家当前位置的最近邻顺序排列。
- **动态规划**：每次触发激活都会重新生成规划列表。
- **安全闸门**：只有在**没有任何提取点处于激活中**时才会触发新的激活。
- **发现过滤**：当 DiscoverAllPoints=false 时，仅已发现的点参与激活。
- **多人（主机）**：发现逻辑使用**所有玩家位置**（仅主机）。

## 快捷键
- **F3**：激活下一个提取点（按规划列表顺序）

## 配置文件
BepInEx\config\angelcomilk.repo_active.cfg

- AutoActivate（bool）：按固定间隔自动激活。
- ActivateNearest（KeyCode）：手动激活按键（默认 F3）。
- DiscoverAllPoints（bool）：是否视为全图已发现。

## 手动与自动
- **手动（F3）**：与自动模式使用相同规划与安全检查，一次只激活一个点。
- **自动**：周期性触发同一套 F3 逻辑（无特殊激活路径）。

## 选择下一个点的逻辑
1. 从首次有效参考位置捕获**出生点坐标**。
2. 构建符合条件的提取点列表（受发现过滤影响）。
3. 固定**出生点最近的点为第一个目标**。
4. 其余点按玩家当前位置进行**最近邻排序**。
5. 若已有点处于激活中，则**不触发新激活**。

## 安装（r2modman）
1. 在 r2modman 中导入 zip。
2. 确认 DLL 路径为：
   BepInEx\plugins\REPO_Active\REPO_Active.dll

## 说明
- 多人发现逻辑仅在**主机端**执行，客户端不会聚合位置。
- 发现扫描间隔在每局开始时会根据人数固定，降低性能开销。

## 作者
**AngelcoMilk-天使棉**
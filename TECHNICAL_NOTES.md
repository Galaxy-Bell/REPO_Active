# REPO_Active 技术文档（维护/二次开发）

**适用范围**
本文件面向二次开发与维护，记录当前实现的核心逻辑、曾验证过的方案、风险点与调试方式。内容以当前源码为准，路径基于 `C:\Users\Home\Documents\GitHub\REPO_Active`。

**核心目标**
1. 远程激活提取点必须走 `ExtractionPoint.OnClick()` 原生链路（确保金额+白点+广播）。
2. 支持手动（F3）与自动激活。
3. 支持“默认发现全图”与“按发现半径逐步加入”。
4. 按距离规划激活顺序，且保证只在无激活点时触发下一次激活。

**模块结构**
1. `src/REPO_Active/Plugin.cs`
2. `src/REPO_Active/Runtime/ExtractionPointScanner.cs`
3. `src/REPO_Active/Reflection/ExtractionPointInvoker.cs`
4. `src/REPO_Active/Debug/ModLogger.cs`

**激活链路（最关键）**
1. 扫描所有 `ExtractionPoint` 实例。
2. 选择下一目标点（按规划列表第一个）。
3. 通过反射调用 `ExtractionPoint.OnClick()`。
4. 仅当状态从 Idle/Complete 变为其他状态时，才 `MarkActivated`。

**排序与规划逻辑（当前版本）**
规则按以下顺序执行，适用于 F3 与自动模式：
1. 扫描所有提取点（`Object.FindObjectsOfType(Type)`）。
2. 可选：过滤已发现的点（`DiscoverAllPoints=false` 时启用）。
3. 跳过“已完成”与“已激活”的点。
4. 固定第一个点：离出生点最近。
5. 固定最后一个点：在剩余点里离出生点最近。
6. 中间点：最近邻贪心，从玩家当前位置开始逐步选择。

**发现逻辑**
1. `DiscoverAllPoints=true`：直接把所有提取点加入已发现集合。
2. `DiscoverAllPoints=false`：按照玩家位置半径（20m）做“发现”标记。
3. 多人主机模式时，优先使用 `GameDirector.instance.PlayerList` 遍历玩家位置，依次更新发现集合。
4. 若无法获取其他玩家位置，则回退到主机本地位置。

**自动模式逻辑**
1. 自动模式与 F3 共用同一激活流程。
2. 自动模式启用前需要“服务缓冲期”：
   - 先确认场景中已有提取点和可用参考位置。
   - 记录 `autoReadyTime`，等待 `AUTO_READY_BUFFER`（默认 30 秒）后再自动激活。
3. 自动激活间隔 `AUTO_INTERVAL` 默认 5 秒。

**阻塞/并发控制**
1. 任何时刻，只允许一个提取点处于“非 Idle 且非 Complete”的状态。
2. 判定依据：`ExtractionPoint.currentState` 字段（反射读取）。
3. 若判定为激活中，F3/自动都直接跳过。

**状态判定逻辑**
1. Idle 判断：字符串包含 `Idle`。
2. Completed 判断：包含 `success/complete/submitted/finish/done`。
3. 反射失败时，默认认为“激活中”，避免并发激活冲突。

**反射入口与验证（已反编译确认）**
1. `ExtractionPoint.OnClick()` 存在于 `Assembly-CSharp`。
2. `ExtractionPoint.currentState` 为字段（非属性）。
3. Photon 网络与玩家信息：`PhotonNetwork.InRoom/IsMasterClient/PlayerList`。
4. 多人发现：`GameDirector.instance.PlayerList` 为 `PlayerAvatar` 列表。

**日志系统**
1. 独立日志文件写入 `BepInEx\config\REPO_Active\logs\REPO_Active_*.log`。
2. r2modman profile 下路径类似：
   - `C:\Users\Home\AppData\Roaming\r2modmanPlus-local\REPO\profiles\<Profile>\BepInEx\config\REPO_Active\logs`
3. 日志仅在检测到提取点后进入“ready”，避免开局刷屏。
4. 主要日志标签：
   - `[SCAN]` 扫描耗时与数量
   - `[DISCOVER]` 发现增量
   - `[PLAN]` 排序规划结果
   - `[F3]` 激活逻辑与状态
   - `[AUTO]` 自动循环与缓冲

**配置项**
1. `AutoActivate`：自动模式开关。
2. `ActivateNearest`：手动激活按键，默认 F3。
3. `DiscoverAllPoints`：是否默认发现全图。
4. `EnableDebugLog`：日志开关。

**构建与打包**
1. `dotnet build .\src\REPO_Active\REPO_Active.csproj -c Release`
2. DLL 输出：`src\REPO_Active\bin\Release\netstandard2.1\REPO_Active.dll`
3. 打包结构：
   - `manifest.json`
   - `README.md`
   - `icon.png`
   - `BepInEx\plugins\REPO_Active\REPO_Active.dll`

**已尝试/验证过的方案（历史记录）**
1. `ButtonPress` 或 RPC 手动拼接：不稳定，已弃用。
2. `SpawnGate` 状态阻塞：已移除，改为统一时间缓冲。
3. “仅按距离排序”：被新规划逻辑替代（固定首点/末点 + 最近邻）。

**已删除/弃用逻辑**
1. 旧的 spawn 排除逻辑（`UpdateSpawnExcludeIfNeeded` 等）。
2. 旧的 `FindNearest` / `BuildSortedList` 简单排序方法。
3. `IsSpawnGateBlocking` 与 `_blockUntil`（不再参与运行）。

**风险清单**
1. 游戏更新后类名/字段名变动会导致反射失效。
2. `currentState` 字符串匹配依赖枚举名称，变更会影响判定。
3. OnClick 成功但状态延迟变更，可能导致重复尝试同一点。
4. 多人位置获取依赖 `GameDirector.PlayerList`，若修改将退化为主机单点发现。

**建议测试清单**
1. 单人：F3 连续激活，检查队列是否按预期。
2. 单人：自动模式全图发现，确认所有点激活完成。
3. 多人：DiscoverAllPoints=false，轮流靠近提取点，确认发现同步。
4. 多人：检查激活并发限制是否正常生效。
5. 回主菜单重进地图：确认缓冲时间与排序逻辑正确重置。

**维护建议**
1. 若发现“最后一个点不激活”，优先看日志中的 `[PLAN]` 与 `[F3]`。
2. 若出现激活阻塞，检查 `[F3] blocked: activating` 对应点状态。
3. 若发现逻辑异常，优先调整日志，再观察完整激活链路。

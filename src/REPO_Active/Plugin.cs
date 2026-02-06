using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;
using REPO_Active.Runtime;
using REPO_Active.Reflection;
using REPO_Active.Debug;

namespace REPO_Active
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "angelcomilk.repo_active";
        public const string PluginName = "REPO_Active";
        public const string PluginVersion = "4.5.6";

        // Verification notes (decompile cross-check):
        // - ExtractionPoint.OnClick(), ExtractionPoint.currentState -> VERIFIED in Assembly-CSharp\ExtractionPoint.cs.
        // - Photon.Pun.PhotonNetwork.InRoom / IsMasterClient / PlayerList -> VERIFIED in PhotonUnityNetworking\PhotonNetwork.cs.
        // - Photon.Pun.PhotonView.OwnerActorNr -> VERIFIED in PhotonUnityNetworking\PhotonView.cs.
        // - UnityEngine types verified in decompile output:
        //   Time/Vector3/Camera/GameObject/Object/MonoBehaviour are in UnityEngine.CoreModule.
        //   Input is in UnityEngine.InputLegacyModule.
        //   SceneManager is in UnityEngine.CoreModule (UnityEngine.SceneManagement namespace).
        // - BepInEx types verified in decompile output:
        //   BaseUnityPlugin is in BepInEx\BaseUnityPlugin.cs; ConfigFile/ConfigEntry exist in BepInEx\Configuration\*.

        // ---- config ----
        private ConfigEntry<bool> _autoActivate = null!;
        private ConfigEntry<KeyCode> _keyActivateNearest = null!;
        private ConfigEntry<bool> _discoverAllPoints = null!;
        private ConfigEntry<bool> _enableDebugLog = null!;

        private ExtractionPointScanner _scanner = null!;
        private ExtractionPointInvoker _invoker = null!;
        private ModLogger? _dbg;

        private float _autoTimer = 0f;
        private float _discoverTimer = 0f;
        private float _autoReadyTime = -1f;
        private bool _autoPrimed = false;
        private float _discoverIntervalFixed = -1f;
        private bool _logReady = false;

        private const float RESCAN_COOLDOWN = 0.6f;
        private const bool VERBOSE = false;
        private const bool SKIP_ACTIVATED = true;
        private const float AUTO_INTERVAL = 5.0f;
        private const float DISCOVER_INTERVAL_BASE = 0.5f;
        private const float DISCOVER_INTERVAL_4_6 = 1.0f;
        private const float DISCOVER_INTERVAL_7_9 = 1.5f;
        private const float DISCOVER_INTERVAL_10_12 = 2.0f;
        private const float DISCOVER_RADIUS = 20f;
        private const float AUTO_READY_BUFFER = 30f;

        private void Awake()
        {
            _autoActivate = Config.Bind("Auto", "AutoActivate", false, "Auto activate when idle.");
            _keyActivateNearest = Config.Bind("Keybinds", "ActivateNearest", KeyCode.F3, "Press to activate next extraction point (uses OnClick via reflection)." );
            _discoverAllPoints = Config.Bind("Discovery", "DiscoverAllPoints", false, "If true, treat all extraction points as discovered.");
            _enableDebugLog = Config.Bind("Debug", "EnableDebugLog", false, "Enable detailed mod logs in BepInEx\\config\\REPO_Active\\logs.");

            _invoker = new ExtractionPointInvoker(Logger, VERBOSE);
            _scanner = new ExtractionPointScanner(Logger, _invoker, RESCAN_COOLDOWN, VERBOSE);
            _dbg = new ModLogger(Logger, _enableDebugLog.Value);
            _scanner.DebugLog = _dbg.Log;

            SceneManager.sceneLoaded += OnSceneLoaded;
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. (OnClick reflection activation)");
            _dbg.Log($"[INIT] {PluginName} {PluginVersion} Auto={_autoActivate.Value} DiscoverAll={_discoverAllPoints.Value}");
        }

        private void OnDestroy()
        {
            try { SceneManager.sceneLoaded -= OnSceneLoaded; } catch { }
        }

        private void Update()
        {
            if (_dbg != null) _dbg.Enabled = _enableDebugLog.Value;

            // manual
            // [VERIFY] UnityEngine.Input.GetKeyDown (UnityEngine.InputLegacyModule).
            if (Input.GetKeyDown(_keyActivateNearest.Value))
            {
                _dbg?.Log("[INPUT] F3 pressed");
                ActivateNearest();
            }


            // discovery polling
            // [VERIFY] UnityEngine.Time.deltaTime (UnityEngine.CoreModule).
            _discoverTimer += Time.deltaTime;
            float interval = _discoverIntervalFixed > 0f ? _discoverIntervalFixed : DISCOVER_INTERVAL_BASE;
            if (_discoverTimer >= interval)
            {
                _discoverTimer = 0f;
                DiscoveryTick();
            }

            // auto mode
            if (_autoActivate.Value)
            {
                if (_autoReadyTime < 0f)
                {
                    if (_scanner.EnsureReady())
                    {
                        var all = _scanner.ScanAndGetAllPoints();
                        var refPos = _scanner.GetReferencePos();
                        // [VERIFY] UnityEngine.Time.realtimeSinceStartup (UnityEngine.CoreModule).
                        if (all.Count > 0 && refPos != Vector3.zero)
                        {
                            _scanner.CaptureSpawnPosIfNeeded(refPos);
                            _autoReadyTime = Time.realtimeSinceStartup;
                            _autoTimer = 0f;
                            _discoverIntervalFixed = ComputeDiscoveryInterval();
                            _dbg?.Log($"[AUTO] primed count={all.Count} refPos={refPos}");
                        }
                    }
                }
                else
                {
                    // wait for services-ready buffer before allowing auto activation
                    if ((Time.realtimeSinceStartup - _autoReadyTime) >= AUTO_READY_BUFFER)
                    {
                        // Prime: if the first planned point already activated, mark it once
                        if (!_autoPrimed)
                        {
                            PrimeFirstPointIfAlreadyActivated();
                            _autoPrimed = true;
                            _dbg?.Log("[AUTO] primed-first");
                        }

                        _autoTimer += Time.deltaTime;
                        if (_autoTimer >= AUTO_INTERVAL)
                        {
                            _autoTimer = 0f;
                            _dbg?.Log("[AUTO] tick");
                            AutoActivateIfIdle();
                        }
                    }
                    else
                    {
                        var left = AUTO_READY_BUFFER - (Time.realtimeSinceStartup - _autoReadyTime);
                        if (left > 0f)
                            _dbg?.Log($"[AUTO] buffer wait {left:0.0}s");
                    }
                }
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Reset per-round timers so auto mode re-enters its buffer on every map load
            // [VERIFY] UnityEngine.SceneManagement.SceneManager/Scene (UnityEngine.CoreModule).
            _autoReadyTime = -1f;
            _autoPrimed = false;
            _discoverIntervalFixed = -1f;
            _autoTimer = 0f;
            _discoverTimer = 0f;
            _scanner.ResetForNewRound();
            _logReady = false;
            _dbg?.Log("[SCENE] new scene loaded, reset timers");
        }

        private void DiscoveryTick()
        {
            if (!_scanner.EnsureReady()) return;

            // Always rescan on discovery tick to keep cache fresh even while points are activating
            var all = _scanner.ScanAndGetAllPoints();
            var refPos = _scanner.GetReferencePos();
            if (!_logReady && all.Count > 0)
            {
                _logReady = true;
                _dbg?.Log("[LOG] ready: ExtractionPoint detected");
            }
            if (_logReady)
            {
                _dbg?.Log($"[DISCOVER] tick all={all.Count} discovered={_scanner.DiscoveredCount}");
            }

            if (_discoverAllPoints.Value)
            {
                _scanner.MarkAllDiscovered(all);
            }
            else
            {
                var allPos = TryGetAllPlayerPositionsHost();
                if (allPos.Count == 0)
                {
                    _scanner.UpdateDiscovered(refPos, DISCOVER_RADIUS);
                    _dbg?.Log($"[DISCOVER] hostPos fallback={refPos}");
                }
                else
                {
                    for (int i = 0; i < allPos.Count; i++)
                        _scanner.UpdateDiscovered(allPos[i], DISCOVER_RADIUS);
                    _dbg?.Log($"[DISCOVER] hostPos count={allPos.Count}");
                }
            }
        }

        private void AutoActivateIfIdle()
        {
            if (!_scanner.EnsureReady())
            {
                _dbg?.Log("[AUTO] EnsureReady=false");
                return;
            }
            ActivateNearest();
        }

        private void ActivateNearest()
        {
            if (!_scanner.EnsureReady())
            {
                Logger.LogWarning("ExtractionPoint type not found yet.");
                _dbg?.Log("[F3] EnsureReady=false");
                return;
            }

            _scanner.ScanIfNeeded(force: true);

            var allPoints = _scanner.ScanAndGetAllPoints();
            _dbg?.Log($"[F3] allPoints={allPoints.Count}");

            // 1) 必须没有任何提取点处于激活中
            if (_scanner.IsAnyExtractionPointActivating(allPoints))
            {
                Logger.LogWarning("有提取点正在激活中，F3 忽略");
                _dbg?.Log("[F3] blocked: activating");
                return;
            }

            // 2) spawnPos + startPos
            var startPos = _scanner.GetReferencePos();
            _scanner.CaptureSpawnPosIfNeeded(startPos);
            var spawnPos = _scanner.GetSpawnPos();
            _dbg?.Log($"[F3] startPos={startPos} spawnPos={spawnPos}");

            // discovery filter
            if (_discoverAllPoints.Value)
                _scanner.MarkAllDiscovered(allPoints);
            else
                _scanner.UpdateDiscovered(startPos, DISCOVER_RADIUS);

            var eligible = _discoverAllPoints.Value ? allPoints : _scanner.FilterDiscovered(allPoints);
            _dbg?.Log($"[F3] eligible={eligible.Count} discoverAll={_discoverAllPoints.Value}");

            // 4) 构建规划列表
            var plan = _scanner.BuildStage1PlannedList(eligible, spawnPos, startPos, skipActivated: SKIP_ACTIVATED);
            if (plan.Count == 0)
            {
                Logger.LogWarning("没有可激活的提取点（可能都已激活或未发现）");
                _dbg?.Log("[F3] plan empty");
                return;
            }

            var next = plan[0];
            var nextState = _scanner.ReadStateName(next);
            _dbg?.Log($"[F3] next={next.gameObject.name} state={nextState}");

            // 5) 激活方式不改：仍走 OnClick invoker
            Logger.LogInfo($"[F3] Activate planned EP: {next.gameObject.name} pos={next.transform.position} dist={Vector3.Distance(startPos, next.transform.position):0.00}");

            var invokeOk = _invoker.InvokeOnClick(next);
            _dbg?.Log($"[F3] invoke={invokeOk} next={next.gameObject.name}");

            if (invokeOk)
            {
                // Guard against false-positive activation:
                // OnClick can succeed even if the EP doesn't actually enter an active state yet.
                // Only mark as activated if its state is not Idle/Complete after the call.
                var st = _scanner.ReadStateName(next);
                if (!string.IsNullOrEmpty(st) &&
                    !ExtractionPointScanner.IsIdleLikeState(st) &&
                    !ExtractionPointScanner.IsCompletedLikeState(st))
                {
                    _scanner.MarkActivated(next);
                    _dbg?.Log($"[F3] marked activated: {next.gameObject.name} state={st}");
                }
                else
                {
                    _dbg?.Log($"[F3] NOT marked (state not active): {next.gameObject.name} state={st}");
                }
            }
        }

        private void PrimeFirstPointIfAlreadyActivated()
        {
            var all = _scanner.ScanAndGetAllPoints();
            if (all.Count == 0) return;

            var refPos = _scanner.GetReferencePos();
            _scanner.CaptureSpawnPosIfNeeded(refPos);

            if (_discoverAllPoints.Value)
                _scanner.MarkAllDiscovered(all);
            else
                _scanner.UpdateDiscovered(refPos, DISCOVER_RADIUS);

            var eligible = _discoverAllPoints.Value ? all : _scanner.FilterDiscovered(all);
            var plan = _scanner.BuildStage1PlannedList(eligible, _scanner.GetSpawnPos(), refPos, skipActivated: SKIP_ACTIVATED);
            if (plan.Count == 0) return;

            var first = plan[0];
            var state = _scanner.ReadStateName(first);
            if (!string.IsNullOrEmpty(state) && !ExtractionPointScanner.IsIdleLikeState(state))
            {
                _scanner.MarkActivated(first);
            }
        }

        private List<Vector3> TryGetAllPlayerPositionsHost()
        {
            var list = new List<Vector3>();

            try
            {
                // [VERIFY] PhotonNetwork.InRoom / IsMasterClient exist in decompiled PhotonNetwork.cs.
                var photonNetworkType = Type.GetType("Photon.Pun.PhotonNetwork, PhotonUnityNetworking");
                if (photonNetworkType == null) return list;

                var inRoomProp = photonNetworkType.GetProperty("InRoom");
                var isMasterProp = photonNetworkType.GetProperty("IsMasterClient");

                bool inRoom = inRoomProp != null && inRoomProp.GetValue(null) is bool b1 && b1;
                bool isMaster = isMasterProp != null && isMasterProp.GetValue(null) is bool b2 && b2;

                if (!inRoom || !isMaster) return list;

                // Prefer GameDirector.instance.PlayerList (more stable than name-based PhotonView scanning)
                var gdType = Type.GetType("GameDirector, Assembly-CSharp");
                if (gdType == null) return list;

                object? gdInst = null;
                var instProp = gdType.GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (instProp != null) gdInst = instProp.GetValue(null, null);
                if (gdInst == null)
                {
                    var instField = gdType.GetField("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (instField != null) gdInst = instField.GetValue(null);
                }
                if (gdInst == null) return list;

                object? playerListObj = null;
                var plProp = gdType.GetProperty("PlayerList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (plProp != null) playerListObj = plProp.GetValue(gdInst, null);
                if (playerListObj == null)
                {
                    var plField = gdType.GetField("PlayerList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (plField != null) playerListObj = plField.GetValue(gdInst);
                }
                if (playerListObj == null) return list;

                var asEnumerable = playerListObj as System.Collections.IEnumerable;
                if (asEnumerable == null) return list;

                foreach (var p in asEnumerable)
                {
                    if (p == null) continue;
                    // GameDirector.PlayerList is List<PlayerAvatar> in decompiled GameDirector.cs.
                    // So items are already PlayerAvatar (MonoBehaviour/Component).
                    if (p is Component comp && comp.transform != null)
                    {
                        var pos = comp.transform.position;
                        if (pos != Vector3.zero) list.Add(pos);
                        continue;
                    }

                    // Fallback: if list items are wrappers, try PlayerAvatar property/field.
                    var pt = p.GetType();
                    object? avatarObj = null;
                    var avatarProp = pt.GetProperty("PlayerAvatar", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (avatarProp != null) avatarObj = avatarProp.GetValue(p, null);
                    if (avatarObj == null)
                    {
                        var avatarField = pt.GetField("PlayerAvatar", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (avatarField != null) avatarObj = avatarField.GetValue(p);
                    }
                    if (avatarObj == null) continue;

                    if (avatarObj is Component comp2 && comp2.transform != null)
                    {
                        var pos = comp2.transform.position;
                        if (pos != Vector3.zero) list.Add(pos);
                    }
                    else
                    {
                        var trProp = avatarObj.GetType().GetProperty("transform", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        var tr = trProp != null ? trProp.GetValue(avatarObj, null) as Transform : null;
                        if (tr != null)
                        {
                            var pos = tr.position;
                            if (pos != Vector3.zero) list.Add(pos);
                        }
                    }
                }
            }
            catch
            {
                return list;
            }

            return list;
        }

        private float ComputeDiscoveryInterval()
        {
            int players = GetPlayerCountHost();
            if (players <= 3) return DISCOVER_INTERVAL_BASE;
            if (players <= 6) return DISCOVER_INTERVAL_4_6;
            if (players <= 9) return DISCOVER_INTERVAL_7_9;
            return DISCOVER_INTERVAL_10_12;
        }

        private int GetPlayerCountHost()
        {
            try
            {
                var photonNetworkType = Type.GetType("Photon.Pun.PhotonNetwork, PhotonUnityNetworking");
                if (photonNetworkType == null) return 1;

                var inRoomProp = photonNetworkType.GetProperty("InRoom");
                var isMasterProp = photonNetworkType.GetProperty("IsMasterClient");
                var playerListProp = photonNetworkType.GetProperty("PlayerList");

                bool inRoom = inRoomProp != null && inRoomProp.GetValue(null) is bool b1 && b1;
                bool isMaster = isMasterProp != null && isMasterProp.GetValue(null) is bool b2 && b2;
                if (!inRoom || !isMaster) return 1;

                if (playerListProp != null)
                {
                    // [VERIFY] PhotonNetwork.PlayerList exists in decompiled PhotonNetwork.cs (returns Player[]).
                    var arr = playerListProp.GetValue(null) as Array;
                    if (arr != null && arr.Length > 0) return arr.Length;
                }
            }
            catch { }

            return 1;
        }

    }
}



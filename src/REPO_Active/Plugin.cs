using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;
using REPO_Active.Runtime;
using REPO_Active.Reflection;

namespace REPO_Active
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "angelcomilk.repo_active";
        public const string PluginName = "REPO_Active";
        public const string PluginVersion = "4.5.0";

        // ---- config (ONLY 3 items) ----
        private ConfigEntry<bool> _autoActivate = null!;
        private ConfigEntry<KeyCode> _keyActivateNearest = null!;
        private ConfigEntry<bool> _discoverAllPoints = null!;

        private ExtractionPointScanner _scanner = null!;
        private ActivationQueue _queue = null!;
        private ExtractionPointInvoker _invoker = null!;

        private float _autoTimer = 0f;
        private float _discoverTimer = 0f;
        private float _autoReadyTime = -1f;
        private bool _autoPrimed = false;
        private float _discoverIntervalFixed = -1f;

        private const float RESCAN_COOLDOWN = 0.6f;
        private const float PER_ACTIVATION_DELAY = 0.15f;
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

            _invoker = new ExtractionPointInvoker(Logger, VERBOSE);
            _scanner = new ExtractionPointScanner(Logger, _invoker, RESCAN_COOLDOWN, VERBOSE);
            _queue = new ActivationQueue(Logger, _invoker, _scanner, PER_ACTIVATION_DELAY, VERBOSE);

            SceneManager.sceneLoaded += OnSceneLoaded;
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. (OnClick reflection activation)");
        }

        private void OnDestroy()
        {
            try { SceneManager.sceneLoaded -= OnSceneLoaded; } catch { }
        }

        private void Update()
        {
            // manual
            if (Input.GetKeyDown(_keyActivateNearest.Value))
            {
                ActivateNearest();
            }


            // discovery polling
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
                        if (all.Count > 0 && refPos != Vector3.zero)
                        {
                            _scanner.CaptureSpawnPosIfNeeded(refPos);
                            _autoReadyTime = Time.realtimeSinceStartup;
                            _autoTimer = 0f;
                            _discoverIntervalFixed = ComputeDiscoveryInterval();
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
                        }

                        _autoTimer += Time.deltaTime;
                        if (_autoTimer >= AUTO_INTERVAL)
                        {
                            _autoTimer = 0f;
                            AutoActivateIfIdle();
                        }
                    }
                }
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Reset per-round timers so auto mode re-enters its buffer on every map load
            _autoReadyTime = -1f;
            _autoPrimed = false;
            _discoverIntervalFixed = -1f;
            _autoTimer = 0f;
            _discoverTimer = 0f;
            _scanner.ResetForNewRound();
        }

        private void DiscoveryTick()
        {
            if (!_scanner.EnsureReady()) return;

            // Always rescan on discovery tick to keep cache fresh even while points are activating
            var all = _scanner.ScanAndGetAllPoints();
            var refPos = _scanner.GetReferencePos();

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
                }
                else
                {
                    for (int i = 0; i < allPos.Count; i++)
                        _scanner.UpdateDiscovered(allPos[i], DISCOVER_RADIUS);
                }
            }
        }

        private void AutoActivateIfIdle()
        {
            if (!_scanner.EnsureReady())
                return;
            ActivateNearest();
        }

        private void ActivateNearest()
        {
            if (!_scanner.EnsureReady())
            {
                Logger.LogWarning("ExtractionPoint type not found yet.");
                return;
            }

            _scanner.ScanIfNeeded(force: true);

            var allPoints = _scanner.ScanAndGetAllPoints();

            // 1) 必须没有任何提取点处于激活中
            if (_scanner.IsAnyExtractionPointActivating(allPoints))
            {
                Logger.LogWarning("有提取点正在激活中，F3 忽略");
                return;
            }

            // 2) spawnPos + startPos
            var startPos = _scanner.GetReferencePos();
            _scanner.CaptureSpawnPosIfNeeded(startPos);
            var spawnPos = _scanner.GetSpawnPos();

            // discovery filter
            if (_discoverAllPoints.Value)
                _scanner.MarkAllDiscovered(allPoints);
            else
                _scanner.UpdateDiscovered(startPos, DISCOVER_RADIUS);

            var eligible = _discoverAllPoints.Value ? allPoints : _scanner.FilterDiscovered(allPoints);

            // 4) 构建规划列表
            var plan = _scanner.BuildStage1PlannedList(eligible, spawnPos, startPos, skipActivated: SKIP_ACTIVATED);
            if (plan.Count == 0)
            {
                Logger.LogWarning("没有可激活的提取点（可能都已激活或未发现）");
                return;
            }

            var next = plan[0];

            // 5) 激活方式不改：仍走 OnClick invoker
            Logger.LogInfo($"[F3] Activate planned EP: {next.gameObject.name} pos={next.transform.position} dist={Vector3.Distance(startPos, next.transform.position):0.00}");

            var invokeOk = _invoker.InvokeOnClick(next);

            if (invokeOk)
            {
                _scanner.MarkActivated(next);
            }
        }

        private void BuildQueueAndRun()
        {
            if (!_scanner.EnsureReady())
            {
                Logger.LogWarning("ExtractionPoint type not found yet.");
                return;
            }

            _scanner.ScanIfNeeded(force: true);

            var refPos = _scanner.GetReferencePos();
            _scanner.CaptureSpawnPosIfNeeded(refPos);

            var all = _scanner.ScanAndGetAllPoints();

            if (_discoverAllPoints.Value)
                _scanner.MarkAllDiscovered(all);
            else
                _scanner.UpdateDiscovered(refPos, DISCOVER_RADIUS);

            var eligible = _discoverAllPoints.Value ? all : _scanner.FilterDiscovered(all);
            var list = _scanner.BuildStage1PlannedList(eligible, _scanner.GetSpawnPos(), refPos, skipActivated: SKIP_ACTIVATED);

            if (list.Count == 0)
            {
                Logger.LogWarning("Queue is empty (no eligible extraction points).");
                return;
            }

            Logger.LogInfo($"[F4] Queue built: {list.Count} EP(s). Start activating...");
            _queue.StartQueue(this, list, onActivated: ep => _scanner.MarkActivated(ep));
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
                var photonNetworkType = Type.GetType("Photon.Pun.PhotonNetwork, PhotonUnityNetworking");
                if (photonNetworkType == null) return list;

                var inRoomProp = photonNetworkType.GetProperty("InRoom");
                var isMasterProp = photonNetworkType.GetProperty("IsMasterClient");

                bool inRoom = inRoomProp != null && inRoomProp.GetValue(null) is bool b1 && b1;
                bool isMaster = isMasterProp != null && isMasterProp.GetValue(null) is bool b2 && b2;

                if (!inRoom || !isMaster) return list;

                var photonViewType = Type.GetType("Photon.Pun.PhotonView, PhotonUnityNetworking");
                if (photonViewType == null) return list;

                var found = UnityEngine.Object.FindObjectsOfType(photonViewType);
                var ownerActorNrProp = photonViewType.GetProperty("OwnerActorNr");

                var byActor = new Dictionary<int, Vector3>();

                for (int i = 0; i < found.Length; i++)
                {
                    if (!(found[i] is Component c) || c == null) continue;
                    int actor = 0;
                    if (ownerActorNrProp != null)
                    {
                        var v = ownerActorNrProp.GetValue(c, null);
                        if (v is int ai) actor = ai;
                    }
                    if (actor <= 0) continue;

                    string name = c.gameObject != null ? c.gameObject.name : "";
                    if (string.IsNullOrEmpty(name)) continue;

                    bool isAvatar = name.IndexOf("Player Avatar Controller", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isLocalCam = name.IndexOf("Local Camera", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!isAvatar && !isLocalCam) continue;

                    var pos = c.transform != null ? c.transform.position : Vector3.zero;
                    if (pos == Vector3.zero) continue;

                    if (!byActor.ContainsKey(actor) || isAvatar)
                        byActor[actor] = pos;
                }

                foreach (var kv in byActor)
                    list.Add(kv.Value);
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
                    var arr = playerListProp.GetValue(null) as Array;
                    if (arr != null && arr.Length > 0) return arr.Length;
                }
            }
            catch { }

            return 1;
        }

    }
}

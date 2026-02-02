# CHANGELOG_DEV.md for REPO_Active

## v1.2.0 - Targeted Fixes & Optimizations (Based on User Specification)

### Summary of Changes:
-   **Fixed "Loading Stuck" Issue:** Implemented a robust state machine (`_runtimeReady`) to defer expensive game object scanning and logic execution until the game is fully loaded, the local player is found, and extraction points are scanned. This prevents performance bottlenecks during scene transitions and room loading.
-   **Fixed `ActivationKey` UI Display:** Changed `ActivationKey` from `ConfigEntry<KeyCode>` to `ConfigEntry<string>` to ensure it is editable and visible within the in-game BepInEx configuration UI. A helper function `GetActivationKey()` now parses the string into a `KeyCode`.
-   **Improved Player Finding (`EnsureLocalPlayer`):**
    -   Now throttled to search every 2 seconds when the player is not found.
    -   Prioritizes `Camera.main.transform` as a more stable way to find the local player during loading.
    -   Includes a fallback to `PhotonNetwork.PhotonViewCollection` for `IsMine` views.
-   **Dynamic Scan Frequency:** `SCAN_INTERVAL` is now 2.0s during loading/initialization and reverts to 0.8s once `_runtimeReady` is true.
-   **Rate-Limited Discovery Broadcasts:** Added `_lastBroadcastTime` and `BROADCAST_COOLDOWN` (0.2s) to prevent spamming Photon events during discovery. Broadcasts are also gated by `_runtimeReady`.
-   **Robust External Activation Handling:** `RefreshPoints()` now checks if an extraction point is externally unavailable (`!IsPointActivatable`) and automatically adds it to the `_activated` set, preventing redundant activation attempts by the mod.
-   **Refined Busy Check (`IsAnyPointBusy`):**
    -   The busy check now uses more specific keywords (`"active", "running", "countdown", "evac", "depart", "progress", "busy"`) from the `currentState` enum, reducing false positives.
-   **Resilient `reservedLastId` Logic:** The logic for selecting the `_reservedLastId` (the point closest to spawn, to be activated last) is now more robust, ensuring it is re-evaluated if the previously reserved point becomes activated or unavailable.

### Full `Plugin.cs` Content (v1.2.0):

```csharp
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;

using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;

namespace REPO_Active
{
    [BepInPlugin("local.repo_active", "REPO_Active", "1.2.0")] // Version bump
    public class Plugin : BaseUnityPlugin, IOnEventCallback
    {
        // ===== Config =====
        private ConfigEntry<bool> EnableAutoActivate;
        private ConfigEntry<string> ActivationKeyStr;
        private ConfigEntry<bool> OnlyDiscovered; // Renamed from DiscoverAllPoints for clarity

        private static ManualLogSource Log;

        // ===== Photon Discover Sync =====
        private const byte EVT_DISCOVER_POINT = 201;

        // ===== State Machine & Throttling =====
        private bool _runtimeReady = false;
        private string _lastScene = null;
        private float _nextScanAt = 0f;
        private float _nextAutoTryAt = 0f;
        private float _nextPlayerSearchAt = 0f;
        private float _lastBroadcastTime = 0f;
        
        private const float DISCOVER_RADIUS = 55f;
        private const float BROADCAST_COOLDOWN = 0.2f;

        // ===== Game State =====
        private bool _spawnCaptured = false;
        private Vector3 _spawnPos;
        private Transform _localPlayerTf;

        // ===== Reflection =====
        private Type _tExtractionPoint;
        private MethodInfo _mButtonPress;
        private FieldInfo _fIsShop;
        private FieldInfo _fButtonActive;
        private FieldInfo _fButtonDenyActive;
        private FieldInfo _fStateEnum;

        // ===== Point State =====
        private readonly Dictionary<string, PointInfo> _points = new Dictionary<string, PointInfo>();
        private readonly HashSet<string> _discovered = new HashSet<string>();
        private readonly HashSet<string> _activated = new HashSet<string>();
        private string _reservedLastId = null;

        private class PointInfo
        {
            public string Id;
            public Component Comp;
            public Vector3 Pos;
            public float SpawnDist;
        }

        private void Awake()
        {
            Log = Logger;

            // Config - Phase 3.1
            EnableAutoActivate = Config.Bind("General", "EnableAutoActivate", true, "Whether to automatically activate extraction points (Host only).");
            ActivationKeyStr = Config.Bind("General", "ActivationKey", "X", "Key to manually activate the next extraction point (e.g., X, F, Mouse0).");
            OnlyDiscovered = Config.Bind("General", "OnlyDiscovered", false, "If true, only extraction points that have been 'discovered' will be considered for activation.");

            // Reflection init
            InitializeReflection();

            Log.LogInfo($"REPO_Active v1.2.0 loaded. Activation Key: {GetActivationKey()}.");
        }

        private void OnEnable() => PhotonNetwork.AddCallbackTarget(this);
        private void OnDisable() => PhotonNetwork.RemoveCallbackTarget(this);

        public void OnEvent(EventData photonEvent)
        {
            if (photonEvent.Code != EVT_DISCOVER_POINT) return;
            if (!(photonEvent.CustomData is object[] data) || data.Length == 0) return;
            if (!(data[0] is string id) || string.IsNullOrEmpty(id)) return;

            if (_discovered.Add(id))
            {
                Log.LogInfo($"Received discovery event for point: {id}");
            }
        }

        private KeyCode GetActivationKey() // Phase 3.1
        {
            if (Enum.TryParse<KeyCode>(ActivationKeyStr.Value, true, out var key))
            {
                return key;
            }
            return KeyCode.X; // Default fallback
        }
        
        private void Update()
        {
            // Phase 2.1: Scene change check
            string currentScene = SceneManager.GetActiveScene().name;
            if (currentScene != _lastScene)
            {
                Log.LogInfo($"Scene changed from '{_lastScene ?? "N/A"}' to '{currentScene}'. Resetting state.");
                _lastScene = currentScene;
                _runtimeReady = false;
                _spawnCaptured = false;
                _localPlayerTf = null;
                _points.Clear();
                _discovered.Clear();
                _activated.Clear();
                _reservedLastId = null;
            }

            // Phase 2.1: Gate all logic until in a room
            if (!PhotonNetwork.InRoom)
            {
                _runtimeReady = false; // Ensure not ready if we leave a room
                return;
            }
            
            // Find player at low frequency
            if (_localPlayerTf == null && Time.time >= _nextPlayerSearchAt)
            {
                EnsureLocalPlayer();
                _nextPlayerSearchAt = Time.time + 2.0f; // Low-frequency search
            }

            // Capture spawn once player is found
            if (_localPlayerTf != null && !_spawnCaptured)
            {
                CaptureSpawn();
            }

            // High-frequency logic only runs when fully ready
            if (!_runtimeReady)
            {
                 // Check if we can become ready
                if (_spawnCaptured && _points.Count > 0)
                {
                    Log.LogInfo("Runtime is now ready. Main logic enabled.");
                    _runtimeReady = true;
                }
                return; // Do not proceed further if not ready
            }

            // --- Main Runtime Loop (runs only when ready) ---
            if (Input.GetKeyDown(GetActivationKey()))
            {
                TryActivateNext("manual");
            }

            if (Time.time >= _nextScanAt)
            {
                float scanInterval = _runtimeReady ? 0.8f : 2.0f; // Phase 2.3
                _nextScanAt = Time.time + scanInterval;

                RefreshPoints();
                RefreshReservedLastPoint();
                UpdateDiscovery();
            }

            if (EnableAutoActivate.Value && Time.time >= _nextAutoTryAt)
            {
                _nextAutoTryAt = Time.time + 0.5f;
                TryActivateNext("auto");
            }
        }
        
        private void InitializeReflection()
        {
            _tExtractionPoint = AccessTools.TypeByName("ExtractionPoint");
            if (_tExtractionPoint == null)
            {
                Log.LogError("Failed to find type 'ExtractionPoint'. Mod will be disabled.");
                return;
            }
            _mButtonPress = AccessTools.Method(_tExtractionPoint, "ButtonPress");
            _fIsShop = AccessTools.Field(_tExtractionPoint, "isShop");
            _fButtonActive = AccessTools.Field(_tExtractionPoint, "buttonActive");
            _fButtonDenyActive = AccessTools.Field(_tExtractionPoint, "buttonDenyActive");
            _fStateEnum = AccessTools.Field(_tExtractionPoint, "currentState"); // From dump
        }

        private void EnsureLocalPlayer() // Phase 2.2
        {
            if (_localPlayerTf != null) return;
            Log.LogInfo("Attempting to find local player...");

            // Prefer Camera.main as it's often more stable during load
            if (Camera.main != null)
            {
                 _localPlayerTf = Camera.main.transform;
                 Log.LogInfo($"Found player via Camera.main: {_localPlayerTf.name}");
                 return;
            }
            
            // Fallback to PhotonView search
            try
            {
                foreach (var view in PhotonNetwork.PhotonViewCollection)
                {
                    if (view != null && view.IsMine)
                    {
                        _localPlayerTf = view.transform;
                        Log.LogInfo($"Found player via PhotonView: {view.name}");
                        return;
                    }
                }
            }
            catch(Exception e) { Log.LogWarning($"Error while searching for PhotonViews: {e.Message}"); }
        }

        private void CaptureSpawn()
        {
            _spawnCaptured = true;
            _spawnPos = _localPlayerTf.position;
            Log.LogInfo($"Spawn point captured at: {_spawnPos}");

            // Immediately run a point refresh to calculate spawn distances
            RefreshPoints();
            RefreshReservedLastPoint();
        }

        private void RefreshPoints() // Phase 4.1
        {
            _points.Clear();
            var objs = FindObjectsOfType(_tExtractionPoint);
            foreach (var o in objs)
            {
                var comp = o as Component;
                if (comp == null || (_fIsShop != null && (_fIsShop.GetValue(o) as bool? ?? false)))
                {
                    continue;
                }

                var pos = comp.transform.position;
                string id = MakePointId(pos);
                _points[id] = new PointInfo
                {
                    Id = id, Comp = comp, Pos = pos,
                    SpawnDist = _spawnCaptured ? Vector3.Distance(_spawnPos, pos) : float.MaxValue
                };

                // External activation check
                if (!IsPointActivatable(comp) && !_activated.Contains(id))
                {
                    Log.LogInfo($"Point {id} is externally unavailable. Marking as activated.");
                    _activated.Add(id);
                }
            }

            if (OnlyDiscovered.Value == false)
            {
                foreach (var id in _points.Keys) _discovered.Add(id);
            }
        }
        
        private void RefreshReservedLastPoint() // Phase 4.3
        {
            if (!_spawnCaptured || _points.Count == 0) return;

            bool needsNewReservedPoint = string.IsNullOrEmpty(_reservedLastId) ||
                                         !_points.ContainsKey(_reservedLastId) ||
                                         _activated.Contains(_reservedLastId);

            if (needsNewReservedPoint)
            {
                _reservedLastId = _points.Values
                    .Where(p => !_activated.Contains(p.Id))
                    .OrderBy(p => p.SpawnDist)
                    .FirstOrDefault()?.Id;
                
                if(!string.IsNullOrEmpty(_reservedLastId))
                    Log.LogInfo($"New reserved last point is: {_reservedLastId}");
            }
        }

        private void UpdateDiscovery()
        {
            if (OnlyDiscovered.Value == false || _localPlayerTf == null) return;
            
            foreach (var p in _points.Values)
            {
                if (!_discovered.Contains(p.Id) && Vector3.Distance(_localPlayerTf.position, p.Pos) <= DISCOVER_RADIUS)
                {
                    MarkDiscoveredAndBroadcast(p);
                }
            }
        }
        
        private void MarkDiscoveredAndBroadcast(PointInfo p) // Phase 2.4
        {
            if (p == null || !_discovered.Add(p.Id)) return;
            if (!_runtimeReady) return; // Don't broadcast during load
            if (Time.time < _lastBroadcastTime + BROADCAST_COOLDOWN) return; // Rate limit

            Log.LogInfo($"Locally discovered {p.Id}. Broadcasting...");
            _lastBroadcastTime = Time.time;
            
            var payload = new object[] { p.Id };
            var options = new RaiseEventOptions { Receivers = ReceiverGroup.All };
            PhotonNetwork.RaiseEvent(EVT_DISCOVER_POINT, payload, options, SendOptions.SendReliable);
        }

        private void TryActivateNext(string reason)
        {
            if (!_runtimeReady || (PhotonNetwork.InRoom && !PhotonNetwork.IsMasterClient)) return;

            var candidates = _points.Values
                .Where(p => !_activated.Contains(p.Id) && (!OnlyDiscovered.Value || _discovered.Contains(p.Id)) && IsPointActivatable(p.Comp))
                .ToList();

            if (candidates.Count == 0) return;

            // Busy check - Phase 4.2
            if (IsAnyPointBusy(candidates))
            {
                Log.LogInfo("An extraction is already in progress. Holding activation.");
                return;
            }

            PointInfo chosen = candidates
                .Where(p => candidates.Count <= 1 || p.Id != _reservedLastId) // Exclude reserved if there are other options
                .OrderBy(p => Vector3.Distance(_localPlayerTf.position, p.Pos))
                .FirstOrDefault();

            // If only the reserved point is left, choose it
            chosen ??= candidates.FirstOrDefault(p => p.Id == _reservedLastId);

            if (chosen != null && InvokeButtonPress(chosen))
            {
                _activated.Add(chosen.Id);
                Log.LogInfo($"[{reason}] Activated point: {chosen.Id}");
            }
        }
        
        private bool InvokeButtonPress(PointInfo p)
        {
            if (p?.Comp == null || _mButtonPress == null) return false;
            try
            {
                _mButtonPress.Invoke(p.Comp, null);
                return true;
            }
            catch (Exception e)
            {
                Log.LogWarning($"ButtonPress invoke failed for {p.Id}: {e.Message}");
                return false;
            }
        }

        private bool IsAnyPointBusy(List<PointInfo> points) // Phase 4.2
        {
            if (_fStateEnum == null) return false; // Cannot determine state, fail safe to not block
            
            var busyKeywords = new[] { "active", "running", "countdown", "evac", "depart", "progress", "busy" };

            foreach (var p in points)
            {
                var stateName = GetStateName(p)?.ToLowerInvariant();
                if (!string.IsNullOrEmpty(stateName) && busyKeywords.Any(k => stateName.Contains(k)))
                {
                    return true;
                }
            }
            return false;
        }

        private bool IsPointActivatable(Component pointComp) // Phase 4.1
        {
            if (pointComp == null) return false;

            if (_fButtonActive != null && (_fButtonActive.GetValue(pointComp) as bool? ?? true) == false) return false;
            if (_fButtonDenyActive != null && (_fButtonDenyActive.GetValue(pointComp) as bool? ?? false) == true) return false;
            
            return true;
        }

        private string GetStateName(PointInfo p)
        {
            if (p?.Comp == null || _fStateEnum == null) return null;
            try { return _fStateEnum.GetValue(p.Comp)?.ToString(); } catch { return null; }
        }

        private string MakePointId(Vector3 pos)
        {
            int x = Mathf.RoundToInt(pos.x);
            int y = Mathf.RoundToInt(pos.y);
            int z = Mathf.RoundToInt(pos.z);
            return $"p_{x}_{y}_{z}";
        }
    }
}
```

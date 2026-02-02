// Plugin.cs - v1.3.1
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;

namespace REPO_Active
{
    [BepInPlugin("local.repo_active", "REPO_Active", "1.3.1")]
    public class Plugin : BaseUnityPlugin, IOnEventCallback
    {
        #region Config
        private ConfigEntry<bool> EnableAutoActivate;
        private ConfigEntry<string> ActivationKey;
        private ConfigEntry<bool> OnlyDiscovered;
        #endregion

        private static ManualLogSource Log;
        private const byte EVT_DISCOVER_POINT = 201;

        #region State Machine
        private bool _runtimeReady = false;
        private string _lastScene = null;
        private bool _cbRegistered = false;
        private float _readyCheckNextAt = 0f;
        private float _notReadyLogNextAt = 0f;
        private float _scanNextAt = 0f;
        private float _autoTryNextAt = 0f;
        #endregion

        #region Game State
        private bool _spawnCaptured = false;
        private Vector3 _spawnPos;
        private Transform _localPlayerTf;
        #endregion

        #region Reflection
        private Type _tExtractionPoint;
        private MethodInfo _mButtonPress;
        private FieldInfo _fIsShop;
        private FieldInfo _fStateEnum;
        private FieldInfo _fButtonActive;
        private FieldInfo _fButtonDenyActive;
        #endregion

        #region Point State
        private readonly Dictionary<string, PointInfo> _points = new Dictionary<string, PointInfo>();
        private readonly HashSet<string> _discovered = new HashSet<string>();
        private readonly HashSet<string> _activated = new HashSet<string>();
        private string _reservedLastId = null;
        #endregion

        private class PointInfo
        {
            public string Id;
            public Component Comp;
            public GameObject Go;
            public Vector3 Pos;
            public float SpawnDist;
        }

        private void Awake()
        {
            Log = Logger;
            
            EnableAutoActivate = Config.Bind("General", "EnableAutoActivate", true, "Automatically activate extraction points (Host/MasterClient only).");
            ActivationKey = Config.Bind("General", "ActivationKey", "X", "Key to manually activate the next point (e.g., X, F, Mouse0). Visible in in-game config UI.");
            OnlyDiscovered = Config.Bind("General", "OnlyDiscovered", false, "If true, only extraction points you have discovered will be activated.");
            
            InitializeReflection();
            Log.LogInfo($"REPO_Active v1.3.1 Loaded. Key: {GetActivationKey()}");
        }

        private void InitializeReflection()
        {
            _tExtractionPoint = AccessTools.TypeByName("ExtractionPoint");
            if (_tExtractionPoint == null) { Log.LogError("Type 'ExtractionPoint' not found."); return; }
            
            _mButtonPress = AccessTools.Method(_tExtractionPoint, "ButtonPress");
            _fIsShop = AccessTools.Field(_tExtractionPoint, "isShop");

            // A-1 / A-2: Reflection for state and activatable status
            _fStateEnum = AccessTools.Field(_tExtractionPoint, "currentState")
                          ?? AccessTools.Field(_tExtractionPoint, "stateCurrent")
                          ?? AccessTools.Field(_tExtractionPoint, "stateSetTo")
                          ?? AccessTools.Field(_tExtractionPoint, "state");

            _fButtonActive = AccessTools.Field(_tExtractionPoint, "buttonActive");
            _fButtonDenyActive = AccessTools.Field(_tExtractionPoint, "buttonDenyActive");

            if (_mButtonPress == null) Log.LogWarning("Method 'ButtonPress' not found. Activation will fail.");
            if (_fStateEnum == null) Log.LogWarning("State field not found. Busy check will be less reliable.");
        }

        private void Update()
        {
            string currentScene = SceneManager.GetActiveScene().name;
            if (currentScene != _lastScene)
            {
                Log.LogInfo($"Scene changed to '{currentScene}'. Resetting state.");
                _lastScene = currentScene;
                ResetState();
            }

            if (Time.time < _readyCheckNextAt) return;
            _readyCheckNextAt = Time.time + 0.5f;

            if (IsGameplayReady())
            {
                if (!_runtimeReady)
                {
                    Log.LogInfo($"Runtime is now ready. Scene: '{currentScene}', Points: {Resources.FindObjectsOfTypeAll(_tExtractionPoint).Length}, InRoom: {PhotonNetwork.InRoom}, Offline: {PhotonNetwork.OfflineMode}");
                    _runtimeReady = true;
                }
                
                HandleCallbackRegistration();
                MainUpdate();
            }
            else
            {
                if (_runtimeReady)
                {
                    Log.LogInfo("Gameplay is no longer ready. Disabling main logic.");
                    _runtimeReady = false;
                }
            }
        }
        
        private void OnDestroy() => HandleCallbackRegistration(false);
        
        private void ResetState()
        {
            HandleCallbackRegistration(false);
            _runtimeReady = false;
            _spawnCaptured = false;
            _localPlayerTf = null;
            _points.Clear();
            _discovered.Clear();
            _activated.Clear();
            _reservedLastId = null;
        }

        private void HandleCallbackRegistration(bool shouldBeRegistered = true)
        {
            if (shouldBeRegistered && PhotonNetwork.InRoom && !_cbRegistered)
            {
                PhotonNetwork.AddCallbackTarget(this);
                _cbRegistered = true;
                Log.LogInfo("Photon callback target registered.");
            }
            else if (!shouldBeRegistered && _cbRegistered)
            {
                PhotonNetwork.RemoveCallbackTarget(this);
                _cbRegistered = false;
                Log.LogInfo("Photon callback target unregistered.");
            }
        }
        
        private bool IsGameplayReady() // A-5
        {
            string reason = "";
            if (string.IsNullOrEmpty(_lastScene) || _lastScene.ToLower().Contains("menu") || _lastScene.ToLower().Contains("lobby") || _lastScene.ToLower().Contains("title"))
                reason = "Not in a gameplay scene";
            else if (!PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode)
                reason = "Not in a room";
            else if (Time.timeSinceLevelLoad <= 1.5f)
                reason = "Level loading";
            else if (_localPlayerTf == null)
            {
                EnsureLocalPlayer();
                if(_localPlayerTf == null) reason = "Player transform not found";
            }
            else if (Resources.FindObjectsOfTypeAll(_tExtractionPoint).Length == 0)
                reason = "No ExtractionPoints found in scene";

            if (string.IsNullOrEmpty(reason))
            {
                if(!_spawnCaptured) CaptureSpawn();
                return true;
            }
            
            if (Time.time > _notReadyLogNextAt)
            {
                Log.LogInfo($"[Not Ready] Reason: {reason}");
                _notReadyLogNextAt = Time.time + 5.0f;
            }
            return false;
        }

        private void MainUpdate()
        {
            if (Time.time >= _scanNextAt)
            {
                _scanNextAt = Time.time + 0.8f;
                RefreshPoints();
                RefreshReservedLastPoint();
                UpdateDiscovery();
            }

            if (Input.GetKeyDown(GetActivationKey())) TryActivateNext("Manual");
            if (EnableAutoActivate.Value && Time.time >= _autoTryNextAt)
            {
                _autoTryNextAt = Time.time + 1.0f;
                TryActivateNext("Auto");
            }
        }

        private void EnsureLocalPlayer()
        {
            if(Camera.main != null) _localPlayerTf = Camera.main.transform;
        }

        private void CaptureSpawn()
        {
            if (_localPlayerTf == null) return;
            _spawnPos = _localPlayerTf.position;
            _spawnCaptured = true;
            Log.LogInfo($"Spawn point captured: {_spawnPos}");
        }

        private void RefreshPoints()
        {
            _points.Clear();
            var objs = Resources.FindObjectsOfTypeAll(_tExtractionPoint);
            foreach (var o in objs)
            {
                var comp = o as Component;
                if (comp == null || (_fIsShop != null && (_fIsShop.GetValue(o) as bool? ?? false))) continue;
                
                string id = MakePointId(comp.gameObject);
                _points[id] = new PointInfo
                {
                    Id = id, Comp = comp, Go = comp.gameObject, Pos = comp.transform.position,
                    SpawnDist = _spawnCaptured ? Vector3.Distance(_spawnPos, comp.transform.position) : float.MaxValue
                };
            }
            if (!OnlyDiscovered.Value)
            {
                foreach (var id in _points.Keys) _discovered.Add(id);
            }
            Log.LogInfo($"Refreshed points. Total: {_points.Count}, Discovered: {_discovered.Count}");
        }

        private void RefreshReservedLastPoint()
        {
            if (!_spawnCaptured || _points.Count == 0) return;
            bool needsNew = string.IsNullOrEmpty(_reservedLastId) || !_points.ContainsKey(_reservedLastId) || _activated.Contains(_reservedLastId);
            if (needsNew)
            {
                _reservedLastId = _points.Values
                    .Where(p => !_activated.Contains(p.Id))
                    .OrderBy(p => p.SpawnDist)
                    .FirstOrDefault()?.Id;
                if(!string.IsNullOrEmpty(_reservedLastId)) Log.LogInfo($"New reserved last point: {_reservedLastId}");
            }
        }

        private void UpdateDiscovery()
        {
            if (!OnlyDiscovered.Value || _localPlayerTf == null) return;
            foreach (var p in _points.Values)
            {
                if (!_discovered.Contains(p.Id) && Vector3.Distance(_localPlayerTf.position, p.Pos) <= 55f)
                    MarkDiscoveredAndBroadcast(p);
            }
        }

        private void MarkDiscoveredAndBroadcast(PointInfo p)
        {
            if (p == null || !_discovered.Add(p.Id)) return;
            Log.LogInfo($"Discovered {p.Id}, broadcasting event.");
            PhotonNetwork.RaiseEvent(EVT_DISCOVER_POINT, new object[] { p.Id }, new RaiseEventOptions { Receivers = ReceiverGroup.All }, SendOptions.SendReliable);
        }

        private void TryActivateNext(string reason)
        {
            if (PhotonNetwork.InRoom && !PhotonNetwork.IsMasterClient) return;

            var candidates = _points.Values.Where(p => !_activated.Contains(p.Id) && (!OnlyDiscovered.Value || _discovered.Contains(p.Id))).ToList();
            if (IsAnyPointBusy(candidates))
            {
                Log.LogInfo($"[{reason}] Skipped: A point is already busy.");
                return;
            }

            var activatable = candidates.Where(IsPointActivatable).ToList();
            PointInfo chosen = activatable.Where(p => activatable.Count <= 1 || p.Id != _reservedLastId)
                .OrderBy(p => Vector3.Distance(_localPlayerTf.position, p.Pos)).FirstOrDefault();
            chosen ??= activatable.FirstOrDefault(p => p.Id == _reservedLastId);

            if (chosen == null)
            {
                Log.LogInfo($"[{reason}] No activatable point found. Candidates: {candidates.Count}, Activatable: {activatable.Count}");
                return;
            }

            StartCoroutine(InvokeAndConfirmActivation(chosen, reason));
        }

        private IEnumerator InvokeAndConfirmActivation(PointInfo p, string reason) // A-3
        {
            string preState = GetStateName(p);
            Log.LogInfo($"[{reason}] Activating {p.Id}. Pre-state: {preState ?? "N/A"}");
            
            if (!InvokeButtonPress(p))
            {
                Log.LogWarning($"[{reason}] Failed to invoke ButtonPress on {p.Id}.");
                yield break;
            }

            yield return new WaitForSeconds(0.2f); // Wait for state to update

            string postState = GetStateName(p);
            if (postState != preState && !(postState?.ToLowerInvariant().Contains("idle") ?? true))
            {
                Log.LogInfo($"Activation confirmed for {p.Id}. State changed to: {postState}");
                _activated.Add(p.Id);
                BroadcastActivation(p); // A-4
            }
            else
            {
                Log.LogWarning($"ButtonPress called on {p.Id} but state remains '{postState ?? "N/A"}'");
            }
        }
        
        private void BroadcastActivation(PointInfo p) // A-4
        {
            try
            {
                var view = p.Comp.GetComponent<PhotonView>() ?? p.Comp.GetComponentInParent<PhotonView>() ?? p.Comp.GetComponentInChildren<PhotonView>();
                if (view != null)
                {
                    view.RPC("ButtonGoGreenRPC", RpcTarget.All);
                    Log.LogInfo("Best-effort RPC 'ButtonGoGreenRPC' sent.");

                    var nestedType = _tExtractionPoint.GetNestedType("State", BindingFlags.Public | BindingFlags.NonPublic);
                    if (nestedType != null)
                    {
                        var activeVal = Enum.Parse(nestedType, "Active"); // Or "Extracting"
                        view.RPC("StateSetRPC", RpcTarget.All, activeVal);
                        Log.LogInfo("Best-effort RPC 'StateSetRPC' sent with state 'Active'.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Failed to send best-effort RPCs: {ex.Message}");
            }
        }

        private bool IsAnyPointBusy(List<PointInfo> points) // A-1
        {
            if (_fStateEnum == null) return false;
            var busyKeywords = new[] { "extracting", "active", "warning" };
            foreach (var p in points)
            {
                var stateName = GetStateName(p)?.ToLowerInvariant();
                if (!string.IsNullOrEmpty(stateName) && busyKeywords.Any(k => stateName.Contains(k)))
                    return true;
            }
            return false;
        }

        private bool IsPointActivatable(PointInfo p) // A-2
        {
            if (p?.Comp == null || p.Go == null) return false;
            
            var stateName = GetStateName(p)?.ToLowerInvariant();
            if (!string.IsNullOrEmpty(stateName) && !(stateName.Contains("idle") || stateName.Contains("none")))
                return false;

            if (_fButtonActive != null && !(_fButtonActive.GetValue(p.Comp) as bool? ?? true)) return false;
            if (_fButtonDenyActive != null && (_fButtonDenyActive.GetValue(p.Comp) as bool? ?? false)) return false;
            
            return true;
        }

        private string GetStateName(PointInfo p)
        {
            if (p?.Comp == null || _fStateEnum == null) return null;
            try { return _fStateEnum.GetValue(p.Comp)?.ToString(); } catch { return null; }
        }

        private KeyCode GetActivationKey()
        {
            return Enum.TryParse<KeyCode>(ActivationKey.Value, true, out var key) ? key : KeyCode.X;
        }
        
        private bool InvokeButtonPress(PointInfo p)
        {
            if (p?.Comp == null || _mButtonPress == null) return false;
            try { _mButtonPress.Invoke(p.Comp, null); return true; }
            catch { return false; }
        }

        private string MakePointId(GameObject go) // A-5
        {
            var pos = go.transform.position;
            int x = Mathf.RoundToInt(pos.x * 10f);
            int y = Mathf.RoundToInt(pos.y * 10f);
            int z = Mathf.RoundToInt(pos.z * 10f);
            return $"{_lastScene}:{go.name}:{x}_{y}_{z}";
        }

        public void OnEvent(EventData photonEvent) {} // Already defined, merged logic
    }
}
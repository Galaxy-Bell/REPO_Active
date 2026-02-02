// Plugin.cs - v1.4.0
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;

namespace REPO_Active
{
    [BepInPlugin("local.repo_active", "REPO_Active", "1.4.0")]
    public class Plugin : BaseUnityPlugin, IOnEventCallback
    {
        #region Config
        private ConfigEntry<bool> EnableAutoActivate;
        private ConfigEntry<string> ActivationKey;
        private ConfigEntry<bool> RequireDiscovered;
        private ConfigEntry<bool> EnableDebugLog; // Step 3
        #endregion

        private static ManualLogSource BepInExLogger;
        private static StreamWriter _fileLogger; // Step 4
        private static bool _logToFile = false;

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
            public object RawInstance; // Step 2.4
            public Component Comp;
            public GameObject Go;
            public Vector3 Pos;
            public float SpawnDist;
        }

        private void Awake()
        {
            BepInExLogger = Logger;
            
            EnableAutoActivate = Config.Bind("1. General", "EnableAutoActivate", true, "Automatically activate extraction points (Host/MasterClient only).");
            ActivationKey = Config.Bind("1. General", "ActivationKey", "X", "Key to manually activate the next point. Must be a valid KeyCode string (e.g., X, F, Mouse0).");
            RequireDiscovered = Config.Bind("1. General", "RequireDiscovered", true, "If true, only extraction points you have discovered will be activated.");
            EnableDebugLog = Config.Bind("2. Debug", "EnableDebugLog", false, "Enable detailed file logging for troubleshooting.");
            
            EnableDebugLog.SettingChanged += (s, e) => SetupFileLogger();
            SetupFileLogger();

            InitializeReflection();
            Log($"REPO_Active v1.4.0 Loaded. Manual key: {GetActivationKey()}");
        }

        private void InitializeReflection()
        {
            _tExtractionPoint = AccessTools.TypeByName("ExtractionPoint");
            if (_tExtractionPoint == null) { Log("Type 'ExtractionPoint' not found.", isError: true); return; }
            
            _mButtonPress = AccessTools.Method(_tExtractionPoint, "ButtonPress");
            _fIsShop = AccessTools.Field(_tExtractionPoint, "isShop");

            _fStateEnum = AccessTools.Field(_tExtractionPoint, "currentState")
                          ?? AccessTools.Field(_tExtractionPoint, "stateCurrent")
                          ?? AccessTools.Field(_tExtractionPoint, "stateSetTo")
                          ?? AccessTools.Field(_tExtractionPoint, "state");

            _fButtonActive = AccessTools.Field(_tExtractionPoint, "buttonActive");
            _fButtonDenyActive = AccessTools.Field(_tExtractionPoint, "buttonDenyActive");

            if (_mButtonPress == null) Log("Method 'ButtonPress' not found. Activation will fail.", isWarning: true);
            if (_fStateEnum == null) Log("State field not found. Busy check will be less reliable.", isWarning: true);
        }

        #region Lifecycle & State Management
        private void Update()
        {
            string currentScene = SceneManager.GetActiveScene().name;
            if (currentScene != _lastScene)
            {
                Log($"Scene changed to '{currentScene}'. Resetting state.", isDebug: true);
                _lastScene = currentScene;
                ResetState();
            }

            if (Time.time < _readyCheckNextAt) return;
            _readyCheckNextAt = Time.time + 0.5f;

            if (IsGameplayReady())
            {
                if (!_runtimeReady)
                {
                    Log($"Runtime is now ready. Scene: '{currentScene}', Points: {Resources.FindObjectsOfTypeAll(_tExtractionPoint).Length}, InRoom: {PhotonNetwork.InRoom}, Offline: {PhotonNetwork.OfflineMode}");
                    _runtimeReady = true;
                }
                
                HandleCallbackRegistration(true);
                MainUpdate();
            }
            else
            {
                if (_runtimeReady)
                {
                    Log("Gameplay is no longer ready. Disabling main logic.");
                    _runtimeReady = false;
                }
            }
        }
        
        private void OnDestroy() => ResetState();
        
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
            }
            else if (!shouldBeRegistered && _cbRegistered)
            {
                PhotonNetwork.RemoveCallbackTarget(this);
                _cbRegistered = false;
            }
        }
        
        private bool IsGameplayReady()
        {
            string reason = "";
            if (string.IsNullOrEmpty(_lastScene) || new[] { "menu", "lobby", "title" }.Any(s => _lastScene.ToLower().Contains(s)))
                reason = "Not in a gameplay scene";
            else if (!PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode)
                reason = "Not in a room";
            else if (Time.timeSinceLevelLoad <= 1.5f)
                reason = "Level loading";
            else if (_localPlayerTf == null)
            {
                TryResolveLocalPlayer();
                if(_localPlayerTf == null) reason = "Player transform not found";
            }
            else if (Resources.FindObjectsOfTypeAll(_tExtractionPoint).Length == 0)
                reason = "No ExtractionPoints found in scene (using FindObjectsOfTypeAll)";

            if (string.IsNullOrEmpty(reason))
            {
                if(!_spawnCaptured) CaptureSpawn();
                return true;
            }
            
            if (Time.time > _notReadyLogNextAt)
            {
                Log($"[Not Ready] Reason: {reason}", isDebug: true);
                _notReadyLogNextAt = Time.time + 5.0f;
            }
            return false;
        }
        #endregion

        #region Main Logic
        private void MainUpdate()
        {
            if (Time.time >= _scanNextAt)
            {
                _scanNextAt = Time.time + 1.0f; 
                RefreshPoints();
                RefreshReservedLastPoint();
                UpdateDiscovery();
            }

            if (Input.GetKeyDown(GetActivationKey())) TryActivateNext("Manual");
            if (EnableAutoActivate.Value && Time.time >= _autoTryNextAt)
            {
                _autoTryNextAt = Time.time + 1.5f;
                TryActivateNext("Auto");
            }
        }

        private void TryActivateNext(string reason)
        {
            if (PhotonNetwork.InRoom && !PhotonNetwork.IsMasterClient) return;

            var candidates = _points.Values.Where(p => !_activated.Contains(p.Id) && (!RequireDiscovered.Value || _discovered.Contains(p.Id))).ToList();
            if (IsAnyPointBusy(candidates))
            {
                Log($"[{reason}] Skipped: An extraction is already in progress.", isDebug: true);
                return;
            }

            var activatable = candidates.Where(IsPointActivatable).ToList();
            PointInfo chosen = activatable.Where(p => activatable.Count <= 1 || p.Id != _reservedLastId)
                .OrderBy(p => Vector3.Distance(_localPlayerTf.position, p.Pos)).FirstOrDefault();
            chosen ??= activatable.FirstOrDefault(p => p.Id == _reservedLastId);

            if (chosen == null)
            {
                Log($"[{reason}] No activatable point found. Candidates: {candidates.Count}, Activatable: {activatable.Count}", isDebug: true);
                return;
            }

            StartCoroutine(InvokeAndConfirmActivation(chosen, reason));
        }

        private IEnumerator InvokeAndConfirmActivation(PointInfo p, string reason)
        {
            string preState = GetStateName(p);
            Log($"[{reason}] Activating {p.Id}. Pre-state: {preState ?? "N/A"}");
            
            if (InvokeButtonPress(p))
            {
                yield return new WaitForSeconds(0.5f);

                string postState = GetStateName(p);
                if (preState != postState && !(postState?.ToLowerInvariant().Contains("idle") ?? true))
                {
                    Log($"Activation confirmed for {p.Id}. State changed to: {postState}");
                    _activated.Add(p.Id);
                    BroadcastActivation(p);
                }
                else
                {
                    Log($"[WARN] ButtonPress called on {p.Id} but state remains '{postState ?? "N/A"}' (suspected failed activation).", isWarning: true);
                }
            }
            else
            {
                Log($"[WARN] InvokeButtonPress failed for {p.Id}.", isWarning: true);
            }
        }
        
        private void BroadcastActivation(PointInfo p)
        {
            try
            {
                var view = p.Comp.GetComponent<PhotonView>();
                if (view != null)
                {
                    Log($"Attempting best-effort broadcast for {p.Id}...", isDebug: true);
                    view.RPC("ButtonGoGreenRPC", RpcTarget.All);

                    var nestedType = _tExtractionPoint.GetNestedType("State", BindingFlags.Public | BindingFlags.NonPublic);
                    if (nestedType != null)
                    {
                        var activeVal = Enum.Parse(nestedType, "Active");
                        view.RPC("StateSetRPC", RpcTarget.All, activeVal);
                    }
                    Log("Best-effort RPCs sent.");
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to send best-effort RPCs for {p.Id}: {ex.Message}", isWarning: true);
            }
        }
        #endregion

        #region Helpers & State Checks
        private bool IsAnyPointBusy(List<PointInfo> points)
        {
            if (_fStateEnum == null) return false;
            var busyKeywords = new[] { "extracting", "active", "warning", "countdown" };
            foreach (var p in points)
            {
                var stateName = GetStateName(p)?.ToLowerInvariant();
                if (!string.IsNullOrEmpty(stateName) && busyKeywords.Any(k => stateName.Contains(k))) return true;
            }
            return false;
        }

        private bool IsPointActivatable(PointInfo p)
        {
            if (p?.Comp == null) return false;
            
            var stateName = GetStateName(p)?.ToLowerInvariant();
            if (!string.IsNullOrEmpty(stateName) && !(stateName.Contains("idle") || stateName.Contains("none"))) return false;
            
            if (_fButtonActive != null && (_fButtonActive.GetValue(p.Comp) as bool? ?? true) == false) return false;
            if (_fButtonDenyActive != null && (_fButtonDenyActive.GetValue(p.Comp) as bool? ?? false) == true) return false;
            
            return true;
        }

        private void UpdateDiscovery()
        {
            if (!RequireDiscovered.Value || _localPlayerTf == null) return;
            foreach (var p in _points.Values)
            {
                if (!_discovered.Contains(p.Id) && Vector3.Distance(_localPlayerTf.position, p.Pos) <= 55f)
                    MarkDiscoveredAndBroadcast(p);
            }
        }

        private void MarkDiscoveredAndBroadcast(PointInfo p)
        {
            if (p == null || !_discovered.Add(p.Id)) return;
            Log($"Discovered {p.Id}, broadcasting event.");
            PhotonNetwork.RaiseEvent(EVT_DISCOVER_POINT, new object[] { p.Id }, new RaiseEventOptions { Receivers = ReceiverGroup.All }, SendOptions.SendReliable);
        }

        public void OnEvent(EventData photonEvent)
        {
            if (photonEvent.Code != EVT_DISCOVER_POINT) return;
            if (!(photonEvent.CustomData is object[] data) || data.Length == 0) return;
            if (!(data[0] is string id) || string.IsNullOrEmpty(id)) return;
            if (_discovered.Add(id)) Log($"Received discovery event for point: {id}");
        }
        #endregion

        #region Utility
        private void TryResolveLocalPlayer()
        {
            if(Camera.main != null && Camera.main.isActiveAndEnabled)
            {
                 _localPlayerTf = Camera.main.transform;
                 Log("Local player resolved via Camera.main.", isDebug: true);
                 return;
            }
            
            try
            {
                if (PhotonNetwork.LocalPlayer != null)
                {
                    var playerGo = PhotonNetwork.LocalPlayer.TagObject as GameObject;
                    if(playerGo != null)
                    {
                        _localPlayerTf = playerGo.transform;
                        Log($"Local player resolved via PhotonNetwork.LocalPlayer: {playerGo.name}", isDebug: true);
                        return;
                    }
                }
            } catch {}

            Log("Could not resolve local player in this pass.", isDebug: true);
        }

        private void CaptureSpawn()
        {
            if (_localPlayerTf == null) return;
            _spawnPos = _localPlayerTf.position;
            _spawnCaptured = true;
            Log($"Spawn point captured: {_spawnPos}");
        }

        private void RefreshPoints()
        {
            var objs = Resources.FindObjectsOfTypeAll(_tExtractionPoint);
            if (objs.Length == _points.Count && _points.Count > 0) return;

            _points.Clear();
            foreach (var o in objs)
            {
                var comp = o as Component;
                if (comp == null || (_fIsShop != null && (_fIsShop.GetValue(o) as bool? ?? false))) continue;
                
                string id = MakePointId(comp.gameObject);
                _points[id] = new PointInfo
                {
                    Id = id, RawInstance = o, Comp = comp, Go = comp.gameObject, Pos = comp.transform.position,
                    SpawnDist = _spawnCaptured ? Vector3.Distance(_spawnPos, comp.transform.position) : float.MaxValue
                };
            }
            if (!RequireDiscovered.Value)
                foreach (var id in _points.Keys) _discovered.Add(id);
            Log($"Refreshed points. Total: {_points.Count}, Discovered: {_discovered.Count}", isDebug: true);
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
                if(!string.IsNullOrEmpty(_reservedLastId)) Log($"New reserved last point: {_reservedLastId}", isDebug: true);
            }
        }
        
        private string GetStateName(PointInfo p) => p?.Comp != null && _fStateEnum != null ? _fStateEnum.GetValue(p.Comp)?.ToString() : null;
        private KeyCode GetActivationKey() => Enum.TryParse<KeyCode>(ActivationKey.Value, true, out var key) ? key : KeyCode.X;
        private bool InvokeButtonPress(PointInfo p)
        {
            if (p?.RawInstance == null || _mButtonPress == null) return false;
            try { _mButtonPress.Invoke(p.RawInstance, null); return true; }
            catch (Exception ex) { Log($"InvokeButtonPress exception: {ex.Message}", isWarning: true); return false; }
        }
        private string MakePointId(GameObject go)
        {
            var pos = go.transform.position;
            int x = Mathf.RoundToInt(pos.x * 100f);
            int y = Mathf.RoundToInt(pos.y * 100f);
            int z = Mathf.RoundToInt(pos.z * 100f);
            return $"{_lastScene}:{go.name}:{x}_{y}_{z}";
        }
        #endregion

        #region Logging
        private void SetupFileLogger()
        {
            if (EnableDebugLog.Value && _fileLogger == null)
            {
                try
                {
                    string logDir = Path.Combine(Paths.PluginPath, "REPO_Active", "_logs");
                    Directory.CreateDirectory(logDir);
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string logFile = Path.Combine(logDir, $"REPO_Active_v1.4.0_{timestamp}.log");
                    _fileLogger = new StreamWriter(logFile, true) { AutoFlush = true };
                    _logToFile = true;
                    Log("File logging enabled.");
                }
                catch(Exception ex)
                {
                    BepInExLogger.LogError($"Failed to initialize file logger: {ex.Message}");
                }
            }
            else if (!EnableDebugLog.Value && _fileLogger != null)
            {
                Log("File logging disabled.");
                _fileLogger.Close();
                _fileLogger = null;
                _logToFile = false;
            }
        }

        private void Log(string message, bool isDebug = false, bool isWarning = false, bool isError = false)
        {
            if (isError) BepInExLogger.LogError(message);
            else if (isWarning) BepInExLogger.LogWarning(message);
            else BepInExLogger.LogInfo(message);
            
            if (_logToFile && (isDebug || isWarning || isError))
            {
                _fileLogger?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
            }
        }
        #endregion
    }
}
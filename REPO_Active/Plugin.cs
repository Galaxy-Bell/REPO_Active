// Plugin.cs - v1.3.1 (Corrected)
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
using System.Text;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;

namespace REPO_Active
{
    [BepInPlugin("local.repo_active", "REPO_Active", "1.3.1")]
    public class Plugin : BaseUnityPlugin, IOnEventCallback
    {
        private ConfigEntry<bool> EnableAutoActivate;
        private ConfigEntry<string> ActivationKeyStr;
        private ConfigEntry<bool> OnlyDiscovered;
        private KeyCode _activationKey = KeyCode.X;
        private static ManualLogSource Log;
        private const byte EVT_DISCOVER_POINT = 201;
        private float _nextScanAt = 0f;
        private float _nextAutoTryAt = 0f;
        private const float SCAN_INTERVAL = 0.8f;
        private const float AUTO_TRY_INTERVAL = 0.5f;
        private const float DISCOVER_RADIUS = 28f;
        private bool _spawnCaptured = false;
        private Vector3 _spawnPos;
        private Transform _localPlayerTf;
        private Type _tExtractionPoint;
        private Type _tStateEnum;
        private MethodInfo _mButtonPress;
        private MethodInfo _mDiscover;
        private MethodInfo _mStateSet;
        private MethodInfo _mStateSetRPC;
        private FieldInfo _fIsShop;
        private FieldInfo _fButtonActive;
        private FieldInfo _fButtonDenyActive;
        private FieldInfo _fCurrentState;
        private FieldInfo _fStateSetTo;
        private class PointInfo { public string Id; public Component Comp; public Vector3 Pos; public float SpawnDist; }
        private readonly Dictionary<string, PointInfo> _points = new Dictionary<string, PointInfo>();
        private readonly HashSet<string> _discovered = new HashSet<string>();
        private readonly HashSet<string> _activated = new HashSet<string>();
        private string _reservedLastId = null;
        private StreamWriter _fileLog;
        private string _logPath;

        private void Awake()
        {
            Log = Logger;
            EnableAutoActivate = Config.Bind("General", "EnableAutoActivate", true, "是否自动激活提取点（多人房间仅房主生效）");
            ActivationKeyStr = Config.Bind("General", "ActivationKey", "X", "手动激活下一提取点的按键（例：X / F1 / Z；注意填 KeyCode 名称）");
            OnlyDiscovered = Config.Bind("General", "OnlyDiscovered", false, "是否只对“已发现”的提取点进行激活（true=未发现则跳过；false=未发现也会进入激活队列）");
            ParseActivationKey();
            ActivationKeyStr.SettingChanged += (_, __) => ParseActivationKey();
            InitFileLogger();
            InitializeReflection();
            LogI("REPO_Active 1.3.1 loaded. key=" + _activationKey + " auto=" + EnableAutoActivate.Value + " onlyDiscovered=" + OnlyDiscovered.Value);
        }
        private void OnEnable() { try { PhotonNetwork.AddCallbackTarget(this); } catch { } }
        private void OnDisable() { try { PhotonNetwork.RemoveCallbackTarget(this); } catch { } }
        private void OnDestroy() { try { if (_fileLog != null) { _fileLog.Flush(); _fileLog.Dispose(); _fileLog = null; } } catch { } }
        private void ParseActivationKey() { if (Enum.TryParse<KeyCode>((ActivationKeyStr.Value ?? "X").Trim(), true, out var k)) _activationKey = k; else _activationKey = KeyCode.X; }
        private void InitFileLogger() { try { var dir = Path.Combine(Paths.ConfigPath, "REPO_Active", "logs"); Directory.CreateDirectory(dir); var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss"); _logPath = Path.Combine(dir, $"REPO_Active_{stamp}.log"); _fileLog = new StreamWriter(_logPath, false, new UTF8Encoding(false)) { AutoFlush = true }; LogI("FileLog: " + _logPath); } catch (Exception e) { Log.LogWarning("InitFileLogger failed: " + e.Message); } }
        private void LogI(string msg) { Log.LogInfo(msg); try { _fileLog?.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}"); } catch { } }
        private void LogW(string msg) { Log.LogWarning(msg); try { _fileLog?.WriteLine($"[{DateTime.Now:HH:mm:ss}] [W] {msg}"); } catch { } }
        private void LogE(string msg) { Log.LogError(msg); try { _fileLog?.WriteLine($"[{DateTime.Now:HH:mm:ss}] [E] {msg}"); } catch { } }
        private void InitializeReflection() { try { _tExtractionPoint = AccessTools.TypeByName("ExtractionPoint") ?? Type.GetType("ExtractionPoint, Assembly-CSharp"); if (_tExtractionPoint == null) { LogE("Type not found: ExtractionPoint. Mod disabled."); return; } _tStateEnum = AccessTools.Inner(_tExtractionPoint, "State"); if (_tStateEnum == null) { LogW("Inner enum not found: ExtractionPoint+State. Will fallback to ButtonPress only."); } _mButtonPress = AccessTools.Method(_tExtractionPoint, "ButtonPress"); _mDiscover = AccessTools.Method(_tExtractionPoint, "Discover"); _mStateSet = AccessTools.Method(_tExtractionPoint, "StateSet"); _mStateSetRPC = AccessTools.Method(_tExtractionPoint, "StateSetRPC"); _fIsShop = AccessTools.Field(_tExtractionPoint, "isShop"); _fButtonActive = AccessTools.Field(_tExtractionPoint, "buttonActive"); _fButtonDenyActive = AccessTools.Field(_tExtractionPoint, "buttonDenyActive"); _fCurrentState = AccessTools.Field(_tExtractionPoint, "currentState"); _fStateSetTo = AccessTools.Field(_tExtractionPoint, "stateSetTo"); LogI("Reflection OK: ButtonPress=" + (_mButtonPress != null) + " StateSet=" + (_mStateSet != null) + " StateSetRPC=" + (_mStateSetRPC != null) + " currentStateField=" + (_fCurrentState != null)); } catch (Exception e) { LogE("InitializeReflection failed: " + e); } }
        public void OnEvent(EventData photonEvent) { if (photonEvent == null) return; if (photonEvent.Code != EVT_DISCOVER_POINT) return; try { var data = photonEvent.CustomData as object[]; if (data == null || data.Length < 1) return; var id = data[0] as string; if (string.IsNullOrEmpty(id)) return; _discovered.Add(id); } catch (Exception e) { LogW("OnEvent parse failed: " + e.Message); } }
        private void Update() { if (_tExtractionPoint == null) return; if (Input.GetKeyDown(_activationKey)) { TryActivateNext("manual"); } if (Time.time >= _nextScanAt) { _nextScanAt = Time.time + SCAN_INTERVAL; EnsureLocalPlayer(); CaptureSpawnIfNeeded(); RefreshPoints(); RefreshReservedLastPoint(); if (OnlyDiscovered.Value) UpdateDiscovery(); } if (EnableAutoActivate.Value && Time.time >= _nextAutoTryAt) { _nextAutoTryAt = Time.time + AUTO_TRY_INTERVAL; TryActivateNext("auto"); } }
        private void EnsureLocalPlayer() { if (_localPlayerTf != null) return; try { var views = Resources.FindObjectsOfTypeAll<PhotonView>(); foreach (var v in views) { if (v == null) continue; if (!v.IsMine) continue; _localPlayerTf = v.transform; return; } } catch { } try { var cam = Camera.main; if (cam != null) _localPlayerTf = cam.transform; } catch { } }
        private void CaptureSpawnIfNeeded() { if (_spawnCaptured) return; if (_localPlayerTf == null) return; _spawnCaptured = true; _spawnPos = _localPlayerTf.position; LogI("Spawn captured: " + _spawnPos); }
        private void RefreshPoints() { _points.Clear(); var objs = Resources.FindObjectsOfTypeAll(_tExtractionPoint); foreach (var o in objs) { var comp = o as Component; if (comp == null) continue; if (_fIsShop != null) { try { var v = _fIsShop.GetValue(o); if (v is bool b && b) continue; } catch { } } var pos = comp.transform.position; var id = MakePointId(pos); var pi = new PointInfo { Id = id, Comp = comp, Pos = pos, SpawnDist = _spawnCaptured ? Vector3.Distance(_spawnPos, pos) : float.MaxValue }; _points[id] = pi; if (!OnlyDiscovered.Value) _discovered.Add(id); } }
        private void RefreshReservedLastPoint() { if (!_spawnCaptured) return; if (_points.Count == 0) return; if (!string.IsNullOrEmpty(_reservedLastId)) { if (_activated.Contains(_reservedLastId) || !_points.ContainsKey(_reservedLastId)) _reservedLastId = null; } if (string.IsNullOrEmpty(_reservedLastId)) { float best = float.MaxValue; string bestId = null; foreach (var kv in _points) { var id = kv.Key; var p = kv.Value; if (_activated.Contains(id)) continue; if (p.SpawnDist < best) { best = p.SpawnDist; bestId = id; } } _reservedLastId = bestId; } }
        private void UpdateDiscovery() { if (_localPlayerTf == null) return; foreach (var kv in _points) { var p = kv.Value; if (_discovered.Contains(p.Id)) continue; if (Vector3.Distance(_localPlayerTf.position, p.Pos) <= DISCOVER_RADIUS) { MarkDiscoveredAndBroadcast(p); } } }
        private void MarkDiscoveredAndBroadcast(PointInfo p) { if (p == null) return; if (_discovered.Contains(p.Id)) return; _discovered.Add(p.Id); TryInvokeDiscoverLocal(p.Comp); try { if (PhotonNetwork.InRoom) { object[] payload = new object[] { p.Id, p.Pos.x, p.Pos.y, p.Pos.z }; var opt = new RaiseEventOptions { Receivers = ReceiverGroup.All }; PhotonNetwork.RaiseEvent(EVT_DISCOVER_POINT, payload, opt, SendOptions.SendReliable); } } catch { } }
        private void TryActivateNext(string reason) { if (PhotonNetwork.InRoom && !PhotonNetwork.IsMasterClient) { LogI($"[{reason}] Not MasterClient, skip activation."); return; } if (_points.Count == 0) return; var candidates = new List<PointInfo>(); foreach (var kv in _points) { var p = kv.Value; if (_activated.Contains(p.Id)) continue; if (OnlyDiscovered.Value && !_discovered.Contains(p.Id)) continue; if (IsPointActivatableNow(p.Comp)) candidates.Add(p); } if (candidates.Count == 0) { return; } var curPos = (_localPlayerTf != null) ? _localPlayerTf.position : Vector3.zero; PointInfo chosen = null; float best = float.MaxValue; foreach (var p in candidates) { if (!string.IsNullOrEmpty(_reservedLastId) && p.Id == _reservedLastId && candidates.Count > 1) continue; var d = Vector3.Distance(curPos, p.Pos); if (d < best) { best = d; chosen = p; } } if (chosen == null) { foreach (var p in candidates) { if (p.Id == _reservedLastId) { chosen = p; break; } } } if (chosen == null) return; bool ok = ActivatePoint(chosen.Comp); if (ok) { _activated.Add(chosen.Id); LogI($"[{reason}] Activated: {chosen.Id} pos={chosen.Pos}"); } else { LogW($"[{reason}] Activate failed (no state change): {chosen.Id} pos={chosen.Pos}"); } }
        private bool IsPointActivatableNow(Component comp) { if (comp == null) return false; if (_fButtonDenyActive != null) { try { var v = _fButtonDenyActive.GetValue(comp); if (v is bool b && b) return false; } catch { } } if (_fButtonActive != null) { try { var v = _fButtonActive.GetValue(comp); if (v is bool b && !b) return false; } catch { } } var st = GetCurrentStateName(comp); if (!string.IsNullOrEmpty(st)) { var s = st.ToLowerInvariant(); if (!s.Contains("idle")) return false; } return true; }
        private string GetCurrentStateName(Component comp) { if (comp == null) return null; if (_fCurrentState == null) return null; try { var v = _fCurrentState.GetValue(comp); return v?.ToString(); } catch { return null; } }
        private bool ActivatePoint(Component comp) { if (comp == null) return false; if (PhotonNetwork.InRoom) { try { var pv = comp.GetComponent<PhotonView>(); if (pv != null && _mStateSetRPC != null && _tStateEnum != null) { TryInvokeDiscoverRPC(pv); object activeState = Enum.Parse(_tStateEnum, "Active"); pv.RPC("StateSetRPC", RpcTarget.All, activeState); return true; } } catch (Exception e) { LogW("RPC activate failed, fallback local: " + e.Message); } } try { if (_mStateSet != null && _tStateEnum != null) { TryInvokeDiscoverLocal(comp); object activeState = Enum.Parse(_tStateEnum, "Active"); _mStateSet.Invoke(comp, new object[] { activeState }); var st = GetCurrentStateName(comp); if (!string.IsNullOrEmpty(st) && !st.ToLowerInvariant().Contains("idle")) return true; return true; } } catch (Exception e) { LogW("StateSet activate failed, fallback ButtonPress: " + e.Message); } try { if (_mButtonPress != null) { _mButtonPress.Invoke(comp, null); return true; } } catch (Exception e) { LogW("ButtonPress invoke failed: " + e.Message); } return false; }
        private void TryInvokeDiscoverLocal(Component comp) { try { if (_mDiscover != null) _mDiscover.Invoke(comp, null); } catch { } }
        private void TryInvokeDiscoverRPC(PhotonView pv) { try { pv.RPC("Discover", RpcTarget.All); } catch { } }
        private string MakePointId(Vector3 pos) { var scene = SceneManager.GetActiveScene().name ?? "scene"; int x = Mathf.RoundToInt(pos.x * 10f); int y = Mathf.RoundToInt(pos.y * 10f); int z = Mathf.RoundToInt(pos.z * 10f); return $"{scene}:{x}_{y}_{z}"; }
    }
}
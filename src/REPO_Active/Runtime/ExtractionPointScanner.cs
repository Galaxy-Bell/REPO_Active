using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace REPO_Active.Runtime
{
    public sealed class ExtractionPointScanner
    {
        private readonly ManualLogSource _log;
        // Verification notes (decompile cross-check):
        // - ExtractionPoint type and member currentState -> VERIFIED in Assembly-CSharp\ExtractionPoint.cs.
        // - UnityEngine.Object.FindObjectsOfType(Type) and Time.* are verified in UnityEngine.CoreModule.
        // - This file does NOT call Photon APIs directly.

        private Type? _epType;
        private readonly List<Component> _cached = new();
        private float _lastScanRealtime;
        private int _lastScanCount = -1;

        private Vector3? _spawnPos;
        private readonly HashSet<int> _activatedIds = new();
        private readonly HashSet<int> _discovered = new();
        private static readonly Dictionary<string, bool> _idleCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, bool> _completeCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // =====================
        // Stage1: planning helpers
        // =====================

        public float RescanCooldown { get; set; }
        public Action<string>? DebugLog { get; set; }
        public bool LogReady { get; set; }

        public int CachedCount => _cached.Count;
        public int DiscoveredCount => _discovered.Count;

        public ExtractionPointScanner(ManualLogSource log, float rescanCooldown)
        {
            _log = log;
            RescanCooldown = rescanCooldown;
        }

        public bool EnsureReady()
        {
            if (_epType != null) return true;

            // [VERIFY] ExtractionPoint type exists in decompiled Assembly-CSharp (ExtractionPoint.cs).
            _epType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch { return Array.Empty<Type>(); }
                })
                .FirstOrDefault(t => t != null && t.Name == "ExtractionPoint");

            return _epType != null;
        }

        public void ScanIfNeeded(bool force)
        {
            try
            {
                // [VERIFY] UnityEngine.Time.realtimeSinceStartup (UnityEngine.CoreModule).
                var t0 = Time.realtimeSinceStartup;
                var now = t0;
                if (!force && (now - _lastScanRealtime) < RescanCooldown) return;
                _lastScanRealtime = now;

                if (!EnsureReady()) return;

                // [VERIFY] UnityEngine.Object.FindObjectsOfType(Type) (UnityEngine.CoreModule).
                var found = UnityEngine.Object.FindObjectsOfType(_epType!);
                _cached.Clear();
                _cached.AddRange(found.OfType<Component>().Where(c => c != null));

                var dt = Time.realtimeSinceStartup - t0;
                if (_lastScanCount != _cached.Count)
                {
                    _lastScanCount = _cached.Count;
                    if (LogReady)
                        DebugLog?.Invoke($"[SCAN] count={_cached.Count} dt={dt:0.000}s");
                }
            }
            catch (Exception e)
            {
                _log.LogError($"ScanIfNeeded failed: {e}");
                if (LogReady)
                    DebugLog?.Invoke($"[SCAN][ERR] {e.GetType().Name}: {e.Message}");
            }
        }

        public Vector3 GetReferencePos()
        {
            // [VERIFY] UnityEngine.Camera.main and GameObject.FindWithTag (UnityEngine.CoreModule).
            try
            {
                if (Camera.main != null) return Camera.main.transform.position;
            }
            catch { }

            try
            {
                var p = GameObject.FindWithTag("Player");
                if (p != null) return p.transform.position;
            }
            catch { }

            return Vector3.zero;
        }

        public void MarkAllDiscovered(List<Component> allPoints)
        {
            int before = _discovered.Count;
            for (int i = 0; i < allPoints.Count; i++)
            {
                var ep = allPoints[i];
                if (!ep) continue;
                _discovered.Add(ep.GetInstanceID());
            }
            int added = _discovered.Count - before;
            if (added > 0)
            {
                if (LogReady)
                    DebugLog?.Invoke($"[DISCOVER] mark-all +{added} total={_discovered.Count}");
            }
        }

        public void UpdateDiscovered(Vector3 refPos, float radius)
        {
            UpdateDiscoveredDetailed(refPos, radius, null);
        }

        public int UpdateDiscoveredDetailed(Vector3 refPos, float radius, List<Component>? newly)
        {
            if (_cached.Count == 0) return 0;

            int before = _discovered.Count;
            for (int i = 0; i < _cached.Count; i++)
            {
                var ep = _cached[i];
                if (!ep) continue;
                var id = ep.GetInstanceID();
                if (_discovered.Contains(id)) continue;

                var d2 = (refPos - ep.transform.position).sqrMagnitude;
                if (d2 <= radius * radius)
                {
                    _discovered.Add(id);
                    if (newly != null) newly.Add(ep);
                }
            }
            int added = _discovered.Count - before;
            if (added > 0 && newly == null)
            {
                if (LogReady)
                    DebugLog?.Invoke($"[DISCOVER] +{added} total={_discovered.Count} radius={radius:0.0}");
            }
            return added;
        }

        public List<Component> FilterDiscovered(List<Component> allPoints)
        {
            var list = new List<Component>();
            for (int i = 0; i < allPoints.Count; i++)
            {
                var ep = allPoints[i];
                if (!ep) continue;
                if (_discovered.Contains(ep.GetInstanceID()))
                    list.Add(ep);
            }
            return list;
        }

        public Vector3 GetSpawnPos()
        {
            return _spawnPos ?? Vector3.zero;
        }

        public void CaptureSpawnPosIfNeeded(Vector3 refPos)
        {
            if (_spawnPos != null) return;
            if (refPos == Vector3.zero) return;
            _spawnPos = refPos;
            if (LogReady)
                DebugLog?.Invoke($"[SPAWN] pos={refPos}");
        }

        public List<Component> ScanAndGetAllPoints()
        {
            ScanIfNeeded(force: true);
            return _cached.Where(c => c != null).ToList();
        }

        public void ResetForNewRound()
        {
            _cached.Clear();
            _activatedIds.Clear();
            _discovered.Clear();
            _spawnPos = null;
            _lastScanRealtime = 0f;
            _lastScanCount = -1;
        }

        public void MarkActivated(Component ep)
        {
            if (ep == null) return;
            _activatedIds.Add(ep.GetInstanceID());
        }

        private bool IsMarkedActivated(Component ep)
        {
            if (ep == null) return false;
            return _activatedIds.Contains(ep.GetInstanceID());
        }

        public bool IsAnyExtractionPointActivating(List<Component> allPoints)
        {
            string _;
            int __;
            return TryGetActivatingInfo(allPoints, out _, out __);
        }

        public bool TryGetActivatingInfo(List<Component> allPoints, out string info, out int busyCount)
        {
            info = "";
            busyCount = 0;
            if (allPoints == null || allPoints.Count == 0) return false;

            for (int i = 0; i < allPoints.Count; i++)
            {
                var ep = allPoints[i];
                if (!ep) continue;

                try
                {
                    var t = ep.GetType();
                    var f = t.GetField("currentState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    var p = t.GetProperty("currentState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    object? v = null;
                    if (p != null) v = p.GetValue(ep, null);
                    if (v == null && f != null) v = f.GetValue(ep);
                    // [VERIFY] In decompiled ExtractionPoint.cs, `currentState` exists as an internal field (not a property).
                    if (v == null) continue;

                    var s = v.ToString() ?? "";
                    if (s.Length == 0) continue;

                    if (IsIdleLikeState(s)) continue;
                    if (IsCompletedLikeState(s)) continue;

                    busyCount++;
                    if (string.IsNullOrEmpty(info))
                        info = $"{ep.gameObject.name} state={s}";
                }
                catch
                {
                    // fail-safe: if can't read, consider it activating
                    if (busyCount == 0) busyCount = 1;
                    if (string.IsNullOrEmpty(info)) info = "state read failed";
                    return true;
                }
            }

            return busyCount > 0;
        }

        internal static bool IsIdleLikeState(string stateName)
        {
            if (string.IsNullOrEmpty(stateName)) return false;
            if (_idleCache.TryGetValue(stateName, out bool cached)) return cached;
            // [VERIFY] ExtractionPoint.State.Idle exists in decompiled ExtractionPoint.cs enum.
            bool res = stateName.IndexOf("Idle", StringComparison.OrdinalIgnoreCase) >= 0;
            _idleCache[stateName] = res;
            return res;
        }

        internal static bool IsCompletedLikeState(string stateName)
        {
            if (string.IsNullOrEmpty(stateName)) return false;
            if (_completeCache.TryGetValue(stateName, out bool cached)) return cached;
            var s = stateName.ToLowerInvariant();
            // [VERIFY] ExtractionPoint.State.Success / Complete exist in decompiled ExtractionPoint.cs enum.
            bool res = s.Contains("success")
                || s.Contains("complete")
                || s.Contains("submitted")
                || s.Contains("finish")
                || s.Contains("done");
            _completeCache[stateName] = res;
            return res;
        }

        public string ReadStateName(Component ep)
        {
            if (!ep) return "";
            try
            {
                var t = ep.GetType();
                var f = t.GetField("currentState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var p = t.GetProperty("currentState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                object? v = null;
                if (p != null) v = p.GetValue(ep, null);
                if (v == null && f != null) v = f.GetValue(ep);
                // [VERIFY] In decompiled ExtractionPoint.cs, `currentState` exists as an internal field (not a property).
                if (v == null) return "";
                return v.ToString() ?? "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 构建“F3 顺序激活列表”：
        /// - 固定第一个：离出生点最近的提取点
        /// - 固定最后一个：在剩余点里离出生点最近
        /// - 中间：最近邻贪心（从 startPos 开始）
        /// - 跳过已完成/已激活的点
        /// </summary>
        public List<Component> BuildStage1PlannedList(
            List<Component> allPoints,
            Vector3 spawnPos,
            Vector3 startPos,
            bool skipActivated)
        {
            var t0 = Time.realtimeSinceStartup;
            var result = new List<Component>();
            if (allPoints == null || allPoints.Count == 0) return result;

            // Fallbacks to keep ordering stable if spawn/start are not yet valid.
            if (spawnPos == Vector3.zero) spawnPos = startPos;
            if (startPos == Vector3.zero) startPos = spawnPos;

            // 复制候选集：（可选）排除已激活
            var candidates = new List<Component>(allPoints.Count);
            for (int i = 0; i < allPoints.Count; i++)
            {
                var ep = allPoints[i];
                if (!ep) continue;

                // Skip points already completed (do not keep them at head of queue)
                var st = ReadStateName(ep);
                if (IsCompletedLikeState(st))
                {
                    DebugLog?.Invoke($"[PLAN][SKIP] completed name={ep.gameObject.name} state={st}");
                    continue;
                }

                if (skipActivated && IsMarkedActivated(ep))
                {
                    DebugLog?.Invoke($"[PLAN][SKIP] activated name={ep.gameObject.name}");
                    continue;
                }

                candidates.Add(ep);
            }

            if (candidates.Count == 0) return result;

            // 固定第一个：离出生点最近的提取点（不参与动态排序）
            Component? first = null;
            float bestFirst = float.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                float d2 = (candidates[i].transform.position - spawnPos).sqrMagnitude;
                if (d2 < bestFirst)
                {
                    bestFirst = d2;
                    first = candidates[i];
                }
            }

            if (first != null)
            {
                if (!skipActivated || !IsMarkedActivated(first))
                {
                    result.Add(first);
                }
                candidates.Remove(first);
            }

            if (candidates.Count == 0) return result;

            // 找“最后一个点”：在剩余点里，离出生点最近
            int lastIdx = -1;
            float best = float.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                float d2 = (candidates[i].transform.position - spawnPos).sqrMagnitude;
                if (d2 < best)
                {
                    best = d2;
                    lastIdx = i;
                }
            }

            Component last = candidates[lastIdx];
            candidates.RemoveAt(lastIdx);

            // 其余点：最近邻贪心，从 startPos 出发
            Vector3 cur = startPos;
            while (candidates.Count > 0)
            {
                int pick = 0;
                float bd = float.MaxValue;
                for (int i = 0; i < candidates.Count; i++)
                {
                    float d2 = (cur - candidates[i].transform.position).sqrMagnitude;
                    if (d2 < bd)
                    {
                        bd = d2;
                        pick = i;
                    }
                }

                var chosen = candidates[pick];
                candidates.RemoveAt(pick);
                result.Add(chosen);
                cur = chosen.transform.position;
            }

            // 最后追加 last（离出生点最近的那个）
            result.Add(last);
            var dt = Time.realtimeSinceStartup - t0;
            if (LogReady)
            {
                float firstSpawnDist = first != null ? Mathf.Sqrt(bestFirst) : -1f;
                float lastSpawnDist = (last.transform.position - spawnPos).magnitude;
                DebugLog?.Invoke($"[PLAN] all={allPoints.Count} eligible={result.Count} first={first?.gameObject?.name ?? "null"} last={last.gameObject.name} firstSpawnDist={firstSpawnDist:0.00} lastSpawnDist={lastSpawnDist:0.00} dt={dt:0.000}s");
                DebugLogPlanList(result, startPos, spawnPos, first, last);
            }
            return result;
        }

        private void DebugLogPlanList(List<Component> plan, Vector3 startPos, Vector3 spawnPos, Component? first, Component last)
        {
            if (plan == null || plan.Count == 0) return;
            for (int i = 0; i < plan.Count; i++)
            {
                var ep = plan[i];
                if (!ep) continue;
                var pos = ep.transform.position;
                var d = Vector3.Distance(startPos, pos);
                var ds = Vector3.Distance(spawnPos, pos);
                var st = ReadStateName(ep);
                var act = IsMarkedActivated(ep);
                var disc = _discovered.Contains(ep.GetInstanceID());
                string tag = ep == first ? "FIRST" : (ep == last ? "LAST" : "");
                DebugLog?.Invoke($"[PLAN][{i}] name={ep.gameObject.name} dist={d:0.00} spawnDist={ds:0.00} discovered={disc} activated={act} state={st} {tag}".Trim());
            }
        }
    }
}

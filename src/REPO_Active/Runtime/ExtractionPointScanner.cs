using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;
using REPO_Active.Reflection;

namespace REPO_Active.Runtime
{
    public sealed class ExtractionPointScanner
    {
        private readonly ManualLogSource _log;
        private readonly ExtractionPointInvoker _invoker;

        // Verification notes (decompile cross-check):
        // - ExtractionPoint type and member currentState -> VERIFIED in Assembly-CSharp\ExtractionPoint.cs.
        // - UnityEngine.Object.FindObjectsOfType(Type) and Time.* are verified in UnityEngine.CoreModule.
        // - This file does NOT call Photon APIs directly.

        private Type? _epType;
        private readonly List<Component> _cached = new();
        private float _lastScanRealtime;

        private Vector3? _spawnPos;
        private int? _spawnExcludedInstanceId;

        private readonly HashSet<int> _activatedIds = new();
        private readonly HashSet<int> _discovered = new();

        // =====================
        // Stage1: planning helpers
        // =====================

        private float _blockUntil = -1f;

        public float RescanCooldown { get; set; }
        public bool Verbose { get; set; }
        public Action<string>? DebugLog { get; set; }

        public int CachedCount => _cached.Count;
        public int DiscoveredCount => _discovered.Count;

        public ExtractionPointScanner(ManualLogSource log, ExtractionPointInvoker invoker, float rescanCooldown, bool verbose)
        {
            _log = log;
            _invoker = invoker;
            RescanCooldown = rescanCooldown;
            Verbose = verbose;
            _blockUntil = Time.realtimeSinceStartup + 10f;
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

                if (Verbose)
                    _log.LogInfo($"[SCAN] ExtractionPoint rescan: {_cached.Count}");
                var dt = Time.realtimeSinceStartup - t0;
                DebugLog?.Invoke($"[SCAN] count={_cached.Count} dt={dt:0.000}s");
            }
            catch (Exception e)
            {
                _log.LogError($"ScanIfNeeded failed: {e}");
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
                DebugLog?.Invoke($"[DISCOVER] mark-all +{added} total={_discovered.Count}");
        }

        public void UpdateDiscovered(Vector3 refPos, float radius)
        {
            if (_cached.Count == 0) return;

            int before = _discovered.Count;
            for (int i = 0; i < _cached.Count; i++)
            {
                var ep = _cached[i];
                if (!ep) continue;
                var id = ep.GetInstanceID();
                if (_discovered.Contains(id)) continue;

                var d = Vector3.Distance(refPos, ep.transform.position);
                if (d <= radius)
                    _discovered.Add(id);
            }
            int added = _discovered.Count - before;
            if (added > 0)
                DebugLog?.Invoke($"[DISCOVER] +{added} total={_discovered.Count} radius={radius:0.0}");
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
            if (Verbose)
                _log.LogInfo($"[SPAWN] spawnPos captured: {_spawnPos.Value}");
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
            _spawnExcludedInstanceId = null;
            _lastScanRealtime = 0f;
        }

        public void UpdateSpawnExcludeIfNeeded(Vector3 refPos, float spawnExcludeRadius)
        {
            // capture spawnPos once when we first have a meaningful reference
            if (_spawnPos == null)
            {
                if (refPos != Vector3.zero)
                {
                    _spawnPos = refPos;
                    if (Verbose)
                        _log.LogInfo($"[SPAWN] spawnPos captured: {_spawnPos.Value}");
                }
                else
                {
                    // don't set spawnPos to zero unless we must
                    return;
                }
            }

            if (_spawnExcludedInstanceId != null) return;
            if (_cached.Count == 0) return;

            // pick nearest EP to spawnPos
            var sp = _spawnPos.Value;
            Component? nearest = null;
            float best = float.MaxValue;

            foreach (var ep in _cached)
            {
                if (ep == null) continue;
                var d = Vector3.Distance(sp, ep.transform.position);
                if (d < best)
                {
                    best = d;
                    nearest = ep;
                }
            }

            if (nearest == null) return;

            if (best <= spawnExcludeRadius)
            {
                _spawnExcludedInstanceId = nearest.GetInstanceID();
                _log.LogInfo($"[SPAWN] Excluding spawn-nearest EP: {nearest.gameObject.name} dist={best:0.00} (<= {spawnExcludeRadius:0.00})");
            }
            else if (Verbose)
            {
                _log.LogInfo($"[SPAWN] Nearest EP is {best:0.00}m away (> {spawnExcludeRadius:0.00}), no spawn exclusion applied.");
            }
        }

        public Component? FindNearest(Vector3 refPos, bool excludeSpawn, float spawnExcludeRadius, bool skipActivated)
        {
            if (_cached.Count == 0) return null;

            Component? nearest = null;
            float best = float.MaxValue;

            foreach (var ep in _cached)
            {
                if (ep == null) continue;

                if (excludeSpawn && IsSpawnExcluded(ep, spawnExcludeRadius))
                    continue;

                if (skipActivated && _activatedIds.Contains(ep.GetInstanceID()))
                    continue;

                var d = Vector3.Distance(refPos, ep.transform.position);
                if (d < best)
                {
                    best = d;
                    nearest = ep;
                }
            }

            return nearest;
        }

        public List<Component> BuildSortedList(Vector3 refPos, bool excludeSpawn, float spawnExcludeRadius, bool skipActivated)
        {
            IEnumerable<Component> q = _cached.Where(c => c != null);

            if (excludeSpawn)
                q = q.Where(ep => !IsSpawnExcluded(ep, spawnExcludeRadius));

            if (skipActivated)
                q = q.Where(ep => !_activatedIds.Contains(ep.GetInstanceID()));

            return q
                .OrderBy(ep => Vector3.Distance(refPos, ep.transform.position))
                .ToList();
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

        private bool IsSpawnExcluded(Component ep, float spawnExcludeRadius)
        {
            if (ep == null) return true;

            // if we already pinned a spawnExcluded id, use it
            if (_spawnExcludedInstanceId != null)
                return ep.GetInstanceID() == _spawnExcludedInstanceId.Value;

            // fallback rule: if spawnPos exists, exclude the EP within radius that is closest to spawn
            if (_spawnPos == null) return false;

            var d = Vector3.Distance(_spawnPos.Value, ep.transform.position);
            return d <= spawnExcludeRadius && ep.GetInstanceID() == GetNearestToSpawnInstanceId();
        }

        private int? GetNearestToSpawnInstanceId()
        {
            if (_spawnPos == null) return null;
            if (_cached.Count == 0) return null;

            var sp = _spawnPos.Value;
            Component? nearest = null;
            float best = float.MaxValue;

            foreach (var ep in _cached)
            {
                if (ep == null) continue;
                var d = Vector3.Distance(sp, ep.transform.position);
                if (d < best)
                {
                    best = d;
                    nearest = ep;
                }
            }

            return nearest?.GetInstanceID();
        }

        /// <summary>
        /// 启动时间窗阻塞：用于避免加载期误触发。
        /// 仅依赖时间，不再基于 SpawnGate 状态阻塞。
        /// </summary>
        public bool IsSpawnGateBlocking(List<Component> allPoints)
        {
            return Time.realtimeSinceStartup < _blockUntil;
        }

        public bool IsAnyExtractionPointActivating(List<Component> allPoints)
        {
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

                    return true;
                }
                catch
                {
                    // fail-safe: if can't read, consider it activating
                    return true;
                }
            }

            return false;
        }

        internal static bool IsIdleLikeState(string stateName)
        {
            if (string.IsNullOrEmpty(stateName)) return false;
            // [VERIFY] ExtractionPoint.State.Idle exists in decompiled ExtractionPoint.cs enum.
            return stateName.IndexOf("Idle", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool IsCompletedLikeState(string stateName)
        {
            if (string.IsNullOrEmpty(stateName)) return false;
            var s = stateName.ToLowerInvariant();
            // [VERIFY] ExtractionPoint.State.Success / Complete exist in decompiled ExtractionPoint.cs enum.
            return s.Contains("success")
                || s.Contains("complete")
                || s.Contains("submitted")
                || s.Contains("finish")
                || s.Contains("done");
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
        /// - 永久排除 SpawnGate 点（离出生点最近的那个）
        /// - 保证最后一个点：在“剩余点”里离出生点最近
        /// - 其他点：用贪心最近邻（从 startPos 开始）尽量短路径
        /// - skipActivated: 排除你自己记录为已激活过的点
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
                float d = Vector3.Distance(candidates[i].transform.position, spawnPos);
                if (d < bestFirst)
                {
                    bestFirst = d;
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
                float d = Vector3.Distance(candidates[i].transform.position, spawnPos);
                if (d < best)
                {
                    best = d;
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
                    float d = Vector3.Distance(cur, candidates[i].transform.position);
                    if (d < bd)
                    {
                        bd = d;
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
            DebugLog?.Invoke($"[PLAN] all={allPoints.Count} eligible={result.Count} first={first?.gameObject?.name ?? "null"} last={last.gameObject.name} dt={dt:0.000}s");
            DebugLogPlanList(result, startPos);
            return result;
        }

        private void DebugLogPlanList(List<Component> plan, Vector3 startPos)
        {
            if (plan == null || plan.Count == 0) return;
            for (int i = 0; i < plan.Count; i++)
            {
                var ep = plan[i];
                if (!ep) continue;
                var pos = ep.transform.position;
                var d = Vector3.Distance(startPos, pos);
                var st = ReadStateName(ep);
                var act = IsMarkedActivated(ep);
                var disc = _discovered.Contains(ep.GetInstanceID());
                DebugLog?.Invoke($"[PLAN][{i}] name={ep.gameObject.name} dist={d:0.00} discovered={disc} activated={act} state={st}");
            }
        }
    }
}

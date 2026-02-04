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

        private Type? _epType;
        private readonly List<Component> _cached = new();
        private float _lastScanRealtime;

        private Vector3? _spawnPos;
        private int? _spawnExcludedInstanceId;

        private readonly HashSet<int> _activatedIds = new();

        // =====================
        // Stage1: planning helpers
        // =====================

        private int _spawnGateInstanceId = 0; // 出生点门口最近的提取点（永远排除 + 阻塞）

        public float RescanCooldown { get; set; }
        public bool Verbose { get; set; }

        public ExtractionPointScanner(ManualLogSource log, ExtractionPointInvoker invoker, float rescanCooldown, bool verbose)
        {
            _log = log;
            _invoker = invoker;
            RescanCooldown = rescanCooldown;
            Verbose = verbose;
        }

        public bool EnsureReady()
        {
            if (_epType != null) return true;

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
                var now = Time.realtimeSinceStartup;
                if (!force && (now - _lastScanRealtime) < RescanCooldown) return;
                _lastScanRealtime = now;

                if (!EnsureReady()) return;

                var found = UnityEngine.Object.FindObjectsOfType(_epType!);
                _cached.Clear();
                _cached.AddRange(found.OfType<Component>().Where(c => c != null));

                // 每次 Scan 后都更新 SpawnGate（前提是 spawnPos 已捕获）
                if (_spawnPos != null)
                    UpdateSpawnGateAlwaysNearest(_cached, _spawnPos.Value);

                if (Verbose)
                    _log.LogInfo($"[SCAN] ExtractionPoint rescan: {_cached.Count}");
            }
            catch (Exception e)
            {
                _log.LogError($"ScanIfNeeded failed: {e}");
            }
        }

        public Vector3 GetReferencePos()
        {
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

        public Vector3 GetSpawnPos()
        {
            return _spawnPos ?? Vector3.zero;
        }

        public List<Component> ScanAndGetAllPoints()
        {
            ScanIfNeeded(force: true);
            return _cached.Where(c => c != null).ToList();
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

        public Component? GetSpawnGatePoint(List<Component> allPoints)
        {
            if (_spawnGateInstanceId == 0) return null;
            for (int i = 0; i < allPoints.Count; i++)
            {
                if (allPoints[i] && allPoints[i].GetInstanceID() == _spawnGateInstanceId)
                    return allPoints[i];
            }
            return null;
        }

        /// <summary>
        /// 每次 Scan 后都要更新：直接把“离出生点最近的提取点”设为 SpawnGate（不使用阈值）
        /// </summary>
        private void UpdateSpawnGateAlwaysNearest(List<Component> allPoints, Vector3 spawnPos)
        {
            if (allPoints == null || allPoints.Count == 0)
            {
                _spawnGateInstanceId = 0;
                return;
            }

            Component? best = null;
            float bestD = float.MaxValue;

            for (int i = 0; i < allPoints.Count; i++)
            {
                var ep = allPoints[i];
                if (!ep) continue;

                float d = Vector3.Distance(ep.transform.position, spawnPos);
                if (d < bestD)
                {
                    bestD = d;
                    best = ep;
                }
            }

            _spawnGateInstanceId = best ? best.GetInstanceID() : 0;
        }

        /// <summary>
        /// SpawnGate 点未“提交完成”就阻塞其他点
        /// 注意：如果找不到完成状态字段/属性，要尽量通过反射兜底判断
        /// </summary>
        public bool IsSpawnGateBlocking(List<Component> allPoints)
        {
            var gate = GetSpawnGatePoint(allPoints);
            if (!gate) return false; // 没有识别到 gate，就不阻塞（避免死锁）

            // 关键：只要未提交完成，就阻塞
            return !IsExtractionPointSubmitted(gate);
        }

        private bool IsExtractionPointSubmitted(Component ep)
        {
            if (!ep) return true;

            var t = ep.GetType();

            // 1) 常见 bool 字段/属性名（优先）
            string[] boolNames =
            {
                "Submitted","submitted",
                "IsSubmitted","isSubmitted",
                "HasSubmitted","hasSubmitted",
                "Completed","completed",
                "IsCompleted","isCompleted",
                "HasCompleted","hasCompleted",
                "Finished","finished",
                "IsFinished","isFinished"
            };

            foreach (var n in boolNames)
            {
                var p = t.GetProperty(n, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (p != null && p.PropertyType == typeof(bool))
                {
                    return (bool)p.GetValue(ep, null);
                }

                var f = t.GetField(n, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (f != null && f.FieldType == typeof(bool))
                {
                    return (bool)f.GetValue(ep);
                }
            }

            // 2) 兜底：看 state/status 类字段（枚举/字符串/整数），包含 “success/complete/submitted/finished/done” 视为提交完成
            string[] stateNames = { "state", "State", "status", "Status", "currentState", "CurrentState", "phase", "Phase" };
            foreach (var n in stateNames)
            {
                object? v = null;

                var p = t.GetProperty(n, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (p != null) v = p.GetValue(ep, null);

                var f = t.GetField(n, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (v == null && f != null) v = f.GetValue(ep);

                if (v == null) continue;

                var s = v.ToString() ?? "";
                s = s.ToLowerInvariant();

                if (s.Contains("success") || s.Contains("complete") || s.Contains("submitted") || s.Contains("finish") || s.Contains("done"))
                    return true;
            }

            // 3) 最后兜底：无法判断 => 不阻塞（视为已提交）
            return true;
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
                    if (v == null) continue;

                    var s = v.ToString() ?? "";
                    if (s.Length == 0) continue;

                    if (s.IndexOf("Idle", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (s.IndexOf("Complete", StringComparison.OrdinalIgnoreCase) >= 0) continue;

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
            var result = new List<Component>();
            if (allPoints == null || allPoints.Count == 0) return result;

            // 先确定 SpawnGate
            UpdateSpawnGateAlwaysNearest(allPoints, spawnPos);

            // 复制候选集：排除 SpawnGate +（可选）排除已激活
            var candidates = new List<Component>(allPoints.Count);
            for (int i = 0; i < allPoints.Count; i++)
            {
                var ep = allPoints[i];
                if (!ep) continue;

                if (_spawnGateInstanceId != 0 && ep.GetInstanceID() == _spawnGateInstanceId)
                    continue;

                if (skipActivated && IsMarkedActivated(ep))
                    continue;

                candidates.Add(ep);
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
            return result;
        }
    }
}

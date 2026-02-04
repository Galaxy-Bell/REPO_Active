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
    }
}

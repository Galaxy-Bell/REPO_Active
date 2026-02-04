using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using REPO_Active.Runtime;
using REPO_Active.Reflection;

namespace REPO_Active
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "angelcomilk.repo_active";
        public const string PluginName = "REPO_Active";
        public const string PluginVersion = "3.5.3";

        // ---- config ----
        private ConfigEntry<KeyCode> _keyActivateNearest = null!;
        private ConfigEntry<KeyCode> _keyBuildQueueAndRun = null!;
        private ConfigEntry<bool> _excludeSpawnNearest = null!;
        private ConfigEntry<float> _spawnExcludeRadius = null!;
        private ConfigEntry<float> _rescanCooldown = null!;
        private ConfigEntry<float> _perActivationDelay = null!;
        private ConfigEntry<bool> _skipAlreadyActivated = null!;
        private ConfigEntry<bool> _logVerbose = null!;

        private ExtractionPointScanner _scanner = null!;
        private ActivationQueue _queue = null!;
        private ExtractionPointInvoker _invoker = null!;

        private void Awake()
        {
            _keyActivateNearest = Config.Bind("Keybinds", "ActivateNearest", KeyCode.F3, "Press to activate nearest extraction point (uses OnClick via reflection).");
            _keyBuildQueueAndRun = Config.Bind("Keybinds", "BuildQueueAndRun", KeyCode.F4, "Press to build queue (sorted by distance) and activate one by one.");

            _excludeSpawnNearest = Config.Bind("SpawnExclude", "ExcludeNearestToSpawn", true, "Exclude the extraction point nearest to spawn from queue/nearest selection.");
            _spawnExcludeRadius = Config.Bind("SpawnExclude", "SpawnExcludeRadius", 14f, "Only exclude spawn-nearest EP if its distance <= this radius (meters).");

            _rescanCooldown = Config.Bind("Runtime", "RescanCooldown", 0.6f, "Cooldown seconds between scans.");
            _perActivationDelay = Config.Bind("Runtime", "PerActivationDelay", 0.15f, "Delay between queue activations (seconds).");
            _skipAlreadyActivated = Config.Bind("Runtime", "SkipAlreadyActivated", true, "Skip EPs already activated by this plugin in the current run.");
            _logVerbose = Config.Bind("Debug", "VerboseLog", false, "More logs.");

            _invoker = new ExtractionPointInvoker(Logger, _logVerbose.Value);
            _scanner = new ExtractionPointScanner(Logger, _invoker, _rescanCooldown.Value, _logVerbose.Value);
            _queue = new ActivationQueue(Logger, _invoker, _scanner, _perActivationDelay.Value, _logVerbose.Value);

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. (OnClick reflection activation)");
        }

        private void Update()
        {
            // hot-update config values that affect runtime behavior
            _scanner.RescanCooldown = _rescanCooldown.Value;
            _scanner.Verbose = _logVerbose.Value;

            _queue.PerActivationDelay = _perActivationDelay.Value;
            _queue.Verbose = _logVerbose.Value;

            _invoker.Verbose = _logVerbose.Value;

            if (Input.GetKeyDown(_keyActivateNearest.Value))
            {
                ActivateNearest();
            }

            if (Input.GetKeyDown(_keyBuildQueueAndRun.Value))
            {
                BuildQueueAndRun();
            }
        }

        private void ActivateNearest()
        {
            if (!_scanner.EnsureReady())
            {
                Logger.LogWarning("ExtractionPoint type not found yet.");
                return;
            }

            _scanner.ScanIfNeeded(force: true);

            var refPos = _scanner.GetReferencePos();
            if (_excludeSpawnNearest.Value)
                _scanner.UpdateSpawnExcludeIfNeeded(refPos, _spawnExcludeRadius.Value);

            var nearest = _scanner.FindNearest(refPos,
                excludeSpawn: _excludeSpawnNearest.Value,
                spawnExcludeRadius: _spawnExcludeRadius.Value,
                skipActivated: _skipAlreadyActivated.Value);

            if (nearest == null)
            {
                Logger.LogWarning("No eligible extraction point found.");
                return;
            }

            Logger.LogInfo($"[F3] Activate nearest EP: {nearest.gameObject.name} pos={nearest.transform.position} dist={Vector3.Distance(refPos, nearest.transform.position):0.00}");

            if (_invoker.InvokeOnClick(nearest))
            {
                _scanner.MarkActivated(nearest);
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
            if (_excludeSpawnNearest.Value)
                _scanner.UpdateSpawnExcludeIfNeeded(refPos, _spawnExcludeRadius.Value);

            var list = _scanner.BuildSortedList(refPos,
                excludeSpawn: _excludeSpawnNearest.Value,
                spawnExcludeRadius: _spawnExcludeRadius.Value,
                skipActivated: _skipAlreadyActivated.Value);

            if (list.Count == 0)
            {
                Logger.LogWarning("Queue is empty (no eligible extraction points).");
                return;
            }

            Logger.LogInfo($"[F4] Queue built: {list.Count} EP(s). Start activating...");
            _queue.StartQueue(this, list, onActivated: ep => _scanner.MarkActivated(ep));
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using REPO_Active.Reflection;

namespace REPO_Active.Runtime
{
    public sealed class ActivationQueue
    {
        private readonly ManualLogSource _log;
        private readonly ExtractionPointInvoker _invoker;
        private readonly ExtractionPointScanner _scanner;

        private Coroutine? _running;

        public float PerActivationDelay { get; set; }
        public bool Verbose { get; set; }

        public ActivationQueue(ManualLogSource log, ExtractionPointInvoker invoker, ExtractionPointScanner scanner, float perActivationDelay, bool verbose)
        {
            _log = log;
            _invoker = invoker;
            _scanner = scanner;
            PerActivationDelay = perActivationDelay;
            Verbose = verbose;
        }

        public void StartQueue(MonoBehaviour host, List<Component> queue, Action<Component>? onActivated)
        {
            if (_running != null)
            {
                host.StopCoroutine(_running);
                _running = null;
            }

            _running = host.StartCoroutine(Run(queue, onActivated));
        }

        private IEnumerator Run(List<Component> queue, Action<Component>? onActivated)
        {
            for (int i = 0; i < queue.Count; i++)
            {
                var ep = queue[i];
                if (ep == null) continue;

                var ok = _invoker.InvokeOnClick(ep);
                if (ok)
                {
                    onActivated?.Invoke(ep);
                    if (Verbose)
                        _log.LogInfo($"[QUEUE] Activated {i + 1}/{queue.Count}: {ep.gameObject.name}");
                }
                else
                {
                    _log.LogWarning($"[QUEUE] Activation failed: {ep.gameObject.name}");
                }

                if (PerActivationDelay > 0)
                    yield return new WaitForSeconds(PerActivationDelay);
                else
                    yield return null;
            }

            _log.LogInfo("[QUEUE] Done.");
            _running = null;
        }
    }
}

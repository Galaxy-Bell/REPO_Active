using System;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace REPO_Active.Reflection
{
    public sealed class ExtractionPointInvoker
    {
        private readonly ManualLogSource _log;

        public bool Verbose { get; set; }

        public ExtractionPointInvoker(ManualLogSource log, bool verbose)
        {
            _log = log;
            Verbose = verbose;
        }

        public bool InvokeOnClick(Component ep)
        {
            if (ep == null) return false;

            try
            {
                // [VERIFY] ExtractionPoint.OnClick() exists in decompiled Assembly-CSharp and is parameterless.
                // [NOTE] Default-args fallback is safe but not required for current game build.
                var t = ep.GetType();
                var mi = t.GetMethod("OnClick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (mi == null)
                {
                    _log.LogWarning($"OnClick not found on {t.FullName}. (No activation performed)");
                    return false;
                }

                var args = BuildDefaultArgs(mi);
                if (Verbose)
                    _log.LogInfo($"Invoke: {t.Name}.OnClick({FormatSig(mi)}) argsLen={(args == null ? 0 : args.Length)}");

                mi.Invoke(ep, args);
                return true;
            }
            catch (Exception e)
            {
                _log.LogError($"InvokeOnClick failed: {e}");
                return false;
            }
        }

        private static object[]? BuildDefaultArgs(MethodInfo mi)
        {
            try
            {
                var ps = mi.GetParameters();
                if (ps == null || ps.Length == 0) return null;

                var args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    var p = ps[i];
                    if (p.HasDefaultValue)
                    {
                        args[i] = p.DefaultValue;
                        continue;
                    }

                    var pt = p.ParameterType;
                    if (pt.IsByRef)
                        pt = pt.GetElementType() ?? pt;

                    if (!pt.IsValueType)
                    {
                        args[i] = null!;
                        continue;
                    }

                    try { args[i] = Activator.CreateInstance(pt)!; }
                    catch { args[i] = null!; }
                }
                return args;
            }
            catch
            {
                return null;
            }
        }

        private static string FormatSig(MethodInfo mi)
        {
            try
            {
                var ps = mi.GetParameters();
                return string.Join(",", ps.Select(x => x.ParameterType.Name));
            }
            catch
            {
                return "";
            }
        }
    }
}

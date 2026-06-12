using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace EnchorCrowdRequests
{
    // Diagnostic (enabled by config DiagnoseRescan): Harmony-hooks every 0- or 1-bool-arg
    // method on Clone Hero's SongScan type and logs the first call to each. Click the
    // in-game "Scan Songs" button and the log reveals exactly which method the game
    // invokes (and the bool it passes) - so the auto-rescan can call the same one.
    public static class RescanDiag
    {
        private static readonly HashSet<string> Seen = new HashSet<string>();
        private static ManualLogSource _log;
        private static Stopwatch _sw;

        public static void Install(ManualLogSource log)
        {
            _log = log;
            _sw = Stopwatch.StartNew();

            Type t = Rescan.FindGameType("SongScan");
            if (t == null) { log.LogWarning("[RescanDiag] SongScan type not found."); return; }

            var harmony = new Harmony("encorebrowser.diag");
            MethodInfo logName = typeof(RescanDiag).GetMethod(nameof(LogName), BindingFlags.Public | BindingFlags.Static);
            MethodInfo logBool = typeof(RescanDiag).GetMethod(nameof(LogBool), BindingFlags.Public | BindingFlags.Static);

            int patched = 0;
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                  BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.IsSpecialName || m.IsAbstract || m.IsGenericMethod) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length > 1) continue;
                bool boolArg = ps.Length == 1 && ps[0].ParameterType == typeof(bool);
                if (ps.Length == 1 && !boolArg) continue; // only 0-arg or 1-bool

                try
                {
                    harmony.Patch(m, prefix: new HarmonyMethod(boolArg ? logBool : logName));
                    patched++;
                }
                catch (Exception ex)
                {
                    _log.LogWarning("[RescanDiag] could not hook " + m.Name + ": " + ex.Message);
                }
            }
            log.LogInfo("[RescanDiag] hooked " + patched + " SongScan methods. " +
                        "Open Settings > General and click 'Scan Songs' now.");
        }

        public static void LogName(MethodBase __originalMethod) { Report(__originalMethod, ""); }
        public static void LogBool(MethodBase __originalMethod, bool __0) { Report(__originalMethod, "bool=" + __0); }

        private static void Report(MethodBase m, string extra)
        {
            string key = m.Name + "|" + extra;
            lock (Seen) { if (!Seen.Add(key)) return; }
            _log.LogInfo("[RescanDiag] " + _sw.Elapsed.TotalSeconds.ToString("F1") + "s  CALLED SongScan." +
                         m.Name + "(" + extra + ")");
        }
    }
}


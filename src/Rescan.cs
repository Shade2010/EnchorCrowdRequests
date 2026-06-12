using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace EnchorCrowdRequests
{
    // Triggers Clone Hero's own song scan so downloaded charts appear without leaving
    // the game.
    //
    // Clone Hero v1.x is IL2CPP with STRIPPED METHOD NAMES, so the scanner's methods
    // arrive as synthetic names like Method_Public_Coroutine_Boolean_0. Type names DO
    // survive, so we target the type "SongScan" and match by signature: its public
    // `Coroutine <m>(bool)` is the scan starter (the bool = full scan). It returns a
    // Coroutine, i.e. it self-starts via StartCoroutine, so a plain reflection Invoke
    // kicks off the scan. We pass true (full scan).
    //
    // Everything is overridable via RescanType/RescanMethod, and there's a generic
    // name-based fallback for non-stripped builds/other games. MUST run on the main thread.
    public static class Rescan
    {
        private static MethodInfo _method;
        private static object _target;
        private static object[] _args;
        private static bool _resolved;

        // The live SongScan instance, captured from the game's own scan calls via a
        // Harmony hook. Far more reliable than reflecting for it (IL2CPP object wrappers
        // make a type-based search unreliable). Set once, on the first SongScan call.
        private static object _captured;
        private static bool _captureInstalled;

        private static readonly string[] GameAsmNames =
            { "CloneHero", "StrikeCore", "Assembly-CSharp", "Il2Cpp-Assembly-CSharp" };

        private const string ScannerTypeName = "SongScan";

        private static readonly string[] NameHints =
            { "scansongs", "rescansongs", "scanlibrary", "rescanlibrary", "rescan", "scan" };

        public static void Trigger(ManualLogSource log, string typeOverride, string methodOverride)
        {
            try
            {
                if (!_resolved)
                {
                    Resolve(log, typeOverride, methodOverride);
                    _resolved = true;
                }

                if (_method == null)
                {
                    log.LogWarning("Auto-rescan: no scan method resolved. Rescan manually via " +
                                   "Settings > General > Scan Songs, or set RescanType/RescanMethod in the config.");
                    return;
                }

                object target = null;
                if (!_method.IsStatic)
                {
                    // Re-acquire the CURRENT instance every time. Playing a song reloads the
                    // menu/library scene, which destroys+recreates SongScan - so a cached
                    // reference goes stale (that's why the rescan stopped working after play).
                    target = FindCurrentScanner(_method.DeclaringType);
                    if (target == null && IsAlive(_captured)) target = _captured;     // capture, if still alive
                    if (target == null) target = FindInstance(_method.DeclaringType); // last-ditch
                    if (target == null)
                    {
                        log.LogWarning("Auto-rescan: no live " + _method.DeclaringType.Name + " instance found.");
                        return;
                    }
                }

                _method.Invoke(_method.IsStatic ? null : target, _args);
                log.LogInfo("Auto-rescan: invoked " + _method.DeclaringType.Name + "." + _method.Name +
                            "(" + (_args == null ? "" : "true") + ").");
            }
            catch (Exception ex)
            {
                log.LogError("Auto-rescan failed: " + ex.Message);
            }
        }

        private static void Resolve(ManualLogSource log, string typeOverride, string methodOverride)
        {
            var types = new List<Type>(AllGameTypes());
            if (types.Count == 0)
            {
                log.LogWarning("Auto-rescan: game assemblies (CloneHero/StrikeCore) not loaded yet.");
                return;
            }

            // 1) Explicit override wins.
            if (!string.IsNullOrEmpty(methodOverride))
            {
                foreach (Type t in types)
                {
                    if (!string.IsNullOrEmpty(typeOverride) &&
                        t.Name != typeOverride && t.FullName != typeOverride) continue;
                    foreach (MethodInfo m in Methods(t))
                    {
                        if (m.Name != methodOverride) continue;
                        object[] args = ArgsFor(m);
                        if (args != null || m.GetParameters().Length == 0) { Bind(log, m, args); return; }
                    }
                }
                log.LogWarning("Auto-rescan: override '" + typeOverride + "." + methodOverride +
                               "()' not found or unsupported signature; trying auto-discovery.");
            }

            // 2) Clone Hero: SongScan.<Coroutine>(bool) matched by signature (names are stripped).
            foreach (Type t in types)
            {
                if (!string.Equals(t.Name, ScannerTypeName, StringComparison.OrdinalIgnoreCase)) continue;
                MethodInfo m = PickScanStarter(t);
                if (m != null) { Bind(log, m, ArgsFor(m)); return; }
            }

            // 3) Generic fallback: a parameterless scan-named method (works if names aren't stripped).
            MethodInfo best = null;
            int bestScore = int.MinValue;
            foreach (Type t in types)
            {
                foreach (MethodInfo m in Methods(t))
                {
                    if (m.GetParameters().Length != 0 || m.IsAbstract || m.IsGenericMethod) continue;
                    int hint = IndexOfHint(m.Name.ToLowerInvariant(), NameHints);
                    if (hint < 0) continue;
                    int score = (NameHints.Length - hint) * 10;
                    if (m.ReturnType == typeof(void)) score += 2;
                    if (score > bestScore) { bestScore = score; best = m; }
                }
            }
            if (best != null) { Bind(log, best, null); return; }

            log.LogWarning("Auto-rescan: could not identify the scan method.");
            DumpCandidates(log);
        }

        // SongScan's scan starter: public instance method returning a Coroutine and taking
        // a single bool. Prefer the primary (non-"PDM") synthetic overload.
        private static MethodInfo PickScanStarter(Type t)
        {
            MethodInfo fallback = null;
            foreach (MethodInfo m in Methods(t))
            {
                if (m.IsStatic) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != 1 || ps[0].ParameterType != typeof(bool)) continue;
                if (m.ReturnType.Name != "Coroutine") continue;
                if (m.Name.IndexOf("PDM", StringComparison.OrdinalIgnoreCase) < 0) return m;
                if (fallback == null) fallback = m;
            }
            return fallback;
        }

        // Reads SongScan.isScanning if available (used by the optional self-test).
        public static bool TryIsScanning(out bool scanning)
        {
            scanning = false;
            try
            {
                foreach (Type t in AllGameTypes())
                {
                    if (!string.Equals(t.Name, ScannerTypeName, StringComparison.OrdinalIgnoreCase)) continue;
                    object inst = _captured ?? FindInstance(t);
                    if (inst == null) return false;
                    MethodInfo gm = t.GetMethod("get_isScanning",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, Type.EmptyTypes, null);
                    if (gm != null) { scanning = (bool)gm.Invoke(inst, null); return true; }
                }
            }
            catch { }
            return false;
        }

        public static void DumpCandidates(ManualLogSource log)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("Auto-rescan: methods on scan-related types (set RescanType + RescanMethod to pin one):\n");
            int n = 0;
            foreach (Type t in AllGameTypes())
            {
                string tl = t.Name.ToLowerInvariant();
                if (!(tl.Contains("scan") || tl.Contains("library") || tl.Contains("songcache"))) continue;
                foreach (MethodInfo m in Methods(t))
                {
                    if (m.IsSpecialName || m.IsGenericMethod) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length > 1) continue;
                    string sig = ps.Length == 0 ? "" : ps[0].ParameterType.Name;
                    sb.Append("  ").Append(t.Name).Append('.').Append(m.Name)
                      .Append('(').Append(sig).Append(") -> ").Append(m.ReturnType.Name)
                      .Append(m.IsStatic ? " [static]" : "").Append('\n');
                    if (++n >= 80) { sb.Append("  ... (truncated)\n"); break; }
                }
                if (n >= 80) break;
            }
            if (n == 0) sb.Append("  (no scan-related types found - are CloneHero/StrikeCore loaded yet?)");
            log.LogInfo(sb.ToString());
        }

        private static object[] ArgsFor(MethodInfo m)
        {
            ParameterInfo[] ps = m.GetParameters();
            if (ps.Length == 0) return null;
            if (ps.Length == 1 && ps[0].ParameterType == typeof(bool)) return new object[] { true }; // full scan
            return null; // unsupported signature
        }

        private static void Bind(ManualLogSource log, MethodInfo m, object[] args)
        {
            _method = m;
            _args = args;
            if (!m.IsStatic) _target = FindInstance(m.DeclaringType);
            log.LogInfo("Auto-rescan: using " + m.DeclaringType.Name + "." + m.Name +
                        "(" + (args == null ? "" : "bool=true") + ")" + (m.IsStatic ? " [static]" : "") + ".");
        }

        private static object FindInstance(Type t)
        {
            string[] names = { "Instance", "instance", "Singleton", "Current", "Main", "main" };
            foreach (string n in names)
            {
                PropertyInfo p = t.GetProperty(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (p != null && t.IsAssignableFrom(p.PropertyType))
                {
                    object v = p.GetValue(null, null);
                    if (v != null) return v;
                }
                FieldInfo f = t.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (f != null && t.IsAssignableFrom(f.FieldType))
                {
                    object v = f.GetValue(null);
                    if (v != null) return v;
                }
            }

            try
            {
                if (typeof(UnityEngine.Object).IsAssignableFrom(t))
                {
                    foreach (UnityEngine.Object obj in Resources.FindObjectsOfTypeAll<UnityEngine.Object>())
                    {
                        if (obj != null && t.IsInstanceOfType(obj)) return obj;
                    }
                }
            }
            catch { /* fall through */ }
            return null;
        }

        // True if a captured UnityEngine.Object is still alive (Unity's overloaded == returns
        // false for a destroyed/"fake-null" object).
        private static bool IsAlive(object o)
        {
            if (o == null) return false;
            UnityEngine.Object uo = o as UnityEngine.Object;
            return uo != null;
        }

        // Finds the CURRENT live scanner instance via the generic FindObjectsOfType<T>()
        // (resolved with the runtime proxy type). Returns the first alive, preferring an
        // active-and-enabled one.
        private static MethodInfo _findGeneric;
        private static bool _findResolved;
        private static bool _loggedFind;

        private static object FindCurrentScanner(Type t)
        {
            try
            {
                if (!_findResolved)
                {
                    _findResolved = true;
                    foreach (MethodInfo m in typeof(UnityEngine.Object).GetMethods(BindingFlags.Public | BindingFlags.Static))
                        if (m.Name == "FindObjectsOfType" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0) { _findGeneric = m; break; }
                    if (_findGeneric == null)
                        foreach (MethodInfo m in typeof(UnityEngine.Resources).GetMethods(BindingFlags.Public | BindingFlags.Static))
                            if (m.Name == "FindObjectsOfTypeAll" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0) { _findGeneric = m; break; }
                }
                if (_findGeneric == null) return null;

                object arr = _findGeneric.MakeGenericMethod(t).Invoke(null, null);
                var en = arr as System.Collections.IEnumerable;
                if (en == null) return null;

                object firstAlive = null;
                foreach (object o in en)
                {
                    if (!IsAlive(o)) continue;
                    if (firstAlive == null) firstAlive = o;
                    if (IsActiveEnabled(o)) { LogFind(true); return o; }
                }
                LogFind(firstAlive != null);
                return firstAlive;
            }
            catch (Exception ex) { if (!_loggedFind) { _loggedFind = true; try { Plugin.Logger.LogWarning("Auto-rescan: FindCurrentScanner error: " + ex.Message); } catch { } } }
            return null;
        }

        private static void LogFind(bool found)
        {
            if (_loggedFind) return;
            _loggedFind = true;
            try { Plugin.Logger.LogInfo("Auto-rescan: live-instance lookup via " + (_findGeneric != null ? _findGeneric.Name : "none") + " -> " + (found ? "found" : "none")); } catch { }
        }

        private static bool IsActiveEnabled(object o)
        {
            try
            {
                PropertyInfo p = o.GetType().GetProperty("isActiveAndEnabled", BindingFlags.Public | BindingFlags.Instance);
                if (p != null) return (bool)p.GetValue(o, null);
            }
            catch { }
            return false;
        }

        private static MethodInfo[] Methods(Type t)
        {
            try
            {
                return t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            }
            catch { return Array.Empty<MethodInfo>(); }
        }

        // Installs a Harmony hook that captures the live SongScan instance the first time
        // the game calls any of its methods (e.g. the scan on startup). Call once at load.
        public static void InstallCapture(ManualLogSource log)
        {
            if (_captureInstalled) return;
            _captureInstalled = true;
            try
            {
                Type t = FindGameType(ScannerTypeName);
                if (t == null) { log.LogWarning("Auto-rescan: " + ScannerTypeName + " type not found for capture."); return; }

                // Patch ONLY the scan-starter method to capture the live instance. It runs
                // during library scans (startup / after downloads) but NOT during gameplay,
                // so it can't affect song loading. (Patching every SongScan method crashed
                // song loads - several of them are invoked while a song loads to play.)
                MethodInfo scan = PickScanStarter(t);
                if (scan == null)
                {
                    log.LogWarning("Auto-rescan: scan method not found; instance will be located on demand.");
                    return;
                }

                var harmony = new Harmony("encorebrowser.capture");
                MethodInfo prefix = typeof(Rescan).GetMethod(nameof(CaptureInstance), BindingFlags.Public | BindingFlags.Static);
                harmony.Patch(scan, prefix: new HarmonyMethod(prefix));
                log.LogInfo("Auto-rescan: instance-capture armed on " + ScannerTypeName + "." + scan.Name + ".");
            }
            catch (Exception ex)
            {
                log.LogWarning("Auto-rescan: could not arm instance capture: " + ex.Message);
            }
        }

        public static void CaptureInstance(object __instance)
        {
            if (_captured == null && __instance != null) _captured = __instance;
        }

        // Exposed for the diagnostic hook (RescanDiag).
        public static Type FindGameType(string name)
        {
            foreach (Type t in AllGameTypes())
                if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        private static IEnumerable<Type> AllGameTypes()
        {
            foreach (Assembly a in GameAssemblies())
                foreach (Type t in SafeTypes(a))
                    yield return t;
        }

        private static List<Assembly> GameAssemblies()
        {
            var list = new List<Assembly>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = a.GetName().Name;
                foreach (string g in GameAsmNames)
                    if (string.Equals(n, g, StringComparison.OrdinalIgnoreCase)) { if (seen.Add(n)) list.Add(a); break; }
            }

            try
            {
                string interop = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "interop");
                foreach (string g in GameAsmNames)
                {
                    if (seen.Contains(g)) continue;
                    string p = System.IO.Path.Combine(interop, g + ".dll");
                    if (!System.IO.File.Exists(p)) continue;
                    try { Assembly a = Assembly.LoadFrom(p); if (seen.Add(a.GetName().Name)) list.Add(a); }
                    catch { }
                }
            }
            catch { }

            return list;
        }

        private static IEnumerable<Type> SafeTypes(Assembly a)
        {
            Type[] types;
            try { types = a.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            catch { yield break; }
            foreach (Type t in types) if (t != null) yield return t;
        }

        private static int IndexOfHint(string name, string[] hints)
        {
            for (int i = 0; i < hints.Length; i++)
                if (name.Contains(hints[i])) return i;
            return -1;
        }
    }
}


using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EnchorCrowdRequests
{
    // Stops keyboard input from reaching the game while the overlay is open by patching
    // UnityEngine.Input's keyboard reads to return "nothing" when Active. This is needed
    // because Clone Hero's menus read some keys (e.g. Space) via UnityEngine.Input directly,
    // not through Rewired - so disabling the Rewired keyboard alone doesn't stop them.
    //
    // Our own UI keeps working: the TMP InputField gets typed text from the OS event queue
    // (Event.current), not Input.GetKey; and mouse Input is left alone so uGUI clicks work.
    // The toggle key is allowed through so the overlay can still be closed.
    public static class KeyBlock
    {
        public static bool Active;
        private static bool _installed;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                var h = new Harmony("enchor.crowdrequests.keyblock");
                Type t = typeof(UnityEngine.Input);
                Type[] kc = { typeof(KeyCode) };
                Type[] str = { typeof(string) };

                Patch(h, t, "GetKeyDown", kc, nameof(PreKeyDownKc));
                Patch(h, t, "GetKey", kc, nameof(PreBool));
                Patch(h, t, "GetKeyUp", kc, nameof(PreBool));
                Patch(h, t, "GetKeyDown", str, nameof(PreBool));
                Patch(h, t, "GetKey", str, nameof(PreBool));
                Patch(h, t, "GetKeyUp", str, nameof(PreBool));
                Patch(h, t, "GetButton", str, nameof(PreBool));
                Patch(h, t, "GetButtonDown", str, nameof(PreBool));
                Patch(h, t, "GetButtonUp", str, nameof(PreBool));
                Patch(h, t, "get_anyKey", Type.EmptyTypes, nameof(PreBool));
                Patch(h, t, "get_anyKeyDown", Type.EmptyTypes, nameof(PreBool));
                Patch(h, t, "GetAxis", str, nameof(PreFloat));
                Patch(h, t, "GetAxisRaw", str, nameof(PreFloat));

                Plugin.Logger.LogInfo("EnchorCrowdRequests: keyboard blocker installed.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("EnchorCrowdRequests: key blocker failed: " + ex.Message); }
        }

        private static void Patch(Harmony h, Type t, string name, Type[] args, string prefix)
        {
            try
            {
                MethodInfo m = t.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, args, null);
                if (m != null)
                    h.Patch(m, prefix: new HarmonyMethod(typeof(KeyBlock).GetMethod(prefix, BindingFlags.Public | BindingFlags.Static)));
            }
            catch { }
        }

        // return false from a prefix => skip the original; __result is what the method returns.
        public static bool PreBool(ref bool __result) { if (Active) { __result = false; return false; } return true; }
        public static bool PreFloat(ref float __result) { if (Active) { __result = 0f; return false; } return true; }
        public static bool PreKeyDownKc(KeyCode key, ref bool __result)
        {
            if (Active && key != Plugin.ToggleKey) { __result = false; return false; }   // let the toggle key through
            return true;
        }
    }
}

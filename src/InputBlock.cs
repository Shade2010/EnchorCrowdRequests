using System;
using System.Reflection;
using BepInEx.Logging;

namespace EnchorCrowdRequests
{
    // Suppresses the game's input while the overlay is open, so typing a search doesn't
    // also drive Clone Hero's menus. The game uses Rewired, which polls the keyboard
    // independently of IMGUI.
    //
    // We toggle Rewired's Keyboard and Mouse *controllers* via ReInput.controllers - their
    // `enabled` flag is the designed runtime on/off and is cleanly REVERSIBLE (disabling the
    // whole Rewired InputManager component was not - it didn't recover on re-enable). Our
    // overlay still works because it reads UnityEngine.Input + IMGUI events, independent of
    // Rewired.
    //
    // Tick(true) every frame while open; Tick(false) once on close to restore. Main thread.
    public static class InputBlock
    {
        private static bool _ready;
        private static object _keyboard, _mouse;
        private static PropertyInfo _kbEnabled, _mEnabled;
        private static bool _loggedFail;
        private static bool _loggedSet;

        public static void Tick(bool blocked)
        {
            try
            {
                if (!_ready) Resolve();
                if (!_ready) return;

                // Keyboard only - the overlay's full-screen blocker handles mouse, and leaving the
                // Rewired mouse enabled keeps the uGUI pointer/clicks working if it's Rewired-driven.
                if (_kbEnabled != null && _keyboard != null) _kbEnabled.SetValue(_keyboard, !blocked, null);

                if (!_loggedSet && _kbEnabled != null && _keyboard != null)
                {
                    _loggedSet = true;
                    Plugin.Logger.LogInfo("Input suppression: set blocked=" + blocked + ", keyboard.enabled now = " + _kbEnabled.GetValue(_keyboard, null));
                }
            }
            catch (Exception ex)
            {
                if (!_loggedFail) { _loggedFail = true; Plugin.Logger.LogWarning("Input suppression unavailable: " + ex.Message); }
            }
        }

        private static void Resolve()
        {
            Type reInput = FindType("Rewired.ReInput");
            if (reInput == null) return;   // Rewired proxy not loaded yet

            PropertyInfo controllersProp = reInput.GetProperty("controllers",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            object controllers = controllersProp == null ? null : controllersProp.GetValue(null, null);
            if (controllers == null) return;   // Rewired not ready yet (retry next frame)

            Type ct = controllers.GetType();
            PropertyInfo kbProp = ct.GetProperty("Keyboard", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            PropertyInfo mProp = ct.GetProperty("Mouse", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _keyboard = kbProp == null ? null : kbProp.GetValue(controllers, null);
            _mouse = mProp == null ? null : mProp.GetValue(controllers, null);

            if (_keyboard != null) _kbEnabled = _keyboard.GetType().GetProperty("enabled", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (_mouse != null) _mEnabled = _mouse.GetType().GetProperty("enabled", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (_kbEnabled != null || _mEnabled != null)
            {
                _ready = true;
                Plugin.Logger.LogInfo("Input suppression armed (keyboard=" + (_kbEnabled != null) + ", mouse=" + (_mEnabled != null) + ").");
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { Type t = a.GetType(fullName); if (t != null) return t; } catch { }
            }
            try
            {
                string p = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "interop", "Rewired_Core.dll");
                if (System.IO.File.Exists(p)) return Assembly.LoadFrom(p).GetType(fullName);
            }
            catch { }
            return null;
        }
    }
}


using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace EnchorCrowdRequests
{
    [BepInPlugin(Guid, "Enchor - Crowd Requests", "0.1.0")]
    public class Plugin : BasePlugin
    {
        public const string Guid = "enchor.crowdrequests";

        internal static ManualLogSource Logger;
        internal static KeyCode ToggleKey = KeyCode.F9;
        internal static ConfigEntry<string> SongsOverrideEntry;
        internal static string SongsOverride { get { return SongsOverrideEntry != null ? SongsOverrideEntry.Value : ""; } }
        internal static bool AutoRescan = true;
        internal static string RescanType = "";
        internal static string RescanMethod = "";
        internal static int DownloadDelayMs = 1200;
        internal static int SelfTestSeconds = 0;

        public override void Load()
        {
            Logger = Log;

            string keyName = Config.Bind("General", "ToggleKey", "F9",
                "Key that opens / closes the overlay. Any UnityEngine.KeyCode name.").Value;
            SongsOverrideEntry = Config.Bind("General", "SongsFolderOverride", "",
                "Download folder. Empty = auto-detect. Set in-game via the Change Folder button.");
            float delaySec = Config.Bind("General", "DownloadDelaySeconds", 1.2f,
                "Seconds between downloads (one at a time) so the site doesn't rate-limit you.").Value;
            DownloadDelayMs = (int)(Math.Max(0.25f, delaySec) * 1000f);

            AutoRescan = Config.Bind("Rescan", "AutoRescan", true,
                "Automatically rescan the song library after downloads finish.").Value;
            RescanType = Config.Bind("Rescan", "RescanType", "", "Optional. Exact scanner class name.").Value;
            RescanMethod = Config.Bind("Rescan", "RescanMethod", "", "Optional. Exact scan method name.").Value;

            ToggleKey = ParseKey(keyName, KeyCode.F9);
            int.TryParse(Environment.GetEnvironmentVariable("ENCORE_SELFTEST_SECONDS"), out SelfTestSeconds);

            ClassInjector.RegisterTypeInIl2Cpp<UIController>();
            var go = new GameObject("EnchorCrowdRequests");
            go.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<UIController>();

            Logger.LogInfo("Enchor - Crowd Requests loaded. Press " + ToggleKey + " in-game to open. Downloads -> " +
                           SongPath.Resolve(SongsOverride));

            if (AutoRescan) Rescan.InstallCapture(Logger);
            KeyBlock.Install();
        }

        public static void SetSongsFolder(string path)
        {
            if (SongsOverrideEntry != null) SongsOverrideEntry.Value = path ?? "";
        }

        private static KeyCode ParseKey(string name, KeyCode fallback)
        {
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), name, true); }
            catch { return fallback; }
        }
    }
}

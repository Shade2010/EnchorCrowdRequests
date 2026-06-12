using System;
using System.IO;
using System.Text;

namespace EnchorCrowdRequests
{
    // Figures out where to drop downloaded .sng files and keeps filenames legal.
    public static class SongPath
    {
        // The Clone Hero data folder, e.g. <Documents>\Clone Hero. Uses the system
        // Documents path, so it follows any Windows known-folder redirection.
        public static string DataDir()
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(docs, "Clone Hero");
        }

        // Resolution order: explicit override -> settings.ini [directories] path0 -> default Songs folder.
        public static string Resolve(string overridePath)
        {
            if (!string.IsNullOrEmpty(overridePath) && overridePath.Trim().Length > 0)
                return overridePath.Trim();

            string fromIni = FromSettingsIni();
            if (!string.IsNullOrEmpty(fromIni)) return fromIni;

            return Path.Combine(DataDir(), "Songs");
        }

        private static string FromSettingsIni()
        {
            try
            {
                string ini = Path.Combine(DataDir(), "settings.ini");
                if (!File.Exists(ini)) return null;

                bool inDirs = false;
                foreach (string raw in File.ReadAllLines(ini))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        inDirs = string.Equals(line, "[directories]", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (inDirs && line.StartsWith("path0", StringComparison.OrdinalIgnoreCase))
                    {
                        int eq = line.IndexOf('=');
                        if (eq >= 0)
                        {
                            string val = line.Substring(eq + 1).Trim();
                            if (val.Length > 0 && Directory.Exists(val)) return val;
                        }
                    }
                }
            }
            catch { /* fall back to default */ }
            return null;
        }

        // Replaces characters illegal in a Windows filename so File.WriteAllBytes won't throw.
        public static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                bool bad = false;
                for (int j = 0; j < invalid.Length; j++)
                    if (invalid[j] == c) { bad = true; break; }
                sb.Append(bad ? '_' : c);
            }
            string outp = sb.ToString().Trim();
            return outp.Length == 0 ? "_" : outp;
        }
    }
}


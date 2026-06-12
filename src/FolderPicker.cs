using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace EnchorCrowdRequests
{
    // Opens the native Windows "Browse For Folder" dialog. It runs on its own STA thread so
    // the game keeps rendering while the dialog is open; the result is picked up from the
    // main thread via TryGetResult(). Uses the OS shell (shell32) directly, which works from
    // the BepInEx .NET host without any extra runtime (unlike System.Windows.Forms).
    public static class FolderPicker
    {
        private static volatile bool _busy;
        private static volatile string _result;
        private static volatile bool _hasResult;

        public static bool IsOpen { get { return _busy; } }

        public static void Pick(string title, IntPtr owner)
        {
            if (_busy) return;
            _busy = true;
            var t = new Thread(() =>
            {
                string sel = null;
                try { sel = Show(title, owner); }
                catch (Exception ex) { try { Plugin.Logger.LogWarning("Folder picker failed: " + ex.Message); } catch { } }
                if (!string.IsNullOrEmpty(sel)) { _result = sel; _hasResult = true; }
                _busy = false;
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        // Returns true once (and only once) when the user picked a folder.
        public static bool TryGetResult(out string path)
        {
            if (_hasResult) { path = _result; _hasResult = false; return true; }
            path = null;
            return false;
        }

        // The window handle of the calling thread's active window (the game), used as the
        // dialog owner so it appears on top. Call on the main thread.
        [DllImport("user32.dll")] public static extern IntPtr GetActiveWindow();

        // ---- SHBrowseForFolder ----
        private const uint BIF_RETURNONLYFSDIRS = 0x0001;
        private const uint BIF_EDITBOX = 0x0010;
        private const uint BIF_NEWDIALOGSTYLE = 0x0040;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct BROWSEINFO
        {
            public IntPtr hwndOwner;
            public IntPtr pidlRoot;
            public IntPtr pszDisplayName;   // [out] buffer; we allocate one but don't use it
            public string lpszTitle;
            public uint ulFlags;
            public IntPtr lpfn;
            public IntPtr lParam;
            public int iImage;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHBrowseForFolder(ref BROWSEINFO lpbi);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder pszPath);

        [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr pv);
        [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr pvReserved);

        private static string Show(string title, IntPtr owner)
        {
            OleInitialize(IntPtr.Zero);   // BIF_NEWDIALOGSTYLE needs OLE on this STA thread

            IntPtr displayBuf = Marshal.AllocHGlobal(260 * 2);
            try
            {
                var bi = new BROWSEINFO
                {
                    hwndOwner = owner,
                    pidlRoot = IntPtr.Zero,
                    pszDisplayName = displayBuf,
                    lpszTitle = title,
                    ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE | BIF_EDITBOX
                };

                IntPtr pidl = SHBrowseForFolder(ref bi);
                if (pidl == IntPtr.Zero) return null;   // cancelled
                try
                {
                    var sb = new StringBuilder(260);
                    return SHGetPathFromIDList(pidl, sb) ? sb.ToString() : null;
                }
                finally { CoTaskMemFree(pidl); }
            }
            finally { Marshal.FreeHGlobal(displayBuf); }
        }
    }
}


using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace EnchorCrowdRequests
{
    // uGUI front end. M2/M3: search bar + scrollable results list with album-art thumbnails.
    public class UIController : MonoBehaviour
    {
        public UIController(IntPtr ptr) : base(ptr) { }

        private GameObject _root;
        private bool _open, _built;
        private TMP_FontAsset _font;

        private TMP_InputField _input;
        private RectTransform _listContent;   // rows go here
        private RectTransform _queueContent;  // queue rows go here
        private TextMeshProUGUI _status;
        private TextMeshProUGUI _folderText;
        private TextMeshProUGUI _queueTitle;
        private bool _queueDirty;
        private string _songsFolder;

        // filters + paging
        private int _instIdx, _diffIdx, _page = 1, _found;
        private string _lastQuery = "";
        private Button _instBtn, _diffBtn;
        private TextMeshProUGUI _pageText;
        private static readonly string[] InstLabels = { "Any instrument", "Guitar", "Bass", "Rhythm", "Keys", "Drums", "Vocals" };
        private static readonly string[] InstApi    = { null,             "guitar", "bass", "rhythm", "keys", "drums", "vocals" };
        private static readonly string[] DiffLabels = { "Any difficulty", "Easy", "Medium", "Hard", "Expert" };
        private static readonly string[] DiffApi    = { null,             "easy", "medium", "hard", "expert" };
        private const int PageSize = 25;

        // search result handoff (bg Task -> main thread)
        private volatile List<ChartInfo> _pendingResults;
        private volatile string _pendingStatus;
        private volatile bool _busy;
        private List<ChartInfo> _results = new List<ChartInfo>();

        // download worker (sequential, rate-limited) + rescan
        private readonly List<ChartInfo> _jobs = new List<ChartInfo>();
        private bool _workerRunning;
        private readonly HashSet<string> _downloaded = new HashSet<string>();
        private int _activeDownloads;
        private volatile bool _rescanPending;
        private DateTime _lastDownloadUtc;
        private readonly List<ChartInfo> _queue = new List<ChartInfo>();   // "to download later"
        private readonly Dictionary<string, Button> _rowDlBtn = new Dictionary<string, Button>();
        private readonly List<KeyValuePair<LayoutElement, TextMeshProUGUI>> _rowSizers = new List<KeyValuePair<LayoutElement, TextMeshProUGUI>>();
        private volatile bool _downloadedDirty;

        // per-row album art: rows waiting for a texture, and the cache/pending bytes
        private struct ArtRow { public string Md5; public RawImage Img; }
        private readonly List<ArtRow> _artRows = new List<ArtRow>();
        private readonly Dictionary<string, Texture2D> _artTex = new Dictionary<string, Texture2D>();
        private readonly Dictionary<string, byte[]> _artBytes = new Dictionary<string, byte[]>();
        private readonly HashSet<string> _artRequested = new HashSet<string>();
        private static readonly HttpClient Http = new HttpClient();

        private bool _wasOpen;

        public void Update()
        {
            if (Input.GetKeyDown(Plugin.ToggleKey)) Toggle();
            if (_open && Input.GetKeyDown(KeyCode.Escape)) { _open = false; if (_root != null) _root.SetActive(false); }

            // Suppress the game's keyboard while open so typing a search doesn't drive the menu.
            // KeyBlock intercepts UnityEngine.Input keyboard reads (covers keys like Space that the menu
            // reads directly); InputBlock disables the Rewired keyboard too. Our UI types via the OS event
            // queue and clicks via mouse Input, so neither path breaks it.
            KeyBlock.Active = _open;
            if (_open) InputBlock.Tick(true);
            else if (_wasOpen) InputBlock.Tick(false);
            _wasOpen = _open;

            ApplyPendingResults();
            ApplyPendingArt();
            if (_queueDirty) { _queueDirty = false; RefreshQueue(); }
            if (_downloadedDirty)
            {
                _downloadedDirty = false;
                foreach (var kv in _rowDlBtn)
                {
                    bool dn; lock (_downloaded) dn = _downloaded.Contains(kv.Key);
                    if (dn && kv.Value != null) { SetBtnText(kv.Value, "Downloaded"); kv.Value.interactable = false; }
                }
            }

            string ps = _pendingStatus;
            if (ps != null && _pendingResults == null) { _pendingStatus = null; SetStatus(ps); }

            // Debounced auto-rescan after downloads settle (~2s).
            if (_rescanPending && Interlocked.CompareExchange(ref _activeDownloads, 0, 0) == 0 &&
                (DateTime.UtcNow - _lastDownloadUtc).TotalSeconds >= 2.0)
            {
                _rescanPending = false;
                Rescan.Trigger(Plugin.Logger, Plugin.RescanType, Plugin.RescanMethod);
                SetStatus("Library rescanned - new song(s) should appear.");
            }

            // Native folder dialog result -> apply (on main thread).
            string picked;
            if (FolderPicker.TryGetResult(out picked))
            {
                Plugin.SetSongsFolder(picked);
                _songsFolder = picked;
                SetFolderText();
                SetStatus("Songs folder set to: " + picked);
            }
        }

        private void SetFolderText()
        {
            if (_folderText != null) _folderText.text = "Downloads to:  " + _songsFolder;
        }

        private void Toggle()
        {
            if (!_built) Build();
            if (_root == null) return;
            _open = !_open;
            _root.SetActive(_open);
        }

        // ---- build the UI -------------------------------------------------------

        private void Build()
        {
            _built = true;
            try
            {
                _font = FindFont();
                EnsureEventSystem();

                _root = new GameObject("EnchorCrowdRequests_Canvas");
                UnityEngine.Object.DontDestroyOnLoad(_root);
                var canvas = _root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 30000;
                var scaler = _root.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                _root.AddComponent<GraphicRaycaster>();

                // Full-screen blocker: dims the game + catches all clicks so they don't reach the menu behind.
                GameObject blocker = Img("Blocker", _root.transform, new Color(0f, 0f, 0f, 0.5f));
                var blrt = blocker.GetComponent<RectTransform>();
                blrt.anchorMin = new Vector2(0f, 0f); blrt.anchorMax = new Vector2(1f, 1f); blrt.offsetMin = new Vector2(0f, 0f); blrt.offsetMax = new Vector2(0f, 0f);

                // Soft drop shadow behind the panel (depth).
                GameObject shadow = Img("PanelShadow", _root.transform, new Color(0f, 0f, 0f, 0.5f));
                Center(shadow.GetComponent<RectTransform>(), 1180f + 70f, 760f + 70f);
                shadow.GetComponent<RectTransform>().anchoredPosition = new Vector2(0f, -14f);
                SetSprite(shadow, Skin.Shadow(28, 40), new Color(0f, 0f, 0f, 0.5f));

                GameObject panel = Img("Panel", _root.transform, new Color(0.10f, 0.12f, 0.18f, 1f));
                var prt = panel.GetComponent<RectTransform>();
                Center(prt, 1180f, 760f);
                SetSprite(panel, Skin.VGradient(760, 22, new Color(0.17f, 0.20f, 0.29f, 1f), new Color(0.07f, 0.09f, 0.14f, 1f), true, true), Color.white);

                // Title bar - banner baked with smooth rounded-top corners (matches the panel; no Mask,
                // which would give hard/aliased corners). Falls back to a blue gradient if it can't load.
                const float barH = 48f;
                GameObject bar = Img("TitleBar", panel.transform, new Color(0.13f, 0.40f, 0.75f, 1f));
                Top(bar.GetComponent<RectTransform>(), barH);
                var bannerTex = LoadBanner();
                Sprite bannerSprite = bannerTex != null ? Skin.BannerTop(bannerTex, 2360, (int)(barH * 2f), 44) : null;
                if (bannerSprite != null)
                {
                    var bimg = bar.GetComponent<Image>();
                    bimg.sprite = bannerSprite; bimg.type = Image.Type.Simple; bimg.color = Color.white;
                }
                else
                {
                    SetSprite(bar, Skin.VGradient(48, 22, new Color(0.22f, 0.52f, 0.88f, 1f), new Color(0.11f, 0.31f, 0.64f, 1f), true, false), Color.white);
                }
                // title text + a soft dark shadow under it so it stays readable over the banner
                var titleSh = Text(bar.transform, "Enchor  -  Crowd Requests", 24, TextAlignmentOptions.Left, new Color(0f, 0f, 0f, 0.55f));
                FillPad(titleSh.rectTransform, 22, -2, -16, -2);
                var title = Text(bar.transform, "Enchor  -  Crowd Requests", 24, TextAlignmentOptions.Left, new Color(1, 1, 1, 1));
                FillPad(title.rectTransform, 20, 0, -18, 0);
                title.enableVertexGradient = true;
                title.colorGradient = new VertexGradient(new Color(1f, 1f, 1f, 1f), new Color(1f, 1f, 1f, 1f), new Color(0.86f, 0.92f, 1f, 1f), new Color(0.86f, 0.92f, 1f, 1f));

                // Search row (just under the title bar)
                GameObject searchRow = NewUI("SearchRow", panel.transform);
                var srt = searchRow.GetComponent<RectTransform>();
                srt.anchorMin = new Vector2(0f, 1f); srt.anchorMax = new Vector2(1f, 1f); srt.pivot = new Vector2(0.5f, 1f);
                srt.sizeDelta = new Vector2(0f, 44f); srt.anchoredPosition = new Vector2(0f, -52f);

                _input = MakeInput(searchRow.transform, "Search for a song or artist...");
                var irt = _input.GetComponent<RectTransform>();
                irt.anchorMin = new Vector2(0f, 0f); irt.anchorMax = new Vector2(1f, 1f); irt.pivot = new Vector2(0f, 0.5f);
                irt.offsetMin = new Vector2(18f, 6f); irt.offsetMax = new Vector2(-406f, -6f);

                Button searchBtn = MakeButton(searchRow.transform, "Search", () => StartSearch(_input != null ? _input.text : "", 1));
                RightBtn(searchBtn, 110f, -18f);

                _instBtn = null;
                _instBtn = MakeButton(searchRow.transform, InstLabels[_instIdx], () => { _instIdx = (_instIdx + 1) % InstLabels.Length; SetBtnText(_instBtn, InstLabels[_instIdx]); StartSearch(_lastQuery, 1); });
                RightBtn(_instBtn, 130f, -270f);

                _diffBtn = null;
                _diffBtn = MakeButton(searchRow.transform, DiffLabels[_diffIdx], () => { _diffIdx = (_diffIdx + 1) % DiffLabels.Length; SetBtnText(_diffBtn, DiffLabels[_diffIdx]); StartSearch(_lastQuery, 1); });
                RightBtn(_diffBtn, 130f, -134f);

                // Folder row (current download folder + change/default)
                GameObject folderRow = NewUI("FolderRow", panel.transform);
                var frt = folderRow.GetComponent<RectTransform>();
                frt.anchorMin = new Vector2(0f, 1f); frt.anchorMax = new Vector2(1f, 1f); frt.pivot = new Vector2(0.5f, 1f);
                frt.sizeDelta = new Vector2(0f, 28f); frt.anchoredPosition = new Vector2(0f, -100f);

                _songsFolder = SongPath.Resolve(Plugin.SongsOverride);
                _folderText = Text(folderRow.transform, "Downloads to:  " + _songsFolder, 16, TextAlignmentOptions.Left, new Color(0.75f, 0.82f, 0.95f, 1f));
                var ftrt = _folderText.rectTransform;
                ftrt.anchorMin = new Vector2(0f, 0f); ftrt.anchorMax = new Vector2(1f, 1f); ftrt.offsetMin = new Vector2(18f, 0f); ftrt.offsetMax = new Vector2(-280f, 0f);

                Button changeBtn = MakeButton(folderRow.transform, "Change Folder...", () => FolderPicker.Pick("Select your Clone Hero Songs folder", FolderPicker.GetActiveWindow()));
                var cbrt = changeBtn.GetComponent<RectTransform>();
                cbrt.anchorMin = new Vector2(1f, 0.5f); cbrt.anchorMax = new Vector2(1f, 0.5f); cbrt.pivot = new Vector2(1f, 0.5f);
                cbrt.sizeDelta = new Vector2(160f, 26f); cbrt.anchoredPosition = new Vector2(-100f, 0f);

                Button defBtn = MakeButton(folderRow.transform, "Default", () => { Plugin.SetSongsFolder(""); _songsFolder = SongPath.Resolve(""); SetFolderText(); });
                var dbrt = defBtn.GetComponent<RectTransform>();
                dbrt.anchorMin = new Vector2(1f, 0.5f); dbrt.anchorMax = new Vector2(1f, 0.5f); dbrt.pivot = new Vector2(1f, 0.5f);
                dbrt.sizeDelta = new Vector2(90f, 26f); dbrt.anchoredPosition = new Vector2(-18f, 0f);

                // Results (left ~67%) + Queue column (right ~33%)
                _listContent = BuildScroll(panel.transform, "Results",
                    new Vector2(0f, 0f), new Vector2(0.67f, 1f), new Vector2(18f, 44f), new Vector2(-6f, -134f));

                GameObject qCol = NewUI("QueueCol", panel.transform);
                var qcrt = qCol.GetComponent<RectTransform>();
                qcrt.anchorMin = new Vector2(0.67f, 0f); qcrt.anchorMax = new Vector2(1f, 1f);
                qcrt.offsetMin = new Vector2(6f, 44f); qcrt.offsetMax = new Vector2(-18f, -134f);

                _queueTitle = Text(qCol.transform, "Queue (0)", 18, TextAlignmentOptions.Left, new Color(1, 1, 1, 1));
                var qtt = _queueTitle.rectTransform;
                qtt.anchorMin = new Vector2(0f, 1f); qtt.anchorMax = new Vector2(1f, 1f); qtt.offsetMin = new Vector2(2f, -24f); qtt.offsetMax = new Vector2(0f, 0f);

                Button dlAll = MakeButton(qCol.transform, "Download All", () => { foreach (var c in new List<ChartInfo>(_queue)) Enqueue(c); _queue.Clear(); _queueDirty = true; });
                var dart = dlAll.GetComponent<RectTransform>();
                dart.anchorMin = new Vector2(0f, 1f); dart.anchorMax = new Vector2(0.62f, 1f); dart.offsetMin = new Vector2(0f, -60f); dart.offsetMax = new Vector2(-3f, -30f);

                Button clearBtn = MakeButton(qCol.transform, "Clear", () => { _queue.Clear(); _queueDirty = true; });
                var clrt = clearBtn.GetComponent<RectTransform>();
                clrt.anchorMin = new Vector2(0.62f, 1f); clrt.anchorMax = new Vector2(1f, 1f); clrt.offsetMin = new Vector2(3f, -60f); clrt.offsetMax = new Vector2(0f, -30f);

                _queueContent = BuildScroll(qCol.transform, "QueueScroll",
                    new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0f, 0f), new Vector2(0f, -66f));

                // Footer: pager + status + close
                GameObject footer = NewUI("Footer", panel.transform);
                var fort = footer.GetComponent<RectTransform>();
                fort.anchorMin = new Vector2(0f, 0f); fort.anchorMax = new Vector2(1f, 0f); fort.pivot = new Vector2(0.5f, 0f);
                fort.sizeDelta = new Vector2(0f, 36f); fort.anchoredPosition = new Vector2(0f, 4f);

                Button prev = MakeButton(footer.transform, "< Prev", () => { if (_page > 1) StartSearch(_lastQuery, _page - 1); });
                LeftBtn(prev, 80f, 18f);
                _pageText = Text(footer.transform, "Page 1 / 1", 16, TextAlignmentOptions.Center, new Color(0.85f, 0.9f, 1f, 1f));
                var ptr = _pageText.rectTransform; ptr.anchorMin = new Vector2(0f, 0.5f); ptr.anchorMax = new Vector2(0f, 0.5f); ptr.pivot = new Vector2(0f, 0.5f); ptr.sizeDelta = new Vector2(110f, 28f); ptr.anchoredPosition = new Vector2(104f, 0f);
                Button next = MakeButton(footer.transform, "Next >", () => StartSearch(_lastQuery, _page + 1));
                LeftBtn(next, 80f, 220f);

                Button close = MakeButton(footer.transform, "Close", () => { _open = false; if (_root != null) _root.SetActive(false); });
                RightBtn2(close, 90f, -18f);

                _status = Text(footer.transform, "Type a search and press Enter.", 17, TextAlignmentOptions.Left, new Color(0.8f, 0.85f, 0.95f, 1f));
                var strt = _status.rectTransform; strt.anchorMin = new Vector2(0f, 0f); strt.anchorMax = new Vector2(1f, 1f); strt.offsetMin = new Vector2(312f, 0f); strt.offsetMax = new Vector2(-120f, 0f);

                _root.SetActive(false);
                Plugin.Logger.LogInfo("EnchorCrowdRequests: built (font=" + (_font != null ? _font.name : "default") + ").");
            }
            catch (Exception ex) { Plugin.Logger.LogError("EnchorCrowdRequests build failed: " + ex); }
        }

        private RectTransform BuildScroll(Transform parent, string name, Vector2 aMin, Vector2 aMax, Vector2 offMin, Vector2 offMax)
        {
            GameObject scrollGo = Img(name, parent, new Color(0.05f, 0.06f, 0.10f, 0.55f));
            SetSprite(scrollGo, Skin.Rounded(12), new Color(0.05f, 0.06f, 0.10f, 0.55f));
            var scrt = scrollGo.GetComponent<RectTransform>();
            scrt.anchorMin = aMin; scrt.anchorMax = aMax; scrt.offsetMin = offMin; scrt.offsetMax = offMax;

            var scroll = scrollGo.AddComponent<ScrollRect>();
            scroll.horizontal = false; scroll.vertical = true; scroll.scrollSensitivity = 28f;

            GameObject viewport = Img("Viewport", scrollGo.transform, new Color(0, 0, 0, 0.001f));
            var vrt = viewport.GetComponent<RectTransform>();
            vrt.anchorMin = new Vector2(0, 0); vrt.anchorMax = new Vector2(1, 1); vrt.offsetMin = new Vector2(0, 0); vrt.offsetMax = new Vector2(0, 0);
            viewport.AddComponent<RectMask2D>();
            scroll.viewport = vrt;

            GameObject content = NewUI("Content", viewport.transform);
            var crt = content.GetComponent<RectTransform>();
            crt.anchorMin = new Vector2(0, 1); crt.anchorMax = new Vector2(1, 1); crt.pivot = new Vector2(0.5f, 1f);
            crt.anchoredPosition = new Vector2(0, 0); crt.sizeDelta = new Vector2(0, 0);
            var vlg = content.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 6f; vlg.childControlWidth = true; vlg.childControlHeight = false;
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            var pad = new RectOffset(); pad.left = 6; pad.right = 6; pad.top = 6; pad.bottom = 6;
            vlg.padding = pad;
            var fitter = content.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = crt;
            return crt;
        }

        // Rebuild the queue list rows + count (when _queueDirty).
        private void RefreshQueue()
        {
            if (_queueContent == null) return;
            for (int i = _queueContent.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_queueContent.GetChild(i).gameObject);
            if (_queueTitle != null) _queueTitle.text = "Queue (" + _queue.Count + ")";

            foreach (ChartInfo c in _queue)
            {
                ChartInfo cap = c;
                GameObject row = Img("QRow", _queueContent, new Color(0.20f, 0.23f, 0.32f, 0.92f));
                SetSprite(row, Skin.Rounded(10), new Color(0.20f, 0.23f, 0.32f, 0.92f));
                var le = row.AddComponent<LayoutElement>(); le.preferredHeight = 40f; le.minHeight = 40f;
                var t = Text(row.transform, "<size=90%>" + Esc(c.Artist) + " - " + Esc(c.Name) + "</size>", 16, TextAlignmentOptions.Left, new Color(1, 1, 1, 1));
                var trt = t.rectTransform; trt.anchorMin = new Vector2(0f, 0f); trt.anchorMax = new Vector2(1f, 1f); trt.offsetMin = new Vector2(8f, 0f); trt.offsetMax = new Vector2(-40f, 0f);
                Button x = MakeButton(row.transform, "X", () => { _queue.Remove(cap); _queueDirty = true; });
                var xrt = x.GetComponent<RectTransform>(); xrt.anchorMin = new Vector2(1f, 0.5f); xrt.anchorMax = new Vector2(1f, 0.5f); xrt.pivot = new Vector2(1f, 0.5f); xrt.sizeDelta = new Vector2(30f, 28f); xrt.anchoredPosition = new Vector2(-5f, 0f);
            }
        }

        // ---- search -------------------------------------------------------------

        private void StartSearch(string query, int page)
        {
            if (_busy) return;
            string q = (query ?? "").Trim();
            if (q.Length == 0) { SetStatus("Enter a search term first."); return; }
            if (page < 1) page = 1;
            _lastQuery = q; _busy = true; SetStatus("Searching...");
            string inst = InstApi[_instIdx]; string diff = DiffApi[_diffIdx]; int p = page;
            Task.Run(async () =>
            {
                SearchOutcome r = await EncoreApi.SearchAsync(q, inst, diff, p, PageSize).ConfigureAwait(false);
                if (!r.Success) { _pendingStatus = r.Error; _pendingResults = new List<ChartInfo>(); }
                else { _pendingResults = r.Results; _found = r.Found; _page = p; _pendingStatus = r.Found == 0 ? "No results." : ("Found " + r.Found + " chart(s)."); }
                _busy = false;
            });
        }

        private void ApplyPendingResults()
        {
            List<ChartInfo> results = _pendingResults;
            if (results == null) return;
            _pendingResults = null;
            _results = new List<ChartInfo>(results);
            if (_pendingStatus != null) { SetStatus(_pendingStatus); _pendingStatus = null; }
            if (_listContent == null) return;

            // clear old rows
            for (int i = _listContent.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_listContent.GetChild(i).gameObject);
            _artRows.Clear();
            _rowDlBtn.Clear();
            _rowSizers.Clear();

            foreach (ChartInfo c in results) BuildRow(c);
            ResizeRows();

            int pages = Math.Max(1, (_found + PageSize - 1) / PageSize);
            if (_pageText != null) _pageText.text = "Page " + _page + " / " + pages;
        }

        // Size each row to its text's real height. Done after the rows exist so a forced layout pass
        // gives every text rect its true (wrapped) width first - then preferredHeight is accurate,
        // which fixes multi-line issue text spilling out the bottom of the row.
        private void ResizeRows()
        {
            try
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_listContent);
                foreach (var kv in _rowSizers)
                {
                    var t = kv.Value;
                    // Use the larger of TMP's reported preferredHeight and a fresh measure at the text's
                    // real (now-known) width minus a hair - belt-and-suspenders so a long, multi-line
                    // issue line can't under-measure and bleed out the bottom.
                    float w = t.rectTransform.rect.width;
                    float measured = w > 1f ? t.GetPreferredValues(t.text, w - 4f, 0f).y : t.preferredHeight;
                    float textH = Mathf.Max(t.preferredHeight, measured);
                    float h = Mathf.Max(86f, textH + 26f);   // art floor + generous padding (slightly bigger rows)
                    kv.Key.preferredHeight = h; kv.Key.minHeight = h;
                }
                LayoutRebuilder.ForceRebuildLayoutImmediate(_listContent);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("EnchorCrowdRequests: ResizeRows failed: " + ex.Message); }
        }

        private void BuildRow(ChartInfo c)
        {
            GameObject row = Img("Row", _listContent, new Color(0.20f, 0.23f, 0.32f, 0.92f));
            SetSprite(row, Skin.Rounded(12), new Color(0.20f, 0.23f, 0.32f, 0.92f));
            var le = row.AddComponent<LayoutElement>();

            // album art (left), rounded via a mask so it matches the rest of the UI
            GameObject artBg = Img("ArtMask", row.transform, Color.white);
            var art = artBg.GetComponent<RectTransform>();
            art.anchorMin = new Vector2(0f, 0.5f); art.anchorMax = new Vector2(0f, 0.5f); art.pivot = new Vector2(0f, 0.5f);
            art.sizeDelta = new Vector2(72f, 72f); art.anchoredPosition = new Vector2(10f, 0f);
            SetSprite(artBg, Skin.Rounded(10), Color.white);
            var mask = artBg.AddComponent<Mask>(); mask.showMaskGraphic = false;
            GameObject artGo = NewUI("Art", artBg.transform);
            var raw = artGo.AddComponent<RawImage>();
            raw.color = new Color(0.25f, 0.25f, 0.3f, 1f);
            Fill(artGo.GetComponent<RectTransform>());
            RequestArt(c.AlbumArtMd5, raw);

            // text (middle) - one detail per line, nothing truncated
            string diffs = c.Parts.Count > 0 ? string.Join("    ", c.Parts) : "(no parts listed)";
            string text = "<b>" + Esc(c.Name) + "</b>\n"
                        + "<size=92%><color=#c8d2e0>" + Esc(c.Artist) + "</color></size>\n"
                        + "<size=84%><color=#9aa6b8>Charter: " + Esc(c.Charter) + "      Length: " + c.LengthText + "</color></size>\n"
                        + "<size=84%><color=#8fd0ff>" + Esc(diffs) + "</color></size>"
                        + (c.HasIssues ? "\n<size=84%><color=#ffb060>/!\\ ISSUES (" + c.IssueCount + "): " + Esc(c.IssueText) + "</color></size>" : "");
            var t = Text(row.transform, text, 15, TextAlignmentOptions.TopLeft, new Color(1, 1, 1, 1));
            t.enableWordWrapping = true; t.overflowMode = TextOverflowModes.Overflow;
            var trt = t.rectTransform;
            trt.anchorMin = new Vector2(0f, 0f); trt.anchorMax = new Vector2(1f, 1f);
            trt.offsetMin = new Vector2(92f, 6f); trt.offsetMax = new Vector2(-226f, -6f);

            // Provisional height; ResizeRows() sets the real height from the text's actual rendered
            // height once layout has given the text rect its true width (see ApplyPendingResults).
            le.preferredHeight = 96f; le.minHeight = 96f;
            _rowSizers.Add(new KeyValuePair<LayoutElement, TextMeshProUGUI>(le, t));

            // buttons (right)
            bool already; lock (_downloaded) already = _downloaded.Contains(c.Md5);
            Button dl = MakeButton(row.transform, already ? "Downloaded" : "Download", () => Enqueue(c));
            if (already) dl.interactable = false;
            _rowDlBtn[c.Md5] = dl;
            var dlrt = dl.GetComponent<RectTransform>();
            dlrt.anchorMin = new Vector2(1f, 0.5f); dlrt.anchorMax = new Vector2(1f, 0.5f); dlrt.pivot = new Vector2(1f, 0.5f);
            dlrt.sizeDelta = new Vector2(110f, 30f); dlrt.anchoredPosition = new Vector2(-118f, 0f);

            Button q = MakeButton(row.transform, "+ Queue", () => AddToQueue(c));
            var qrt = q.GetComponent<RectTransform>();
            qrt.anchorMin = new Vector2(1f, 0.5f); qrt.anchorMax = new Vector2(1f, 0.5f); qrt.pivot = new Vector2(1f, 0.5f);
            qrt.sizeDelta = new Vector2(100f, 30f); qrt.anchoredPosition = new Vector2(-8f, 0f);
        }

        // ---- download worker + queue --------------------------------------------

        private void AddToQueue(ChartInfo c)
        {
            lock (_downloaded) { if (_downloaded.Contains(c.Md5)) return; }
            foreach (ChartInfo q in _queue) if (q.Md5 == c.Md5) { SetStatus("Already queued: " + c.Name); return; }
            _queue.Add(c); _queueDirty = true;
            SetStatus("Queued: " + c.Name + "  (" + _queue.Count + " in queue)");
        }

        private void Enqueue(ChartInfo c)
        {
            bool start = false;
            lock (_jobs)
            {
                foreach (ChartInfo j in _jobs) if (j.Md5 == c.Md5) return;
                _jobs.Add(c);
                if (!_workerRunning) { _workerRunning = true; start = true; }
            }
            _pendingStatus = "Downloading " + c.Name + "...";
            if (start) Task.Run(() => Worker());
        }

        private async Task Worker()
        {
            try
            {
                while (true)
                {
                    ChartInfo job;
                    lock (_jobs)
                    {
                        if (_jobs.Count == 0) { _workerRunning = false; return; }
                        job = _jobs[0]; _jobs.RemoveAt(0);
                    }

                    Interlocked.Increment(ref _activeDownloads);
                    _pendingStatus = "Downloading: " + job.Name + " ...";
                    Tuple<bool, string> res;
                    try
                    {
                        string folder = SongPath.Resolve(Plugin.SongsOverride);
                        string dest = Path.Combine(folder, SongPath.Sanitize(job.Artist + " - " + job.Name + " (" + job.Charter + ")"));
                        res = await EncoreApi.DownloadAsync(job, dest).ConfigureAwait(false);
                    }
                    catch (Exception ex) { res = Tuple.Create(false, ex.Message); }
                    Interlocked.Decrement(ref _activeDownloads);

                    if (res.Item1)
                    {
                        lock (_downloaded) _downloaded.Add(job.Md5);
                        _downloadedDirty = true;
                        Plugin.Logger.LogInfo("Installed " + res.Item2);
                        _pendingStatus = "Installed: " + job.Name;
                        if (Plugin.AutoRescan) { _lastDownloadUtc = DateTime.UtcNow; _rescanPending = true; }
                    }
                    else { _pendingStatus = "Download failed: " + res.Item2; }

                    bool more; lock (_jobs) more = _jobs.Count > 0;
                    if (more) await Task.Delay(Plugin.DownloadDelayMs).ConfigureAwait(false);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogError("Download worker error: " + ex.Message); lock (_jobs) _workerRunning = false; }
        }

        // ---- album art ----------------------------------------------------------

        private void RequestArt(string md5, RawImage img)
        {
            if (string.IsNullOrEmpty(md5) || img == null) return;
            _artRows.Add(new ArtRow { Md5 = md5, Img = img });
            lock (_artRequested) { if (!_artRequested.Add(md5)) return; }
            _ = FetchArt(md5);
        }

        private async Task FetchArt(string md5)
        {
            try
            {
                byte[] b = await Http.GetByteArrayAsync("https://files.enchor.us/" + md5 + ".jpg").ConfigureAwait(false);
                lock (_artBytes) _artBytes[md5] = b;
            }
            catch { }
        }

        private void ApplyPendingArt()
        {
            // create textures from any downloaded bytes (main thread)
            if (_artBytes.Count > 0)
            {
                List<KeyValuePair<string, byte[]>> ready = null;
                lock (_artBytes) { ready = new List<KeyValuePair<string, byte[]>>(_artBytes); _artBytes.Clear(); }
                foreach (var kv in ready)
                {
                    try { var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false); ImageConversion.LoadImage(tex, kv.Value); _artTex[kv.Key] = tex; }
                    catch { }
                }
            }
            // assign textures to rows that don't have them yet
            for (int i = _artRows.Count - 1; i >= 0; i--)
            {
                ArtRow r = _artRows[i];
                Texture2D tex;
                if (r.Img == null) { _artRows.RemoveAt(i); continue; }
                if (_artTex.TryGetValue(r.Md5, out tex) && tex != null)
                {
                    r.Img.texture = tex;
                    r.Img.color = new Color(1, 1, 1, 1);
                    _artRows.RemoveAt(i);
                }
            }
        }

        // ---- uGUI helpers -------------------------------------------------------

        private void SetStatus(string s) { if (_status != null) _status.text = s; }

        private static GameObject NewUI(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            return go;
        }

        private static GameObject Img(string name, Transform parent, Color color)
        {
            var go = NewUI(name, parent);
            var img = go.AddComponent<Image>();
            img.color = color;
            return go;
        }

        // Apply a generated sprite (rounded / gradient / shadow) to an Image, keeping the given tint.
        // If sprite generation failed (null) the element keeps its flat color, so nothing breaks.
        private static void SetSprite(GameObject go, Sprite sp, Color color)
        {
            var img = go.GetComponent<Image>();
            if (img == null) return;
            img.color = color;
            if (sp != null) { img.sprite = sp; img.type = Image.Type.Sliced; img.pixelsPerUnitMultiplier = 1f; }
        }

        private TextMeshProUGUI Text(Transform parent, string text, float size, TextAlignmentOptions align, Color color)
        {
            var go = NewUI("Text", parent);
            var t = go.AddComponent<TextMeshProUGUI>();
            if (_font != null) t.font = _font;
            t.text = text; t.fontSize = size; t.color = color; t.alignment = align; t.richText = true;
            t.enableWordWrapping = false; t.overflowMode = TextOverflowModes.Ellipsis;
            return t;
        }

        private Button MakeButton(Transform parent, string label, Action onClick)
        {
            GameObject go = Img("Button", parent, new Color(0.24f, 0.28f, 0.40f, 1f));
            var img = go.GetComponent<Image>();
            SetSprite(go, Skin.VGradient(34, 9, new Color(0.32f, 0.38f, 0.54f, 1f), new Color(0.19f, 0.23f, 0.34f, 1f), true, true), Color.white);
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            // Color-tint transitions give the buttons interactive depth (brighten on hover, sink on press).
            var cb = btn.colors;
            cb.normalColor = new Color(0.90f, 0.92f, 1f, 1f);
            cb.highlightedColor = Color.white;
            cb.pressedColor = new Color(0.68f, 0.72f, 0.82f, 1f);
            cb.selectedColor = cb.normalColor;
            cb.disabledColor = new Color(0.5f, 0.5f, 0.55f, 1f);
            cb.fadeDuration = 0.08f;
            btn.colors = cb;
            try { btn.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>((Action)(() => { try { onClick(); } catch (Exception ex) { Plugin.Logger.LogError("click: " + ex.Message); } }))); }
            catch (Exception ex) { Plugin.Logger.LogWarning("button wire failed: " + ex.Message); }
            var t = Text(go.transform, label, 17, TextAlignmentOptions.Center, new Color(1, 1, 1, 1));
            FillPad(t.rectTransform, 4, 0, -4, 0);
            return btn;
        }

        private TMP_InputField MakeInput(Transform parent, string placeholder)
        {
            GameObject go = Img("Input", parent, new Color(0.09f, 0.11f, 0.16f, 1f));
            SetSprite(go, Skin.Rounded(8), new Color(0.09f, 0.11f, 0.16f, 1f));
            var input = go.AddComponent<TMP_InputField>();

            GameObject area = NewUI("TextArea", go.transform);
            FillPad(area.GetComponent<RectTransform>(), 10, 4, -10, -4);
            area.AddComponent<RectMask2D>();

            var txt = Text(area.transform, "", 18, TextAlignmentOptions.Left, new Color(1, 1, 1, 1));
            Fill(txt.rectTransform);
            var ph = Text(area.transform, placeholder, 18, TextAlignmentOptions.Left, new Color(0.6f, 0.6f, 0.7f, 1f));
            Fill(ph.rectTransform);

            input.textViewport = area.GetComponent<RectTransform>();
            input.textComponent = txt;
            input.placeholder = ph;
            input.onSubmit.AddListener(DelegateSupport.ConvertDelegate<UnityAction<string>>((Action<string>)(s => StartSearch(s, 1))));
            return input;
        }

        // Button placement helpers
        private static void RightBtn(Button b, float w, float x)   // search-row, stretch-y, right-anchored
        {
            var rt = b.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 0f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(1f, 0.5f);
            rt.sizeDelta = new Vector2(w, -12f); rt.anchoredPosition = new Vector2(x, 0f);
        }
        private static void LeftBtn(Button b, float w, float x)    // footer, center-y, left-anchored
        {
            var rt = b.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0.5f); rt.anchorMax = new Vector2(0f, 0.5f); rt.pivot = new Vector2(0f, 0.5f);
            rt.sizeDelta = new Vector2(w, 28f); rt.anchoredPosition = new Vector2(x, 0f);
        }
        private static void RightBtn2(Button b, float w, float x)  // footer, center-y, right-anchored
        {
            var rt = b.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 0.5f); rt.anchorMax = new Vector2(1f, 0.5f); rt.pivot = new Vector2(1f, 0.5f);
            rt.sizeDelta = new Vector2(w, 28f); rt.anchoredPosition = new Vector2(x, 0f);
        }
        private static void SetBtnText(Button b, string text)
        {
            if (b == null) return;
            var t = b.GetComponentInChildren<TextMeshProUGUI>();
            if (t != null) t.text = text;
        }

        // RectTransform helpers
        private static void Center(RectTransform rt, float w, float h)
        {
            rt.anchorMin = new Vector2(0.5f, 0.5f); rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(w, h); rt.anchoredPosition = new Vector2(0, 0);
        }
        private static void Top(RectTransform rt, float h)
        {
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, h); rt.anchoredPosition = new Vector2(0f, 0f);
        }
        private static void Fill(RectTransform rt)
        {
            rt.anchorMin = new Vector2(0, 0); rt.anchorMax = new Vector2(1, 1); rt.offsetMin = new Vector2(0, 0); rt.offsetMax = new Vector2(0, 0);
        }
        private static void FillPad(RectTransform rt, float l, float b, float r, float t)
        {
            rt.anchorMin = new Vector2(0, 0); rt.anchorMax = new Vector2(1, 1); rt.offsetMin = new Vector2(l, b); rt.offsetMax = new Vector2(r, t);
        }

        private static void EnsureEventSystem()
        {
            try
            {
                var existing = Resources.FindObjectsOfTypeAll<EventSystem>();
                if (existing != null && existing.Length > 0) return;
                var go = new GameObject("EnchorCrowdRequests_EventSystem");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<EventSystem>();
                go.AddComponent<StandaloneInputModule>();
            }
            catch { }
        }

        private static TMP_FontAsset FindFont()
        {
            try { var f = Resources.FindObjectsOfTypeAll<TMP_FontAsset>(); if (f != null) foreach (var x in f) if (x != null) return x; }
            catch { }
            return null;
        }

        private static Texture2D _bannerTex;
        private static bool _bannerTried;
        private static Texture2D LoadBanner()
        {
            if (_bannerTried) return _bannerTex;
            _bannerTried = true;
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                string resName = null;
                foreach (var n in asm.GetManifestResourceNames())
                    if (n.EndsWith("banner.png", StringComparison.OrdinalIgnoreCase)) { resName = n; break; }
                if (resName == null) { Plugin.Logger.LogWarning("Enchor: banner resource not embedded"); return null; }
                using (var s = asm.GetManifestResourceStream(resName))
                {
                    if (s == null) return null;
                    var bytes = new byte[s.Length];
                    int off = 0, rd;
                    while (off < bytes.Length && (rd = s.Read(bytes, off, bytes.Length - off)) > 0) off += rd;
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    tex.wrapMode = TextureWrapMode.Clamp; tex.filterMode = FilterMode.Bilinear;
                    ImageConversion.LoadImage(tex, bytes);
                    _bannerTex = tex;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("Enchor: banner load failed: " + ex.Message); }
            return _bannerTex;
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            // Insert a zero-width space after '<' / before '>' so TMP doesn't parse them as rich-text tags.
            return s.Replace("<", "<​").Replace(">", "​>");
        }
    }
}

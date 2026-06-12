using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace EnchorCrowdRequests
{
    // A single search result: exactly what the UI shows and what we need to download.
    public class ChartInfo
    {
        public string Name;
        public string Artist;
        public string Charter;
        public string Md5;
        public long LengthMs;
        public readonly List<string> Parts = new List<string>(); // e.g. "Guitar 6", "Bass 5"

        public string AlbumArtMd5;
        public int IssueCount;
        public string IssueText;
        public bool HasIssues { get { return IssueCount > 0; } }

        public string DownloadUrl { get { return "https://files.enchor.us/" + Md5 + ".sng"; } }
        public string ArtUrl { get { return string.IsNullOrEmpty(AlbumArtMd5) ? null : "https://files.enchor.us/" + AlbumArtMd5 + ".jpg"; } }

        public string LengthText
        {
            get
            {
                if (LengthMs <= 0) return "";
                int total = (int)(LengthMs / 1000);
                return total / 60 + ":" + (total % 60).ToString("00");
            }
        }
    }

    public class SearchOutcome
    {
        public bool Success;
        public List<ChartInfo> Results = new List<ChartInfo>();
        public int Found;
        public string Error;

        public static SearchOutcome Ok(List<ChartInfo> results, int found)
        {
            return new SearchOutcome { Success = true, Results = results, Found = found };
        }
        public static SearchOutcome Fail(string error)
        {
            return new SearchOutcome { Success = false, Error = error };
        }
    }

    public static class EncoreApi
    {
        private const string SearchUrl = "https://api.enchor.us/search";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
            c.DefaultRequestHeaders.Add("User-Agent", "EncoreBrowser/1.0 (Clone Hero plugin)");
            c.DefaultRequestHeaders.Add("Accept", "application/json");
            return c;
        }

        // diff_* field -> display label, in the order the site lists them.
        // Value is an intensity tier 0-6; -1 means the chart has no such part.
        private static readonly KeyValuePair<string, string>[] DiffFields =
        {
            new KeyValuePair<string, string>("diff_guitar",      "Guitar"),
            new KeyValuePair<string, string>("diff_guitar_coop", "Co-op"),
            new KeyValuePair<string, string>("diff_rhythm",      "Rhythm"),
            new KeyValuePair<string, string>("diff_bass",        "Bass"),
            new KeyValuePair<string, string>("diff_drums",       "Drums"),
            new KeyValuePair<string, string>("diff_keys",        "Keys"),
            new KeyValuePair<string, string>("diff_guitarghl",   "GHL Guitar"),
            new KeyValuePair<string, string>("diff_bassghl",     "GHL Bass"),
            new KeyValuePair<string, string>("diff_vocals",      "Vocals"),
        };

        public static async Task<SearchOutcome> SearchAsync(string query, string instrument,
            string difficulty, int page, int perPage)
        {
            try
            {
                string body = BuildBody(query, instrument, difficulty, page, perPage);
                using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
                using (var resp = await Http.PostAsync(SearchUrl, content).ConfigureAwait(false))
                {
                    string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        return SearchOutcome.Fail("HTTP " + (int)resp.StatusCode + " from enchor.us");

                    var results = new List<ChartInfo>();
                    JNode root = JNode.Parse(text);
                    int found = root["found"].AsInt;
                    JNode data = root["data"];
                    for (int k = 0; k < data.Count; k++)
                        results.Add(ParseChart(data[k]));
                    return SearchOutcome.Ok(results, found);
                }
            }
            catch (Exception ex)
            {
                return SearchOutcome.Fail("Search error: " + ex.Message);
            }
        }

        // Downloads the chart and EXTRACTS the .sng container into destFolder (a song
        // folder - the most version-compatible format). Falls back to writing the raw
        // .sng if the container can't be parsed. Returns (success, message).
        public static async Task<Tuple<bool, string>> DownloadAsync(ChartInfo chart, string destFolder)
        {
            try
            {
                byte[] data = await Http.GetByteArrayAsync(chart.DownloadUrl).ConfigureAwait(false);
                try
                {
                    SngExtractor.ExtractToFolder(data, destFolder);
                    return Tuple.Create(true, destFolder);
                }
                catch (Exception exParse)
                {
                    string sng = destFolder + ".sng";
                    File.WriteAllBytes(sng, data);
                    return Tuple.Create(true, sng + "  (kept as .sng: " + exParse.Message + ")");
                }
            }
            catch (Exception ex)
            {
                return Tuple.Create(false, ex.Message);
            }
        }

        private static ChartInfo ParseChart(JNode n)
        {
            var c = new ChartInfo
            {
                Name = n["name"].AsString,
                Artist = n["artist"].AsString,
                Charter = n["charter"].AsString,
                Md5 = n["md5"].AsString,
                LengthMs = n["song_length"].AsLong,
            };

            foreach (var f in DiffFields)
            {
                JNode d = n[f.Key];
                if (d.IsNull) continue;
                int tier = d.AsInt;
                if (tier >= 0) c.Parts.Add(f.Value + " " + tier);
            }

            c.AlbumArtMd5 = n["albumArtMd5"].AsString;
            JNode chartIssues = n["notesData"]["chartIssues"];
            JNode folderIssues = n["folderIssues"];
            JNode metaIssues = n["metadataIssues"];
            c.IssueCount = chartIssues.Count + folderIssues.Count + metaIssues.Count;
            if (chartIssues.Count > 0) c.IssueText = chartIssues[0]["description"].AsString;
            else if (folderIssues.Count > 0) c.IssueText = folderIssues[0]["description"].AsString;
            else if (metaIssues.Count > 0) c.IssueText = metaIssues[0]["description"].AsString;

            return c;
        }

        private static string BuildBody(string query, string instrument, string difficulty, int page, int perPage)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"search\":\"").Append(Escape(query)).Append("\",");
            sb.Append("\"page\":").Append(page).Append(',');
            sb.Append("\"per_page\":").Append(perPage).Append(',');
            sb.Append("\"instrument\":").Append(instrument == null ? "null" : "\"" + Escape(instrument) + "\"").Append(',');
            sb.Append("\"difficulty\":").Append(difficulty == null ? "null" : "\"" + Escape(difficulty) + "\"").Append(',');
            sb.Append("\"drumType\":null,");
            sb.Append("\"sort\":null");
            sb.Append('}');
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}


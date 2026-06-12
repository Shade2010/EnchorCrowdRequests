using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace EnchorCrowdRequests
{
    // Minimal, dependency-free JSON reader. Uses a null-object pattern: any missing
    // key/index returns JNull, so callers can chain (root["data"][0]["name"].AsString)
    // without null checks. Read-only; just enough to consume the enchor.us API.
    public abstract class JNode
    {
        public virtual JNode this[string key] { get { return JNull.Instance; } }
        public virtual JNode this[int index] { get { return JNull.Instance; } }
        public virtual int Count { get { return 0; } }
        public virtual string AsString { get { return ""; } }
        public virtual bool IsNull { get { return false; } }
        protected virtual string Raw { get { return AsString; } }

        public int AsInt
        {
            get
            {
                long v;
                return long.TryParse(Raw, NumberStyles.Any, CultureInfo.InvariantCulture, out v) ? (int)v : 0;
            }
        }

        public long AsLong
        {
            get
            {
                long v;
                return long.TryParse(Raw, NumberStyles.Any, CultureInfo.InvariantCulture, out v) ? v : 0;
            }
        }

        public bool AsBool { get { return Raw == "true"; } }

        public static JNode Parse(string json)
        {
            int i = 0;
            return JParser.ParseValue(json ?? "", ref i);
        }
    }

    public sealed class JObject : JNode
    {
        public readonly Dictionary<string, JNode> Dict = new Dictionary<string, JNode>();
        public override JNode this[string key]
        {
            get { JNode n; return Dict.TryGetValue(key, out n) ? n : JNull.Instance; }
        }
        public override int Count { get { return Dict.Count; } }
    }

    public sealed class JArray : JNode
    {
        public readonly List<JNode> List = new List<JNode>();
        public override JNode this[int index]
        {
            get { return (index >= 0 && index < List.Count) ? List[index] : JNull.Instance; }
        }
        public override int Count { get { return List.Count; } }
    }

    public sealed class JValue : JNode
    {
        private readonly string _v;
        public JValue(string v) { _v = v ?? ""; }
        public override string AsString { get { return _v; } }
        protected override string Raw { get { return _v; } }
    }

    public sealed class JNull : JNode
    {
        public static readonly JNull Instance = new JNull();
        public override bool IsNull { get { return true; } }
    }

    internal static class JParser
    {
        public static JNode ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return JNull.Instance;
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return new JValue(ParseString(s, ref i));
                case 't': i += 4; return new JValue("true");
                case 'f': i += 5; return new JValue("false");
                case 'n': i += 4; return JNull.Instance;
                default: return ParseNumber(s, ref i);
            }
        }

        private static JNode ParseObject(string s, ref int i)
        {
            var o = new JObject();
            i++; // consume '{'
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return o; }
            while (i < s.Length)
            {
                SkipWs(s, ref i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                o.Dict[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; }
                break;
            }
            return o;
        }

        private static JNode ParseArray(string s, ref int i)
        {
            var a = new JArray();
            i++; // consume '['
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return a; }
            while (i < s.Length)
            {
                a.List.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; }
                break;
            }
            return a;
        }

        private static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            if (i < s.Length && s[i] == '"') i++; // opening quote
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\' && i < s.Length)
                {
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 <= s.Length)
                            {
                                int code;
                                if (int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                    sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static JNode ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E' || (c >= '0' && c <= '9')) i++;
                else break;
            }
            return new JValue(s.Substring(start, i - start));
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }
    }
}


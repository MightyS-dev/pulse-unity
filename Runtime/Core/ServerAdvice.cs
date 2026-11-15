using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mightys.Pulse
{
    // Scheduling fields the server may return with a response.
    internal sealed class ServerAdvice
    {
        internal double? RecommendedSendIntervalSeconds;
        internal bool? Pause;
        internal string Error;
        internal static ServerAdvice Parse(string body)
        {
            var advice = new ServerAdvice();
            try
            {
                if (string.IsNullOrWhiteSpace(body) || body.Length > 65536) return advice;
                var root = SmallJson.Parse(body) as Dictionary<string, object>;
                if (root == null) return advice;
                if (root.TryGetValue("recommendedSendIntervalSeconds", out var interval) && interval is double n &&
                    n > 0 && !double.IsNaN(n) && !double.IsInfinity(n))
                    advice.RecommendedSendIntervalSeconds = Math.Max(5, Math.Min(3600, n));
                if (root.TryGetValue("pause", out var pause) && pause is bool flag)
                    advice.Pause = flag;
                if (root.TryGetValue("error", out var error) && error is string code)
                    advice.Error = code;
            }
            catch (FormatException) { }
            return advice;
        }
    }

    // A bounded strict JSON reader for response advice. It does not use reflection or dynamic code.
    internal sealed class SmallJson
    {
        private readonly string text;
        private int position;
        private SmallJson(string text) { this.text = text; }
        internal static object Parse(string text)
        {
            var parser = new SmallJson(text);
            var result = parser.Value(0);
            parser.Space();
            if (parser.position != text.Length) throw new FormatException();
            return result;
        }
        private void Space()
        {
            while (position < text.Length && (text[position] == ' ' || text[position] == '\r' ||
                text[position] == '\n' || text[position] == '\t')) position++;
        }
        private bool Take(char c)
        { Space(); if (position < text.Length && text[position] == c) { position++; return true; } return false; }
        private void Need(char c) { if (!Take(c)) throw new FormatException(); }
        private object Value(int depth)
        {
            if (depth > 32) throw new FormatException();
            Space();
            if (position >= text.Length) throw new FormatException();
            var c = text[position];
            if (c == '"') return String();
            if (c == '{')
            {
                position++;
                var map = new Dictionary<string, object>(StringComparer.Ordinal);
                if (Take('}')) return map;
                do
                {
                    Space(); var key = String(); Need(':');
                    if (map.ContainsKey(key)) throw new FormatException();
                    map.Add(key, Value(depth + 1));
                    if (Take('}')) return map;
                } while (Take(','));
                throw new FormatException();
            }
            if (c == '[')
            {
                position++; var array = new List<object>();
                if (Take(']')) return array;
                do { array.Add(Value(depth + 1)); if (Take(']')) return array; } while (Take(','));
                throw new FormatException();
            }
            foreach (var literal in new[] { "true", "false", "null" })
                if (position + literal.Length <= text.Length &&
                    string.CompareOrdinal(text, position, literal, 0, literal.Length) == 0)
                { position += literal.Length; return literal == "null" ? null : (object)(literal == "true"); }
            var start = position;
            if (text[position] == '-') position++;
            if (position >= text.Length) throw new FormatException();
            if (text[position] == '0') position++;
            else { if (text[position] < '1' || text[position] > '9') throw new FormatException(); Digits(); }
            if (position < text.Length && text[position] == '.') { position++; RequireDigits(); }
            if (position < text.Length && (text[position] == 'e' || text[position] == 'E'))
            {
                position++;
                if (position < text.Length && (text[position] == '+' || text[position] == '-')) position++;
                RequireDigits();
            }
            // Syntax was validated above. Preserve an unrepresentable number as a nonfinite
            // value so that one invalid hint does not discard another valid field or unknown key.
            return double.TryParse(text.Substring(start, position - start), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var number) ? number : double.NaN;
        }
        private void Digits() { while (position < text.Length && text[position] >= '0' && text[position] <= '9') position++; }
        private void RequireDigits() { var start = position; Digits(); if (position == start) throw new FormatException(); }
        private string String()
        {
            if (position >= text.Length || text[position++] != '"') throw new FormatException();
            var b = new StringBuilder();
            while (position < text.Length)
            {
                var c = text[position++];
                if (c == '"') return b.ToString();
                if (c < 32) throw new FormatException();
                if (c != '\\') { b.Append(c); continue; }
                if (position >= text.Length) throw new FormatException();
                c = text[position++];
                switch (c)
                {
                    case '"': case '\\': case '/': b.Append(c); break;
                    case 'b': b.Append('\b'); break;
                    case 'f': b.Append('\f'); break;
                    case 'n': b.Append('\n'); break;
                    case 'r': b.Append('\r'); break;
                    case 't': b.Append('\t'); break;
                    case 'u':
                        if (position + 4 > text.Length || !ushort.TryParse(text.Substring(position, 4),
                            NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)) throw new FormatException();
                        b.Append((char)code); position += 4; break;
                    default: throw new FormatException();
                }
            }
            throw new FormatException();
        }
    }
}

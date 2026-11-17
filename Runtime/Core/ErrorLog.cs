using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Mightys.Pulse
{
    // Builds an ErrorEvent from the two strings of a log callback: the condition text and a stack trace in the usual game engine format.
    internal static class ErrorLog
    {
        internal const string FallbackType = "LogError";
        private const int MaxConditionChars = 4000, MaxTraceChars = 65536, MaxLineChars = 2000;
        private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);
        private const RegexOptions Options = RegexOptions.CultureInvariant;
        private static Regex Make(string pattern) => new Regex(pattern, Options, Timeout);

        private static readonly Regex TypeName = Make(@"^[A-Za-z_][A-Za-z0-9_.]*$");
        private static readonly Regex AtMarker = Make(@"\s*\(at (?<file>.*?)(?::(?<line>\d+))?\)\s*$");
        private static readonly Regex InMarker = Make(@"\s*\[0x[0-9A-Fa-f]+\]\s+in (?<file>.*?)(?::(?<line>\d+))?\s*$");
        private static readonly Regex Offset = Make(@"\s*\[0x[0-9A-Fa-f]+\]\s*$");
        private static readonly Regex Wrapper = Make(@"^\(wrapper [^)]*\)\s*");
        private static readonly Regex Generic = Make(@"`\d+(\[[^\]]*\]+)?");
        private static readonly Regex NameGeneric = Make(@"<[^<>]*>$");

        internal static ErrorEvent FromLog(string condition, string stackTrace, string kind, IReadOnlyList<string> skipLeadingFramePrefixes = null)
        {
            var report = new ErrorEvent { Kind = kind, Type = FallbackType };
            var text = condition ?? "";
            if (text.Length > MaxConditionChars) text = text.Substring(0, MaxConditionChars);
            var type = ParseType(text, out var rest);
            report.Type = type;
            report.Message = ErrorReport.FilterMessage(rest);
            report.Frames = ParseFrames(stackTrace, skipLeadingFramePrefixes);
            report.Symbolicated = report.Frames.Count > 0;
            return report;
        }

        // The text before the first ": " is the type when it looks like a type name (the namespace is dropped, as for caught exceptions).
        private static string ParseType(string condition, out string message)
        {
            message = condition;
            var cut = condition.IndexOf(": ", StringComparison.Ordinal);
            var head = cut >= 0 ? condition.Substring(0, cut) : condition;
            var named = TypeName.IsMatch(head) && (cut >= 0 || head.EndsWith("Exception", StringComparison.Ordinal));
            if (!named) return FallbackType;
            var name = head.Substring(head.LastIndexOf('.') + 1);
            if (name.Length == 0) return FallbackType;
            message = cut >= 0 ? condition.Substring(cut + 2) : "";
            return name.Length > ErrorReport.MaxTypeChars ? name.Substring(0, ErrorReport.MaxTypeChars) : name;
        }

        private static List<string> ParseFrames(string stackTrace, IReadOnlyList<string> skipPrefixes)
        {
            var frames = new List<string>();
            if (string.IsNullOrEmpty(stackTrace)) return frames;
            if (stackTrace.Length > MaxTraceChars) stackTrace = stackTrace.Substring(0, MaxTraceChars);
            var leading = true;
            foreach (var raw in stackTrace.Split('\n'))
            {
                if (frames.Count >= ErrorReport.MaxFrames) break;
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.Length > MaxLineChars) line = line.Substring(0, MaxLineChars);
                var frame = ParseFrame(line, out var owner);
                if (frame == null) continue;
                // The logger's own frames at the top say nothing about the app; the platform names them by type prefix.
                if (leading && IsSkipped(owner, skipPrefixes)) continue;
                leading = false;
                frame = ErrorFilter.Apply(frame, true);
                frames.Add(frame.Length > ErrorReport.MaxFrameChars ? ErrorReport.CutSurrogate(frame, ErrorReport.MaxFrameChars) : frame);
            }
            return frames;
        }

        private static bool IsSkipped(string owner, IReadOnlyList<string> prefixes)
        {
            if (prefixes == null) return false;
            for (var i = 0; i < prefixes.Count; i++)
                if (!string.IsNullOrEmpty(prefixes[i]) && owner.StartsWith(prefixes[i], StringComparison.Ordinal)) return true;
            return false;
        }

        // "Ns.Type.Method() File.cs:42"; null when the line has no method name.
        private static string ParseFrame(string line, out string owner)
        {
            owner = "";
            string file = null, number = null;
            var head = line;
            var match = AtMarker.Match(head);
            if (!match.Success) match = InMarker.Match(head);
            if (match.Success)
            {
                file = match.Groups["file"].Value; number = match.Groups["line"].Value;
                head = head.Substring(0, match.Index);
            }
            head = Offset.Replace(Wrapper.Replace(head.Trim(), ""), "");
            var paren = head.IndexOf('(');
            if (paren < 0) return null;
            var qualified = head.Substring(0, paren).Trim();
            if (qualified.Length == 0) return null;
            string name;
            var colon = qualified.IndexOf(':');
            if (colon > 0 && colon < qualified.Length - 1) { owner = qualified.Substring(0, colon); name = qualified.Substring(colon + 1); }
            else
            {
                var dot = qualified.LastIndexOf('.');
                if (dot > 0 && qualified[dot - 1] == '.') dot--;
                if (dot <= 0 || dot >= qualified.Length - 1) return null;
                owner = qualified.Substring(0, dot); name = qualified.Substring(dot + 1);
            }
            name = Tidy(ref owner, name);
            if (name.Length == 0) return null;
            var text = new StringBuilder();
            if (owner.Length > 0) text.Append(owner).Append('.');
            text.Append(name).Append("()");
            AppendFile(text, file, number);
            return text.ToString();
        }

        // Nested types read as dotted names; compiler generated state machines and closures show the method the developer wrote.
        private static string Tidy(ref string owner, string name)
        {
            var parts = new List<string>(Generic.Replace(owner, "").Split('+'));
            while (parts.Count > 0)
            {
                var last = parts[parts.Count - 1];
                if (last.Length == 0 || last[0] != '<') break;
                var close = last.IndexOf('>');
                if (close > 1 && (last.IndexOf("d__", StringComparison.Ordinal) > close || name == "MoveNext")) name = last.Substring(1, close - 1);
                parts.RemoveAt(parts.Count - 1);
            }
            owner = string.Join(".", parts).Trim('.');
            name = name.Trim();
            if (name.Length > 0 && name[0] == '<')
            {
                var close = name.IndexOf('>');
                if (close > 1) name = name.Substring(1, close - 1);
            }
            else name = NameGeneric.Replace(name, "");
            return name;
        }

        private static void AppendFile(StringBuilder text, string file, string number)
        {
            if (string.IsNullOrEmpty(file)) return;
            var cut = file.LastIndexOfAny(new[] { '/', '\\' });
            if (cut >= 0) file = file.Substring(cut + 1);
            file = file.Trim();
            // Builds without file info print a hash such as <8f2c...> in its place.
            if (file.Length == 0 || file[0] == '<') return;
            text.Append(' ').Append(file);
            if (int.TryParse(number, System.Globalization.NumberStyles.None, SignalJson.Invariant, out var line) && line > 0)
                text.Append(':').Append(line.ToString(SignalJson.Invariant));
        }
    }
}

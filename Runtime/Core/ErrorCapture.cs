using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Mightys.Pulse
{
    internal static class ErrorKinds
    {
        internal const string Crash = "crash", Error = "error", Handled = "handled";
        internal const string SignalType = "pulse.error";
    }

    // One captured error, already filtered. Serialization and the size limit are in ErrorJson.
    internal sealed class ErrorEvent
    {
        internal string Kind, Type, Message;
        internal List<string> Frames = new List<string>();
        internal bool Symbolicated;
    }

    // Removes personal data from error text. Same rules and order in every Pulse SDK.
    internal static class ErrorFilter
    {
        internal const string Mask = "<filtered>";
        private const string Hex = "[0-9A-Fa-f]{1,4}";
        private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);
        private const RegexOptions Options = RegexOptions.CultureInvariant;
        private static Regex Make(string pattern) => new Regex(pattern, Options, Timeout);

        private static readonly Regex Email = Make(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}");
        // Scheme, host and path stay; the query string and the fragment go.
        private static readonly Regex UrlTail = Make(@"([A-Za-z][A-Za-z0-9+.-]*://[^\s?#]*)[?#]\S*");
        private static readonly Regex Uuid = Make(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        private static readonly Regex Ipv4 = Make(@"\b\d{1,3}(\.\d{1,3}){3}\b");
        private static readonly Regex Ipv6 = Make(
            @"(?<![0-9A-Za-z_:])(?:" +
            "(?:" + Hex + ":){7}" + Hex + "|" +
            "(?:" + Hex + ":){1,7}:|" +
            "(?:" + Hex + ":){1,6}:" + Hex + "|" +
            "(?:" + Hex + ":){1,5}(?::" + Hex + "){1,2}|" +
            "(?:" + Hex + ":){1,4}(?::" + Hex + "){1,3}|" +
            "(?:" + Hex + ":){1,3}(?::" + Hex + "){1,4}|" +
            "(?:" + Hex + ":){1,2}(?::" + Hex + "){1,5}|" +
            Hex + ":(?::" + Hex + "){1,6}|" +
            ":(?::" + Hex + "){1,7})(?![0-9A-Za-z_:])");
        private static readonly Regex Digits = Make(@"\b0[xX][0-9a-fA-F]+|\d{6,}");
        // A line and optional column right after a file name in a frame.
        private static readonly Regex Position = Make(@"(?<=\.[A-Za-z0-9]{1,5}:)\d+(?::\d+)?");
        private static readonly Regex FileUrl = Make(@"file://[^\s?#]*/([^\s/?#]*)");
        private static readonly Regex WindowsPath = Make(@"(?<![A-Za-z0-9])[A-Za-z]:[\\/](?:[^\s\\/:*?""<>|]+[\\/])*([^\s\\/:*?""<>|]*)");
        private static readonly Regex UnixPath = Make(@"(?<![\w.:/\\\-])/(?:[^\s/:""'<>|()]+/)*([^\s/:""'<>|()]+)");

        // frame: true keeps a line and column after a file name; a message has no such exemption.
        internal static string Apply(string text, bool frame = false)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            try
            {
                text = Email.Replace(text, Mask);
                text = UrlTail.Replace(text, "$1" + Mask);
                text = Uuid.Replace(text, Mask);
                text = Ipv4.Replace(text, Mask);
                text = Ipv6.Replace(text, Mask);
                var kept = new List<Match>();
                if (frame) foreach (Match k in Position.Matches(text)) kept.Add(k);
                text = Digits.Replace(text, m => m.Value.StartsWith("0x") || m.Value.StartsWith("0X") || kept.Exists(k => k.Index < m.Index + m.Length && m.Index < k.Index + k.Length) ? m.Value : Mask);
                text = FileUrl.Replace(text, "$1");
                text = WindowsPath.Replace(text, "$1");
                text = UnixPath.Replace(text, "$1");
                return text;
            }
            catch (Exception) { return Mask; }
        }
    }

    // Builds an ErrorEvent from an exception and writes it as the wire object.
    internal static class ErrorReport
    {
        internal const int MaxFrames = 30, MaxFrameChars = 300, MaxMessageChars = 500, MaxTypeChars = 200, MaxErrorBytes = 16384;
        private const int MaxInputChars = 4000;
        private static readonly Regex Generic = new Regex(@"`\d+(\[\[.*?\]\])?", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

        internal static ErrorEvent FromException(Exception exception, string kind)
        {
            var leaf = Unwrap(exception);
            var report = new ErrorEvent { Kind = kind, Type = TypeName(leaf), Message = Message(leaf) };
            var frames = Frames(leaf);
            report.Frames = frames;
            report.Symbolicated = frames.Count > 0;
            return report;
        }

        private static Exception Unwrap(Exception exception)
        {
            for (var i = 0; i < 8; i++)
            {
                var aggregate = exception as AggregateException;
                if (aggregate == null || aggregate.InnerExceptions.Count == 0) break;
                exception = aggregate.InnerExceptions[0];
            }
            return exception;
        }

        private static string TypeName(Exception exception)
        {
            var name = "";
            try { name = exception.GetType().Name; } catch (Exception) { }
            name = Generic.Replace(name, "");
            if (string.IsNullOrWhiteSpace(name)) name = "Exception";
            return name.Length > MaxTypeChars ? name.Substring(0, MaxTypeChars) : name;
        }

        internal static ErrorEvent FromLog(string condition, string stackTrace, string kind, IReadOnlyList<string> skipLeadingFramePrefixes = null)
        {
            try { return ErrorLog.FromLog(condition, stackTrace, kind, skipLeadingFramePrefixes); }
            catch (Exception) { return new ErrorEvent { Kind = kind, Type = ErrorLog.FallbackType, Message = "" }; }
        }

        private static string Message(Exception exception)
        {
            string text;
            try { text = exception.Message ?? ""; } catch (Exception) { text = ""; }
            return FilterMessage(text);
        }

        internal static string FilterMessage(string text)
        {
            if (text == null) text = "";
            if (text.Length > MaxInputChars) text = text.Substring(0, MaxInputChars);
            text = ErrorFilter.Apply(text);
            return text.Length > MaxMessageChars ? CutSurrogate(text, MaxMessageChars) : text;
        }

        private static List<string> Frames(Exception exception)
        {
            var result = new List<string>();
            try
            {
                var trace = new StackTrace(exception, true);
                var frames = trace.GetFrames();
                if (frames == null) return result;
                foreach (var frame in frames)
                {
                    if (result.Count >= MaxFrames) break;
                    var line = FormatFrame(frame);
                    if (line == null) continue;
                    line = ErrorFilter.Apply(line, true);
                    result.Add(line.Length > MaxFrameChars ? CutSurrogate(line, MaxFrameChars) : line);
                }
            }
            catch (Exception) { }
            return result;
        }

        // type.method() file.cs:line. File name without directory; line only when known. Null when the method is unknown.
        internal static string FormatFrame(StackFrame frame)
        {
            var method = frame?.GetMethod();
            if (method == null) return null;
            string name = method.Name, owner = null;
            var type = method.DeclaringType;
            // Compiler generated state machines and closures are shown as the method and type the developer wrote.
            while (type != null && type.Name.Length > 0 && type.Name[0] == '<')
            {
                var close = type.Name.IndexOf('>');
                if (close > 1 && (type.Name.IndexOf("d__", StringComparison.Ordinal) > close || name == "MoveNext"))
                    name = type.Name.Substring(1, close - 1);
                type = type.DeclaringType;
            }
            if (name.Length > 0 && name[0] == '<')
            {
                var close = name.IndexOf('>');
                if (close > 1) name = name.Substring(1, close - 1);
            }
            if (type != null) owner = Generic.Replace(type.FullName ?? type.Name, "").Replace('+', '.');
            var text = new StringBuilder();
            if (!string.IsNullOrEmpty(owner)) text.Append(owner).Append('.');
            text.Append(name).Append("()");
            var file = frame.GetFileName();
            if (!string.IsNullOrEmpty(file))
            {
                var cut = file.LastIndexOfAny(new[] { '/', '\\' });
                if (cut >= 0) file = file.Substring(cut + 1);
                if (file.Length > 0)
                {
                    text.Append(' ').Append(file);
                    var number = frame.GetFileLineNumber();
                    if (number > 0) text.Append(':').Append(number.ToString(SignalJson.Invariant));
                }
            }
            return text.ToString();
        }

        internal static string CutSurrogate(string text, int length)
        {
            if (text.Length <= length) return text;
            if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
            return text.Substring(0, length);
        }
    }

    internal static class ErrorJson
    {
        // The error object is at most 16384 bytes: frames are cut from the bottom first, then the message.
        internal static string Serialize(ErrorEvent error, PulseErrorDetail detail)
        {
            if (detail == PulseErrorDetail.TypeOnly)
                return Build(error, false, 0, null);
            var frames = error.Frames.Count;
            var message = error.Message;
            var json = Build(error, true, frames, message);
            while (Size(json) > ErrorReport.MaxErrorBytes && frames > 0) json = Build(error, true, --frames, message);
            while (Size(json) > ErrorReport.MaxErrorBytes && !string.IsNullOrEmpty(message))
            {
                message = message.Length <= 1 ? "" : ErrorReport.CutSurrogate(message, message.Length / 2);
                json = Build(error, true, frames, message);
            }
            return json;
        }

        private static int Size(string json) => Encoding.UTF8.GetByteCount(json);

        private static string Build(ErrorEvent error, bool full, int frameCount, string message)
        {
            var b = new StringBuilder("{\"kind\":").Append(SignalJson.Quote(error.Kind));
            b.Append(",\"type\":").Append(SignalJson.Quote(error.Type));
            if (full)
            {
                if (!string.IsNullOrEmpty(message)) b.Append(",\"message\":").Append(SignalJson.Quote(message));
                b.Append(",\"frames\":[");
                for (var i = 0; i < frameCount; i++)
                { if (i > 0) b.Append(','); b.Append(SignalJson.Quote(error.Frames[i])); }
                b.Append("],\"symbolicated\":").Append(error.Symbolicated ? "true" : "false");
            }
            return b.Append('}').ToString();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Mightys.Pulse
{
    internal sealed class SignalInput
    {
        internal DateTimeOffset Timestamp;
        internal string Type, PropsJson, SessionId, UserHash;
        internal double? Value;
        // True when no user id was set at capture, so the anonymous install id hash applies.
        internal bool Anonymous;
        // Set for pulse.error signals; written as the top-level error object.
        internal ErrorEvent Error;
        // Set for pulse.heatmap signals; the ready made top-level heatmap object.
        internal string HeatmapJson;
    }

    internal static class SignalJson
    {
        internal const int MaxBodyBytes = 1048576;
        internal const int MaxPropsBytes = 8192;
        internal static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        internal static string Hash(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                var result = new StringBuilder(64);
                foreach (var b in bytes) result.Append(b.ToString("x2", Invariant));
                return result.ToString();
            }
        }

        internal static SignalInput Create(string type, double? value, IDictionary<string, object> props,
            DateTimeOffset now, string session, string userHash, Action<string> warn)
        {
            if (string.IsNullOrWhiteSpace(type) || type.Length > 200 ||
                (value.HasValue && (double.IsNaN(value.Value) || double.IsInfinity(value.Value))))
            { warn("Invalid signal type or value; signal dropped."); return null; }
            // Caller order is kept: the server keeps the first entries in input order when it applies its key caps.
            // A key repeated after Unicode cleanup keeps its first position and its last value.
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            var order = new List<string>();
            var propsSize = 2;
            if (props != null)
                foreach (var pair in props)
                {
                    if ((pair.Key != null && pair.Key.Length > MaxPropsBytes) ||
                        (pair.Value is string raw && raw.Length > MaxPropsBytes))
                    { warn("Properties exceed 8192 bytes; signal dropped."); return null; }
                    var key = CleanUnicode(pair.Key);
                    var item = Prop(pair.Value);
                    if (key == null || item == null) { warn("Unsupported property dropped."); continue; }
                    var quotedKey = Quote(key);
                    var fieldSize = Encoding.UTF8.GetByteCount(quotedKey) + 1 + Encoding.UTF8.GetByteCount(item);
                    if (fields.TryGetValue(key, out var previous)) propsSize -= Encoding.UTF8.GetByteCount(previous);
                    else { propsSize += fieldSize + (fields.Count == 0 ? 0 : 1); }
                    if (previous != null) propsSize += Encoding.UTF8.GetByteCount(item);
                    if (propsSize > MaxPropsBytes)
                    { warn("Properties exceed 8192 bytes; signal dropped."); return null; }
                    if (previous == null) order.Add(key);
                    fields[key] = item;
                }
            var json = new StringBuilder("{");
            foreach (var key in order)
            {
                if (json.Length > 1) json.Append(',');
                json.Append(Quote(key)).Append(':').Append(fields[key]);
            }
            json.Append('}');
            if (Encoding.UTF8.GetByteCount(json.ToString()) > MaxPropsBytes)
            { warn("Properties exceed 8192 bytes; signal dropped."); return null; }
            return new SignalInput { Type = CleanUnicode(type), Value = value, PropsJson = json.ToString(),
                Timestamp = now.ToUniversalTime(), SessionId = session, UserHash = userHash };
        }

        private static string Prop(object value)
        {
            if (value is string text) return Quote(text);
            if (value is bool flag) return flag ? "\"true\"" : "\"false\"";
            if (value is double d) return double.IsNaN(d) || double.IsInfinity(d) ? null : d.ToString("R", Invariant);
            if (value is float f) return float.IsNaN(f) || float.IsInfinity(f) ? null : f.ToString("R", Invariant);
            if (value is decimal dec) return dec.ToString(Invariant);
            if (value is byte || value is sbyte || value is short || value is ushort || value is int ||
                value is uint || value is long || value is ulong) return ((IFormattable)value).ToString(null, Invariant);
            return null;
        }

        // Fixed fields first, then the platform's static extra fields. Keys the platform did not give are left out.
        internal static string SystemJson(IEnvironmentInfo env, PulseOptions options, HashSet<string> written = null, HashSet<string> hidden = null)
        {
            written = written ?? new HashSet<string>(StringComparer.Ordinal);
            var b = new StringBuilder("{");
            Field(b, "platform", env.Platform); Field(b, "osName", env.OsName); Field(b, "osVersion", env.OsVersion);
            Field(b, "appVersion", options.AppVersion ?? env.AppVersion); Field(b, "appBuildNumber", env.AppBuildNumber);
            Field(b, "deviceModel", env.DeviceModel); Field(b, "locale", env.Locale);
            Field(b, "sdkName", PulseInfo.SdkName); Field(b, "sdkVersion", PulseInfo.Version);
            if (!string.IsNullOrEmpty(env.BundleId)) Field(b, "bundleId", env.BundleId);
            if (options.SendAppIdentity) Field(b, "signingCertSha256", env.SigningCertSha256);
            foreach (var key in new[] { "platform", "osName", "osVersion", "appVersion", "appBuildNumber", "deviceModel", "locale",
                "sdkName", "sdkVersion", "bundleId", "signingCertSha256" }) written.Add(key);
            // The SDK's own usage fields are never taken from the platform.
            foreach (var key in SystemFields.UsageKeys) written.Add(key);
            foreach (var key in SystemFields.AutoKeys) written.Add(key);
            // Keys the portal settings switch off stay in the written set for good, so no later step adds them.
            if (hidden != null) foreach (var key in hidden) written.Add(key);
            SystemFields.Append(b, (env as IExtraSystemFields)?.StaticFields, written);
            foreach (var key in SystemFields.UsageKeys) if (hidden == null || !hidden.Contains(key)) written.Remove(key);
            foreach (var key in SystemFields.AutoKeys) if (hidden == null || !hidden.Contains(key)) written.Remove(key);
            return b.Append('}').ToString();
        }

        // Adds the fields that belong to one signal (dynamic platform fields, then usage statistics) to the base system object.
        // Statistic fields win over platform fields; keys already in the base are never repeated.
        internal static string SystemJsonFor(string baseSystem, HashSet<string> baseKeys,
            IEnumerable<KeyValuePair<string, string>> dynamicFields, IEnumerable<KeyValuePair<string, string>> usageFields)
        {
            var b = new StringBuilder(baseSystem, 0, baseSystem.Length - 1, baseSystem.Length + 512);
            var written = new HashSet<string>(baseKeys, StringComparer.Ordinal);
            if (usageFields != null)
            {
                var usage = new List<KeyValuePair<string, string>>(usageFields);
                SystemFields.Append(b, usage, written);
            }
            SystemFields.Append(b, dynamicFields, written);
            return b.Append('}').ToString();
        }

        private static void Field(StringBuilder b, string key, string value)
        {
            if (b.Length > 1) b.Append(',');
            value = value ?? "";
            if (value.Length > 1024) value = value.Substring(0, 1024);
            b.Append(Quote(key)).Append(':').Append(Quote(value));
        }

        // Reduces a platform OS description to its leading dotted version number, for example
        // "Android OS 14 / API-34 (UP1A.231005.007/10754064)" to "14" and "iOS 17.0.3" to "17.0.3". Text after the
        // first '/' or '(' (Android API level and firmware build id, Windows kernel build) is never considered.
        // Returns an empty string when no version number is found, so a raw description is never sent.
        // One format for every Pulse SDK: at most three parts, and the patch part is dropped when it is zero.
        private static string OneVersionFormat(string number)
        {
            var parts = number.Split('.');
            var count = parts.Length > 3 ? 3 : parts.Length;
            if (count == 3 && parts[2].TrimStart('0').Length == 0) count = 2;
            return string.Join(".", parts, 0, count);
        }

        internal static string OsVersionNumber(string description)
        {
            if (string.IsNullOrEmpty(description)) return "";
            var end = description.IndexOfAny(new[] { '/', '(' });
            if (end < 0) end = description.Length;
            for (var i = 0; i < end; i++)
            {
                if (!IsAsciiDigit(description[i]) || (i > 0 && char.IsLetterOrDigit(description[i - 1]))) continue;
                var j = i;
                while (j < end && (IsAsciiDigit(description[j]) ||
                    (description[j] == '.' && j + 1 < end && IsAsciiDigit(description[j + 1])))) j++;
                if (j < end && char.IsLetter(description[j])) { i = j; continue; }
                return j - i > 32 ? "" : OneVersionFormat(description.Substring(i, j - i));
            }
            return "";
        }

        private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

        internal static string Serialize(SignalInput signal, string system, bool debug, PulseErrorDetail detail = PulseErrorDetail.Full)
        {
            var b = new StringBuilder("{\"timestamp\":");
            b.Append(Quote(signal.Timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", Invariant)));
            b.Append(",\"sessionId\":").Append(Quote(signal.SessionId));
            if (!string.IsNullOrEmpty(signal.UserHash)) b.Append(",\"userHash\":").Append(Quote(signal.UserHash));
            b.Append(",\"type\":").Append(Quote(signal.Type)).Append(",\"value\":");
            b.Append(signal.Value.HasValue ? signal.Value.Value.ToString("R", Invariant) : "null");
            b.Append(",\"debug\":").Append(debug ? "true" : "false");
            b.Append(",\"system\":").Append(system).Append(",\"props\":").Append(signal.PropsJson);
            if (signal.Error != null) b.Append(",\"error\":").Append(ErrorJson.Serialize(signal.Error, detail));
            if (signal.HeatmapJson != null) b.Append(",\"heatmap\":").Append(signal.HeatmapJson);
            return b.Append('}').ToString();
        }

        internal static string Quote(string text)
        {
            text = CleanUnicode(text) ?? "";
            var b = new StringBuilder("\"");
            foreach (var c in text)
            {
                if (c == '"') b.Append("\\\"");
                else if (c == '\\') b.Append("\\\\");
                else if (c < 32) b.Append("\\u").Append(((int)c).ToString("x4", Invariant));
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }

        private static string CleanUnicode(string text)
        {
            if (text == null) return null;
            var b = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                { b.Append(c).Append(text[++i]); continue; }
                b.Append(char.IsSurrogate(c) ? '\ufffd' : c);
            }
            return b.ToString();
        }
    }
}

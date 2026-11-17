using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mightys.Pulse
{
    // Everything App Control keeps on the device, in one small file: the last good answer (body, ETag, time) and the local counters
    // (Update returns, shown notices, dismissals). It never holds an identity.
    internal sealed class ControlCacheData
    {
        internal const int MaxShown = 50;
        internal string Body, ETag;
        internal DateTimeOffset? OkAt;
        /// <summary>The app version at the last launch, and the version the app was just updated to.</summary>
        internal string Seen, UpdatedTo;
        internal string WhatsNewShown;
        internal string DismissedVersion;
        internal DateTimeOffset? DismissedAt;
        internal string ReturnsVersion;
        internal int Returns;
        /// <summary>The user pressed Update and has not come back yet.</summary>
        internal bool Pending;
        internal readonly List<string> Shown = new List<string>();
    }

    internal static class ControlCache
    {
        internal const int MaxFileBytes = 262144;
        private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        // A missing, unreadable or damaged file gives empty data. Never throws.
        internal static ControlCacheData Read(IStorage storage, string name)
        {
            var data = new ControlCacheData();
            try
            {
                var bytes = storage?.Read(name);
                if (bytes == null || bytes.Length == 0 || bytes.Length > MaxFileBytes) return data;
                var root = SmallJson.Parse(Encoding.UTF8.GetString(bytes)) as Dictionary<string, object>;
                if (root == null) return data;
                data.Body = Text(root, "body"); data.ETag = Text(root, "etag");
                data.OkAt = Time(root, "okAt"); data.Seen = Text(root, "seen"); data.UpdatedTo = Text(root, "updatedTo");
                data.WhatsNewShown = Text(root, "whatsNewShown"); data.DismissedVersion = Text(root, "dismissedVersion");
                data.DismissedAt = Time(root, "dismissedAt"); data.ReturnsVersion = Text(root, "returnsVersion");
                if (root.TryGetValue("returns", out var r) && r is double d && d >= 0 && d < 1000) data.Returns = (int)d;
                data.Pending = root.TryGetValue("pending", out var p) && p is bool flag && flag;
                if (root.TryGetValue("shown", out var s) && s is List<object> list)
                    foreach (var item in list)
                        if (item is string id && data.Shown.Count < ControlCacheData.MaxShown) data.Shown.Add(id);
                return data;
            }
            catch (Exception) { return new ControlCacheData(); }
        }

        internal static byte[] Serialize(ControlCacheData d)
        {
            var b = new StringBuilder("{\"v\":1");
            Field(b, "body", d.Body); Field(b, "etag", d.ETag);
            if (d.OkAt.HasValue) Field(b, "okAt", d.OkAt.Value.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture));
            Field(b, "seen", d.Seen); Field(b, "updatedTo", d.UpdatedTo); Field(b, "whatsNewShown", d.WhatsNewShown);
            Field(b, "dismissedVersion", d.DismissedVersion);
            if (d.DismissedAt.HasValue) Field(b, "dismissedAt", d.DismissedAt.Value.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture));
            Field(b, "returnsVersion", d.ReturnsVersion);
            b.Append(",\"returns\":").Append(d.Returns.ToString(CultureInfo.InvariantCulture));
            b.Append(",\"pending\":").Append(d.Pending ? "true" : "false");
            b.Append(",\"shown\":[");
            for (var i = 0; i < d.Shown.Count; i++) { if (i > 0) b.Append(','); b.Append(SignalJson.Quote(d.Shown[i])); }
            return Encoding.UTF8.GetBytes(b.Append("]}").ToString());
        }

        private static void Field(StringBuilder b, string key, string value)
        {
            if (value != null) b.Append(',').Append(SignalJson.Quote(key)).Append(':').Append(SignalJson.Quote(value));
        }

        private static string Text(Dictionary<string, object> map, string key) => map.TryGetValue(key, out var v) ? v as string : null;

        private static DateTimeOffset? Time(Dictionary<string, object> map, string key)
        {
            var text = Text(map, key);
            return text != null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : (DateTimeOffset?)null;
        }
    }
}

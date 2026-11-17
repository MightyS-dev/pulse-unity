using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mightys.Pulse
{
    internal sealed class ControlUpdateRule
    {
        internal string Min, Latest, Mode, StoreVersion, StoreUrl;
    }

    internal sealed class ControlBlockedEntry
    {
        internal string Version;
        internal List<string> Platforms; // null means every platform
        internal bool AllowContinue;
    }

    internal sealed class ControlMaintenanceRule
    {
        internal DateTimeOffset? StartsAt, EndsAt;
        internal int HeadsUpMinutes;
        internal bool ExemptAboveStore;
        internal List<string> Platforms; // null means every platform
    }

    internal sealed class ControlAnnouncementRule
    {
        internal string Id, Title, Body, Url, ButtonText;
        internal DateTimeOffset? Until;
        internal List<string> Platforms; // null means every platform
    }

    internal sealed class ControlWhatsNewRule
    {
        internal string Version, Body;
    }

    internal sealed class ControlCustomText
    {
        internal string Title, Body, ButtonText;
    }

    // The rules of App Control, format 1. Reading is strict about types: a known field of the wrong type, a format other than 1,
    // a body that is not a JSON object or a body over 65536 bytes make the whole body unusable (Parse returns null), and the caller
    // keeps what it had. Unknown fields are ignored and null is accepted wherever a field may be empty. Never throws.
    internal sealed class ControlRules
    {
        internal const int MaxBodyBytes = 65536;
        private static readonly string[] Platforms = { "ios", "android", "macos", "windows", "linux", "web" };

        internal int Version;
        internal bool Off;
        internal readonly Dictionary<string, ControlUpdateRule> Update = new Dictionary<string, ControlUpdateRule>(StringComparer.Ordinal);
        internal readonly List<ControlBlockedEntry> Blocked = new List<ControlBlockedEntry>();
        internal ControlMaintenanceRule Maintenance;
        internal ControlAnnouncementRule Announcement;
        internal ControlWhatsNewRule WhatsNew;
        internal readonly Dictionary<string, ControlCustomText> Custom = new Dictionary<string, ControlCustomText>(StringComparer.Ordinal);
        internal readonly List<string> DisabledSdkVersions = new List<string>();

        /// <summary>The value for If-None-Match, or null when the rules carry no version.</summary>
        internal string ETag => Version > 0 ? "\"" + Version.ToString(CultureInfo.InvariantCulture) + "\"" : null;

        internal static ControlRules Parse(string body)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(body) || Encoding.UTF8.GetByteCount(body) > MaxBodyBytes) return null;
                var root = SmallJson.Parse(body) as Dictionary<string, object>;
                if (root == null) return null;
                if (!(root.TryGetValue("format", out var format) && format is double f && f == 1)) return null;
                var rules = new ControlRules();
                rules.Version = Int(root, "version") ?? 0;
                rules.Off = Bool(root, "off");
                ReadUpdate(root, rules);
                ReadBlocked(root, rules);
                rules.Maintenance = ReadMaintenance(Map(root, "maintenance"));
                rules.Announcement = ReadAnnouncement(Map(root, "announcement"));
                rules.WhatsNew = ReadWhatsNew(Map(root, "whatsNew"));
                ReadTexts(root, rules);
                var disabled = Strings(root, "disabledSdkVersions");
                if (disabled != null) rules.DisabledSdkVersions.AddRange(disabled);
                return rules;
            }
            catch (Exception) { return null; }
        }

        private static void ReadUpdate(Dictionary<string, object> root, ControlRules rules)
        {
            var update = Map(root, "update");
            if (update == null) return;
            foreach (var platform in Platforms)
            {
                var entry = Map(update, platform);
                if (entry == null) continue;
                rules.Update[platform] = new ControlUpdateRule
                {
                    Min = Str(entry, "min"), Latest = Str(entry, "latest"), Mode = Str(entry, "mode"),
                    StoreVersion = Str(entry, "storeVersion"), StoreUrl = WebUrl(Str(entry, "storeUrl"))
                };
            }
        }

        private static void ReadBlocked(Dictionary<string, object> root, ControlRules rules)
        {
            if (!root.TryGetValue("blocked", out var value) || value == null) return;
            if (!(value is List<object> list)) throw new FormatException();
            foreach (var item in list)
            {
                if (!(item is Dictionary<string, object> map)) throw new FormatException();
                rules.Blocked.Add(new ControlBlockedEntry
                { Version = Str(map, "version"), Platforms = Strings(map, "platforms"), AllowContinue = Bool(map, "allowContinue") });
            }
        }

        private static ControlMaintenanceRule ReadMaintenance(Dictionary<string, object> map) => map == null ? null : new ControlMaintenanceRule
        {
            StartsAt = Time(map, "startsAt"), EndsAt = Time(map, "endsAt"), HeadsUpMinutes = Math.Max(0, Int(map, "headsUpMinutes") ?? 0),
            ExemptAboveStore = Bool(map, "exemptAboveStore"), Platforms = Strings(map, "platforms")
        };

        private static ControlAnnouncementRule ReadAnnouncement(Dictionary<string, object> map) => map == null ? null : new ControlAnnouncementRule
        {
            Id = Str(map, "id"), Title = Str(map, "title"), Body = Str(map, "body"), Url = WebUrl(Str(map, "url")),
            ButtonText = Str(map, "buttonText"), Until = Time(map, "until"), Platforms = Strings(map, "platforms")
        };

        private static ControlWhatsNewRule ReadWhatsNew(Dictionary<string, object> map) =>
            map == null ? null : new ControlWhatsNewRule { Version = Str(map, "version"), Body = Str(map, "body") };

        private static void ReadTexts(Dictionary<string, object> root, ControlRules rules)
        {
            var texts = Map(root, "texts");
            var custom = texts == null ? null : Map(texts, "custom");
            if (custom == null) return;
            foreach (var pair in custom)
            {
                if (pair.Value == null) continue;
                if (!(pair.Value is Dictionary<string, object> entry)) throw new FormatException();
                rules.Custom[pair.Key] = new ControlCustomText { Title = Str(entry, "title"), Body = Str(entry, "body"), ButtonText = Str(entry, "buttonText") };
            }
        }

        // Only absolute http and https addresses are ever offered to the app; anything else counts as no link.
        internal static string WebUrl(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http") && uri.UserInfo.Length == 0
                ? uri.AbsoluteUri : null;
        }

        // ---- typed readers: missing or null gives the default, a wrong type throws ----

        private static string Str(Dictionary<string, object> map, string key)
        {
            if (!map.TryGetValue(key, out var value) || value == null) return null;
            if (value is string text) return text;
            throw new FormatException();
        }

        private static bool Bool(Dictionary<string, object> map, string key)
        {
            if (!map.TryGetValue(key, out var value) || value == null) return false;
            if (value is bool flag) return flag;
            throw new FormatException();
        }

        private static int? Int(Dictionary<string, object> map, string key)
        {
            if (!map.TryGetValue(key, out var value) || value == null) return null;
            if (value is double d && !double.IsNaN(d) && !double.IsInfinity(d) && Math.Floor(d) == d && d >= int.MinValue && d <= int.MaxValue)
                return (int)d;
            throw new FormatException();
        }

        private static DateTimeOffset? Time(Dictionary<string, object> map, string key)
        {
            var text = Str(map, key);
            if (text == null) return null;
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                return at;
            throw new FormatException();
        }

        private static Dictionary<string, object> Map(Dictionary<string, object> map, string key)
        {
            if (!map.TryGetValue(key, out var value) || value == null) return null;
            if (value is Dictionary<string, object> inner) return inner;
            throw new FormatException();
        }

        private static List<string> Strings(Dictionary<string, object> map, string key)
        {
            if (!map.TryGetValue(key, out var value) || value == null) return null;
            if (!(value is List<object> list)) throw new FormatException();
            var result = new List<string>(list.Count);
            foreach (var item in list)
            {
                if (item is string text) result.Add(text);
                else throw new FormatException();
            }
            return result;
        }
    }
}

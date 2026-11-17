using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mightys.Pulse
{
    /// <summary>Optional extra system fields supplied by a platform adapter. Implement it next to <see cref="IEnvironmentInfo"/>
    /// on the same object. Keys outside the fixed list of system fields are dropped, empty values are omitted, and
    /// values are checked against the format of their key.</summary>
    public interface IExtraSystemFields
    {
        /// <summary>Fields that cannot change while the process runs. Read once at start.</summary>
        IReadOnlyDictionary<string, string> StaticFields { get; }
        /// <summary>Fields that can change, read each time a signal is captured. Must be cheap and must not block.</summary>
        IReadOnlyDictionary<string, string> DynamicFields();
    }

    // The fixed list of optional system fields and the format of each. Anything else is dropped.
    internal static class SystemFields
    {
        private enum Kind { Free, Enum, Bool, Int, Decimal, Date }
        private sealed class Rule
        {
            internal Kind Kind; internal HashSet<string> Values;
        }

        private static readonly Dictionary<string, Rule> Rules = Build();

        private static Dictionary<string, Rule> Build()
        {
            var rules = new Dictionary<string, Rule>(StringComparer.Ordinal);
            void Add(Kind kind, params string[] keys) { foreach (var key in keys) rules[key] = new Rule { Kind = kind }; }
            void AddEnum(string key, params string[] values) =>
                rules[key] = new Rule { Kind = Kind.Enum, Values = new HashSet<string>(values, StringComparer.Ordinal) };
            Add(Kind.Free, "deviceBrand", "architecture", "runtimeVersion", "appName", "extensionId", "timeZone",
                "language", "preferredLanguage", "region", "contentSizeCategory");
            Add(Kind.Int, "osApiLevel", "screenWidthPx", "screenHeightPx", "screenDpi", "screenCount", "utcOffsetMinutes",
                "fontWeightAdjustment", "totalSessionsCount", "distinctDaysUsed", "distinctDaysUsedLastMonth",
                "previousSessionSeconds", "averageSessionSeconds", "sessionSeconds", "foregroundsToday", "previousForegroundSeconds",
                "launchMs", "droppedAutomatic");
            Add(Kind.Decimal, "screenWidth", "screenHeight", "screenScale", "fontScale");
            Add(Kind.Bool, "isSimulator", "isDebugBuild", "reduceMotion", "boldText", "invertColors", "increaseContrast",
                "reduceTransparency", "differentiateWithoutColor", "lowPowerMode");
            Add(Kind.Date, "firstSessionDate");
            AddEnum("deviceType", "phone", "tablet", "desktop", "tv", "watch", "vision", "car", "web");
            AddEnum("scriptingBackend", "il2cpp", "mono");
            AddEnum("installSource", "appstore", "testflight", "playstore", "galaxystore", "amazon", "sideload", "debug", "other");
            AddEnum("targetEnvironment", "native", "simulator", "catalyst", "ios-on-mac", "capacitor", "cordova", "webview");
            AddEnum("orientation", "portrait", "landscape", "square");
            AddEnum("colorScheme", "light", "dark");
            AddEnum("layoutDirection", "ltr", "rtl");
            AddEnum("connectionType", "wifi", "cellular", "ethernet", "none", "other");
            AddEnum("isNewInstall", "true");
            AddEnum("notificationPermission", "granted", "denied", "notDetermined", "provisional", "ephemeral");
            AddEnum("trackingPermission", "authorized", "denied", "restricted", "notDetermined");
            return rules;
        }

        // Fields the SDK computes itself from the usage file.
        internal static readonly string[] UsageKeys = { "isNewInstall", "firstSessionDate", "totalSessionsCount", "distinctDaysUsed",
            "distinctDaysUsedLastMonth", "previousSessionSeconds", "averageSessionSeconds", "sessionSeconds",
            "foregroundsToday", "previousForegroundSeconds" };
        // Fields the SDK sets itself from the automatic events; a platform never supplies them through the field hooks.
        internal static readonly string[] AutoKeys = { "notificationPermission", "trackingPermission", "launchMs", "droppedAutomatic" };

        internal static IEnumerable<string> Keys => Rules.Keys;

        internal static bool IsKnown(string key) => key != null && Rules.ContainsKey(key);

        /// <summary>Returns the value in wire form, or null when the key is unknown, the value is empty or it does not fit the key's format.</summary>
        internal static string Clean(string key, string value)
        {
            if (key == null || value == null || !Rules.TryGetValue(key, out var rule)) return null;
            value = value.Trim();
            if (value.Length == 0) return null;
            switch (rule.Kind)
            {
                case Kind.Free: return value.Length > 64 ? value.Substring(0, 64) : value;
                case Kind.Enum: return rule.Values.Contains(value) ? value : null;
                case Kind.Bool: return value == "true" || value == "false" ? value : null;
                case Kind.Int:
                    return long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole)
                        ? whole.ToString(CultureInfo.InvariantCulture) : null;
                case Kind.Decimal:
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                        double.IsNaN(number) || double.IsInfinity(number)) return null;
                    return number.ToString("0.###", CultureInfo.InvariantCulture);
                case Kind.Date:
                    return DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? value : null;
                default: return null;
            }
        }

        /// <summary>Appends the cleaned fields to a JSON object body, skipping keys already written in <paramref name="written"/>.</summary>
        internal static void Append(StringBuilder b, IEnumerable<KeyValuePair<string, string>> fields, HashSet<string> written)
        {
            if (fields == null) return;
            foreach (var pair in fields)
            {
                if (written.Contains(pair.Key ?? "")) continue;
                var value = Clean(pair.Key, pair.Value);
                if (value == null) continue;
                written.Add(pair.Key);
                if (b.Length > 1) b.Append(',');
                b.Append(SignalJson.Quote(pair.Key)).Append(':').Append(SignalJson.Quote(value));
            }
        }
    }
}

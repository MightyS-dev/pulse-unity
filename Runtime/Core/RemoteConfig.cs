using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mightys.Pulse
{
    // Settings the portal can switch for an app. Everything is on unless the response says otherwise: a missing key,
    // a value of the wrong type, an unknown key or an unknown signal name never switches anything off.
    internal sealed class RemoteConfig
    {
        internal const string FileName = "config";
        internal const string ScreenViewType = "pulse.screen_view";
        internal const string AppOpenedType = "pulse.app_opened", AppBackgroundedType = "pulse.app_backgrounded",
            DeepLinkType = "pulse.deep_link", PermissionChangedType = "pulse.permission_changed",
            HeatmapType = "pulse.heatmap";
        internal static readonly string[] AutomaticTypes =
        {
            "pulse.session_started", "pulse.new_install", "pulse.app_updated", ScreenViewType,
            PermissionChangedType, DeepLinkType, AppOpenedType, AppBackgroundedType, HeatmapType
        };
        // Sent only when the cached settings list them in enabledSignals (they make many signals).
        internal static readonly string[] OptInTypes = { AppOpenedType, AppBackgroundedType };
        private const int MaxListed = 64, MaxTypeLength = 200;

        // Group names and the system keys each one holds. bundleId and the base system keys belong to no group.
        internal static readonly string[] GroupNames = { "device", "screen", "settings", "network", "app", "usage", "permissions" };
        private static readonly Dictionary<string, string[]> GroupKeys = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["device"] = new[] { "deviceType", "deviceBrand", "architecture", "osApiLevel", "runtimeVersion", "scriptingBackend",
                "isSimulator", "targetEnvironment", "extensionId" },
            ["screen"] = new[] { "screenWidth", "screenHeight", "screenScale", "screenWidthPx", "screenHeightPx", "screenDpi",
                "screenCount", "orientation" },
            ["settings"] = new[] { "colorScheme", "layoutDirection", "timeZone", "utcOffsetMinutes", "language", "preferredLanguage",
                "region", "fontScale", "contentSizeCategory", "reduceMotion", "boldText", "invertColors", "increaseContrast",
                "reduceTransparency", "differentiateWithoutColor", "fontWeightAdjustment" },
            ["network"] = new[] { "connectionType", "lowPowerMode" },
            ["app"] = new[] { "appName", "installSource", "isDebugBuild" },
            ["usage"] = new[] { "isNewInstall", "firstSessionDate", "totalSessionsCount", "distinctDaysUsed", "distinctDaysUsedLastMonth",
                "previousSessionSeconds", "averageSessionSeconds", "sessionSeconds", "foregroundsToday", "previousForegroundSeconds" },
            ["permissions"] = new[] { "notificationPermission", "trackingPermission" }
        };

        internal static readonly RemoteConfig AllOn = new RemoteConfig();

        /// <summary>Version of the settings, or null when the response carried none.</summary>
        internal int? Version { get; private set; }
        internal bool ScreenViews { get; private set; } = true;
        internal bool AutomaticSignals { get; private set; } = true;
        /// <summary>the portal switched heatmaps on for this app. Default false, so no cache or a failed read means off.</summary>
        internal bool Heatmaps { get; private set; }
        private readonly HashSet<string> offGroups = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> disabled = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> enabled = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> paused = new HashSet<string>(StringComparer.Ordinal);

        internal bool GroupOn(string group) => !offGroups.Contains(group);
        internal bool IsDisabled(string type) => type != null && disabled.Contains(type);
        internal IReadOnlyCollection<string> DisabledSignals => disabled;
        /// <summary>True when an opt-in type is listed in enabledSignals. Without a cached list nothing is enabled.</summary>
        internal bool IsEnabled(string type) => type != null && enabled.Contains(type);
        /// <summary>True when the server paused the type (pausedSignals), for example near the end of the plan.</summary>
        internal bool IsPaused(string type) => type != null && paused.Contains(type);
        internal IReadOnlyCollection<string> EnabledSignals => enabled;
        internal IReadOnlyCollection<string> PausedSignals => paused;

        /// <summary>The system keys the settings leave out. Empty when every group is on.</summary>
        internal HashSet<string> HiddenKeys()
        {
            var hidden = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in offGroups) foreach (var key in GroupKeys[group]) hidden.Add(key);
            return hidden;
        }

        /// <summary>Every key of every group, for tests and for the platform.</summary>
        internal static IEnumerable<string> KeysOf(string group) => GroupKeys.TryGetValue(group, out var keys) ? keys : new string[0];

        /// <summary>Reads a response body. Returns null when the body is not a JSON object (nothing usable). Never throws.</summary>
        internal static RemoteConfig Parse(string body)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(body) || body.Length > 65536) return null;
                var root = SmallJson.Parse(body) as Dictionary<string, object>;
                if (root == null) return null;
                var config = new RemoteConfig();
                if (root.TryGetValue("version", out var v) && v is double number && !double.IsNaN(number) && !double.IsInfinity(number) &&
                    number >= 0 && number <= int.MaxValue && Math.Floor(number) == number)
                    config.Version = (int)number;
                if (root.TryGetValue("screenViews", out var sv) && sv is bool screens) config.ScreenViews = screens;
                if (root.TryGetValue("automaticSignals", out var au) && au is bool automatic) config.AutomaticSignals = automatic;
                if (root.TryGetValue("heatmaps", out var hm) && hm is bool heat) config.Heatmaps = heat;
                if (root.TryGetValue("groups", out var g) && g is Dictionary<string, object> groups)
                    foreach (var name in GroupNames)
                        if (groups.TryGetValue(name, out var on) && on is bool flag && !flag) config.offGroups.Add(name);
                if (root.TryGetValue("disabledSignals", out var d) && d is List<object> list)
                    foreach (var item in list)
                        if (item is string type && Array.IndexOf(AutomaticTypes, type) >= 0) config.disabled.Add(type);
                // pauseExpensiveAt90 is for the server; the SDK ignores it and obeys pausedSignals.
                ReadTypes(root, "enabledSignals", config.enabled, OptInTypes);
                ReadTypes(root, "pausedSignals", config.paused, null);
                return config;
            }
            catch (Exception) { return null; }
        }

        // A list of type names. Items that are not strings are skipped; with a filter only those names are kept.
        private static void ReadTypes(Dictionary<string, object> root, string key, HashSet<string> into, string[] only)
        {
            if (!root.TryGetValue(key, out var value) || !(value is List<object> list)) return;
            foreach (var item in list)
                if (item is string type && type.Length > 0 && type.Length <= MaxTypeLength && into.Count < MaxListed &&
                    (only == null || Array.IndexOf(only, type) >= 0)) into.Add(type);
        }

        /// <summary>Reads the cached settings. A missing, unreadable or damaged file means everything on.</summary>
        internal static RemoteConfig Load(IStorage storage)
        {
            try
            {
                var bytes = storage?.Read(FileName);
                if (bytes == null || bytes.Length == 0) return AllOn;
                return Parse(Encoding.UTF8.GetString(bytes)) ?? AllOn;
            }
            catch (Exception) { return AllOn; }
        }

        /// <summary>The header value for If-None-Match, or null without a cached version.</summary>
        internal string ETag => Version.HasValue ? "\"" + Version.Value.ToString(CultureInfo.InvariantCulture) + "\"" : null;
    }

    // What the app does with the settings, in one place. A code option can only switch things off: the server drops
    // whatever the portal switched off, so true in code never forces a signal on.
    internal sealed class SignalPolicy
    {
        private readonly bool codeScreens, codeAutomatic, codeHeatmaps;
        private readonly RemoteConfig config;
        private volatile RemoteConfig live;

        internal SignalPolicy(PulseOptions options, RemoteConfig config)
        {
            codeScreens = options?.AutomaticScreenViews ?? true; codeAutomatic = options?.AutomaticSignals ?? true;
            codeHeatmaps = options?.CaptureHeatmaps ?? true;
            this.config = config ?? RemoteConfig.AllOn;
        }

        internal RemoteConfig Config => config;

        // The newest settings of a refresh in this run. Only the heatmap rules follow it at once; everything else holds for the run.
        internal RemoteConfig Live { get => live ?? config; set => live = value; }

        // Heatmaps: heatmaps need the portal switch, the code option, automatic signals, an allowed type and allowed screen views.
        internal bool HeatmapAllowed
        {
            get
            {
                var c = Live;
                return codeHeatmaps && codeAutomatic && c.Heatmaps && c.AutomaticSignals && !c.IsDisabled(RemoteConfig.HeatmapType) &&
                    !c.IsPaused(RemoteConfig.HeatmapType) && ScreenViewsAllowed;
            }
        }

        // The portal allows pulse.screen_view: screenViews and automaticSignals on, type not disabled.
        internal bool ScreenViewsAllowed =>
            config.ScreenViews && config.AutomaticSignals && !config.IsDisabled(RemoteConfig.ScreenViewType);

        // Whether a platform integration should capture screens. Platforms read this to decide whether to install their hook.
        internal bool AutomaticScreenHook => codeScreens && ScreenViewsAllowed && !config.IsPaused(RemoteConfig.ScreenViewType);

        // Whether a signal may be queued. Screen views from manual calls need only the portal; the hook path checks AutomaticScreenHook first.
        internal bool Allows(string type)
        {
            if (type == RemoteConfig.HeatmapType) return HeatmapAllowed;
            if (config.IsPaused(type)) return false;
            if (type == RemoteConfig.ScreenViewType) return ScreenViewsAllowed;
            if (Array.IndexOf(RemoteConfig.AutomaticTypes, type) < 0) return true;
            // The opt-in types need the portal switch on, and no cache or a failed read never switches them on.
            if (Array.IndexOf(RemoteConfig.OptInTypes, type) >= 0 && !config.IsEnabled(type)) return false;
            return config.AutomaticSignals && !config.IsDisabled(type) && codeAutomatic;
        }
    }
}

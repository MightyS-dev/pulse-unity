using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mightys.Pulse
{
    // The platform key App Control uses for rules, from the operating system name the SDK already sends as osName.
    internal static class ControlPlatform
    {
        internal static string FromOsName(string osName)
        {
            switch ((osName ?? "").Trim().ToLowerInvariant())
            {
                case "ios": case "ipados": return "ios";
                case "android": return "android";
                case "macos": case "maccatalyst": case "osx": return "macos";
                case "windows": case "winui": return "windows";
                case "linux": return "linux";
                case "web": case "webgl": case "browser": return "web";
                default: return null;
            }
        }
    }

    // What the builder needs to know about the app and the device.
    internal sealed class ControlContext
    {
        internal string[] Locales = new string[0];
        internal string AppName = "", AppVersion = "", Platform = "", BundleId = "";
        internal DateTimeOffset Now;
        /// <summary>Converts a UTC time to the device's local time. Replaceable for tests.</summary>
        internal Func<DateTimeOffset, DateTimeOffset> ToLocal = t => t.ToLocalTime();

        /// <summary>The store page of the app: the rules' link, or one the SDK can build itself (Android: the Play page by package name).</summary>
        internal string StoreLink(ControlRules rules)
        {
            if (rules != null && rules.Update.TryGetValue(Platform ?? "", out var rule) && rule.StoreUrl != null) return rule.StoreUrl;
            return CanBuildStoreLink ? ControlRules.WebUrl("https://play.google.com/store/apps/details?id=" + Uri.EscapeDataString(BundleId)) : null;
        }

        internal bool CanBuildStoreLink => Platform == "android" && !string.IsNullOrEmpty(BundleId);
    }

    // Turns a decision into the public state: language, custom texts, filled-in markers, links.
    internal static class ControlStateBuilder
    {
        internal static ControlState Build(ControlRules rules, ControlOutcome outcome, ControlContext context)
        {
            if (rules == null) return ControlState.Normal;
            var language = LanguageFor(context.Locales);
            var states = new List<ControlState>();
            if (outcome.State != ControlKind.Normal) states.Add(Make(outcome.State, rules, outcome, context, language));
            if (outcome.Card.HasValue) states.Add(Make(outcome.Card.Value, rules, outcome, context, language));
            if (outcome.Banners.Contains(ControlOutcome.BannerMaintenanceUpcoming))
                states.Add(Make(ControlKind.MaintenanceUpcoming, rules, outcome, context, language));
            if (states.Count == 0) return ControlState.Normal;
            var primary = states[0];
            states.RemoveAt(0);
            primary.Extras = states.ToArray();
            return primary;
        }

        private static ControlState Make(ControlKind kind, ControlRules rules, ControlOutcome outcome, ControlContext c, string language)
        {
            var state = new ControlState(kind);
            rules.Update.TryGetValue(c.Platform ?? "", out var update);
            var maintenance = rules.Maintenance;
            string version = null, title = "", body = "", button = "", url = null;
            switch (kind)
            {
                case ControlKind.UpdateRequired:
                case ControlKind.UpdateSuggested:
                    version = update?.Latest ?? update?.StoreVersion ?? update?.Min;
                    url = c.StoreLink(rules);
                    title = Text(language, kind, "title"); body = Text(language, kind, "body"); button = Text(language, kind, "button");
                    break;
                case ControlKind.VersionBlocked:
                    version = c.AppVersion;
                    url = c.StoreLink(rules);
                    title = Text(language, kind, "title");
                    body = ControlTexts.Get(language, url == null ? "versionBlocked.bodyNoUpdate" : "versionBlocked.body");
                    button = Text(language, kind, "button");
                    break;
                case ControlKind.Maintenance:
                    title = Text(language, kind, "title"); body = Text(language, kind, "body"); button = Text(language, kind, "button");
                    break;
                case ControlKind.MaintenanceUpcoming:
                    body = ControlTexts.Get(language, "maintenanceUpcoming.banner");
                    break;
                case ControlKind.Announcement:
                    var a = rules.Announcement;
                    title = a.Title ?? ""; body = a.Body ?? ""; url = a.Url;
                    button = a.ButtonText ?? ControlTexts.Get(language, "announcement.button");
                    break;
                case ControlKind.WhatsNew:
                    version = rules.WhatsNew.Version;
                    title = ControlTexts.Get(language, "whatsNew.title"); body = rules.WhatsNew.Body ?? "";
                    button = ControlTexts.Get(language, "whatsNew.button");
                    break;
            }
            if (rules.Custom.TryGetValue(ControlKinds.Wire(kind), out var custom))
            {
                if (custom.Title != null) title = custom.Title;
                if (custom.Body != null) body = custom.Body;
                if (custom.ButtonText != null) button = custom.ButtonText;
            }
            state.Version = version; state.Url = url;
            var starts = maintenance?.StartsAt.HasValue == true ? FormatTime(maintenance.StartsAt.Value, c, language) : "";
            var ends = maintenance?.EndsAt.HasValue == true ? FormatTime(maintenance.EndsAt.Value, c, language) : "";
            state.Title = Fill(title, c, version, starts, ends); state.Body = Fill(body, c, version, starts, ends);
            state.ButtonText = Fill(button, c, version, starts, ends);
            if (kind == ControlKind.Maintenance || kind == ControlKind.MaintenanceUpcoming)
            { state.StartsAt = maintenance?.StartsAt; state.EndsAt = maintenance?.EndsAt; }
            var locking = kind == ControlKind.UpdateRequired || kind == ControlKind.VersionBlocked || kind == ControlKind.Maintenance;
            if (locking)
            {
                state.AllowContinue = outcome.State == kind && outcome.AllowContinue;
                if (outcome.Banners.Contains(ControlOutcome.BannerTestBuild))
                {
                    state.IsTestBuild = true;
                    state.TestBuildText = ControlTexts.Get(language, "testBuild.banner");
                }
            }
            if (kind == ControlKind.UpdateSuggested) state.DismissText = ControlTexts.Get(language, "updateSuggested.dismiss");
            else if (locking && state.AllowContinue) state.DismissText = ControlTexts.Get(language, "continue");
            else if (kind == ControlKind.Announcement || kind == ControlKind.WhatsNew) state.DismissText = ControlTexts.Get(language, "continue");
            return state;
        }

        private static string Text(string language, ControlKind kind, string part) => ControlTexts.Get(language, ControlKinds.Wire(kind) + "." + part);

        private static string Fill(string text, ControlContext c, string version, string startTime, string endTime)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('{') < 0) return text ?? "";
            return text.Replace("{appName}", string.IsNullOrWhiteSpace(c.AppName) ? "App" : c.AppName)
                .Replace("{version}", version ?? c.AppVersion ?? "")
                .Replace("{startTime}", startTime).Replace("{endTime}", endTime);
        }

        // Short time in the device's time zone; with the date when it is not today.
        internal static string FormatTime(DateTimeOffset utc, ControlContext c, string language)
        {
            try
            {
                var culture = Culture(language);
                var local = c.ToLocal(utc);
                var today = c.ToLocal(c.Now);
                var time = local.ToString("t", culture);
                return local.Date == today.Date ? time : local.ToString("d", culture) + " " + time;
            }
            catch (Exception) { return utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture); }
        }

        private static CultureInfo Culture(string language)
        {
            try { return CultureInfo.GetCultureInfo(language); } catch (Exception) { return CultureInfo.InvariantCulture; }
        }

        // ---- language and time ----

        internal static string LanguageFor(IEnumerable<string> locales)
        {
            foreach (var locale in locales ?? new string[0])
            {
                var language = Match(locale);
                if (language != null) return language;
            }
            return ControlTexts.FallbackLanguage;
        }

        private static string Match(string locale)
        {
            if (string.IsNullOrWhiteSpace(locale)) return null;
            var parts = locale.Trim().Replace('_', '-').Split('-');
            var primary = parts[0].ToLowerInvariant();
            switch (primary)
            {
                case "en": case "de": case "fr": case "es": case "it": case "ja": case "ko": case "tr": case "ru": return primary;
                case "pt": return "pt-BR";
                case "sr": return "sr-Latn";
                case "zh":
                    foreach (var part in parts)
                    {
                        var p = part.ToLowerInvariant();
                        if (p == "hant" || p == "tw" || p == "hk" || p == "mo") return null;
                    }
                    return "zh-Hans";
                default: return null;
            }
        }
    }
}

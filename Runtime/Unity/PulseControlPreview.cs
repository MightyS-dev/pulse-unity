using System;
using System.Globalization;

namespace Mightys.Pulse
{
    /// <summary>Shows the built-in App Control screens without a server, to check how they look in your game (any language, light or dark).
    /// It only draws; it changes no rules and sends nothing. Call <see cref="Hide"/> to return to the real state.</summary>
    public static class PulseControlPreview
    {
        /// <summary>Shows a screen for <paramref name="kind"/> with the built-in texts in <paramref name="language"/> (en, de, fr, es, it, pt-BR, ja, ko,
        /// zh-Hans, tr, ru or sr-Latn).</summary>
        /// <param name="kind">The state to show. <see cref="ControlKind.Normal"/> hides the preview.</param>
        /// <param name="language">The language of the built-in texts.</param>
        /// <param name="dark">True for the dark theme, false for the light one.</param>
        /// <param name="testBuild">True to add the strip a debug build shows on a locking screen.</param>
        /// <param name="customBody">A body text to use instead of the built-in one, for example a long announcement.</param>
        public static void Show(ControlKind kind, string language = "en", bool dark = true, bool testBuild = false, string customBody = null)
        {
            try
            {
                if (kind == ControlKind.Normal) { Hide(); return; }
                var state = Build(kind, language, testBuild, customBody);
                ControlOverlay.PreviewDark = dark; ControlOverlay.PreviewTheme = true;
                ControlOverlay.Preview = state;
            }
            catch (Exception) { }
        }

        /// <summary>True while a preview is on screen.</summary>
        public static bool IsShowing => ControlOverlay.Preview != null;

        /// <summary>Removes the preview.</summary>
        public static void Hide() { ControlOverlay.Preview = null; ControlOverlay.PreviewTheme = false; }

        internal static ControlState Build(ControlKind kind, string language, bool testBuild, string customBody)
        {
            var now = DateTimeOffset.UtcNow;
            string Time(TimeSpan offset) => now.Add(offset).ToString("o", CultureInfo.InvariantCulture);
            const string store = "\"storeUrl\":\"https://play.google.com/store/apps/details?id=dev.example.demo\"";
            string rules;
            switch (kind)
            {
                case ControlKind.UpdateRequired: rules = "{\"format\":1,\"update\":{\"android\":{\"min\":\"99.0.0\",\"mode\":\"required\"," + store + "}}}"; break;
                case ControlKind.UpdateSuggested: rules = "{\"format\":1,\"update\":{\"android\":{\"latest\":\"99.0.0\",\"mode\":\"suggested\"," + store + "}}}"; break;
                case ControlKind.VersionBlocked: rules = "{\"format\":1,\"blocked\":[{\"version\":\"1.0.0\"}],\"update\":{\"android\":{" + store + "}}}"; break;
                case ControlKind.Maintenance:
                    rules = "{\"format\":1,\"maintenance\":{\"startsAt\":\"" + Time(TimeSpan.FromMinutes(-10)) + "\",\"endsAt\":\"" + Time(TimeSpan.FromMinutes(50)) + "\"}}"; break;
                case ControlKind.MaintenanceUpcoming:
                    rules = "{\"format\":1,\"maintenance\":{\"startsAt\":\"" + Time(TimeSpan.FromMinutes(10)) + "\",\"endsAt\":\"" + Time(TimeSpan.FromMinutes(70)) + "\",\"headsUpMinutes\":30}}"; break;
                case ControlKind.Announcement:
                    rules = "{\"format\":1,\"announcement\":{\"id\":\"preview\",\"title\":\"Weekend event\",\"body\":\"" + Escape(customBody ?? "Double rewards until Sunday night. Thanks for playing!") +
                        "\",\"url\":\"https://example.com/event\",\"buttonText\":\"Read more\",\"until\":\"" + Time(TimeSpan.FromDays(1)) + "\"}}"; break;
                case ControlKind.WhatsNew:
                    rules = "{\"format\":1,\"whatsNew\":{\"version\":\"1.0.0\",\"body\":\"" + Escape(customBody ?? "New levels, faster loading and many small fixes.") + "\"}}"; break;
                default: return ControlState.Normal;
            }
            var parsed = ControlRules.Parse(rules);
            if (parsed == null) return ControlState.Normal;
            var context = new ControlContext
            {
                Locales = new[] { language ?? "en" }, AppName = "Demo App", AppVersion = "1.0.0", Platform = "android", BundleId = "dev.example.demo", Now = now
            };
            var input = new ControlInput
            {
                Rules = parsed, Now = now, Platform = "android", AppVersion = "1.0.0", Debug = testBuild, Sdk = "preview",
                JustUpdated = kind == ControlKind.WhatsNew, CanBuildStoreLink = true
            };
            var state = ControlStateBuilder.Build(parsed, ControlDecision.Decide(input), context);
            // A custom body also replaces the built-in one of the other kinds.
            if (customBody != null && kind != ControlKind.Announcement && kind != ControlKind.WhatsNew) state.Body = customBody;
            return state;
        }

        private static string Escape(string text) => (text ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    }
}

using System;
using System.Collections.Generic;

namespace Mightys.Pulse
{
    // What the SDK knows when it decides. The same inputs as the shared decision table.
    internal sealed class ControlInput
    {
        internal ControlRules Rules;
        internal DateTimeOffset Now;
        internal string Platform = "ios", AppVersion = "", Sdk = "";
        internal bool Debug, FromCache, PlayUpdateNotAvailable, JustUpdated, WhatsNewShown;
        internal bool CanBuildStoreLink = true;
        internal double CacheAgeHours;
        /// <summary>How many times the user pressed Update and came back on the same app version.</summary>
        internal int UpdateReturns;
        /// <summary>Hours since a suggestion was dismissed on this app version, or null.</summary>
        internal double? SuggestDismissedHoursAgo;
        internal IReadOnlyList<string> ShownAnnouncements = new string[0];
    }

    // The result: one main state, whether the screen lets the user continue, banners and at most one card.
    internal sealed class ControlOutcome
    {
        internal const string BannerMaintenanceUpcoming = "maintenanceUpcoming", BannerTestBuild = "testBuild";
        internal ControlKind State = ControlKind.Normal;
        internal bool AllowContinue = true;
        internal readonly List<string> Banners = new List<string>();
        internal ControlKind? Card;
    }

    // The App Control decision, the same in every SDK and checked by the shared decision table. Doubt always means no lock: an
    // incomparable version is never below a minimum and never blocked, locking rules from a cache older than 30 days are ignored,
    // and the loop guard turns a required update into a suggestion whenever there is no way out.
    internal static class ControlDecision
    {
        internal const double CacheLockHours = 30 * 24, CacheMaintenanceHours = 72, SuggestQuietHours = 24;
        internal const int UpdateReturnsLimit = 3;

        internal static ControlOutcome Decide(ControlInput input)
        {
            var outcome = new ControlOutcome();
            var rules = input.Rules;
            // 1. Switched off for everyone or for this SDK version.
            if (rules == null || rules.Off || rules.DisabledSdkVersions.Contains(input.Sdk)) return outcome;
            var staleLocks = input.FromCache && input.CacheAgeHours > CacheLockHours;
            rules.Update.TryGetValue(input.Platform ?? "", out var platformRule);
            var storeVersion = platformRule?.StoreVersion;
            var aboveStore = storeVersion != null && ControlVersion.Compare(input.AppVersion, storeVersion) == 1;
            var main = ControlKind.Normal;
            var allowContinue = true;

            // 2. Maintenance inside its window (from the cache never past 72 hours after the start), unless exempt.
            var maintenance = rules.Maintenance;
            if (maintenance != null && maintenance.StartsAt.HasValue && maintenance.EndsAt.HasValue && Covers(maintenance.Platforms, input.Platform))
            {
                var starts = maintenance.StartsAt.Value;
                var ends = maintenance.EndsAt.Value;
                var inWindow = starts <= input.Now && input.Now < ends && (!input.FromCache || input.Now < starts.AddHours(CacheMaintenanceHours));
                var exempt = maintenance.ExemptAboveStore && aboveStore;
                if (inWindow && !exempt) { main = ControlKind.Maintenance; allowContinue = false; }
                if (input.Now < starts && starts.AddMinutes(-maintenance.HeadsUpMinutes) <= input.Now)
                    outcome.Banners.Add(ControlOutcome.BannerMaintenanceUpcoming);
            }
            // 3. A blocked version.
            if (main == ControlKind.Normal && !staleLocks)
                foreach (var entry in rules.Blocked)
                    if (ControlVersion.Compare(input.AppVersion, entry.Version) == 0 && Covers(entry.Platforms, input.Platform))
                    { main = ControlKind.VersionBlocked; allowContinue = entry.AllowContinue; break; }
            // 4. Below the minimum (a version above the store version never is), then the loop guard.
            if (main == ControlKind.Normal && platformRule != null && platformRule.Min != null &&
                ControlVersion.Compare(input.AppVersion, platformRule.Min) == -1 && !aboveStore)
            {
                if (platformRule.Mode == "required" && !staleLocks)
                {
                    var noWayOut = (storeVersion != null && ControlVersion.Compare(storeVersion, input.AppVersion) <= 0) ||
                        input.UpdateReturns >= UpdateReturnsLimit || input.PlayUpdateNotAvailable ||
                        (platformRule.StoreUrl == null && !input.CanBuildStoreLink);
                    if (noWayOut) main = ControlKind.UpdateSuggested;
                    else { main = ControlKind.UpdateRequired; allowContinue = false; }
                }
                // A required minimum from a cache older than 30 days gives nothing: no lock and no suggestion.
                else if (platformRule.Mode == "suggested") main = ControlKind.UpdateSuggested;
            }
            // 5. Suggested by the newest version.
            if (main == ControlKind.Normal && platformRule != null && platformRule.Mode == "suggested" && platformRule.Latest != null &&
                ControlVersion.Compare(input.AppVersion, platformRule.Latest) == -1)
                main = ControlKind.UpdateSuggested;
            if (main == ControlKind.UpdateSuggested && input.SuggestDismissedHoursAgo.HasValue && input.SuggestDismissedHoursAgo.Value < SuggestQuietHours)
                main = ControlKind.Normal;
            if (main == ControlKind.UpdateSuggested) allowContinue = true;
            // Debug builds: every locking screen shows the test banner and Continue.
            if (input.Debug && (main == ControlKind.Maintenance || main == ControlKind.VersionBlocked || main == ControlKind.UpdateRequired))
            { outcome.Banners.Add(ControlOutcome.BannerTestBuild); allowContinue = true; }
            // 6. At most one card: what is new first, then the announcement.
            var whatsNew = rules.WhatsNew;
            var announcement = rules.Announcement;
            if (input.JustUpdated && !input.WhatsNewShown && whatsNew != null && ControlVersion.Compare(input.AppVersion, whatsNew.Version) == 0)
                outcome.Card = ControlKind.WhatsNew;
            else if (announcement != null && announcement.Until.HasValue && input.Now < announcement.Until.Value && Covers(announcement.Platforms, input.Platform) &&
                announcement.Id != null && !Contains(input.ShownAnnouncements, announcement.Id))
                outcome.Card = ControlKind.Announcement;
            outcome.State = main;
            outcome.AllowContinue = allowContinue;
            return outcome;
        }

        private static bool Covers(List<string> platforms, string platform) => platforms == null || platforms.Contains(platform);

        private static bool Contains(IReadOnlyList<string> list, string value)
        {
            foreach (var item in list) if (string.Equals(item, value, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}

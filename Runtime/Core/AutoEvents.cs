using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;

namespace Mightys.Pulse
{
    // The facts behind the automatic events and their quota guards, kept in one small file named "auto" next to the usage file:
    // the local day with its automatic-signal count and foreground count, the length of the previous foreground period and the
    // last known permission statuses. The per-session counts and the dropped count live in memory only. Reading happens
    // on first use; writes are coalesced and run on the thread pool. Storage trouble never throws: the numbers then live in
    // memory for this process. Opt-out deletes the file.
    internal sealed class AutoState
    {
        internal const string FileName = "auto";
        internal const int MaxPerDay = 300;
        private readonly object gate = new object();
        private readonly Func<IStorage> storage;
        private readonly Func<DateTimeOffset, string> localDate;
        private readonly Dictionary<string, int> sessionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> permissions = new Dictionary<string, string>(StringComparer.Ordinal);
        private string countSession, day;
        private int dayCount, foregrounds, dropped;
        private long? previousForeground;
        private DateTimeOffset? foregroundStart;
        private bool loaded, writing, dirty, writeFailed;
        private Task idle = Task.CompletedTask;

        // Per session and type caps. Types that are not listed have no per-session cap (only the per-day total).
        private static readonly Dictionary<string, int> SessionCaps = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [RemoteConfig.AppOpenedType] = 20, [RemoteConfig.AppBackgroundedType] = 20, [RemoteConfig.DeepLinkType] = 10,
            [RemoteConfig.PermissionChangedType] = 5, [RemoteConfig.ScreenViewType] = 100
        };

        internal AutoState(Func<IStorage> storage, Func<DateTimeOffset, string> localDate = null)
        {
            this.storage = storage;
            this.localDate = localDate ?? (t => t.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        /// <summary>Completes when every queued write has finished. Used by tests.</summary>
        internal Task Idle { get { lock (gate) return idle; } }

        /// <summary>Opt-out: forgets every number and deletes the file.</summary>
        internal void Forget()
        {
            lock (gate)
            {
                sessionCounts.Clear(); permissions.Clear(); countSession = day = null; dayCount = foregrounds = dropped = 0;
                previousForeground = null; foregroundStart = null; loaded = true; dirty = false; writeFailed = false;
                Chain(() => { try { storage().Delete(FileName); } catch (Exception) { } });
            }
        }

        // ---- quota guards ----

        /// <summary>Decides whether one automatic signal may be queued. Counts it when it may. When it may not, counts it as dropped.
        /// Types that are not automatic are always allowed and not counted.</summary>
        internal bool Admit(string type, string sessionId, DateTimeOffset timestamp)
        {
            if (Array.IndexOf(RemoteConfig.AutomaticTypes, type) < 0 || type == RemoteConfig.HeatmapType) return true;
            lock (gate)
            {
                EnsureLoaded(); RollDay(timestamp);
                if (countSession != sessionId) { sessionCounts.Clear(); countSession = sessionId; }
                sessionCounts.TryGetValue(type, out var count);
                if ((SessionCaps.TryGetValue(type, out var cap) && count >= cap) || dayCount >= MaxPerDay)
                { if (dropped < int.MaxValue) dropped++; return false; }
                sessionCounts[type] = count + 1; dayCount++; MarkDirty();
                return true;
            }
        }

        /// <summary>The number of dropped automatic signals since the last signal that carried it, then resets it to zero.</summary>
        internal int TakeDropped() { lock (gate) { var value = dropped; dropped = 0; return value; } }

        // ---- foreground counters ----

        /// <summary>A real transition to the foreground: counts it for the local day and starts timing the period.</summary>
        internal void NoteForeground(DateTimeOffset timestamp)
        {
            lock (gate) { EnsureLoaded(); RollDay(timestamp); foregrounds++; foregroundStart = timestamp; MarkDirty(); }
        }

        /// <summary>Whole seconds of the current foreground period, from its start or, when none was reported, from <paramref name="fallbackStart"/>.</summary>
        internal long CurrentForegroundSeconds(DateTimeOffset timestamp, DateTimeOffset fallbackStart)
        {
            lock (gate) return Math.Max(0, (long)(timestamp - (foregroundStart ?? fallbackStart)).TotalSeconds);
        }

        /// <summary>A real transition to the background: the finished period becomes "previous".</summary>
        internal void NoteBackground(long seconds)
        {
            lock (gate) { EnsureLoaded(); previousForeground = Math.Max(0, seconds); foregroundStart = null; MarkDirty(); }
        }

        // ---- permissions ----

        /// <summary>Records a status. Returns the previous status when it differs (a change to report), otherwise null.
        /// The first status ever seen is stored without a change.</summary>
        internal string NotePermission(string key, string status)
        {
            lock (gate)
            {
                EnsureLoaded();
                permissions.TryGetValue(key, out var before);
                if (before == status) return null;
                permissions[key] = status; MarkDirty();
                return before;
            }
        }

        /// <summary>The system fields of this state for a signal captured at <paramref name="timestamp"/>.</summary>
        internal void Fill(Dictionary<string, string> fields, DateTimeOffset timestamp)
        {
            lock (gate)
            {
                EnsureLoaded();
                if (foregrounds > 0 && day == localDate(timestamp)) fields["foregroundsToday"] = foregrounds.ToString(CultureInfo.InvariantCulture);
                if (previousForeground.HasValue) fields["previousForegroundSeconds"] = previousForeground.Value.ToString(CultureInfo.InvariantCulture);
                foreach (var pair in permissions) fields[pair.Key] = pair.Value;
            }
        }

        private void RollDay(DateTimeOffset timestamp)
        {
            var today = localDate(timestamp);
            if (day == today) return;
            day = today; dayCount = 0; foregrounds = 0; MarkDirty();
        }

        // ---- storage ----

        private void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                var bytes = storage().Read(FileName);
                if (bytes == null) return;
                if (!(MiniJson.Parse(Encoding.UTF8.GetString(bytes)) is Dictionary<string, object> root)) return;
                day = root.TryGetValue("day", out var d) ? d as string : null;
                dayCount = (int)Num(root, "count"); foregrounds = (int)Num(root, "foregrounds");
                if (root.TryGetValue("previous", out var p) && p is double pd && pd >= 0) previousForeground = (long)pd;
                if (root.TryGetValue("permissions", out var perms) && perms is Dictionary<string, object> map)
                    foreach (var pair in map)
                        if (pair.Value is string status && SystemFields.Clean(pair.Key, status) == status &&
                            Array.IndexOf(SystemFields.AutoKeys, pair.Key) >= 0) permissions[pair.Key] = status;
            }
            catch (Exception) { day = null; dayCount = foregrounds = 0; previousForeground = null; permissions.Clear(); }
        }

        private static long Num(Dictionary<string, object> root, string key) =>
            root.TryGetValue(key, out var value) && value is double number && number >= 0 && number < int.MaxValue ? (long)number : 0;

        private string Serialize()
        {
            var b = new StringBuilder("{\"v\":1");
            b.Append(",\"day\":").Append(SignalJson.Quote(day ?? ""));
            b.Append(",\"count\":").Append(dayCount.ToString(CultureInfo.InvariantCulture));
            b.Append(",\"foregrounds\":").Append(foregrounds.ToString(CultureInfo.InvariantCulture));
            if (previousForeground.HasValue) b.Append(",\"previous\":").Append(previousForeground.Value.ToString(CultureInfo.InvariantCulture));
            b.Append(",\"permissions\":{");
            var first = true;
            foreach (var pair in permissions)
            {
                if (!first) b.Append(','); first = false;
                b.Append(SignalJson.Quote(pair.Key)).Append(':').Append(SignalJson.Quote(pair.Value));
            }
            return b.Append("}}").ToString();
        }

        // Called with the lock held. One writer at a time; a write always saves the newest state.
        private void MarkDirty()
        {
            if (writeFailed) return;
            dirty = true;
            if (writing) return;
            writing = true;
            Chain(WriteLoop);
        }

        private void WriteLoop()
        {
            while (true)
            {
                string text;
                lock (gate) { if (!dirty) { writing = false; return; } dirty = false; text = Serialize(); }
                try { storage().Replace(FileName, Encoding.UTF8.GetBytes(text)); }
                catch (Exception) { lock (gate) { writing = false; dirty = false; writeFailed = true; } return; }
            }
        }

        private void Chain(Action work) { idle = idle.ContinueWith(_ => work(), TaskScheduler.Default); }
    }

    // What a deep link link reveals: scheme, host, and a path with ids removed. Never the query, the fragment or other parameters.
    internal static class DeepLinkProps
    {
        internal const int MaxPath = 128, MaxCampaign = 64;
        private static readonly string[] Campaign = { "utm_source", "utm_medium", "utm_campaign" };
        private static readonly string[] CampaignProps = { "utmSource", "utmMedium", "utmCampaign" };

        /// <summary>Props for <c>pulse.deep_link</c>, or null when the text is not an absolute link.</summary>
        internal static Dictionary<string, object> From(string link)
        {
            if (string.IsNullOrWhiteSpace(link) || link.Length > 4096) return null;
            var text = link.Trim();
            var colon = text.IndexOf(':');
            if (colon < 1) return null;
            var scheme = text.Substring(0, colon).ToLowerInvariant();
            foreach (var c in scheme) if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '+' || c == '-' || c == '.')) return null;
            if (!(scheme[0] >= 'a' && scheme[0] <= 'z')) return null;
            var rest = text.Substring(colon + 1);
            var hash = rest.IndexOf('#'); if (hash >= 0) rest = rest.Substring(0, hash);
            var question = rest.IndexOf('?');
            var query = question >= 0 ? rest.Substring(question + 1) : "";
            if (question >= 0) rest = rest.Substring(0, question);
            string host = "", path = rest;
            if (rest.StartsWith("//", StringComparison.Ordinal))
            {
                var slash = rest.IndexOf('/', 2);
                var authority = slash >= 0 ? rest.Substring(2, slash - 2) : rest.Substring(2);
                path = slash >= 0 ? rest.Substring(slash) : "";
                var at = authority.LastIndexOf('@'); if (at >= 0) authority = authority.Substring(at + 1);
                var port = authority.LastIndexOf(':'); if (port >= 0 && authority.IndexOf(']') < port) authority = authority.Substring(0, port);
                host = authority.ToLowerInvariant();
            }
            var props = new Dictionary<string, object>
            {
                ["scheme"] = scheme, ["host"] = Cut(ErrorFilter.Apply(host), MaxPath), ["path"] = Cut(CleanPath(path), MaxPath)
            };
            foreach (var pair in query.Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq < 1) continue;
                var index = Array.IndexOf(Campaign, pair.Substring(0, eq));
                if (index < 0 || props.ContainsKey(CampaignProps[index])) continue;
                string value;
                try { value = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' ')); } catch (Exception) { continue; }
                value = Cut(ErrorFilter.Apply(value.Trim()), MaxCampaign);
                if (value.Length > 0) props[CampaignProps[index]] = value;
            }
            return props;
        }

        // Numeric and uuid-like segments become :id. Other text is run through the personal data filter.
        private static string CleanPath(string path)
        {
            if (path.Length == 0) return "";
            var parts = path.Split('/');
            for (var i = 0; i < parts.Length; i++)
            {
                string segment;
                try { segment = Uri.UnescapeDataString(parts[i]); } catch (Exception) { segment = parts[i]; }
                parts[i] = IsId(segment) ? ":id" : ErrorFilter.Apply(segment);
            }
            return string.Join("/", parts);
        }

        private static bool IsId(string segment)
        {
            if (segment.Length == 0) return false;
            var digits = true;
            foreach (var c in segment) if (c < '0' || c > '9') { digits = false; break; }
            if (digits) return true;
            if (segment.Length != 36) return false;
            for (var i = 0; i < 36; i++)
            {
                var c = segment[i];
                if (i == 8 || i == 13 || i == 18 || i == 23) { if (c != '-') return false; }
                else if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            }
            return true;
        }

        private static string Cut(string text, int max) => text.Length > max ? text.Substring(0, max) : text;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;

namespace Mightys.Pulse
{
    /// <summary>One signal the SDK sends by itself.</summary>
    internal sealed class AutoSignal
    {
        internal string Type;
        internal string PropsJson;
    }

    /// <summary>What the usage file says about the session that a signal belongs to.</summary>
    internal sealed class UsageObservation
    {
        internal readonly List<AutoSignal> Signals = new List<AutoSignal>();
        internal readonly Dictionary<string, string> Fields = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    // Session statistics and the facts behind the three automatic signals, kept in one small file named "usage" next to the
    // install id. Reading happens once at start; writes are coalesced and run on the thread pool, never on a public call.
    // Storage trouble never throws: the numbers then live in memory for this process. Opt-out deletes the file.
    internal sealed class UsageTracker
    {
        internal const string FileName = "usage";
        private const int RecentDays = 30, KeepDays = 90, MaxCompleted = 2000;
        private readonly object gate = new object();
        private readonly Func<IStorage> storage;
        private readonly Func<DateTimeOffset, string> localDate;
        private readonly Func<bool> installIsNew;
        private readonly List<string> recent = new List<string>();
        private readonly List<Done> completed = new List<Done>();
        private bool loaded, readFailed, fresh, freshHandled, updateChecked, writing, dirty, writeFailed;
        private DateTimeOffset savedLast;
        private string firstDate, lastDay, lastVersion, lastBuild, currentId, firstSessionId;
        private long totalSessions, lifetimeDays;
        private long? previousSeconds;
        private DateTimeOffset start, last;
        private Task idle = Task.CompletedTask;

        private struct Done { internal string Day; internal long Seconds; }

        /// <summary>Creates a tracker.
        /// <param name="storage">Storage holding the usage file.</param>
        /// <param name="localDate">Device-local calendar date (yyyy-MM-dd) of a moment. Defaults to the system time zone.</param>
        /// <param name="installIsNew">Returns true once when the install id was created just now. Without it, a missing usage file means a new install.</param></summary>
        internal UsageTracker(Func<IStorage> storage, Func<DateTimeOffset, string> localDate = null, Func<bool> installIsNew = null)
        {
            this.storage = storage; this.installIsNew = installIsNew;
            this.localDate = localDate ?? (t => t.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        /// <summary>Completes when every queued write has finished. Used by tests.</summary>
        internal Task Idle { get { lock (gate) return idle; } }

        /// <summary>Reads the file now, so the first signal does not wait for storage.</summary>
        internal void Prime() { lock (gate) EnsureLoaded(); }

        /// <summary>Opt-out: forgets every number and deletes the file. The next signal starts a fresh history.</summary>
        internal void Forget()
        {
            lock (gate)
            {
                recent.Clear(); completed.Clear();
                loaded = true; readFailed = false; fresh = true; freshHandled = false; updateChecked = false; dirty = false; writeFailed = false; savedLast = default;
                firstDate = lastDay = lastVersion = lastBuild = currentId = firstSessionId = null;
                totalSessions = lifetimeDays = 0; previousSeconds = null; start = last = default;
                Chain(() => { try { storage().Delete(FileName); } catch (Exception) { } });
            }
        }

        /// <summary>Records one signal of a session and returns the automatic signals that are due and the statistic fields to attach.</summary>
        internal UsageObservation Observe(string sessionId, DateTimeOffset timestamp, string appVersion, string appBuild, bool emitsUpdate = true)
        {
            var result = new UsageObservation();
            lock (gate)
            {
                EnsureLoaded();
                var today = localDate(timestamp);
                if (sessionId != currentId)
                {
                    CloseCurrent(); Prune(today);
                    currentId = sessionId; start = last = timestamp;
                    totalSessions++;
                    if (firstDate == null) firstDate = today;
                    if (lastDay != today && !recent.Contains(today)) { lifetimeDays++; }
                    if (!recent.Contains(today)) recent.Add(today);
                    lastDay = today;
                    PruneRecent(today);
                    var firstEver = false;
                    if (fresh && !freshHandled)
                    {
                        freshHandled = true;
                        firstEver = !readFailed && SafeNew();
                        if (firstEver) { firstSessionId = sessionId; result.Signals.Add(new AutoSignal { Type = "pulse.new_install" }); }
                    }
                    if (!updateChecked)
                    {
                        updateChecked = true;
                        var changed = !firstEver && !fresh && !readFailed && lastVersion != null &&
                            ((appVersion ?? "") != lastVersion || (appBuild ?? "") != (lastBuild ?? ""));
                        var queued = false;
                        if (changed && emitsUpdate && ((appVersion ?? "").Length > 0 || (appBuild ?? "").Length > 0))
                        {
                            queued = true;
                            result.Signals.Add(new AutoSignal { Type = "pulse.app_updated", PropsJson =
                                "{\"previousVersion\":" + SignalJson.Quote(lastVersion) + ",\"previousBuild\":" + SignalJson.Quote(lastBuild ?? "") + "}" });
                        }
                        // The stored version moves only when the event is queued (or nothing changed), so a suppressed update is not lost.
                        if (queued || !changed || lastVersion == null) { lastVersion = appVersion ?? ""; lastBuild = appBuild ?? ""; }
                    }
                    result.Signals.Add(new AutoSignal { Type = "pulse.session_started" });
                    MarkDirty();
                }
                else if (timestamp > last) { last = timestamp; if ((last - savedLast).TotalSeconds >= 30) MarkDirty(); }
                Fill(result.Fields, sessionId, timestamp, today);
            }
            return result;
        }

        private bool SafeNew() { try { return installIsNew == null || installIsNew(); } catch (Exception) { return false; } }

        private void Fill(Dictionary<string, string> fields, string sessionId, DateTimeOffset timestamp, string today)
        {
            fields["firstSessionDate"] = firstDate;
            fields["totalSessionsCount"] = totalSessions.ToString(CultureInfo.InvariantCulture);
            fields["distinctDaysUsed"] = lifetimeDays.ToString(CultureInfo.InvariantCulture);
            var cutoff = DateString(ParseDay(today).AddDays(-(RecentDays - 1)));
            var month = 0;
            foreach (var day in recent) if (string.CompareOrdinal(day, cutoff) >= 0) month++;
            fields["distinctDaysUsedLastMonth"] = month.ToString(CultureInfo.InvariantCulture);
            if (previousSeconds.HasValue) fields["previousSessionSeconds"] = previousSeconds.Value.ToString(CultureInfo.InvariantCulture);
            if (completed.Count > 0)
            {
                long sum = 0; foreach (var d in completed) sum += d.Seconds;
                fields["averageSessionSeconds"] = ((long)Math.Round((double)sum / completed.Count, MidpointRounding.AwayFromZero))
                    .ToString(CultureInfo.InvariantCulture);
            }
            fields["sessionSeconds"] = Math.Max(0, (long)(timestamp - start).TotalSeconds).ToString(CultureInfo.InvariantCulture);
            if (sessionId == firstSessionId && firstSessionId != null) fields["isNewInstall"] = "true";
        }

        private void CloseCurrent()
        {
            if (currentId == null && start == default) return;
            var seconds = Math.Max(0, (long)(last - start).TotalSeconds);
            completed.Add(new Done { Day = localDate(last), Seconds = seconds });
            previousSeconds = seconds;
            currentId = null; start = last = default;
        }

        private void Prune(string today)
        {
            if (completed.Count == 0) return;
            var cutoff = DateString(ParseDay(today).AddDays(-KeepDays));
            completed.RemoveAll(d => string.CompareOrdinal(d.Day, cutoff) < 0);
            if (completed.Count > MaxCompleted) completed.RemoveRange(0, completed.Count - MaxCompleted);
        }

        private void PruneRecent(string today)
        {
            var cutoff = DateString(ParseDay(today).AddDays(-(RecentDays - 1)));
            recent.RemoveAll(day => string.CompareOrdinal(day, cutoff) < 0);
        }

        private static DateTime ParseDay(string day) =>
            DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed : new DateTime(2000, 1, 1);
        private static string DateString(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            byte[] bytes;
            try { bytes = storage().Read(FileName); }
            catch (Exception) { readFailed = true; fresh = true; return; }
            if (bytes == null) { fresh = true; return; }
            try { Parse(Encoding.UTF8.GetString(bytes)); }
            catch (Exception) { ResetState(); readFailed = true; fresh = true; }
        }

        private void ResetState()
        {
            recent.Clear(); completed.Clear(); firstDate = lastDay = lastVersion = lastBuild = null;
            totalSessions = lifetimeDays = 0; previousSeconds = null; start = last = default; currentId = null;
        }

        private void Parse(string text)
        {
            var root = MiniJson.Parse(text) as Dictionary<string, object>;
            if (root == null) throw new FormatException("usage");
            firstDate = root.TryGetValue("first", out var f) ? f as string : null;
            lastDay = root.TryGetValue("lastDay", out var l) ? l as string : null;
            lastVersion = root.TryGetValue("version", out var v) ? v as string : null;
            lastBuild = root.TryGetValue("build", out var bd) ? bd as string : null;
            totalSessions = Num(root, "sessions"); lifetimeDays = Num(root, "days");
            if (root.TryGetValue("previous", out var p) && p is double pd) previousSeconds = (long)pd;
            if (root.TryGetValue("recent", out var r) && r is List<object> days)
                foreach (var day in days) if (day is string s && DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) recent.Add(s);
            if (root.TryGetValue("done", out var d) && d is List<object> items)
                foreach (var item in items)
                    if (item is List<object> pair && pair.Count == 2 && pair[0] is string day && pair[1] is double sec)
                        completed.Add(new Done { Day = day, Seconds = (long)sec });
            if (firstDate == null) throw new FormatException("usage");
            // A session that was running when the process ended counts as completed, up to its last signal.
            if (root.TryGetValue("start", out var st) && st is double sd && root.TryGetValue("last", out var la) && la is double ld)
            {
                start = DateTimeOffset.FromUnixTimeSeconds((long)sd); last = DateTimeOffset.FromUnixTimeSeconds((long)ld);
                currentId = "stored"; CloseCurrent(); dirty = true;
            }
            Prune(lastDay ?? (completed.Count > 0 ? completed[completed.Count - 1].Day : firstDate));
        }

        private static long Num(Dictionary<string, object> root, string key) =>
            root.TryGetValue(key, out var value) && value is double d && d >= 0 ? (long)d : 0;

        private string Serialize()
        {
            var b = new StringBuilder("{\"v\":1");
            b.Append(",\"first\":").Append(SignalJson.Quote(firstDate ?? ""));
            b.Append(",\"lastDay\":").Append(SignalJson.Quote(lastDay ?? ""));
            b.Append(",\"sessions\":").Append(totalSessions.ToString(CultureInfo.InvariantCulture));
            b.Append(",\"days\":").Append(lifetimeDays.ToString(CultureInfo.InvariantCulture));
            if (previousSeconds.HasValue) b.Append(",\"previous\":").Append(previousSeconds.Value.ToString(CultureInfo.InvariantCulture));
            b.Append(",\"version\":").Append(SignalJson.Quote(lastVersion ?? ""));
            b.Append(",\"build\":").Append(SignalJson.Quote(lastBuild ?? ""));
            b.Append(",\"recent\":[");
            for (var i = 0; i < recent.Count; i++) { if (i > 0) b.Append(','); b.Append(SignalJson.Quote(recent[i])); }
            b.Append("],\"done\":[");
            for (var i = 0; i < completed.Count; i++)
            {
                if (i > 0) b.Append(',');
                b.Append('[').Append(SignalJson.Quote(completed[i].Day)).Append(',')
                    .Append(completed[i].Seconds.ToString(CultureInfo.InvariantCulture)).Append(']');
            }
            b.Append(']');
            if (currentId != null)
                b.Append(",\"start\":").Append(start.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
                    .Append(",\"last\":").Append(last.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            return b.Append('}').ToString();
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
                lock (gate) { if (!dirty) { writing = false; return; } dirty = false; text = Serialize(); savedLast = last; }
                try { storage().Replace(FileName, Encoding.UTF8.GetBytes(text)); }
                catch (Exception) { lock (gate) { writing = false; dirty = false; writeFailed = true; } return; }
            }
        }

        private void Chain(Action work) { idle = idle.ContinueWith(_ => work(), TaskScheduler.Default); }
    }

    // Reads the small JSON of the usage file: objects, arrays, strings and numbers only.
    internal static class MiniJson
    {
        internal static object Parse(string text)
        {
            var i = 0; var value = Value(text, ref i); Skip(text, ref i);
            if (i != text.Length) throw new FormatException("json");
            return value;
        }

        private static void Skip(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        private static object Value(string s, ref int i)
        {
            Skip(s, ref i);
            if (i >= s.Length) throw new FormatException("json");
            var c = s[i];
            if (c == '{')
            {
                i++; var map = new Dictionary<string, object>(StringComparer.Ordinal); Skip(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return map; }
                while (true)
                {
                    Skip(s, ref i); var key = Str(s, ref i); Skip(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException("json");
                    i++; map[key] = Value(s, ref i); Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return map; }
                    throw new FormatException("json");
                }
            }
            if (c == '[')
            {
                i++; var list = new List<object>(); Skip(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(Value(s, ref i)); Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return list; }
                    throw new FormatException("json");
                }
            }
            if (c == '"') return Str(s, ref i);
            var begin = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+')) i++;
            if (i == begin || !double.TryParse(s.Substring(begin, i - begin), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                throw new FormatException("json");
            return number;
        }

        private static string Str(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') throw new FormatException("json");
            i++; var b = new StringBuilder();
            while (i < s.Length)
            {
                var c = s[i++];
                if (c == '"') return b.ToString();
                if (c != '\\') { b.Append(c); continue; }
                if (i >= s.Length) break;
                var e = s[i++];
                switch (e)
                {
                    case '"': b.Append('"'); break;
                    case '\\': b.Append('\\'); break;
                    case '/': b.Append('/'); break;
                    case 'n': b.Append('\n'); break;
                    case 'r': b.Append('\r'); break;
                    case 't': b.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("json");
                        b.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)); i += 4; break;
                    default: throw new FormatException("json");
                }
            }
            throw new FormatException("json");
        }
    }
}

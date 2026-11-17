using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mightys.Pulse
{
    // App Control for one app: checks the rules, keeps the cache, decides, and applies scheduled boundaries by the clock.
    //
    // Fail open, always. Nothing here blocks the caller: Start and OnForeground only queue work on the thread pool. One request per
    // check, no retry loop, 3 s to the response headers and 5 s in all, at most one automatic check in 60 s. Any failure keeps the last
    // known rules (from the cache) or Normal. The request carries no app key and no other identity, and it does not depend on the
    // analytics opt-out. The app is never closed from here.
    internal sealed class ControlClient
    {
        internal static readonly TimeSpan MinCheckGap = TimeSpan.FromSeconds(60);
        internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
        // Rules that arrived this long ago still count as fresh; older rules are treated as cached rules.
        internal static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromHours(24), MinTimerDelay = TimeSpan.FromMilliseconds(10);

        private readonly object gate = new object();
        private readonly IClock clock;
        private readonly ITransport transport;
        private readonly IStorage storage;
        private readonly ControlMode mode;
        private readonly Func<bool> debug;
        private readonly Action<string> warn;
        private readonly Uri url;
        private readonly string cacheName, sdk;
        private readonly ControlContext template;
        private readonly CancellationTokenSource life = new CancellationTokenSource();
        private ControlCacheData data = new ControlCacheData();
        private ControlRules rules;
        private bool started, loaded, freshThisRun, writing, dirty;
        private DateTimeOffset? lastAttempt;
        private Task inFlight;
        private ControlState state = ControlState.Normal;
        private string signature = ControlState.Normal.Signature();
        private string continuedKey;
        private CancellationTokenSource timerWake;
        private Task startup = Task.CompletedTask, idle = Task.CompletedTask;
        private readonly TaskCompletionSource<bool> loadedSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ControlClient(string appKey, string host, ITransport transport, IStorage storage, IClock clock, ControlContext template,
            string sdk, Func<bool> debug, ControlMode mode, Action<string> warn)
        {
            this.transport = transport; this.storage = storage; this.clock = clock; this.template = template; this.sdk = sdk ?? "";
            this.debug = debug ?? (() => false); this.mode = mode; this.warn = warn ?? (_ => { });
            url = new Uri(host.TrimEnd('/') + "/api/v1/control/" + ControlIds.ForKey(appKey));
            cacheName = ControlIds.CacheName(appKey);
        }

        /// <summary>Raised on a thread pool thread, outside every lock, when the state changed.</summary>
        internal event Action<ControlState> Changed;

        internal ControlState State { get { lock (gate) return state; } }
        /// <summary>The background start (cache read plus the first check), for tests.</summary>
        internal Task Startup { get { lock (gate) return startup; } }
        /// <summary>Completes when every queued cache write has finished, for tests.</summary>
        internal Task Idle { get { lock (gate) return idle; } }
        internal int Attempts;
        /// <summary>Set by an Android platform layer when the Play In-App Updates library answered that no update is available. The decision then turns a required update into a suggestion.</summary>
        internal bool PlayUpdateNotAvailable { get; set; }

        // Off, or a transport that cannot make an identity-free request: App Control does nothing.
        private bool Active => mode != ControlMode.Off && transport is IControlTransport;

        /// <summary>Queues the first check and the scheduler. Returns at once. Does nothing in Off mode or without a capable transport.</summary>
        internal void Start()
        {
            lock (gate)
            {
                if (started || !Active || life.IsCancellationRequested) return;
                started = true;
                startup = Task.Run(StartAsync);
            }
        }

        internal void Stop()
        {
            try { life.Cancel(); } catch (Exception) { }
            lock (gate) { try { timerWake?.Cancel(); } catch (Exception) { } }
        }

        private async Task StartAsync()
        {
            try
            {
                try { LoadCache(); }
                catch (Exception) { lock (gate) loaded = true; loadedSource.TrySetResult(true); throw; }
                Evaluate();
                _ = Task.Run(RunTimerAsync);
                await CheckAsync(false).ConfigureAwait(false);
            }
            catch (Exception) { warn("App Control start failed."); }
        }

        private void LoadCache()
        {
            var read = ControlCache.Read(storage, cacheName);
            lock (gate)
            {
                data = read;
                rules = read.Body == null ? null : ControlRules.Parse(read.Body);
                if (rules == null) { data.Body = null; data.ETag = null; data.OkAt = null; }
                var version = template.AppVersion ?? "";
                if (data.Seen != version)
                {
                    if (data.Seen != null) data.UpdatedTo = version;
                    data.Seen = version;
                    MarkDirty();
                }
                loaded = true;
            }
            loadedSource.TrySetResult(true);
        }

        /// <summary>The platform reports the app came to the foreground. Counts a return from the store and checks again, at most once in 60 s.</summary>
        internal void OnForeground()
        {
            try
            {
                if (!Active) return;
                bool returned = false;
                lock (gate)
                {
                    if (!started || !loaded || life.IsCancellationRequested) return;
                    if (data.Pending)
                    {
                        data.Pending = false;
                        var version = template.AppVersion ?? "";
                        if (data.ReturnsVersion == version) data.Returns++; else { data.ReturnsVersion = version; data.Returns = 1; }
                        MarkDirty(); returned = true;
                    }
                    if (!lastAttempt.HasValue || clock.UtcNow - lastAttempt.Value >= MinCheckGap) Task.Run(() => CheckAsync(false));
                }
                if (returned) Evaluate();
            }
            catch (Exception) { warn("App Control foreground failed."); }
        }

        /// <summary>Checks now, ignoring the 60 s gap. Joins a check that is already running. Never throws.</summary>
        internal async Task CheckNowAsync()
        {
            try
            {
                if (!Active) return;
                Start();
                lock (gate) { if (!started) return; }
                await loadedSource.Task.ConfigureAwait(false);
                await CheckAsync(true).ConfigureAwait(false);
            }
            catch (Exception) { }
        }

        private Task CheckAsync(bool force)
        {
            lock (gate)
            {
                if (inFlight != null && !inFlight.IsCompleted) return inFlight;
                if (!loaded || life.IsCancellationRequested) return Task.CompletedTask;
                var now = clock.UtcNow;
                if (!force && lastAttempt.HasValue && now - lastAttempt.Value < MinCheckGap) return Task.CompletedTask;
                lastAttempt = now; Attempts++;
                inFlight = Task.Run(DoCheckAsync);
                return inFlight;
            }
        }

        private async Task DoCheckAsync()
        {
            try
            {
                string etag;
                lock (gate) etag = data.ETag ?? rules?.ETag;
                var request = TransportRequest.ForControl(url, etag);
                TransportResponse response;
                using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(life.Token))
                {
                    cancellation.CancelAfter(RequestTimeout);
                    var sending = transport.SendAsync(request, cancellation.Token) ?? Task.FromResult(new TransportResponse(0));
                    // A transport that ignores cancellation must not keep this check alive past the timeout.
                    var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (cancellation.Token.Register(() => canceled.TrySetResult(true)))
                    {
                        if (await Task.WhenAny(sending, canceled.Task).ConfigureAwait(false) != sending)
                        {
                            _ = sending.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                            return;
                        }
                        response = await sending.ConfigureAwait(false);
                    }
                }
                if (response == null) return;
                if (response.StatusCode == 304) Touch();
                else if (response.StatusCode == 200) Accept(response.Body);
                // Every other status, a failed request and a body that cannot be read keep the last known rules.
            }
            catch (Exception) { warn("App Control check failed; the last known state is kept."); }
        }

        // 304: the cached rules are current again.
        private void Touch()
        {
            lock (gate)
            {
                if (rules == null) return;
                data.OkAt = clock.UtcNow; freshThisRun = true; MarkDirty();
            }
            Evaluate(); WakeTimer();
        }

        private void Accept(string body)
        {
            var parsed = ControlRules.Parse(body);
            if (parsed == null) { warn("App Control response not understood; the last known state is kept."); return; }
            lock (gate)
            {
                rules = parsed; data.Body = body; data.ETag = parsed.ETag; data.OkAt = clock.UtcNow; freshThisRun = true;
                MarkDirty();
            }
            Evaluate(); WakeTimer();
        }

        // ---- user actions ----

        /// <summary>The address behind the main button of the current state, or null. For an update state it also counts the press, so a
        /// user who comes back three times on the same version is let through.</summary>
        internal string NoteStorePress()
        {
            lock (gate)
            {
                var current = state;
                if (current.Url == null) return null;
                if (current.Kind == ControlKind.UpdateRequired || current.Kind == ControlKind.UpdateSuggested || current.Kind == ControlKind.VersionBlocked)
                { data.Pending = true; MarkDirty(); }
                return current.Url;
            }
        }

        /// <summary>Dismisses what can be dismissed: a suggestion for 24 hours, a card for good, or (debug build) a preview lock for this run.</summary>
        internal void Dismiss()
        {
            lock (gate)
            {
                var target = state.CanDismiss ? state : null;
                if (target == null) foreach (var extra in state.Extras) if (extra.CanDismiss) { target = extra; break; }
                if (target == null) return;
                var now = clock.UtcNow;
                switch (target.Kind)
                {
                    case ControlKind.UpdateSuggested:
                        data.DismissedVersion = template.AppVersion ?? ""; data.DismissedAt = now; MarkDirty(); break;
                    case ControlKind.Announcement:
                        var id = rules?.Announcement?.Id;
                        if (id != null && !data.Shown.Contains(id))
                        {
                            if (data.Shown.Count >= ControlCacheData.MaxShown) data.Shown.RemoveAt(0);
                            data.Shown.Add(id); MarkDirty();
                        }
                        break;
                    case ControlKind.WhatsNew:
                        data.WhatsNewShown = rules?.WhatsNew?.Version; MarkDirty(); break;
                    default:
                        continuedKey = ContinueKey(target.Kind); break;
                }
            }
            Evaluate(); WakeTimer();
        }

        private string ContinueKey(ControlKind kind) => kind + ":" + (rules?.Version ?? 0);

        // ---- state ----

        private ControlContext ContextAt(DateTimeOffset now) => new ControlContext
        {
            Locales = template.Locales, AppName = template.AppName, AppVersion = template.AppVersion, Platform = template.Platform,
            BundleId = template.BundleId, ToLocal = template.ToLocal, Now = now
        };

        private bool IsFromCache(DateTimeOffset now) => !(freshThisRun && data.OkAt.HasValue && now - data.OkAt.Value < FreshWindow);

        private ControlState Compute(DateTimeOffset now)
        {
            if (rules == null) return ControlState.Normal;
            var version = template.AppVersion ?? "";
            var context = ContextAt(now);
            var input = new ControlInput
            {
                Rules = rules, Now = now, Platform = template.Platform, AppVersion = version, Debug = SafeDebug(), Sdk = sdk,
                FromCache = IsFromCache(now), CacheAgeHours = data.OkAt.HasValue ? Math.Max(0, (now - data.OkAt.Value).TotalHours) : 0,
                UpdateReturns = data.ReturnsVersion == version ? data.Returns : 0, PlayUpdateNotAvailable = PlayUpdateNotAvailable, CanBuildStoreLink = context.CanBuildStoreLink,
                SuggestDismissedHoursAgo = data.DismissedVersion == version && data.DismissedAt.HasValue ? Math.Max(0, (now - data.DismissedAt.Value).TotalHours) : (double?)null,
                ShownAnnouncements = data.Shown.ToArray(), JustUpdated = data.UpdatedTo == version && version.Length > 0,
                WhatsNewShown = rules.WhatsNew != null && data.WhatsNewShown == rules.WhatsNew.Version
            };
            var outcome = ControlDecision.Decide(input);
            // A debug build that pressed Continue on a preview lock is not shown that lock again in this run.
            if (outcome.State != ControlKind.Normal && outcome.AllowContinue && continuedKey == ContinueKey(outcome.State))
            { outcome.State = ControlKind.Normal; outcome.Banners.Remove(ControlOutcome.BannerTestBuild); }
            return ControlStateBuilder.Build(rules, outcome, context);
        }

        private bool SafeDebug() { try { return debug(); } catch (Exception) { return false; } }

        private void Evaluate()
        {
            ControlState changed;
            lock (gate) changed = Apply(clock.UtcNow);
            Raise(changed);
        }

        // Computes the state at the given moment and keeps it. Returns the new state when it differs from the old one. Call under the lock.
        private ControlState Apply(DateTimeOffset now)
        {
            var next = Compute(now);
            var sig = next.Signature();
            if (sig == signature) return null;
            signature = sig; state = next;
            return next;
        }

        private void Raise(ControlState changed)
        {
            if (changed == null) return;
            try { Changed?.Invoke(changed); } catch (Exception) { }
        }

        // ---- scheduled boundaries ----

        private void WakeTimer() { lock (gate) { try { timerWake?.Cancel(); } catch (Exception) { } } }

        private async Task RunTimerAsync()
        {
            while (!life.IsCancellationRequested)
            {
                ControlState changed;
                CancellationTokenSource wake;
                DateTimeOffset until;
                try
                {
                    lock (gate)
                    {
                        // The state and the next boundary are both worked out at this one moment.
                        var now = clock.UtcNow;
                        changed = Apply(now);
                        wake = CancellationTokenSource.CreateLinkedTokenSource(life.Token);
                        timerWake = wake;
                        until = now + NextDelay(now);
                    }
                    Raise(changed);
                    var span = until - clock.UtcNow;
                    var waiting = clock.Delay(span < TimeSpan.Zero ? TimeSpan.Zero : span, wake.Token);
                    // If the clock moved past the boundary while the wait was being set up, go round at once.
                    if (clock.UtcNow >= until) { try { wake.Cancel(); } catch (Exception) { } }
                    try { await waiting.ConfigureAwait(false); } catch (Exception) { }
                }
                catch (Exception) { warn("App Control timer failed."); await Task.Delay(1000).ConfigureAwait(false); }
            }
        }

        // Time to the next moment at which the decision can change by the clock alone.
        private TimeSpan NextDelay(DateTimeOffset now)
        {
            DateTimeOffset? next = null;
            void Consider(DateTimeOffset? at) { if (at.HasValue && at.Value > now && (!next.HasValue || at.Value < next.Value)) next = at; }
            var m = rules?.Maintenance;
            if (m != null && m.StartsAt.HasValue)
            {
                Consider(m.StartsAt.Value.AddMinutes(-m.HeadsUpMinutes)); Consider(m.StartsAt); Consider(m.EndsAt);
                Consider(m.StartsAt.Value.AddHours(ControlDecision.CacheMaintenanceHours));
            }
            Consider(rules?.Announcement?.Until);
            if (data.OkAt.HasValue)
            {
                Consider(data.OkAt.Value + FreshWindow); Consider(data.OkAt.Value.AddHours(ControlDecision.CacheLockHours));
            }
            if (data.DismissedAt.HasValue) Consider(data.DismissedAt.Value.AddHours(ControlDecision.SuggestQuietHours));
            if (!next.HasValue) return MaxTimerDelay;
            // A timer that fires a moment early only makes the next round wait the small remainder.
            var delay = next.Value - now;
            return delay > MaxTimerDelay ? MaxTimerDelay : delay < MinTimerDelay ? MinTimerDelay : delay;
        }

        // ---- cache writes: coalesced, on the thread pool, never on a public call ----

        private void MarkDirty()
        {
            dirty = true;
            if (writing) return;
            writing = true;
            idle = Task.Run(WriteLoop);
        }

        private void WriteLoop()
        {
            while (true)
            {
                byte[] bytes;
                lock (gate)
                {
                    if (!dirty) { writing = false; return; }
                    dirty = false;
                    bytes = ControlCache.Serialize(data);
                }
                try { storage.Replace(cacheName, bytes); }
                catch (Exception) { warn("App Control cache could not be saved."); }
            }
        }
    }
}

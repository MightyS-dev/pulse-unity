using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mightys.Pulse
{
    internal sealed class PulseClient
    {
        private readonly object gate = new object();
        private readonly List<QueueEntry> queue = new List<QueueEntry>();
        private readonly List<FlushWaiter> waiters = new List<FlushWaiter>();
        private readonly List<Batch> halves = new List<Batch>();
        private readonly SemaphoreSlim wake = new SemaphoreSlim(0, 1);
        private readonly SemaphoreSlim processing = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly IClock clock;
        private readonly ITransport transport;
        private readonly IStorage storage;
        private readonly PulseOptions options;
        private readonly string appKey, system;
        private readonly HashSet<string> systemKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly IExtraSystemFields extras;
        private readonly UsageTracker usage;
        private bool coldStartHandled;
        private long? launchMs;
        private readonly string appVersion, appBuild;
        private readonly Uri url, configUrl;
        private readonly SignalPolicy policy;
        private readonly object configGate = new object();
        private bool configRequested;
        private long configEpoch;
        private readonly Action<string> logger;
        private readonly Func<double> random;
        private readonly List<string> persistedIds = new List<string>();
        private Task worker = Task.CompletedTask;
        private FlushWaiter backgroundWaiter;
        private CancellationTokenSource activeRequest;
        private Batch pending;
        private bool crashImported, loaded, optOut, preferenceExplicit, preferenceRead, preferenceDirty, gzipDisabled, halted, hasJournal;
        private long sequence, preferenceVersion;
        private double interval;
        private DateTimeOffset nextFlush, nextAttempt, pauseUntil, storageRetryUntil;
        internal string QueueName { get; }
        internal string CrashName => QueueName + ".crash";
        internal string BatchName => QueueName + ".batch";
        private string PreferenceName => "opt-out";
        internal bool IsOptOut { get { lock (gate) return optOut; } }
        internal int Count { get { lock (gate) return queue.Count; } }
        /// <summary>What the cached portal settings and the options in code allow, fixed for this run.</summary>
        internal SignalPolicy Policy => policy;
        /// <summary>Foreground counters, permission statuses and the quota guards of automatic signals.</summary>
        internal AutoState Auto { get; }
        internal void MoveSessionEnd(string sessionId, DateTimeOffset timestamp) => usage?.MoveSessionEnd(sessionId, timestamp);
        private Task UsageSaved => usage?.Idle ?? Task.CompletedTask;
        /// <summary>Platform supplied time from process start to the first frame, in ms. Used on the cold start session_started.</summary>
        internal void SetLaunchMs(long ms) { lock (gate) if (!coldStartHandled && ms >= 0) launchMs = ms; }
        /// <summary>Where the platform cannot give a first frame time: called once, with the time of the first signal, for the cold start.</summary>
        internal Func<DateTimeOffset, long?> LaunchProvider { get; set; }
        /// <summary>The settings request of this launch, or a completed task when none was started. Used by tests.</summary>
        internal Task ConfigFetch { get; private set; } = Task.CompletedTask;
        /// <summary>the debug screenshot uploader (markers, retry and hourly rules).</summary>
        internal HeatmapShots Shots { get; }
        /// <summary>Called after a settings refresh of this run was applied, outside every lock. The runtime drops heatmap points when capture went off.</summary>
        internal Action ConfigRefreshed { get; set; }
        /// <summary>App Control of this app. Independent of the analytics opt-out and of the queue worker.</summary>
        internal ControlClient Control { get; }

        private static ControlContext ControlTemplate(IEnvironmentInfo environment, string appVersion)
        {
            string name = null;
            try { (environment as IExtraSystemFields)?.StaticFields?.TryGetValue("appName", out name); } catch (Exception) { }
            string ui = "";
            try { ui = System.Globalization.CultureInfo.CurrentUICulture.Name; } catch (Exception) { }
            return new ControlContext
            {
                Platform = ControlPlatform.FromOsName(environment.OsName), AppName = name ?? "", AppVersion = appVersion ?? "",
                BundleId = environment.BundleId ?? "", Locales = LocalePreference(environment, ui)
            };
        }

        // Language order for the screens: the platform language (when the environment supplies one), then the locale, then the UI culture.
        internal static string[] LocalePreference(IEnvironmentInfo environment, string uiCulture)
        {
            string platform = null;
            try { platform = (environment as IScreenLanguage)?.ScreenLanguage; } catch (Exception) { }
            return new[] { platform, environment.Locale, uiCulture };
        }

        internal PulseClient(string appKey, PulseOptions options, IClock clock, ITransport transport,
            IStorage storage, IEnvironmentInfo environment, Action<string> logger = null,
            bool runWorker = true, Func<double> random = null, bool? initialOptOut = null, UsageTracker usage = null,
            Func<DateTimeOffset, string> localDate = null, Func<bool> debugBuild = null)
        {
            Auto = new AutoState(() => storage, localDate);
            this.appKey = appKey; this.options = options.Snapshot(); this.clock = clock;
            this.options.Debug = options.Debug;
            this.transport = transport; this.storage = storage; this.logger = logger;
            this.random = random ?? new Random().NextDouble;
            // The cached portal settings are read once and hold for the whole run; a fetch in this run applies at the next launch.
            var cached = RemoteConfig.Load(storage);
            policy = new SignalPolicy(this.options, cached);
            system = SignalJson.SystemJson(environment, this.options, systemKeys, cached.HiddenKeys());
            extras = environment as IExtraSystemFields; this.usage = usage;
            appVersion = this.options.AppVersion ?? environment.AppVersion ?? ""; appBuild = environment.AppBuildNumber ?? "";
            url = new Uri(this.options.Host + "/api/v1/signals");
            configUrl = new Uri(this.options.Host + "/api/v1/config");
            Shots = new HeatmapShots(() => storage, transport, appKey, this.options.Host, clock, appVersion,
                debugBuild ?? (() => PulseInfo.DebugBuild), Warn);
            Control = new ControlClient(appKey, this.options.Host, transport, storage, clock, ControlTemplate(environment, appVersion),
                PulseInfo.SdkName + "@" + PulseInfo.Version, () => this.options.Debug, this.options.Control, Warn);
            QueueName = "queue-" + SignalJson.Hash(appKey) + ".bin";
            interval = this.options.FlushIntervalSeconds;
            nextFlush = clock.UtcNow.AddSeconds(interval);
            if (initialOptOut.HasValue)
            { optOut = initialOptOut.Value; preferenceExplicit = true; preferenceDirty = true; }
            else
            {
                // Read the one-byte preference synchronously so IsOptOut is truthful before the worker loads.
                // Otherwise a settings toggle could read false and write it over a saved opt-out.
                try { optOut = StoredOptOut(storage.Read(PreferenceName)); preferenceRead = true; }
                catch (Exception) { optOut = true; Warn("Privacy preference unreadable; collection paused until it loads."); }
            }
            if (runWorker) worker = Task.Run(RunAsync);
        }

        // Missing file means opted in; any value other than a single zero byte fails closed.
        internal static bool StoredOptOut(byte[] preference) =>
            preference != null && (preference.Length != 1 || preference[0] != 0);
        internal static bool ReadStoredOptOut(IStorage storage) => StoredOptOut(storage.Read("opt-out"));

        internal void Warn(string message)
        { if (options.Debug) try { logger?.Invoke(message); } catch (Exception) { } }

        internal void Enqueue(SignalInput input)
        {
            if (input == null) return;
            // The platform callback runs outside the client lock; it must be cheap.
            IReadOnlyDictionary<string, string> dynamicFields = null;
            try { dynamicFields = extras?.DynamicFields(); } catch (Exception) { Warn("Platform fields unavailable."); }
            lock (gate)
            {
                if (optOut || lifetime.IsCancellationRequested) return;
                var observation = usage?.Observe(input.SessionId, input.Timestamp, appVersion, appBuild, policy.Allows("pulse.app_updated"));
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                if (observation != null) foreach (var pair in observation.Fields) fields[pair.Key] = pair.Value;
                Auto.Fill(fields, input.Timestamp);
                var plain = fields.Count == 0 && dynamicFields == null ? system : SignalJson.SystemJsonFor(system, systemKeys, dynamicFields, fields);
                // One signal's system object: the shared one, plus the dropped count the first admitted signal carries and,
                // on the cold start session_started, the launch time.
                string SystemFor(bool cold)
                {
                    // A heatmap signal never carries (and so never uses up) the dropped count.
                    if (input.Type == RemoteConfig.HeatmapType) return plain;
                    var dropped = Auto.TakeDropped();
                    long? launch = cold ? ColdLaunch(input.Timestamp) : null;
                    if (dropped == 0 && !launch.HasValue) return plain;
                    var extra = new Dictionary<string, string>(fields, StringComparer.Ordinal);
                    if (dropped > 0) extra["droppedAutomatic"] = dropped.ToString(CultureInfo.InvariantCulture);
                    if (launch.HasValue) extra["launchMs"] = launch.Value.ToString(CultureInfo.InvariantCulture);
                    return SignalJson.SystemJsonFor(system, systemKeys, dynamicFields, extra);
                }
                if (observation != null)
                    foreach (var auto in observation.Signals)
                    {
                        // The first session_started of the process is the cold start, whether or not it is sent.
                        var cold = auto.Type == "pulse.session_started" && !coldStartHandled;
                        if (cold) coldStartHandled = true;
                        if (!policy.Allows(auto.Type)) continue;
                        var stamp = NextStamp(input.Timestamp);
                        if (!Auto.Admit(auto.Type, input.SessionId, stamp)) continue;
                        var own = new SignalInput { Type = auto.Type, PropsJson = auto.PropsJson ?? "{}", Timestamp = stamp,
                            SessionId = input.SessionId, UserHash = input.UserHash };
                        Add(own, SignalJson.Serialize(own, SystemFor(cold), options.Debug, options.ErrorDetail));
                    }
                if (policy.Allows(input.Type))
                {
                    input.Timestamp = NextStamp(input.Timestamp);
                    if (Auto.Admit(input.Type, input.SessionId, input.Timestamp))
                        Add(input, SignalJson.Serialize(input, SystemFor(false), options.Debug, options.ErrorDetail));
                }
                Prune();
            }
            Pulse();
        }

        // The cold start is the first session_started of this process. The platform's time wins; otherwise the provider measures to this signal.
        private long? ColdLaunch(DateTimeOffset firstSignal)
        {
            if (launchMs.HasValue) return launchMs;
            try { var value = LaunchProvider?.Invoke(firstSignal); return value.HasValue && value.Value >= 0 ? value : null; }
            catch (Exception) { return null; }
        }

        // A crash: the process may end right after this call, so the signal goes to its own small file at once and is
        // sent at the next launch. Without a working disk it stays in the queue, which is the best that is left.
        internal void EnqueueCrash(SignalInput input)
        {
            if (input == null) return;
            IReadOnlyDictionary<string, string> dynamicFields = null;
            try { dynamicFields = extras?.DynamicFields(); } catch (Exception) { }
            lock (gate)
            {
                if (optOut || lifetime.IsCancellationRequested || !policy.Allows(input.Type)) return;
                var systemJson = dynamicFields == null ? system : SignalJson.SystemJsonFor(system, systemKeys, dynamicFields, null);
                input.Timestamp = NextStamp(input.Timestamp);
                var json = SignalJson.Serialize(input, systemJson, options.Debug, options.ErrorDetail);
                try
                {
                    var saved = QueueJournal.Decode(storage.Read(CrashName), 100);
                    saved.Add(new QueueEntry { Id = Guid.NewGuid().ToString("N"), Json = json, Timestamp = input.Timestamp });
                    storage.Replace(CrashName, QueueJournal.Encode(saved, 0, true));
                }
                catch (Exception) { Add(input, json); Warn("Crash could not be saved to disk; queued instead."); }
            }
            Pulse();
        }

        // Strictly increasing capture timestamps at millisecond resolution: the later of the clock reading and the
        // previous timestamp plus one millisecond. Called under the client lock in storing order, automatic signals first.
        private long lastStampMillis = long.MinValue / 2;
        private DateTimeOffset NextStamp(DateTimeOffset reading)
        {
            var now = reading.UtcTicks / TimeSpan.TicksPerMillisecond;
            lastStampMillis = now > lastStampMillis ? now : lastStampMillis + 1;
            return new DateTimeOffset(lastStampMillis * TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        }

        private void Add(SignalInput input, string json) =>
            queue.Add(new QueueEntry { Id = Guid.NewGuid().ToString("N"), Json = json, Timestamp = input.Timestamp, Sequence = ++sequence });

        internal void SetOptOut(bool value)
        {
            lock (gate)
            {
                optOut = value; preferenceExplicit = true; preferenceDirty = true; preferenceVersion++;
                if (value)
                {
                    configEpoch++; configRequested = false; ForgetConfig();
                    try { usage?.Forget(); } catch (Exception) { }
                    try { Auto.Forget(); } catch (Exception) { }
                    try { Shots.Forget(); } catch (Exception) { }
                    queue.Clear();
                    Task.Run(() => { try { storage.Delete(CrashName); } catch (Exception) { } });
                    try { activeRequest?.Cancel(); } catch (Exception) { }
                }
            }
            if (!value) StartConfigFetch();
            Pulse();
        }

        // Opt-out deletes the cached settings with the rest. The delete runs on the thread pool, never on the public call.
        private void ForgetConfig() =>
            Task.Run(() => { lock (configGate) { try { storage.Delete(RemoteConfig.FileName); } catch (Exception) { } } });

        /// <summary>Starts the one settings request of this launch in the background. It never delays Track, Flush or the queue worker.
        /// Does nothing while opted out, after the first call, or after stop.</summary>
        internal Task StartConfigFetch()
        {
            long epoch;
            lock (gate)
            {
                if (configRequested || optOut || lifetime.IsCancellationRequested) return ConfigFetch;
                configRequested = true; epoch = configEpoch;
            }
            var task = Task.Run(() => FetchConfigAsync(epoch));
            lock (gate) ConfigFetch = task;
            return task;
        }

        private async Task FetchConfigAsync(long epoch)
        {
            try
            {
                var request = TransportRequest.ForGet(configUrl, appKey, policy.Config.ETag);
                TransportResponse response;
                using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    cancellation.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
                    var sending = transport.SendAsync(request, cancellation.Token) ?? Task.FromResult(new TransportResponse(0));
                    // A transport that ignores cancellation must not keep this task alive past the timeout.
                    var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (cancellation.Token.Register(() => canceled.TrySetResult(true)))
                    {
                        if (await Task.WhenAny(sending, canceled.Task).ConfigureAwait(false) != sending)
                        {
                            _ = sending.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                            return;
                        }
                        response = await sending.ConfigureAwait(false);
                    }
                }
                // 304, 401, any other status and a failed request keep the cache.
                if (response == null || response.StatusCode != 200) return;
                var parsed = RemoteConfig.Parse(response.Body);
                if (parsed == null) { Warn("Settings response not understood; cached settings kept."); return; }
                lock (configGate)
                {
                    lock (gate) { if (optOut || epoch != configEpoch || lifetime.IsCancellationRequested) return; }
                    try { storage.Replace(RemoteConfig.FileName, Encoding.UTF8.GetBytes(response.Body)); }
                    catch (Exception) { Warn("Settings could not be saved."); }
                    policy.Live = parsed;
                }
                try { ConfigRefreshed?.Invoke(); } catch (Exception) { }
            }
            catch (Exception) { Warn("Settings request failed; cached settings kept."); }
        }

        internal Task FlushAsync(CancellationToken cancellationToken = default, bool background = false)
        {
            if (cancellationToken.IsCancellationRequested) return Task.CompletedTask;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var waiter = new FlushWaiter { Completion = completion };
            lock (gate)
            {
                // A stopped client has no worker left to complete a waiter.
                if (lifetime.IsCancellationRequested) return Task.CompletedTask;
                waiter.Sequence = sequence; waiters.Add(waiter);
                if (background) backgroundWaiter = waiter;
            }
            Pulse();
            return AwaitWithoutThrow(waiter, cancellationToken);
        }

        // A later transition can add signals while an earlier background flush is still in flight.
        // Keep the same bounded wait, but include the newly queued signals in its delivery target.
        internal bool ExtendBackgroundFlush()
        {
            lock (gate)
            {
                if (backgroundWaiter == null || backgroundWaiter.Completion.Task.IsCompleted) return false;
                backgroundWaiter.Sequence = sequence;
            }
            Pulse();
            return true;
        }

        internal int WaiterCount { get { lock (gate) return waiters.Count; } }

        // Completes every outstanding Flush without delivery. The Unity transport calls this when the main thread
        // has not dispatched a request for several seconds, which happens when game code blocks on Flush().Wait().
        internal void ReleaseWaiters()
        {
            lock (gate) { foreach (var waiter in waiters) waiter.Completion.TrySetResult(true); waiters.Clear(); }
        }

        private async Task AwaitWithoutThrow(FlushWaiter waiter, CancellationToken cancellationToken)
        {
            try
            {
                using (cancellationToken.Register(() =>
                {
                    lock (gate) { waiters.Remove(waiter); waiter.Completion.TrySetResult(true); }
                    Pulse();
                })) await waiter.Completion.Task.ConfigureAwait(false);
            }
            catch (Exception) { }
        }

        private void Pulse() { try { wake.Release(); } catch (SemaphoreFullException) { } }

        private async Task RunAsync()
        {
            while (!lifetime.IsCancellationRequested)
            {
                await ProcessAsync().ConfigureAwait(false);
                TimeSpan delay;
                lock (gate)
                {
                    var now = clock.UtcNow;
                    DateTimeOffset due;
                    if (storageRetryUntil > now) due = storageRetryUntil;
                    else if (!loaded || preferenceDirty) due = now.AddSeconds(1);
                    else if (optOut || halted || queue.Count == 0) due = DateTimeOffset.MaxValue;
                    else
                    {
                        due = pending != null ? nextAttempt :
                            (waiters.Count > 0 || halves.Count > 0 || queue.Count >= options.MaxBatchSize ? now : nextFlush);
                        if (pauseUntil > due) due = pauseUntil;
                    }
                    delay = due == DateTimeOffset.MaxValue ? Timeout.InfiniteTimeSpan : due - now;
                    if (delay != Timeout.InfiniteTimeSpan && delay < TimeSpan.FromMilliseconds(1)) delay = TimeSpan.FromMilliseconds(1);
                    // Task.Delay has a finite upper bound; wake again to re-evaluate a distant Retry-After.
                    if (delay > TimeSpan.FromDays(1)) delay = TimeSpan.FromDays(1);
                }
                using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    try
                    {
                        var timer = clock.Delay(delay, waiting.Token);
                        var signaled = wake.WaitAsync(waiting.Token);
                        await Task.WhenAny(timer, signaled).ConfigureAwait(false);
                        waiting.Cancel();
                        try { await Task.WhenAll(timer, signaled).ConfigureAwait(false); } catch (OperationCanceledException) { }
                    }
                    catch (OperationCanceledException) { }
                }
            }
        }

        // Also used by deterministic tests without a running timer or real sleeps.
        internal async Task ProcessAsync(bool force = false)
        {
            await processing.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!force && clock.UtcNow < storageRetryUntil) return;
                Load();
                while (!lifetime.IsCancellationRequested)
                {
                    // Finish a queued session-end write before attempting delivery, even if that signal is lost.
                    await UsageSaved.ConfigureAwait(false);
                    Persist();
                    Batch batch;
                    CancellationTokenSource requestCancellation;
                    lock (gate)
                    {
                        CompleteWaiters();
                        if (optOut || halted || queue.Count == 0) { pending = null; halves.Clear(); return; }
                        var now = clock.UtcNow;
                        if (now < pauseUntil || (pending != null && now < nextAttempt)) return;
                        if (pending == null)
                        {
                            if (!force && waiters.Count == 0 && halves.Count == 0 && queue.Count < options.MaxBatchSize && now < nextFlush) return;
                            while (halves.Count > 0 && !Live(halves[0])) halves.RemoveAt(0);
                            // Track can run during disk IO. Only entries in the completed disk snapshot may be sent.
                            var durable = new HashSet<string>(persistedIds, StringComparer.Ordinal);
                            var candidates = queue.Where(e => durable.Contains(e.Id)).ToArray();
                            if (candidates.Length == 0) continue;
                            if (halves.Count > 0) { pending = halves[0]; halves.RemoveAt(0); } else pending = MakeBatch(candidates, true);
                        }
                        if (!Live(pending)) { pending = null; continue; }
                        batch = pending;
                        requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        activeRequest = requestCancellation;
                    }
                    TransportResponse response;
                    Task<TransportResponse> sending = null;
                    var abandoned = false;
                    try
                    {
                        SavePending();
                        requestCancellation.Token.ThrowIfCancellationRequested();
                        sending = transport.SendAsync(new TransportRequest(url, appKey, batch.Wire, batch.Gzip, batch.Id),
                            requestCancellation.Token) ?? Task.FromResult(new TransportResponse(0));
                        // Cancellation (opt-out or stop) must not depend on the transport noticing it. The Unity
                        // transport needs the main thread, which may be blocked during quit or a domain reload.
                        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        using (requestCancellation.Token.Register(() => canceled.TrySetResult(true)))
                        {
                            if (await Task.WhenAny(sending, canceled.Task).ConfigureAwait(false) != sending)
                            {
                                abandoned = true;
                                response = new TransportResponse(0);
                            }
                            else response = await sending.ConfigureAwait(false) ?? new TransportResponse(0);
                        }
                    }
                    catch (Exception) { response = new TransportResponse(0); Warn("Transport failed; batch retained."); }
                    finally
                    {
                        lock (gate) { if (activeRequest == requestCancellation) activeRequest = null; }
                        var source = requestCancellation;
                        if (abandoned && sending != null)
                            _ = sending.ContinueWith(t => { _ = t.Exception; source.Dispose(); }, CancellationToken.None,
                                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        else source.Dispose();
                    }
                    lock (gate)
                    {
                        if (optOut) { pending = null; halves.Clear(); continue; }
                        var status = response.StatusCode;
                        if (status >= 200 && status <= 299)
                        {
                            Remove(batch); pending = null;
                            var advice = ServerAdvice.Parse(response.Body);
                            var receivedAt = clock.UtcNow;
                            ApplyAdvice(advice, receivedAt);
                            nextFlush = receivedAt.AddSeconds(interval);
                        }
                        else if (status == 415 && batch.Gzip)
                        { gzipDisabled = true; batch.Gzip = false; batch.Wire = batch.Json; nextAttempt = clock.UtcNow; }
                        else if (status == 401)
                        { halted = true; Warn("Application key rejected; sending stopped until restart."); }
                        else if (status == 413 && batch.Gzip)
                        {
                            // A 413 for a compressed body can be the server's gzip expansion ratio limit. The JSON is
                            // always within the 1 MiB limit, so retry it uncompressed once before splitting.
                            batch.Gzip = false; batch.Wire = batch.Json; nextAttempt = clock.UtcNow;
                        }
                        else if (status == 400 || status == 413)
                        {
                            pending = null;
                            if (batch.Entries.Count > 1)
                            {
                                // Split until single bad signals are isolated. Each split halves the batch, so one
                                // batch of n signals costs at most 2n - 1 requests and log2(1000) levels.
                                var middle = batch.Entries.Count / 2;
                                halves.Insert(0, MakeBatch(batch.Entries.GetRange(middle, batch.Entries.Count - middle), false));
                                halves.Insert(0, MakeBatch(batch.Entries.GetRange(0, middle), false));
                            }
                            else { Remove(batch); Warn("Invalid signal dropped after isolation."); }
                        }
                        else
                        {
                            // Network failure, 408, 425, 429, 5xx and any other unexpected status (including 3xx
                            // with redirects disabled and unknown 4xx): keep the exact batch and back off.
                            batch.Failures++;
                            var exponential = Math.Min(300, Math.Pow(2, Math.Min(9, batch.Failures - 1)));
                            var seconds = Math.Max(1, Math.Min(300, exponential * (0.5 + random())));
                            nextAttempt = clock.UtcNow.AddSeconds(seconds);
                            var retryAfter = RetryAfter(response.RetryAfter);
                            if (retryAfter > nextAttempt) nextAttempt = retryAfter;
                            if (status == 429 || status == 503)
                            {
                                var advice = ServerAdvice.Parse(response.Body);
                                var receivedAt = clock.UtcNow;
                                ApplyAdvice(advice, receivedAt);
                                if (advice.RecommendedSendIntervalSeconds.HasValue)
                                {
                                    var recommendedRetry = receivedAt.AddSeconds(interval);
                                    if (recommendedRetry > nextAttempt) nextAttempt = recommendedRetry;
                                }
                            }
                            if (status != 0 && status != 408 && status != 425 && status != 429 && (status < 500 || status > 599))
                                Warn("Unexpected response status; batch retained with backoff.");
                        }
                    }
                }
            }
            catch (Exception)
            {
                lock (gate)
                {
                    storageRetryUntil = clock.UtcNow.AddSeconds(1);
                    // With unusable storage, a Flush that has nothing in memory to deliver must not wait for the disk.
                    for (var i = waiters.Count - 1; i >= 0; i--)
                        if (!queue.Any(e => e.Sequence <= waiters[i].Sequence)) { waiters[i].Completion.TrySetResult(true); waiters.RemoveAt(i); }
                }
                Warn("Queue operation failed; retained data will be retried.");
            }
            finally { processing.Release(); }
        }

        private void ApplyAdvice(ServerAdvice advice, DateTimeOffset receivedAt)
        {
            if (advice.RecommendedSendIntervalSeconds.HasValue) interval = advice.RecommendedSendIntervalSeconds.Value;
            if (advice.Pause.HasValue)
                pauseUntil = advice.Pause.Value ? receivedAt.AddSeconds(interval) : DateTimeOffset.MinValue;
        }

        // Server-provided retry deadlines (Retry-After, persisted retry times) never exceed one hour from now.
        internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromHours(1);

        private DateTimeOffset RetryAfter(string value)
        {
            var now = clock.UtcNow;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
                !double.IsNaN(seconds) && !double.IsInfinity(seconds) && seconds >= 0)
                return now.AddSeconds(Math.Min(seconds, MaxRetryDelay.TotalSeconds));
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                return DateTimeOffset.MinValue;
            return date - now > MaxRetryDelay ? now + MaxRetryDelay : date;
        }

        private void Load()
        {
            if (loaded) return;
            var preference = preferenceRead ? null : storage.Read(PreferenceName);
            var pendingBytes = storage.Read(BatchName);
            // Damaged records are dropped from the first invalid one onward; earlier records survive.
            var restored = QueueJournal.Decode(storage.Read(QueueName), options.MaxQueueSize, out var intact);
            if (!intact) Warn("Damaged queue tail discarded; earlier signals retained.");
            byte[] crashBytes = null;
            try { crashBytes = storage.Read(CrashName); } catch (Exception) { Warn("Saved crash file unreadable."); }
            var crashed = QueueJournal.Decode(crashBytes, 100);
            lock (gate)
            {
                if (!preferenceExplicit && !preferenceRead) optOut = StoredOptOut(preference);
                preferenceRead = true;
                if (optOut) { queue.Clear(); if (crashBytes != null) try { storage.Delete(CrashName); } catch (Exception) { } }
                else
                {
                    // A crash saved by the last run is sent now, after the queue that was there before it.
                    restored.AddRange(crashed); crashImported = crashBytes != null;
                    var newEntries = queue.ToArray(); queue.Clear();
                    queue.AddRange(restored); queue.AddRange(newEntries);
                    // Restored entries precede all current-process entries, including existing flush targets.
                    var offset = restored.Count;
                    for (var i = 0; i < restored.Count; i++) restored[i].Sequence = i + 1;
                    foreach (var entry in newEntries) entry.Sequence += offset;
                    foreach (var waiter in waiters) waiter.Sequence += offset;
                    sequence += offset;
                    if (restored.Count > 0) nextFlush = clock.UtcNow;
                }
                Prune();
                if (!optOut && pendingBytes != null)
                {
                    try { RestorePending(pendingBytes); }
                    catch (Exception) { pending = null; Warn("Corrupt in-flight batch discarded; queue retained."); }
                }
                loaded = true;
            }
            // Always compact on load so stale and partially retained journals never accumulate.
            hasJournal = false;
        }

        private void Persist()
        {
            bool writePreference, preferenceValue; long version;
            QueueEntry[] entries;
            lock (gate)
            {
                Prune(); entries = queue.ToArray(); writePreference = preferenceDirty;
                preferenceValue = optOut; version = preferenceVersion;
            }
            if (writePreference)
            {
                storage.Replace(PreferenceName, new[] { preferenceValue ? (byte)1 : (byte)0 });
                lock (gate) { if (version == preferenceVersion) preferenceDirty = false; }
            }
            var append = hasJournal && persistedIds.Count <= entries.Length;
            if (append)
                for (var i = 0; i < persistedIds.Count; i++)
                    if (persistedIds[i] != entries[i].Id) { append = false; break; }
            if (!append) storage.Replace(QueueName, QueueJournal.Encode(entries, 0, true));
            else if (entries.Length > persistedIds.Count)
                storage.Append(QueueName, QueueJournal.Encode(entries, persistedIds.Count, false));
            persistedIds.Clear(); persistedIds.AddRange(entries.Select(e => e.Id)); hasJournal = true;
            // The saved crashes are in the queue file now.
            if (crashImported) { storage.Delete(CrashName); crashImported = false; }
            SavePending();
        }

        // Persist the exact wire bytes and batch membership before each attempt. A restart may have new
        // queued events, but must not merge those into an ambiguous earlier delivery.
        private void SavePending()
        {
            Batch batch;
            lock (gate) batch = !optOut && pending != null && Live(pending) ? pending : null;
            if (batch == null) { storage.Delete(BatchName); return; }
            byte[] payload;
            using (var output = new MemoryStream())
            using (var writer = new BinaryWriter(output, Encoding.UTF8, true))
            {
                writer.Write("MSB2"); writer.Write(batch.Id); writer.Write(batch.CanSplit); writer.Write(batch.Gzip); writer.Write(batch.Failures);
                writer.Write(nextAttempt.UtcTicks); writer.Write(batch.Entries.Count);
                foreach (var entry in batch.Entries) writer.Write(entry.Id);
                writer.Write(batch.Json.Length); writer.Write(batch.Json); writer.Write(batch.Wire.Length); writer.Write(batch.Wire);
                writer.Flush(); payload = output.ToArray();
            }
            using (var sha = SHA256.Create())
            using (var output = new MemoryStream())
            {
                output.Write(payload, 0, payload.Length); var checksum = sha.ComputeHash(payload); output.Write(checksum, 0, checksum.Length);
                storage.Replace(BatchName, output.ToArray());
            }
        }

        private void RestorePending(byte[] bytes)
        {
            if (bytes.Length < 32 || bytes.Length > SignalJson.MaxBodyBytes * 3) throw new InvalidDataException();
            using (var sha = SHA256.Create())
            {
                var checksum = sha.ComputeHash(bytes, 0, bytes.Length - 32);
                for (var i = 0; i < 32; i++) if (checksum[i] != bytes[bytes.Length - 32 + i]) throw new InvalidDataException();
            }
            using (var input = new MemoryStream(bytes, 0, bytes.Length - 32, false))
            using (var reader = new BinaryReader(input, new UTF8Encoding(false, true), true))
            {
                var magic = reader.ReadString();
                if (magic != "MSB1" && magic != "MSB2") throw new InvalidDataException();
                // A batch saved by an older build has no id; it gets a new one (a duplicate beats a lost batch).
                var savedId = magic == "MSB2" ? reader.ReadString() : NewBatchId();
                if (!BatchIdFormat.IsValid(savedId)) throw new InvalidDataException();
                var batch = new Batch { Id = savedId, CanSplit = reader.ReadBoolean(), Gzip = reader.ReadBoolean(), Failures = reader.ReadInt32(), Entries = new List<QueueEntry>() };
                var retryAt = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
                var count = reader.ReadInt32();
                if (count < 1 || count > 1000 || batch.Failures < 0) throw new InvalidDataException();
                var byId = queue.ToDictionary(e => e.Id, StringComparer.Ordinal);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < count; i++)
                {
                    var id = reader.ReadString();
                    if (!byId.TryGetValue(id, out var entry) || !seen.Add(id)) throw new InvalidDataException();
                    batch.Entries.Add(entry);
                }
                batch.Json = ReadBody(reader); batch.Wire = ReadBody(reader);
                if (input.Position != input.Length) throw new InvalidDataException();
                var expected = Encoding.UTF8.GetBytes("[" + string.Join(",", batch.Entries.Select(e => e.Json)) + "]");
                if (!expected.SequenceEqual(batch.Json)) throw new InvalidDataException();
                if (!batch.Gzip && !batch.Wire.SequenceEqual(batch.Json)) throw new InvalidDataException();
                // A valid checksum covers compressed bytes produced by this SDK. No decompression of disk input is needed.
                // A deadline beyond the cap came from an older build or a clock that was later corrected; retry now.
                var now = clock.UtcNow;
                pending = batch; nextAttempt = retryAt - now > MaxRetryDelay ? now : retryAt;
            }
        }

        private static byte[] ReadBody(BinaryReader reader)
        {
            var length = reader.ReadInt32();
            if (length < 1 || length > SignalJson.MaxBodyBytes + 65536) throw new InvalidDataException();
            var body = reader.ReadBytes(length);
            if (body.Length != length) throw new EndOfStreamException();
            return body;
        }

        private void Prune()
        {
            var now = clock.UtcNow;
            var oldest = now.AddDays(-options.MaxQueueAgeDays);
            queue.RemoveAll(e => e.Timestamp < oldest || e.Timestamp > now.AddHours(1));
            if (queue.Count > options.MaxQueueSize) queue.RemoveRange(0, queue.Count - options.MaxQueueSize);
        }

        private bool Live(Batch batch) => batch.Entries.All(e => queue.Contains(e));
        private void Remove(Batch batch)
        { var ids = new HashSet<string>(batch.Entries.Select(e => e.Id), StringComparer.Ordinal); queue.RemoveAll(e => ids.Contains(e.Id)); }

        /// <summary>16 bytes from the secure generator as 32 lowercase hex characters.</summary>
        internal static string NewBatchId()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var chars = new char[32];
            for (var i = 0; i < 16; i++) { chars[2 * i] = "0123456789abcdef"[bytes[i] >> 4]; chars[2 * i + 1] = "0123456789abcdef"[bytes[i] & 15]; }
            return new string(chars);
        }

        private Batch MakeBatch(IReadOnlyList<QueueEntry> source, bool canSplit)
        {
            var selected = new List<QueueEntry>(); var b = new StringBuilder("["); var size = 2;
            for (var i = 0; i < source.Count && selected.Count < options.MaxBatchSize; i++)
            {
                var entry = source[i]; var bytes = Encoding.UTF8.GetByteCount(entry.Json) + (selected.Count == 0 ? 0 : 1);
                if (size + bytes > SignalJson.MaxBodyBytes) break;
                if (selected.Count > 0) b.Append(',');
                b.Append(entry.Json); selected.Add(entry); size += bytes;
            }
            if (selected.Count == 0) throw new InvalidDataException("Oversized queued signal.");
            var json = Encoding.UTF8.GetBytes(b.Append(']').ToString());
            var batch = new Batch { Id = NewBatchId(), Entries = selected, Json = json, Wire = json, CanSplit = canSplit };
            if (options.Gzip && !gzipDisabled && json.Length > 1024)
            {
                using (var output = new MemoryStream())
                {
                    using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true)) gzip.Write(json, 0, json.Length);
                    batch.Wire = output.ToArray(); batch.Gzip = true;
                }
            }
            nextAttempt = clock.UtcNow;
            return batch;
        }

        private void CompleteWaiters()
        {
            for (var i = waiters.Count - 1; i >= 0; i--)
                if (!preferenceDirty && (halted || !queue.Any(e => e.Sequence <= waiters[i].Sequence)))
                { waiters[i].Completion.TrySetResult(true); waiters.RemoveAt(i); }
        }

        internal async Task StopAsync()
        {
            Control.Stop();
            lifetime.Cancel(); Pulse();
            try { await worker.ConfigureAwait(false); } catch (Exception) { }
            await processing.WaitAsync().ConfigureAwait(false);
            try { Load(); Persist(); lock (gate) { foreach (var waiter in waiters) waiter.Completion.TrySetResult(true); waiters.Clear(); } }
            catch (Exception) { Warn("Final queue save failed."); }
            finally { processing.Release(); }
        }

        private static class BatchIdFormat
        {
            internal static bool IsValid(string id) => id != null && id.Length == 32 && id.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        }
        private sealed class Batch
        { internal string Id; internal List<QueueEntry> Entries; internal byte[] Json, Wire; internal bool Gzip, CanSplit; internal int Failures; }
        private sealed class FlushWaiter
        { internal long Sequence; internal TaskCompletionSource<bool> Completion; }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Mightys.Pulse
{
    /// <summary>Constants that identify the SDK and its defaults.</summary>
    public static class PulseInfo
    {
        /// <summary>Product name.</summary>
        public const string DisplayName = "MightyS Pulse";
        /// <summary>SDK identifier sent with each batch.</summary>
        public const string SdkName = "pulse-unity";
        /// <summary>SDK version.</summary>
        public const string Version = "0.9.0";
        /// <summary>Default ingest host.</summary>
        public const string DefaultHost = "https://in.mightys.dev";
        internal static bool DebugBuild;
    }

    /// <summary>Delivery and queue options. Values are copied and clamped at initialization.</summary>
    public sealed class PulseOptions
    {
        private bool? debug;
        // The server rejects signals older than 30 days; one day of margin covers delivery delay and clock skew.
        internal const double MaxQueueAgeLimitDays = 29;
        /// <summary>Absolute HTTP(S) base URL of the ingest host, without user info, query or fragment. Default <c>https://in.mightys.dev</c>.</summary>
        public string Host { get; set; } = PulseInfo.DefaultHost;
        /// <summary>Seconds between batches after the first one. Default 20, capped at 3600.</summary>
        public double FlushIntervalSeconds { get; set; } = 20;
        /// <summary>Signals per batch. Default 100, clamped to 1 through 1000.</summary>
        public int MaxBatchSize { get; set; } = 100;
        /// <summary>Maximum queued signals; the oldest are dropped first. Default 1000.</summary>
        public int MaxQueueSize { get; set; } = 1000;
        /// <summary>Maximum age of a queued signal in days. Default 7, capped at 29.</summary>
        public double MaxQueueAgeDays { get; set; } = 7;
        /// <summary>Compress request bodies larger than 1024 bytes. Default true.</summary>
        public bool Gzip { get; set; } = true;
        /// <summary>Marks signals as test data and enables diagnostic logging through the platform logger.</summary>
        public bool Debug { get => debug ?? PulseInfo.DebugBuild; set => debug = value; }
        /// <summary>Send provisional app identity fields. Leave disabled.</summary>
        public bool SendAppIdentity { get; set; }
        /// <summary>Overrides the detected application version.</summary>
        public string AppVersion { get; set; }

        internal PulseOptions Snapshot()
        {
            if (!Uri.TryCreate(Host, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "https" && uri.Scheme != "http") ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("Host must be an absolute HTTP or HTTPS URL.");
            var snapshot = new PulseOptions
            {
                Host = uri.AbsoluteUri.TrimEnd('/'),
                FlushIntervalSeconds = FinitePositive(FlushIntervalSeconds, 20, 3600),
                MaxBatchSize = Math.Max(1, Math.Min(1000, MaxBatchSize)),
                MaxQueueSize = Math.Max(1, Math.Min(1000000, MaxQueueSize)),
                MaxQueueAgeDays = FinitePositive(MaxQueueAgeDays, 7, MaxQueueAgeLimitDays),
                Gzip = Gzip, Debug = Debug, SendAppIdentity = SendAppIdentity, AppVersion = AppVersion
            };
            snapshot.debug = debug;
            return snapshot;
        }

        private static double FinitePositive(double value, double fallback, double maximum) =>
            double.IsNaN(value) || double.IsInfinity(value) || value <= 0 ? fallback : Math.Min(value, maximum);
    }

    /// <summary>Time source, replaceable for tests.</summary>
    public interface IClock
    {
        /// <summary>Current UTC time.</summary>
        DateTimeOffset UtcNow { get; }
        /// <summary>Completes after the delay or when cancelled.</summary>
        Task Delay(TimeSpan delay, CancellationToken cancellationToken);
    }

    /// <summary>Clock backed by the system time.</summary>
    public sealed class SystemClock : IClock
    {
        /// <summary>Current UTC time.</summary>
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        /// <summary>Completes after the delay or when cancelled.</summary>
        public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
    }

    // Storage calls are serialized on the background worker, never on a public API call.
    /// <summary>Durable byte storage for the offline queue.</summary>
    public interface IStorage
    {
        /// <summary>Returns the bytes stored under the name, or null when absent.</summary>
        byte[] Read(string name);
        /// <summary>Appends bytes durably.</summary>
        void Append(string name, byte[] bytes);
        /// <summary>Atomically replaces the contents.</summary>
        void Replace(string name, byte[] bytes);
        /// <summary>Deletes the entry if it exists.</summary>
        void Delete(string name);
    }

    /// <summary>Snapshot of non-identifying platform metadata attached to signals.</summary>
    public interface IEnvironmentInfo
    {
        /// <summary>Platform class, for example desktop or mobile.</summary>
        string Platform { get; }
        /// <summary>Operating system name.</summary>
        string OsName { get; }
        /// <summary>Operating system version number.</summary>
        string OsVersion { get; }
        /// <summary>Application version.</summary>
        string AppVersion { get; }
        /// <summary>Application build number, empty when unknown.</summary>
        string AppBuildNumber { get; }
        /// <summary>Device model, empty when not supplied.</summary>
        string DeviceModel { get; }
        /// <summary>UI locale name.</summary>
        string Locale { get; }
        /// <summary>Application identity. Provisional; the .NET environment leaves it empty.</summary>
        string BundleId { get; }
        /// <summary>Signing certificate digest. Provisional; the .NET environment leaves it empty.</summary>
        string SigningCertSha256 { get; }
    }

    /// <summary>One HTTP request prepared by the delivery core.</summary>
    public sealed class TransportRequest
    {
        /// <summary>Destination URL.</summary>
        public Uri Url { get; }
        /// <summary>App key for the request header.</summary>
        public string AppKey { get; }
        /// <summary>Wire bytes of the body, already compressed when <see cref="IsGzip"/> is true.</summary>
        public byte[] Body { get; }
        /// <summary>Whether the body is gzip compressed.</summary>
        public bool IsGzip { get; }
        /// <summary>Whole-request timeout in seconds, scaled with the body size.</summary>
        // Whole-request timeout scaled with the wire size: 10 s plus 1 s per 16 KiB (a slow 128 kbit/s uplink),
        // capped at 90 s, so a full 1 MiB batch on a slow network is not cut off and retried forever.
        public int TimeoutSeconds => TimeoutFor(Body?.Length ?? 0);
        /// <summary>Creates a request.</summary>
        public TransportRequest(Uri url, string appKey, byte[] body, bool isGzip)
        { Url = url; AppKey = appKey; Body = body; IsGzip = isGzip; }
        internal static int TimeoutFor(int bytes) => Math.Min(90, 10 + Math.Max(0, bytes) / 16384);
    }

    /// <summary>Result of one HTTP request.</summary>
    public sealed class TransportResponse
    {
        /// <summary>HTTP status code.</summary>
        public int StatusCode { get; }
        /// <summary>Response body text, possibly empty.</summary>
        public string Body { get; }
        /// <summary>Raw Retry-After header value, or null.</summary>
        public string RetryAfter { get; }
        /// <summary>Creates a response.</summary>
        public TransportResponse(int statusCode, string body = "", string retryAfter = null)
        { StatusCode = statusCode; Body = body; RetryAfter = retryAfter; }
    }

    /// <summary>Sends prepared requests to the ingest host.</summary>
    public interface ITransport
    {
        /// <summary>Sends one request and returns the response.</summary>
        Task<TransportResponse> SendAsync(TransportRequest request, CancellationToken cancellationToken);
    }
}

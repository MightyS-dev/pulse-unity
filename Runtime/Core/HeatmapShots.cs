using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mightys.Pulse
{
    internal sealed class HeatmapShotTicket
    {
        internal string Screen, Bucket, Orientation;
    }

    // Heatmaps: debug screenshot: one picture per (screen, bucket, orientation, appVersion) per install, only from debug builds.
    // The platform takes and encodes the picture; this class decides whether one is wanted and uploads the raw bytes.
    internal sealed class HeatmapShots
    {
        internal const int MaxBytes = 2 * 1024 * 1024, MaxPerHour = 20, MaxMarkers = 1000;
        internal const string FileName = "heatmap-shots";
        private readonly object gate = new object();
        private readonly Func<IStorage> storage;
        private readonly ITransport transport;
        private readonly string appKey, host, appVersion;
        private readonly IClock clock;
        private readonly Func<bool> debugBuild;
        private readonly Action<string> warn;
        private readonly List<string> markers = new List<string>();
        private readonly List<DateTimeOffset> attempts = new List<DateTimeOffset>();
        private readonly HashSet<string> inFlight = new HashSet<string>(StringComparer.Ordinal);
        private DateTimeOffset retryUntil = DateTimeOffset.MinValue;
        private bool loaded;

        internal HeatmapShots(Func<IStorage> storage, ITransport transport, string appKey, string host, IClock clock,
            string appVersion, Func<bool> debugBuild, Action<string> warn)
        {
            this.storage = storage; this.transport = transport; this.appKey = appKey; this.host = host; this.clock = clock;
            this.appVersion = appVersion ?? ""; this.debugBuild = debugBuild ?? (() => false); this.warn = warn ?? (_ => { });
        }

        private string KeyOf(string screen, string bucket, string orientation) =>
            SignalJson.Hash(screen + "|" + bucket + "|" + orientation + "|" + appVersion);

        /// <summary>Opt-out: forgets the markers and deletes the file.</summary>
        internal void Forget()
        {
            lock (gate) { markers.Clear(); attempts.Clear(); loaded = true; retryUntil = DateTimeOffset.MinValue; }
            Task.Run(() => { try { storage().Delete(FileName); } catch (Exception) { } });
        }

        /// <summary>Whether a picture of this view is wanted now. Reads the marker file on first use, so call it off the main thread.</summary>
        internal HeatmapShotTicket Ticket(string screen, double width, double height)
        {
            try
            {
                if (string.IsNullOrEmpty(screen) || !debugBuild() || !(width > 0) || !(height > 0)) return null;
                var bucket = HeatmapCapture.BucketOf(width); var orientation = HeatmapCapture.OrientationOf(width, height);
                var key = KeyOf(screen, bucket, orientation);
                lock (gate)
                {
                    Load();
                    var now = clock.UtcNow;
                    if (markers.Contains(key) || inFlight.Contains(key) || now < retryUntil || Recent(now) >= MaxPerHour) return null;
                }
                return new HeatmapShotTicket { Screen = screen, Bucket = bucket, Orientation = orientation };
            }
            catch (Exception) { return null; }
        }

        internal Task<HeatmapShotTicket> TicketAsync(string screen, double width, double height) =>
            Task.Run(() => Ticket(screen, width, height));

        private int Recent(DateTimeOffset now)
        {
            attempts.RemoveAll(t => now - t >= TimeSpan.FromHours(1));
            return attempts.Count;
        }

        private void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                var bytes = storage().Read(FileName);
                if (bytes == null) return;
                foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n'))
                    if (line.Length == 64 && !markers.Contains(line)) markers.Add(line);
            }
            catch (Exception) { }
        }

        private void Mark(string key)
        {
            string text;
            lock (gate)
            {
                Load();
                if (!markers.Contains(key)) markers.Add(key);
                while (markers.Count > MaxMarkers) markers.RemoveAt(0);
                text = string.Join("\n", markers);
            }
            try { storage().Replace(FileName, Encoding.UTF8.GetBytes(text)); } catch (Exception) { warn("Heatmap marker could not be saved."); }
        }

        private static bool Raster(string type) => type == "image/png" || type == "image/jpeg" || type == "image/webp";

        /// <summary>Uploads one picture. True only when the server stored it (201). Never throws.</summary>
        internal async Task<bool> UploadAsync(HeatmapShotTicket ticket, byte[] bytes, string contentType, CancellationToken cancellationToken = default)
        {
            string key = null;
            try
            {
                if (ticket == null || bytes == null || bytes.Length == 0 || bytes.Length > MaxBytes || !Raster(contentType) || !debugBuild()) return false;
                key = KeyOf(ticket.Screen, ticket.Bucket, ticket.Orientation);
                lock (gate)
                {
                    Load();
                    var now = clock.UtcNow;
                    if (markers.Contains(key) || inFlight.Contains(key) || now < retryUntil || Recent(now) >= MaxPerHour) return false;
                    inFlight.Add(key); attempts.Add(now);
                }
                var url = new Uri(host + "/api/v1/heatmap/screenshot?screen=" + Uri.EscapeDataString(ticket.Screen) +
                    "&bucket=" + ticket.Bucket + "&orientation=" + ticket.Orientation +
                    "&appVersion=" + Uri.EscapeDataString(appVersion) + "&debug=true");
                var response = await transport.SendAsync(TransportRequest.ForUpload(url, appKey, bytes, contentType), cancellationToken).ConfigureAwait(false)
                    ?? new TransportResponse(0);
                var status = response.StatusCode;
                if (status == 201) { Mark(key); return true; }
                if (status == 403 || status == 415 || status == 413 || status == 422) { Mark(key); return false; }
                if (status == 429 || status == 503 || status == 507)
                {
                    var seconds = 60.0;
                    if (double.TryParse(response.RetryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
                        seconds = Math.Min(3600, parsed);
                    lock (gate) retryUntil = clock.UtcNow.AddSeconds(seconds);
                }
                return false;
            }
            catch (Exception) { return false; }
            finally { if (key != null) lock (gate) inFlight.Remove(key); }
        }
    }
}

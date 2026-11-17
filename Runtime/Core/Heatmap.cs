using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mightys.Pulse
{
    // Taps folded into one buffer per (screen, bucket, orientation) and sent as pulse.heatmap signals.
    // The platform reports a tap with its position in logical points and three privacy flags; this class applies every rule.
    // Locking: the lock is never held while calling out (enabled, session, emit), so the runtime lock can be taken freely.
    internal sealed class HeatmapCapture
    {
        internal const int MaxPointsPerSignal = 200, MaxPointsPerSession = 1000;
        internal const double RageSeconds = 1, RageRadius = 30, MaxY = 100000;
        internal const string PendingSession = "pending";
        private readonly object gate = new object();
        private readonly IClock clock;
        private readonly Func<bool> enabled;
        private readonly Func<DateTimeOffset, string> sessionAt;
        private readonly Action<string> emit;
        private readonly List<string> points = new List<string>();
        private string screen, bufferScreen, bufferBucket, bufferOrientation, sessionId;
        private int sessionCount, clusterCount;
        private DateTimeOffset clusterTime;
        private double clusterX, clusterY;

        internal HeatmapCapture(IClock clock, Func<bool> enabled, Func<DateTimeOffset, string> sessionAt, Action<string> emit)
        { this.clock = clock; this.enabled = enabled; this.sessionAt = sessionAt; this.emit = emit; }

        internal string Screen { get { lock (gate) return screen; } }

        internal static string BucketOf(double width) => width < 600 ? "phone" : width < 1024 ? "tablet" : "desktop";
        internal static string OrientationOf(double width, double height) => height >= width ? "portrait" : "landscape";

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        /// <summary>Sets the current screen (null when none is known). The buffer of the old screen is sent first.</summary>
        internal void SetScreen(string name)
        {
            string payload;
            lock (gate)
            {
                if (string.Equals(name, screen, StringComparison.Ordinal)) return;
                payload = Take(); screen = name; clusterCount = 0;
            }
            Emit(payload);
        }

        /// <summary>One tap or click. x and y are in logical points from the top left; width and height are the screen size in the same unit.</summary>
        internal void OnTap(double x, double y, double width, double height, bool keyboardVisible, bool inputTarget, bool ignored)
        {
            if (keyboardVisible || inputTarget || ignored) return;
            if (!Finite(x) || !Finite(y) || !Finite(width) || !Finite(height) || width <= 0 || height <= 0) return;
            if (!SafeEnabled()) { Drop(); return; }
            var now = clock.UtcNow;
            string session; try { session = sessionAt(now); } catch (Exception) { return; }
            var fraction = Math.Round(Math.Min(1, Math.Max(0, x / width)), 3, MidpointRounding.AwayFromZero);
            var top = Math.Round(Math.Min(MaxY, Math.Max(0, y)), 1, MidpointRounding.AwayFromZero);
            var bucket = BucketOf(width); var orientation = OrientationOf(width, height);
            string payload = null;
            lock (gate)
            {
                if (screen == null) return;
                if (sessionId != session) { sessionId = session; sessionCount = 0; }
                if (sessionCount >= MaxPointsPerSession) return;
                if (points.Count > 0 && (bucket != bufferBucket || orientation != bufferOrientation)) payload = Take();
                if (points.Count == 0) { bufferScreen = screen; bufferBucket = bucket; bufferOrientation = orientation; }
                var px = fraction * width;
                var kind = "tap";
                if (clusterCount > 0 && (now - clusterTime).TotalSeconds <= RageSeconds &&
                    Math.Sqrt((px - clusterX) * (px - clusterX) + (top - clusterY) * (top - clusterY)) <= RageRadius)
                {
                    clusterCount++;
                    if (clusterCount >= 3) kind = "rage";
                }
                else { clusterCount = 1; clusterTime = now; clusterX = px; clusterY = top; }
                points.Add("{\"x\":" + fraction.ToString("0.###", CultureInfo.InvariantCulture) + ",\"y\":" +
                    top.ToString("0.#", CultureInfo.InvariantCulture) + ",\"kind\":\"" + kind + "\"}");
                sessionCount++;
                if (points.Count >= MaxPointsPerSignal) payload = payload == null ? Take() : payload + "\n" + Take();
            }
            if (payload != null) foreach (var one in payload.Split('\n')) Emit(one);
        }

        /// <summary>Sends the open buffer, if any.</summary>
        internal void Flush() { string payload; lock (gate) payload = Take(); Emit(payload); }

        /// <summary>Forgets the open buffer without sending it (switched off or opt-out).</summary>
        internal void Drop() { lock (gate) { points.Clear(); clusterCount = 0; } }

        /// <summary>After a heatmap signal started or joined a session, the point count follows that session.</summary>
        internal void AdoptSession(string id) { lock (gate) if (sessionId == PendingSession) sessionId = id; }

        private bool SafeEnabled() { try { return enabled(); } catch (Exception) { return false; } }

        private string Take()
        {
            if (points.Count == 0) return null;
            var b = new StringBuilder("{\"screen\":").Append(SignalJson.Quote(bufferScreen)).Append(",\"bucket\":\"").Append(bufferBucket)
                .Append("\",\"orientation\":\"").Append(bufferOrientation).Append("\",\"points\":[").Append(string.Join(",", points)).Append("]}");
            points.Clear();
            return b.ToString();
        }

        private void Emit(string payload)
        {
            if (payload == null) return;
            try { if (SafeEnabled()) emit(payload); } catch (Exception) { }
        }
    }
}

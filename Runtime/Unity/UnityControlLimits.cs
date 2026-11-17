using System;

namespace Mightys.Pulse
{
    // Limits of the App Control request on the Unity platform, kept free of UnityEngine so the .NET tests cover them.
    internal static class UnityControlLimits
    {
        internal const int HeadersSeconds = 3, TotalSeconds = 5, BodyBytes = 65536;

        // The status the core sees for the control request: 0 (a failed request) for a connection or data error and for a body over the
        // limit, otherwise the HTTP status. The core keeps what it knew on 0.
        internal static int Status(bool connectionOrDataError, bool tooLarge, long responseCode) =>
            connectionOrDataError || tooLarge ? 0 : (int)responseCode;
    }

    // Counts the bytes of a download and says when the limit is passed, so the download can be stopped without reading the rest.
    internal sealed class ControlBodyLimiter
    {
        private readonly int limit;
        internal ControlBodyLimiter(int limit) { this.limit = limit; }

        internal int Total { get; private set; }
        internal bool Over { get; private set; }

        /// <summary>A length announced by the server. A value over the limit marks the body as too large at once.</summary>
        internal void Declared(long length) { if (length > limit) Over = true; }

        /// <summary>Counts a received chunk. Returns false once the limit is passed (then the download should stop).</summary>
        internal bool Add(int bytes)
        {
            if (Over) return false;
            if (bytes < 0 || bytes > limit - Total) { Over = true; return false; }
            Total += bytes;
            return true;
        }
    }
}

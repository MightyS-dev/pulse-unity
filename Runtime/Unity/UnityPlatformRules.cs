using System;
using System.Collections.Generic;

namespace Mightys.Pulse
{
    // 0.9.4 platform rules without UnityEngine types, so the .NET tests cover them.

    // How UnityTransport sends one request: the settings request is a GET with the app key, If-None-Match when the core
    // gives one, and no body; signals are a POST with the JSON body and, from 0.9.5, X-Pulse-Batch when the core cut a
    // batch id (the server then stores a batch sent again after a lost answer only once). Never X-Pulse-Batch on the GET.
    internal sealed class UnityRequestShape
    {
        internal string Method;
        internal byte[] Body;
        internal readonly List<KeyValuePair<string, string>> Headers = new List<KeyValuePair<string, string>>();

        internal static UnityRequestShape For(TransportRequest request)
        {
            var shape = new UnityRequestShape { Method = request.IsGet ? "GET" : "POST", Body = request.IsGet ? null : request.Body };
            shape.Headers.Add(new KeyValuePair<string, string>("X-App-Key", request.AppKey));
            if (request.IsGet)
            {
                if (!string.IsNullOrEmpty(request.IfNoneMatch)) shape.Headers.Add(new KeyValuePair<string, string>("If-None-Match", request.IfNoneMatch));
                return shape;
            }
            shape.Headers.Add(new KeyValuePair<string, string>("Content-Type", "application/json"));
            if (request.IsGzip) shape.Headers.Add(new KeyValuePair<string, string>("Content-Encoding", "gzip"));
            if (!string.IsNullOrEmpty(request.BatchId)) shape.Headers.Add(new KeyValuePair<string, string>("X-Pulse-Batch", request.BatchId));
            return shape;
        }

        // The status the core sees: 0 for a connection or data processing error, else the HTTP status (304 with an empty
        // body is a valid answer to the settings request).
        internal static int Status(bool connectionOrDataError, long responseCode) => connectionOrDataError ? 0 : (int)responseCode;
    }

    // The scene hook is installed at most once, only after Pulse.Initialize created the client (so the code option and
    // the cached portal settings are known), and only when screen views are enabled for this run.
    internal sealed class ScreenHookGate
    {
        private volatile bool initialized;
        private bool decided;

        // Called by the client factory, inside Pulse.Initialize.
        internal void Initialized() { initialized = true; }

        // Main thread, every frame: true exactly once, when the hook should be installed now.
        internal bool TryDecide(Func<bool> enabled)
        {
            if (decided || !initialized) return false;
            decided = true;
            try { return enabled(); } catch (Exception) { return false; }
        }
    }
}

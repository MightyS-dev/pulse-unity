using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace Mightys.Pulse
{
    internal sealed class UnityTransport : ITransport
    {
        // If the main thread does not pick up a request within this time it is blocked, most likely by game code
        // calling Pulse.Flush().Wait() or .Result. The request then fails fast as a network error (batch retained)
        // and outstanding flush waits are released instead of hanging forever.
        private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(5);
        private readonly SynchronizationContext mainThread;
        private readonly Action mainThreadBlocked;
        internal UnityTransport(SynchronizationContext mainThread, Action mainThreadBlocked = null)
        { this.mainThread = mainThread; this.mainThreadBlocked = mainThreadBlocked; }
        public Task<TransportResponse> SendAsync(TransportRequest request, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<TransportResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dispatched = 0;
            _ = Task.Delay(DispatchTimeout).ContinueWith(_ =>
            {
                if (Interlocked.Exchange(ref dispatched, 1) != 0) return;
                completion.TrySetResult(new TransportResponse(0));
                try { mainThreadBlocked?.Invoke(); } catch (Exception) { }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            mainThread.Post(_ =>
            {
                if (Interlocked.Exchange(ref dispatched, 1) != 0) return;
                UnityWebRequest web = null;
                CancellationTokenRegistration registration = default;
                try
                {
                    if (cancellationToken.IsCancellationRequested) { completion.TrySetResult(new TransportResponse(0)); return; }
                    web = new UnityWebRequest(request.Url.AbsoluteUri, "POST");
                    web.uploadHandler = new UploadHandlerRaw(request.Body);
                    web.downloadHandler = new DownloadHandlerBuffer();
                    web.timeout = request.TimeoutSeconds;
                    web.redirectLimit = 0;
                    web.SetRequestHeader("X-App-Key", request.AppKey);
                    web.SetRequestHeader("Content-Type", "application/json");
                    if (request.IsGzip) web.SetRequestHeader("Content-Encoding", "gzip");
                    var active = web;
                    registration = cancellationToken.Register(() => mainThread.Post(__ =>
                    { if (!completion.Task.IsCompleted) try { active.Abort(); } catch (Exception) { } }, null));
                    var operation = web.SendWebRequest();
                    operation.completed += __ =>
                    {
                        try
                        {
                            var status = active.result == UnityWebRequest.Result.ConnectionError ||
                                active.result == UnityWebRequest.Result.DataProcessingError ? 0 : (int)active.responseCode;
                            completion.TrySetResult(new TransportResponse(status, active.downloadHandler.text, active.GetResponseHeader("Retry-After")));
                        }
                        catch (Exception) { completion.TrySetResult(new TransportResponse(0)); }
                        finally { registration.Dispose(); active.Dispose(); }
                    };
                }
                catch (Exception) { registration.Dispose(); web?.Dispose(); completion.TrySetResult(new TransportResponse(0)); }
            }, null);
            return completion.Task;
        }
    }
}

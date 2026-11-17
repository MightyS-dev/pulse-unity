using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace Mightys.Pulse
{
    internal sealed class UnityTransport : ITransport, IControlTransport
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
                ControlBodyHandler control = null;
                CancellationTokenRegistration registration = default;
                try
                {
                    if (cancellationToken.IsCancellationRequested) { completion.TrySetResult(new TransportResponse(0)); return; }
                    // 0.9.4: the settings request is a GET without a body (UnityRequestShape); signals stay a POST.
                    var shape = UnityRequestShape.For(request);
                    web = new UnityWebRequest(request.Url.AbsoluteUri, shape.Method);
                    if (shape.Body != null) web.uploadHandler = new UploadHandlerRaw(shape.Body);
                    // 0.9.7 App Control: the answer is read through a handler that stops the download past 65536 bytes.
                    if (request.IsControl) web.downloadHandler = control = new ControlBodyHandler(new ControlBodyLimiter(UnityControlLimits.BodyBytes));
                    else web.downloadHandler = new DownloadHandlerBuffer();
                    web.timeout = request.TimeoutSeconds;
                    web.redirectLimit = 0;
                    foreach (var header in shape.Headers) web.SetRequestHeader(header.Key, header.Value);
                    var active = web;
                    registration = cancellationToken.Register(() => mainThread.Post(__ =>
                    { if (!completion.Task.IsCompleted) try { active.Abort(); } catch (Exception) { } }, null));
                    if (request.IsControl) WatchHeaders(active, request.ResponseHeadersSeconds, completion);
                    var operation = web.SendWebRequest();
                    operation.completed += __ =>
                    {
                        try
                        {
                            var failed = active.result == UnityWebRequest.Result.ConnectionError || active.result == UnityWebRequest.Result.DataProcessingError;
                            if (control != null)
                            {
                                var status = UnityControlLimits.Status(failed, control.Limiter.Over, active.responseCode);
                                completion.TrySetResult(new TransportResponse(status, status == 200 ? control.Text : ""));
                                return;
                            }
                            var code = UnityRequestShape.Status(failed, active.responseCode);
                            completion.TrySetResult(new TransportResponse(code, active.downloadHandler?.text ?? "", active.GetResponseHeader("Retry-After")));
                        }
                        catch (Exception) { completion.TrySetResult(new TransportResponse(0)); }
                        finally { registration.Dispose(); active.Dispose(); }
                    };
                }
                catch (Exception) { registration.Dispose(); web?.Dispose(); completion.TrySetResult(new TransportResponse(0)); }
            }, null);
            return completion.Task;
        }
        // The response headers must arrive within the deadline (3 s for App Control); the whole request has its own timeout.
        private void WatchHeaders(UnityWebRequest watched, int seconds, TaskCompletionSource<TransportResponse> completion)
        {
            if (seconds <= 0) return;
            _ = Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ => mainThread.Post(__ =>
            {
                try { if (!completion.Task.IsCompleted && !watched.isDone && watched.responseCode == 0) watched.Abort(); } catch (Exception) { }
            }, null), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    // Collects the body of the App Control answer and refuses more than the limit: the download stops at once and the text is empty.
    internal sealed class ControlBodyHandler : DownloadHandlerScript
    {
        internal readonly ControlBodyLimiter Limiter;
        private readonly MemoryStream buffer = new MemoryStream();
        internal ControlBodyHandler(ControlBodyLimiter limiter) : base(new byte[4096]) { Limiter = limiter; }
        protected override void ReceiveContentLengthHeader(ulong contentLength) { Limiter.Declared(contentLength > long.MaxValue ? long.MaxValue : (long)contentLength); }
        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength <= 0) return true;
            if (!Limiter.Add(dataLength)) return false;
            buffer.Write(data, 0, dataLength);
            return true;
        }
        internal string Text => Limiter.Over ? "" : Encoding.UTF8.GetString(buffer.ToArray());
        protected override byte[] GetData() => Limiter.Over ? new byte[0] : buffer.ToArray();
        protected override string GetText() => Text;
    }
}

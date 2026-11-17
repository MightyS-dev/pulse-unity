using System;
using System.Collections.Generic;

namespace Mightys.Pulse
{
    // 0.9.7 error capture rules without UnityEngine types, so the .NET platform tests cover them. PulseLifecycle installs
    // the hooks once after Pulse.Initialize, only when Pulse.CaptureErrorsEnabled, and removes them at shutdown:
    // - the main path wraps Debug.unityLogger.logHandler: LogException hands the Exception object to the core (kind error)
    //   and ALWAYS forwards to the handler that was there before, so the console and other tools see the same output;
    // - Application.logMessageReceived for LogType.Exception is only the fallback for an exception that did not pass the
    //   wrapper (no Exception object): Pulse.CaptureLog reads the type and the frames from the condition and stack text;
    //   the same exception is never sent twice;
    // - AppDomain.UnhandledException (kind crash when the runtime is terminating) and TaskScheduler.UnobservedTaskException
    //   (kind error) cover threads outside the main thread.
    // Nothing here throws, and an error raised while capturing is ignored (the core also guards against re-entry).
    internal sealed class ErrorHooks
    {
        internal const string Crash = "crash", Error = "error";
        private static readonly TimeSpan SeenWindow = TimeSpan.FromSeconds(2);
        private readonly Action<Exception, string> capture;
        private readonly Action<string, string, string, IReadOnlyList<string>> captureLog;
        // The text fallback skips the logger's own leading frames (Debug.LogException and the Logger it calls).
        internal static readonly IReadOnlyList<string> LoggerFramePrefixes = new[] { "UnityEngine.Debug", "UnityEngine.Logger" };
        private readonly Func<DateTime> now;
        private readonly object gate = new object();
        private readonly Queue<KeyValuePair<string, DateTime>> seen = new Queue<KeyValuePair<string, DateTime>>();
        [ThreadStatic] private static int forwarding;
        private volatile bool active;

        internal ErrorHooks(Action<Exception, string> capture, Action<string, string, string, IReadOnlyList<string>> captureLog, Func<DateTime> now = null)
        {
            this.capture = capture; this.captureLog = captureLog;
            this.now = now ?? (() => DateTime.UtcNow);
        }

        internal bool Active => active;
        internal void Activate() { active = true; }
        // After removal the wrapper (if another handler wrapped it meanwhile) only forwards.
        internal void Deactivate() { active = false; }

        // The log handler wrapper. forward is the original handler's LogException.
        internal void LogException(Exception exception, Action forward)
        {
            if (active && exception != null)
            {
                try { Remember(Condition(exception)); capture(exception, Error); }
                catch (Exception) { }
            }
            forwarding++;
            try { forward?.Invoke(); }
            finally { forwarding--; }
        }

        // Application.logMessageReceived: only LogType.Exception, only when the wrapper did not already send it.
        internal void LogMessage(string condition, string stackTrace, bool isException)
        {
            if (!active || !isException || forwarding > 0) return;
            try
            {
                if (WasSeen(condition)) return;
                captureLog(condition ?? "", stackTrace ?? "", Error, LoggerFramePrefixes);
            }
            catch (Exception) { }
        }

        // AppDomain.CurrentDomain.UnhandledException: the process ends when the runtime says it is terminating.
        internal void Unhandled(object exceptionObject, bool isTerminating)
        {
            if (!active) return;
            try { if (exceptionObject is Exception exception) capture(exception, isTerminating ? Crash : Error); }
            catch (Exception) { }
        }

        // TaskScheduler.UnobservedTaskException: a faulted task nobody awaited. Not marked observed (behavior unchanged).
        internal void Unobserved(Exception exception)
        {
            if (!active || exception == null) return;
            try { capture(exception, Error); }
            catch (Exception) { }
        }

        // Unity writes an exception as "TypeName: message" (the condition of logMessageReceived).
        internal static string Condition(Exception exception)
        {
            try { return exception.GetType().Name + ": " + exception.Message; }
            catch (Exception) { return ""; }
        }

        private void Remember(string condition)
        {
            lock (gate)
            {
                Trim();
                seen.Enqueue(new KeyValuePair<string, DateTime>(condition, now()));
                while (seen.Count > 32) seen.Dequeue();
            }
        }

        private bool WasSeen(string condition)
        {
            lock (gate)
            {
                Trim();
                foreach (var entry in seen) if (string.Equals(entry.Key, condition, StringComparison.Ordinal)) return true;
                return false;
            }
        }

        private void Trim()
        {
            var limit = now() - SeenWindow;
            while (seen.Count > 0 && seen.Peek().Value < limit) seen.Dequeue();
        }
    }
}

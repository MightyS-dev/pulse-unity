using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Mightys.Pulse
{
    internal static class UnityUsage
    {
        // Session statistics and the automatic signals (new install, app updated, session started) for one client. The
        // usage file is read now unless the user opted out, so the first signal does not wait for storage. A new install is
        // the install id being created by this process.
        internal static UsageTracker Start(IStorage storage, InstallId installId, bool? optOut, Func<DateTimeOffset, string> localDate = null)
        {
            var usage = new UsageTracker(() => storage, localDate, installId.ConsumeCreated);
            try { if (optOut != true && !PulseClient.ReadStoredOptOut(storage)) usage.Prime(); } catch (Exception) { }
            return usage;
        }
    }

    // Random anonymous install id: 16 secure random bytes as 32 lowercase hex characters, kept in one small file next
    // to the offline queue. Only its SHA-256 hash is ever sent. It is read once at start, created when missing, and
    // forgotten on opt-out. Storage trouble never throws: the id then lives in memory for this process and writing is
    // not retried. The read and the first write happen at start; later writes and deletes run on the thread pool, in order.
    internal sealed class InstallId
    {
        internal const string FileName = "install-id";
        private readonly object gate = new object();
        private readonly Func<IStorage> storage;
        private string id;
        private bool resolved, createdNow;
        private Task idle = Task.CompletedTask;

        internal InstallId(Func<IStorage> storage) { this.storage = storage; }

        /// <summary>Completes when every queued write or delete has finished. Used by tests.</summary>
        internal Task Idle { get { lock (gate) return idle; } }

        /// <summary>Reads or creates the id now, so the first signal does not wait for storage.</summary>
        internal void Prime()
        {
            lock (gate) { if (id == null) id = Resolve(true); }
        }

        /// <summary>SHA-256 of the install id, using the same function as <c>SetUser</c>.</summary>
        internal string Hash()
        {
            lock (gate)
            {
                if (id == null) id = Resolve(false);
                return SignalJson.Hash(id);
            }
        }

        /// <summary>True once when this process created the id (nothing was stored before), then false until a new id is created.</summary>
        internal bool ConsumeCreated()
        {
            lock (gate)
            {
                if (id == null) id = Resolve(false);
                var result = createdNow; createdNow = false; return result;
            }
        }

        /// <summary>Opt-out: forgets the id and deletes it. The next signal after opt-out is turned off gets a new one.</summary>
        internal void Forget()
        {
            lock (gate)
            {
                id = null; resolved = true; createdNow = false; // nothing to read back; the next Hash creates a new id
                Chain(() => { try { storage().Delete(FileName); } catch (Exception) { } });
            }
        }

        private string Resolve(bool writeNow)
        {
            if (!resolved)
            {
                resolved = true;
                try { var stored = Parse(storage().Read(FileName)); if (stored != null) return stored; }
                catch (Exception) { }
            }
            var created = Create(); createdNow = true;
            if (writeNow) Write(created);
            else Chain(() => { lock (gate) { if (id != created) return; } Write(created); });
            return created;
        }

        private void Write(string value)
        { try { storage().Replace(FileName, Encoding.ASCII.GetBytes(value)); } catch (Exception) { } }

        private void Chain(Action work) { idle = idle.ContinueWith(_ => work(), TaskScheduler.Default); }

        private static string Create()
        {
            var bytes = new byte[16];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            var text = new StringBuilder(32);
            foreach (var b in bytes) text.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return text.ToString();
        }

        private static string Parse(byte[] stored)
        {
            if (stored == null || stored.Length != 32) return null;
            foreach (var b in stored) if (!((b >= (byte)'0' && b <= (byte)'9') || (b >= (byte)'a' && b <= (byte)'f'))) return null;
            return Encoding.ASCII.GetString(stored);
        }
    }
}

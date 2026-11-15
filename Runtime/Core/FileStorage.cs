using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Mightys.Pulse
{
    /// <summary>File based <see cref="IStorage"/> that keeps one file per name inside a directory.</summary>
    public class FileStorage : IStorage
    {
        private readonly string directory;
        /// <summary>Creates storage rooted at the directory, which is created on first use.</summary>
        public FileStorage(string directory) { this.directory = directory; }
        private string PathFor(string name)
        {
            if (name != Path.GetFileName(name)) throw new ArgumentException("Invalid storage name.");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, name);
        }
        /// <summary>Returns the file bytes, or null when the file does not exist.</summary>
        public byte[] Read(string name) { var path = PathFor(name); return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        /// <summary>Appends bytes and flushes them to disk.</summary>
        public void Append(string name, byte[] bytes)
        {
            using (var stream = new FileStream(PathFor(name), FileMode.Append, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }
        /// <summary>Replaces the file through a temporary file.</summary>
        public void Replace(string name, byte[] bytes)
        {
            var path = PathFor(name);
            var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        /// <summary>Deletes the file if it exists.</summary>
        public void Delete(string name) { var path = PathFor(name); if (File.Exists(path)) File.Delete(path); }
    }

    internal sealed class QueueEntry
    {
        internal string Id, Json;
        internal DateTimeOffset Timestamp;
        internal long Sequence;
    }

    // Every append has a length and SHA-256 checksum. Recovery rule: keep every record before the first record whose
    // length, checksum or contents are invalid; drop that record and everything after it. A torn tail (process killed
    // mid-append) therefore loses only the partial record, while a corrupted byte in the middle loses the affected
    // record and the later records, which cannot be trusted to start at a valid boundary. A missing or wrong header
    // discards the whole journal. The caller compacts the recovered records with an atomic Replace.
    internal static class QueueJournal
    {
        private static readonly byte[] Header = Encoding.ASCII.GetBytes("MSQ1\n");
        internal static byte[] Encode(IReadOnlyList<QueueEntry> entries, int start, bool header)
        {
            using (var output = new MemoryStream())
            using (var writer = new BinaryWriter(output, Encoding.UTF8, true))
            using (var sha = SHA256.Create())
            {
                if (header) writer.Write(Header);
                for (var i = start; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    byte[] payload;
                    using (var record = new MemoryStream())
                    using (var recordWriter = new BinaryWriter(record, Encoding.UTF8, true))
                    {
                        recordWriter.Write(entry.Timestamp.UtcTicks);
                        recordWriter.Write(entry.Id);
                        recordWriter.Write(entry.Json);
                        recordWriter.Flush(); payload = record.ToArray();
                    }
                    writer.Write(payload.Length); writer.Write(payload); writer.Write(sha.ComputeHash(payload));
                }
                writer.Flush(); return output.ToArray();
            }
        }
        internal static List<QueueEntry> Decode(byte[] bytes, int maximum) => Decode(bytes, maximum, out _);

        // Never throws for damaged input. intact is false when any bytes were dropped.
        internal static List<QueueEntry> Decode(byte[] bytes, int maximum, out bool intact)
        {
            var entries = new List<QueueEntry>();
            intact = true;
            if (bytes == null) return entries;
            if (bytes.Length < Header.Length) { intact = false; return entries; }
            for (var i = 0; i < Header.Length; i++) if (bytes[i] != Header[i]) { intact = false; return entries; }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var strict = new UTF8Encoding(false, true);
            using (var sha = SHA256.Create())
            {
                var position = Header.Length;
                while (position < bytes.Length)
                {
                    var entry = ReadRecord(bytes, ref position, sha, strict);
                    if (entry == null || !ids.Add(entry.Id)) { intact = false; break; }
                    entries.Add(entry);
                    // Bound retained memory while still checking every record in a large journal.
                    if (entries.Count > maximum) entries.RemoveAt(0);
                }
            }
            return entries;
        }

        private static QueueEntry ReadRecord(byte[] bytes, ref int position, SHA256 sha, Encoding strict)
        {
            if (bytes.Length - position < 4) return null;
            // BinaryWriter stores Int32 little-endian on every platform.
            var length = bytes[position] | (bytes[position + 1] << 8) | (bytes[position + 2] << 16) | (bytes[position + 3] << 24);
            var start = position + 4;
            if (length <= 0 || length > SignalJson.MaxBodyBytes || (long)bytes.Length - start < length + 32L) return null;
            var actual = sha.ComputeHash(bytes, start, length);
            for (var i = 0; i < 32; i++) if (bytes[start + length + i] != actual[i]) return null;
            try
            {
                using (var record = new MemoryStream(bytes, start, length, false))
                using (var reader = new BinaryReader(record, strict, true))
                {
                    var entry = new QueueEntry { Timestamp = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero),
                        Id = reader.ReadString(), Json = reader.ReadString() };
                    if (record.Position != record.Length || !Guid.TryParseExact(entry.Id, "N", out _)) return null;
                    position = start + length + 32;
                    return entry;
                }
            }
            catch (Exception) { return null; }
        }
    }
}

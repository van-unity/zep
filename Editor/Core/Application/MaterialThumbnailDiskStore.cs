using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Zep.Core.Application
{
    // Persistence format for rendered material thumbnails: one file per
    // material GUID under Library/com.van-unity.zep/thumbs/.
    // Raw RGBA32 pixels, not PNG — a warm-start load must fit inside the
    // budgeted update tick, and PNG decode costs milliseconds on the main
    // thread while a raw memcpy costs a fraction of one.
    //
    // Pure System.IO by design: no AssetDatabase calls in here, so writes can
    // run on a background thread and tests can exercise the format with plain
    // temp files. Hash validation against the live project is the provider's
    // job; this module only stores and returns what it was given.
    //
    // Follows PersistentAssetIndexCache's contract: magic + version header,
    // temp-file + File.Move atomic writes, and any read failure returns false
    // so the caller falls back to a fresh render.
    public static class MaterialThumbnailDiskStore
    {
        private static readonly object WriteGate = new object();
        private static bool _writesStopped;

        public static void StopWritesForPackageRemoval()
        {
            // Wait for any current write, then refuse queued write-behind jobs.
            lock (WriteGate)
            {
                _writesStopped = true;
            }
        }
        public readonly struct Record
        {
            public readonly string MaterialHash;
            public readonly string[] DependencyPaths;
            public readonly string[] DependencyHashes;
            public readonly int Width;
            public readonly int Height;
            public readonly byte[] Pixels;

            public Record(
                string materialHash,
                string[] dependencyPaths,
                string[] dependencyHashes,
                int width,
                int height,
                byte[] pixels)
            {
                MaterialHash = materialHash;
                DependencyPaths = dependencyPaths;
                DependencyHashes = dependencyHashes;
                Width = width;
                Height = height;
                Pixels = pixels;
            }
        }

        // ASCII "ZPMT" (Zep material thumbnail).
        private static readonly byte[] MagicBytes = { (byte)'Z', (byte)'P', (byte)'M', (byte)'T' };
        private const byte Version = 1;
        private const int MaxDimension = 1024;
        public const int MaxDependencies = 64;
        // Reject files larger than any legitimate record before parsing:
        // corrupt length prefixes inside the file can then at worst allocate
        // file-sized garbage, never gigabytes.
        private const long MaxFileBytes = 16L * 1024 * 1024;

        public static string ThumbsDirectory =>
            Path.Combine("Library", "com.van-unity.zep", "thumbs");

        public static string PathForGuid(string guid) =>
            Path.Combine(ThumbsDirectory, guid + ".bct");

        public static bool TryRead(string filePath, out Record record)
        {
            return TryRead(filePath, pixelBuffer: null, out record);
        }

        // pixelBuffer, when non-null and exactly the record's pixel size, is
        // filled and returned inside the Record instead of a fresh allocation
        // — the warm-scroll path loads up to a dozen 256KB thumbnails per
        // second and must not produce that much GC garbage (the caller copies
        // the pixels into a texture before the next load reuses the buffer).
        public static bool TryRead(string filePath, byte[] pixelBuffer, out Record record)
        {
            record = default;
            try
            {
                if (!File.Exists(filePath))
                {
                    return false;
                }

                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (fs.Length > MaxFileBytes)
                {
                    return false;
                }
                using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);

                for (var i = 0; i < MagicBytes.Length; i++)
                {
                    if (br.ReadByte() != MagicBytes[i])
                    {
                        return false;
                    }
                }

                if (br.ReadByte() != Version)
                {
                    return false;
                }

                var materialHash = br.ReadString();

                var depCount = br.ReadInt32();
                if (depCount < 0 || depCount > MaxDependencies)
                {
                    return false;
                }

                var depPaths = depCount == 0 ? Array.Empty<string>() : new string[depCount];
                var depHashes = depCount == 0 ? Array.Empty<string>() : new string[depCount];
                for (var i = 0; i < depCount; i++)
                {
                    depPaths[i] = br.ReadString();
                    depHashes[i] = br.ReadString();
                }

                var width = br.ReadInt32();
                var height = br.ReadInt32();
                if (width < 1 || width > MaxDimension || height < 1 || height > MaxDimension)
                {
                    return false;
                }

                var pixelLength = br.ReadInt32();
                if (pixelLength != width * height * 4)
                {
                    return false;
                }

                byte[] pixels;
                if (pixelBuffer != null && pixelBuffer.Length == pixelLength)
                {
                    pixels = pixelBuffer;
                    var read = 0;
                    while (read < pixelLength)
                    {
                        var chunk = br.Read(pixels, read, pixelLength - read);
                        if (chunk <= 0)
                        {
                            return false;
                        }
                        read += chunk;
                    }
                }
                else
                {
                    pixels = br.ReadBytes(pixelLength);
                    if (pixels.Length != pixelLength)
                    {
                        return false;
                    }
                }

                record = new Record(materialHash, depPaths, depHashes, width, height, pixels);
                return true;
            }
            catch (IOException) // covers EndOfStreamException from truncated files
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (FormatException) // corrupt 7-bit-encoded string length prefix
            {
                return false;
            }
            catch (ArgumentOutOfRangeException) // negative/insane corrupted length values
            {
                return false;
            }
        }

        // Atomic per-file write. Safe to call from a background thread:
        // distinct materials never share a file, and a same-file race is
        // first-Move-wins (File.Move cannot overwrite) — the loser's Move
        // throws and its temp file is removed in the finally, so no orphan
        // survives. Failures are swallowed — the disk cache is an
        // accelerator, never a source of truth; the worst outcome of a lost
        // write is one extra render next session. Records that cannot
        // roundtrip (dependency count over the read cap) are refused rather
        // than written-and-rejected-forever.
        public static void Write(string filePath, in Record record)
        {
            lock (WriteGate)
            {
                if (!_writesStopped)
                {
                    WriteRecord(filePath, record);
                }
            }
        }

        private static void WriteRecord(string filePath, in Record record)
        {
            var depCount = record.DependencyPaths?.Length ?? 0;
            if (depCount > MaxDependencies)
            {
                return;
            }

            string tempPath = null;
            try
            {
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                tempPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false))
                {
                    bw.Write(MagicBytes);
                    bw.Write(Version);
                    bw.Write(record.MaterialHash ?? string.Empty);

                    bw.Write(depCount);
                    for (var i = 0; i < depCount; i++)
                    {
                        bw.Write(record.DependencyPaths[i] ?? string.Empty);
                        bw.Write(record.DependencyHashes[i] ?? string.Empty);
                    }

                    bw.Write(record.Width);
                    bw.Write(record.Height);
                    bw.Write(record.Pixels.Length);
                    bw.Write(record.Pixels);
                }

                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }

                File.Move(tempPath, filePath);
                tempPath = null;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            finally
            {
                if (tempPath != null)
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        // Session-start housekeeping, run once on a background thread: sweeps
        // stale temp files (crashed/reloaded writes) and bounds total cache
        // size by deleting the oldest thumbnails first. This is also how
        // entries for deleted materials eventually leave the disk — their
        // GUID is unresolvable at delete time, so nothing removes them
        // eagerly, but they stop being touched and age out here.
        public static void Prune(string directory, long maxTotalBytes, TimeSpan tempFileMaxAge)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }

                var now = DateTime.UtcNow;
                foreach (var tempFile in Directory.GetFiles(directory, "*.tmp"))
                {
                    try
                    {
                        if (now - File.GetLastWriteTimeUtc(tempFile) > tempFileMaxAge)
                        {
                            File.Delete(tempFile);
                        }
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }

                var entries = new List<(string path, long size, DateTime touched)>();
                long total = 0;
                foreach (var file in Directory.GetFiles(directory, "*.bct"))
                {
                    var info = new FileInfo(file);
                    entries.Add((file, info.Length, info.LastWriteTimeUtc));
                    total += info.Length;
                }

                if (total <= maxTotalBytes)
                {
                    return;
                }

                entries.Sort((a, b) => a.touched.CompareTo(b.touched));
                for (var i = 0; i < entries.Count && total > maxTotalBytes; i++)
                {
                    try
                    {
                        File.Delete(entries[i].path);
                        total -= entries[i].size;
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        public static void Delete(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

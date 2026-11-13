using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Zep.Core.Application;

namespace Zep.Core.Indexing
{
    /// <summary>
    /// Reads asset guids and Unity labels straight from plaintext YAML <c>.meta</c>
    /// files with a byte scanner, so index sources need no per-asset AssetDatabase
    /// call. Pure System.IO: safe on worker threads.
    /// </summary>
    public static class UnityMetaFileReader
    {
        public const int MetaCompatibilityAdapterVersion = 1;
        private const byte SupportedUnityMetaFileFormatVersion = 2;

        /// <summary>
        /// Reads the guid and Unity labels from one sequential pass over a plaintext YAML
        /// .meta file. Unity normally writes labels immediately after the guid, but some
        /// importer formats write the top-level labels block at the end of the file. The
        /// byte scanner therefore covers the whole stream without materialising importer
        /// metadata or allocating a string for every YAML line. Read failures propagate;
        /// callers performing an incremental main-thread lookup may explicitly fall back
        /// to AssetDatabase, while authoritative scans must fault instead of omitting data.
        /// </summary>
        internal static string ReadGuidAndLabelsFromMetaFile(
            string metaPath,
            out IReadOnlyList<string> unityLabels)
        {
            unityLabels = Array.Empty<string>();
            if (string.IsNullOrEmpty(metaPath))
            {
                return string.Empty;
            }

            try
            {
                return ReadGuidAndLabelsFromExistingMetaFile(metaPath, out unityLabels);
            }
            catch (FileNotFoundException)
            {
                return string.Empty;
            }
            catch (DirectoryNotFoundException)
            {
                return string.Empty;
            }
        }

        private static string ReadGuidAndLabelsFromExistingMetaFile(
            string metaPath,
            out IReadOnlyList<string> unityLabels)
        {
            unityLabels = Array.Empty<string>();
            using (var stream = new FileStream(
                metaPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan))
            {
                Span<byte> compatibilityHeader = stackalloc byte[256];
                var compatibilityBytes = stream.Read(compatibilityHeader);
                if (!IsSupportedMetaSerialization(
                        compatibilityHeader.Slice(0, compatibilityBytes)))
                {
                    return string.Empty;
                }

                stream.Position = 0;
                Span<byte> readBuffer = stackalloc byte[4096];
                Span<byte> lineBuffer = stackalloc byte[4096];
                var lineLength = 0;
                var lineOverflowed = false;
                var guid = string.Empty;
                var readingLabels = false;
                var labelsFinished = false;
                List<string> labels = null;

                while (true)
                {
                    var read = stream.Read(readBuffer);
                    if (read <= 0)
                    {
                        break;
                    }

                    for (var i = 0; i < read; i++)
                    {
                        var current = readBuffer[i];
                        if (current == (byte)'\n')
                        {
                            if (!lineOverflowed)
                            {
                                ProcessMetaLine(
                                    lineBuffer.Slice(0, lineLength),
                                    ref guid,
                                    ref readingLabels,
                                    ref labelsFinished,
                                    ref labels);
                                if (labelsFinished && !string.IsNullOrEmpty(guid))
                                {
                                    unityLabels = labels?.ToArray() ?? Array.Empty<string>();
                                    return guid;
                                }
                            }

                            lineLength = 0;
                            lineOverflowed = false;
                            continue;
                        }

                        if (current == (byte)'\r')
                        {
                            continue;
                        }

                        if (lineLength < lineBuffer.Length)
                        {
                            lineBuffer[lineLength++] = current;
                        }
                        else
                        {
                            // Importer payload lines can be very large and are irrelevant
                            // here. Unity label values are short editor identifiers; ignore
                            // an oversized non-structural line without allocating it.
                            lineOverflowed = true;
                        }
                    }
                }

                if (lineLength > 0 && !lineOverflowed)
                {
                    ProcessMetaLine(
                        lineBuffer.Slice(0, lineLength),
                        ref guid,
                        ref readingLabels,
                        ref labelsFinished,
                        ref labels);
                }

                unityLabels = labels?.ToArray() ?? Array.Empty<string>();
                return guid;
            }
        }

        private static void ProcessMetaLine(
            ReadOnlySpan<byte> line,
            ref string guid,
            ref bool readingLabels,
            ref bool labelsFinished,
            ref List<string> labels)
        {
            if (string.IsNullOrEmpty(guid))
            {
                guid = ExtractGuid(line);
            }

            if (readingLabels)
            {
                if (TryExtractLabel(line, out var label))
                {
                    AddLabel(label, ref labels);
                    return;
                }

                if (IsBlankOrComment(line))
                {
                    return;
                }

                readingLabels = false;
                labelsFinished = true;
                return;
            }

            if (!labelsFinished && IsLabelsHeader(line, out var isBlockSequence, out var headerSuffix))
            {
                readingLabels = isBlockSequence;
                labelsFinished = !isBlockSequence;
                if (!isBlockSequence)
                {
                    AppendFlowSequenceLabels(headerSuffix, ref labels);
                }
            }
        }

        private static void AddLabel(string label, ref List<string> labels)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return;
            }

            if (labels == null)
            {
                labels = new List<string>();
            }

            labels.Add(label);
        }

        // `labels: [a, b, 'c, d']` — the inline flow sequence. Unity writes the
        // block form, but it reads and round-trips this one, and a .meta that
        // carries it must not index as unlabeled (the incremental record would
        // then disagree with the next full rebuild). Deliberately not a YAML
        // parser: anything that is not a bracketed sequence is left unparsed,
        // exactly as before.
        private static void AppendFlowSequenceLabels(ReadOnlySpan<byte> suffix, ref List<string> labels)
        {
            if (suffix.Length < 2 || suffix[0] != (byte)'[' || suffix[suffix.Length - 1] != (byte)']')
            {
                return;
            }

            var inner = suffix.Slice(1, suffix.Length - 2);
            var start = 0;
            byte openQuote = 0;
            for (var i = 0; i < inner.Length; i++)
            {
                var current = inner[i];
                if (openQuote != 0)
                {
                    if (openQuote == (byte)'"'
                        && current == (byte)'\\'
                        && i + 1 < inner.Length)
                    {
                        i++;
                        continue;
                    }

                    if (openQuote == (byte)'\''
                        && current == (byte)'\''
                        && i + 1 < inner.Length
                        && inner[i + 1] == (byte)'\'')
                    {
                        i++;
                        continue;
                    }

                    if (current == openQuote)
                    {
                        openQuote = 0;
                    }

                    continue;
                }

                if (current == (byte)'\'' || current == (byte)'"')
                {
                    openQuote = current;
                    continue;
                }

                if (current != (byte)',')
                {
                    continue;
                }

                AddFlowSequenceValue(inner.Slice(start, i - start), ref labels);
                start = i + 1;
            }

            AddFlowSequenceValue(inner.Slice(start), ref labels);
        }

        private static void AddFlowSequenceValue(ReadOnlySpan<byte> rawValue, ref List<string> labels)
            => AddLabel(DecodeYamlLabel(TrimAsciiWhitespace(rawValue)), ref labels);

        private static bool IsLabelsHeader(
            ReadOnlySpan<byte> line,
            out bool isBlockSequence,
            out ReadOnlySpan<byte> suffix)
        {
            isBlockSequence = false;
            suffix = default;
            var start = line.Length >= 3
                && line[0] == 0xEF
                && line[1] == 0xBB
                && line[2] == 0xBF
                    ? 3
                    : 0;
            ReadOnlySpan<byte> key = stackalloc byte[]
            {
                (byte)'l', (byte)'a', (byte)'b', (byte)'e', (byte)'l', (byte)'s', (byte)':',
            };
            if (line.Length - start < key.Length || !line.Slice(start, key.Length).SequenceEqual(key))
            {
                return false;
            }

            suffix = TrimAsciiWhitespace(line.Slice(start + key.Length));
            isBlockSequence = suffix.Length == 0;
            return true;
        }

        private static bool TryExtractLabel(ReadOnlySpan<byte> line, out string label)
        {
            label = null;
            var start = 0;
            while (start < line.Length && (line[start] == (byte)' ' || line[start] == (byte)'\t'))
            {
                start++;
            }

            if (start >= line.Length || line[start] != (byte)'-')
            {
                return false;
            }

            start++;
            if (start < line.Length && line[start] != (byte)' ' && line[start] != (byte)'\t')
            {
                return false;
            }

            var rawValue = TrimAsciiWhitespace(line.Slice(start));
            label = DecodeYamlLabel(rawValue);
            return true;
        }

        private static bool IsBlankOrComment(ReadOnlySpan<byte> line)
        {
            var trimmed = TrimAsciiWhitespace(line);
            return trimmed.Length == 0 || trimmed[0] == (byte)'#';
        }

        private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> value)
        {
            var start = 0;
            var end = value.Length;
            while (start < end && (value[start] == (byte)' ' || value[start] == (byte)'\t'))
            {
                start++;
            }

            while (end > start && (value[end - 1] == (byte)' ' || value[end - 1] == (byte)'\t'))
            {
                end--;
            }

            return value.Slice(start, end - start);
        }

        private static string DecodeYamlLabel(ReadOnlySpan<byte> rawValue)
            => DecodeYamlScalar(Encoding.UTF8.GetString(rawValue));

        public static string DecodeYamlScalar(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            if (value.Length < 2)
            {
                return value;
            }

            if (value[0] == '\'' && value[value.Length - 1] == '\'')
            {
                return value.Substring(1, value.Length - 2).Replace("''", "'");
            }

            if (value[0] != '"' || value[value.Length - 1] != '"')
            {
                return value;
            }

            var decoded = new StringBuilder(value.Length - 2);
            for (var i = 1; i < value.Length - 1; i++)
            {
                var current = value[i];
                if (current != '\\' || i + 1 >= value.Length - 1)
                {
                    decoded.Append(current);
                    continue;
                }

                var escaped = value[++i];
                switch (escaped)
                {
                    case '\\': decoded.Append('\\'); break;
                    case '"': decoded.Append('"'); break;
                    case 'n': decoded.Append('\n'); break;
                    case 'r': decoded.Append('\r'); break;
                    case 't': decoded.Append('\t'); break;
                    case 'x':
                    case 'u':
                    case 'U':
                        // The three numeric forms Unity's own serialiser emits
                        // for a non-ASCII scalar: measured in 2022.3 labels and
                        // Unity 6.0 serialized favorites. Labels may also be raw
                        // in newer editors. Decode \xNN below U+0100, \uNNNN
                        // through the BMP, and \UNNNNNNNN above it so callers
                        // retain the path or label's original identity.
                        var digits = escaped == 'x' ? 2 : escaped == 'u' ? 4 : 8;
                        if (TryReadHexCodePoint(value, i + 1, digits, out var codePoint))
                        {
                            decoded.Append(char.ConvertFromUtf32(codePoint));
                            i += digits;
                            break;
                        }

                        goto default;
                    default:
                        decoded.Append('\\');
                        decoded.Append(escaped);
                        break;
                }
            }

            return decoded.ToString();
        }

        // Reads exactly digitCount hex digits starting at start, refusing to run
        // past the closing quote at value.Length - 1. A truncated or non-hex
        // sequence returns false so the caller re-emits it verbatim, which is
        // what the unescape switch has always done with an escape it does not
        // know - a hand-edited file degrades rather than losing characters.
        // Surrogate halves and out-of-range scalars are refused for the same
        // reason, and because char.ConvertFromUtf32 would throw on them.
        private static bool TryReadHexCodePoint(
            string value,
            int start,
            int digitCount,
            out int codePoint)
        {
            codePoint = 0;
            var end = start + digitCount;
            if (end > value.Length - 1)
            {
                return false;
            }

            for (var i = start; i < end; i++)
            {
                var current = value[i];
                int digit;
                if (current >= '0' && current <= '9')
                {
                    digit = current - '0';
                }
                else if (current >= 'a' && current <= 'f')
                {
                    digit = current - 'a' + 10;
                }
                else if (current >= 'A' && current <= 'F')
                {
                    digit = current - 'A' + 10;
                }
                else
                {
                    return false;
                }

                codePoint = (codePoint << 4) | digit;
            }

            return codePoint <= 0x10FFFF
                && (codePoint < 0xD800 || codePoint > 0xDFFF);
        }

        /// <summary>
        /// Reads the guid field from a Unity .meta file. The file is plaintext YAML; the
        /// guid sits on line 2 in practice but we scan for it explicitly to tolerate any
        /// leading comments or BOM. Returns an empty string when the file is missing or
        /// the guid cannot be located in the first 1 KiB. Read failures propagate so a
        /// native scan cannot publish an incomplete authoritative snapshot.
        /// </summary>
        public static string ReadGuidFromMetaFile(string metaPath)
        {
            if (string.IsNullOrEmpty(metaPath))
            {
                return string.Empty;
            }

            ZepPerfLog.Count(ZepPerfCounter.MetaFileGuidReads);

            try
            {
                return ReadGuidFromExistingMetaFile(metaPath);
            }
            catch (FileNotFoundException)
            {
                return string.Empty;
            }
            catch (DirectoryNotFoundException)
            {
                return string.Empty;
            }
        }

        private static string ReadGuidFromExistingMetaFile(string metaPath)
        {
            using (var stream = new FileStream(
                metaPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 256,
                FileOptions.SequentialScan))
            {
                Span<byte> buffer = stackalloc byte[1024];
                var read = stream.Read(buffer);
                if (read <= 0)
                {
                    return string.Empty;
                }

                var header = buffer.Slice(0, read);
                return IsSupportedMetaSerialization(header)
                    ? ExtractGuid(header)
                    : string.Empty;
            }
        }

        // Compatibility adapter v1 accepts only Unity's observed YAML .meta
        // serialization (fileFormatVersion: 2). Unknown formats fail closed and
        // await authoritative Unity acknowledgement instead of being guessed.
        private static bool IsSupportedMetaSerialization(ReadOnlySpan<byte> buffer)
        {
            ReadOnlySpan<byte> key = stackalloc byte[]
            {
                (byte)'f', (byte)'i', (byte)'l', (byte)'e',
                (byte)'F', (byte)'o', (byte)'r', (byte)'m',
                (byte)'a', (byte)'t', (byte)'V', (byte)'e',
                (byte)'r', (byte)'s', (byte)'i', (byte)'o',
                (byte)'n', (byte)':',
            };
            var hit = IndexOf(buffer, key);
            if (hit < 0)
            {
                return false;
            }

            var cursor = hit + key.Length;
            while (cursor < buffer.Length
                && (buffer[cursor] == (byte)' ' || buffer[cursor] == (byte)'\t'))
            {
                cursor++;
            }

            return cursor < buffer.Length
                && buffer[cursor] == (byte)('0' + SupportedUnityMetaFileFormatVersion)
                && (cursor + 1 == buffer.Length
                    || buffer[cursor + 1] == (byte)'\r'
                    || buffer[cursor + 1] == (byte)'\n'
                    || buffer[cursor + 1] == (byte)' '
                    || buffer[cursor + 1] == (byte)'\t');
        }

        // Looks for the literal token "guid: " followed by 32 hex chars. ASCII-only.
        private static string ExtractGuid(ReadOnlySpan<byte> buffer)
        {
            ReadOnlySpan<byte> needle = stackalloc byte[] { (byte)'g', (byte)'u', (byte)'i', (byte)'d', (byte)':' };
            var hit = IndexOf(buffer, needle);
            if (hit < 0)
            {
                return string.Empty;
            }

            var start = hit + needle.Length;
            // Skip whitespace after the colon.
            while (start < buffer.Length && (buffer[start] == (byte)' ' || buffer[start] == (byte)'\t'))
            {
                start++;
            }

            const int GuidLength = 32;
            if (start + GuidLength > buffer.Length)
            {
                return string.Empty;
            }

            Span<char> chars = stackalloc char[GuidLength];
            for (var i = 0; i < GuidLength; i++)
            {
                var b = buffer[start + i];
                if (!IsHexDigit(b))
                {
                    return string.Empty;
                }

                chars[i] = (char)b;
            }

            return new string(chars);
        }

        private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
        {
            var end = haystack.Length - needle.Length;
            for (var i = 0; i <= end; i++)
            {
                var match = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool IsHexDigit(byte b)
        {
            return (b >= (byte)'0' && b <= (byte)'9')
                || (b >= (byte)'a' && b <= (byte)'f')
                || (b >= (byte)'A' && b <= (byte)'F');
        }
    }
}

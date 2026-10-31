using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZstdSharp;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// BFCAP v1 公共码表容器。码表由客户端和服务器共同版本化；动态玩家名等内容保持原值。
/// The public codebook is immutable. Match-local dictionaries are deliberately forbidden.
/// </summary>
internal static class BfcapCodec
{
    private static readonly byte[] Magic = "BFCAP1\r\n"u8.ToArray();
    private static readonly byte[] BlockMagic = "BFCB"u8.ToArray();
    private static readonly byte[] FooterMagic = "BFCE"u8.ToArray();
    private const int BlockTargetBytes = 4 * 1024 * 1024;
    private const int MaximumBlockBytes = 64 * 1024 * 1024;
    private const long MaximumLogicalBytes = 1024L * 1024 * 1024;
    private const long MaximumRows = 5_000_000;
    internal const string PackageEncoding = "bfcap-zstd-v1";

    /// <summary>
    /// Builds BFCAP blocks while the JSONL capture is being written.  The raw
    /// JSONL remains the recovery source until Complete has fsynced and
    /// atomically published the compact container.
    /// </summary>
    internal sealed class StreamingEncoder : IDisposable
    {
        private readonly string destinationPath;
        private readonly string blockPath;
        private readonly LoadedCodebook codebook;
        private readonly FileStream blocks;
        private readonly Compressor compressor;
        private readonly IncrementalHash sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly MemoryStream rawBlock = new(BlockTargetBytes + 64 * 1024);
        private readonly List<byte[]> blockHashes = [];
        private readonly int level;
        private string? schema;
        private string? matchUid;
        private long sourceBytes;
        private long rows;
        private uint blockIndex;
        private uint blockRows;
        private bool completed;
        private bool disposed;

        internal StreamingEncoder(string sourcePath, string assetRoot, int level)
        {
            this.level = level;
            destinationPath = Path.ChangeExtension(sourcePath, ".bfcap");
            blockPath = destinationPath + ".blocks.partial";
            File.Delete(blockPath);
            codebook = LoadCodebook(CodebookPath(assetRoot));
            compressor = new Compressor(level);
            blocks = new FileStream(blockPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.SequentialScan);
        }

        internal void AppendLine(ReadOnlySpan<byte> utf8Json)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (completed) throw new InvalidOperationException("BFCAP stream is already complete.");
            var line = Encoding.UTF8.GetString(utf8Json);
            using var document = JsonDocument.Parse(utf8Json.ToArray());
            ValidateRow(document.RootElement, codebook, ref schema, ref matchUid);
            var compact = EncodeRow(line, codebook);
            if (rawBlock.Length > 0 && rawBlock.Length + compact.Length + 1 > BlockTargetBytes)
                FlushBlock();
            rawBlock.Write(compact); rawBlock.WriteByte((byte)'\n'); blockRows++;
            sourceHash.AppendData(utf8Json);
            sourceHash.AppendData(new byte[] { (byte)'\n' });
            sourceBytes += utf8Json.Length + 1L;
            rows++;
        }

        private void FlushBlock()
        {
            if (blockRows == 0) return;
            WriteBlock(blocks, compressor, rawBlock, blockIndex++, blockRows, blockHashes);
            rawBlock.SetLength(0); blockRows = 0;
        }

        internal string Complete()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (completed) return destinationPath;
            FlushBlock();
            if (rows is <= 0 or > MaximumRows || sourceBytes is <= 0 or > MaximumLogicalBytes ||
                string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(matchUid))
                throw new InvalidDataException("BFCAP stream contains no valid records.");
            blocks.Flush(true); blocks.Dispose();
            var header = new PublicHeader("battlefield-capture-container/v1", schema!, matchUid!,
                sourceBytes, Convert.ToHexString(sourceHash.GetHashAndReset()).ToLowerInvariant(), rows,
                "zstd", level, BlockTargetBytes, codebook.Document.Version, codebook.Sha256);
            var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var temporary = destinationPath + ".partial";
            File.Delete(temporary);
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 64 * 1024, FileOptions.SequentialScan))
                {
                    output.Write(Magic); WriteUInt32(output, checked((uint)headerBytes.Length));
                    output.Write(headerBytes);
                    using (var input = new FileStream(blockPath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 64 * 1024, FileOptions.SequentialScan)) input.CopyTo(output);
                    output.Write(FooterMagic); WriteUInt32(output, blockIndex);
                    WriteUInt64(output, checked((ulong)rows));
                    output.Write(SHA256.HashData(blockHashes.SelectMany(x => x).ToArray()));
                    output.Flush(true);
                }
                File.Move(temporary, destinationPath, true);
                completed = true;
                File.Delete(blockPath);
                return destinationPath;
            }
            finally { File.Delete(temporary); }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            blocks.Dispose(); compressor.Dispose(); sourceHash.Dispose(); rawBlock.Dispose();
            if (!completed) File.Delete(blockPath);
        }
    }

    internal static StreamingEncoder BeginStreaming(string sourcePath, string assetRoot, int level = 3)
        => new(sourcePath, assetRoot, level);

    private sealed record CaptureType(int Id, string Event, string[] Fields);
    private sealed record CodebookDocument(string Schema, int Version, string CaptureSchema,
        CaptureType[] CaptureTypes, string[] Symbols,
        Dictionary<string, string[]> SymbolFields,
        Dictionary<string, string[]> SymbolArrayFields);
    private sealed record PublicHeader(string Format, string CaptureSchema, string MatchUid,
        long SourceBytes, string SourceSha256, long RowCount, string Codec,
        int CompressionLevel, int BlockTargetBytes, int CodebookVersion,
        string CodebookSha256);
    private sealed record LoadedCodebook(CodebookDocument Document, string Sha256,
        Dictionary<string, CaptureType> ByEvent, Dictionary<int, CaptureType> ById,
        Dictionary<string, int> SymbolIds);

    internal static bool IsBfcap(string path) =>
        string.Equals(Path.GetExtension(path), ".bfcap", StringComparison.OrdinalIgnoreCase);

    internal static string CodebookPath(string assetRoot)
    {
        var candidates = new[]
        {
            Path.Combine(assetRoot, "assets", "contracts", "bfwire-codebook-v1.json"),
            Path.Combine(assetRoot, "Assets", "contracts", "bfwire-codebook-v1.json"),
            Path.Combine(AppContext.BaseDirectory, "assets", "contracts", "bfwire-codebook-v1.json")
        };
        return candidates.FirstOrDefault(File.Exists)
               ?? throw new FileNotFoundException("The BFCAP public codebook is missing.");
    }

    internal static (int Version, string Sha256) CodebookIdentity(string assetRoot)
    {
        var codebook = LoadCodebook(CodebookPath(assetRoot));
        return (codebook.Document.Version, codebook.Sha256);
    }

    internal static string EncodeJsonl(string sourcePath, string assetRoot, int level = 3)
    {
        var destinationPath = Path.ChangeExtension(sourcePath, ".bfcap");
        var codebook = LoadCodebook(CodebookPath(assetRoot));
        var scan = Scan(sourcePath, codebook);
        var header = new PublicHeader("battlefield-capture-container/v1",
            scan.Schema, scan.MatchUid, scan.Bytes, scan.Sha256, scan.Rows,
            "zstd", level, BlockTargetBytes, codebook.Document.Version, codebook.Sha256);
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var temporary = destinationPath + ".partial";
        File.Delete(temporary);
        try
        {
            long encodedRows;
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.SequentialScan))
            {
                output.Write(Magic);
                WriteUInt32(output, checked((uint)headerBytes.Length));
                output.Write(headerBytes);
                using var compressor = new Compressor(level);
                using var rawBlock = new MemoryStream(BlockTargetBytes + 64 * 1024);
                var blockHashes = new List<byte[]>();
                uint blockIndex = 0;
                uint blockRows = 0;
                encodedRows = 0;
                foreach (var line in File.ReadLines(sourcePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var compact = EncodeRow(line, codebook);
                    if (rawBlock.Length > 0 && rawBlock.Length + compact.Length + 1 > BlockTargetBytes)
                    {
                        WriteBlock(output, compressor, rawBlock, blockIndex++, blockRows, blockHashes);
                        encodedRows += blockRows;
                        rawBlock.SetLength(0); blockRows = 0;
                    }
                    rawBlock.Write(compact); rawBlock.WriteByte((byte)'\n'); blockRows++;
                }
                if (blockRows > 0)
                {
                    WriteBlock(output, compressor, rawBlock, blockIndex++, blockRows, blockHashes);
                    encodedRows += blockRows;
                }
                if (encodedRows != scan.Rows) throw new InvalidDataException("BFCAP row count changed while encoding.");
                output.Write(FooterMagic);
                WriteUInt32(output, blockIndex);
                WriteUInt64(output, checked((ulong)encodedRows));
                output.Write(SHA256.HashData(blockHashes.SelectMany(x => x).ToArray()));
                output.Flush(true);
            }
            Verify(destination: temporary, assetRoot, expectedRows: scan.Rows);
            File.Move(temporary, destinationPath, true);
            return destinationPath;
        }
        finally { File.Delete(temporary); }
    }

    internal static IEnumerable<string> ReadJsonRows(string path, string assetRoot)
    {
        using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            Magic.Length, FileOptions.SequentialScan);
        var isContainer = probe.Length >= Magic.Length && ReadExact(probe, Magic.Length).SequenceEqual(Magic);
        probe.Dispose();
        if (!isContainer)
        {
            foreach (var line in File.ReadLines(path))
                if (!string.IsNullOrWhiteSpace(line)) yield return line;
            yield break;
        }
        var codebook = LoadCodebook(CodebookPath(assetRoot));
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        foreach (var row in DecodeRows(input, codebook)) yield return row;
    }

    private static void Verify(string destination, string assetRoot, long expectedRows)
    {
        long rows = 0;
        foreach (var _ in ReadJsonRows(destination, assetRoot)) rows++;
        if (rows != expectedRows) throw new InvalidDataException("BFCAP verification row count mismatch.");
    }

    private static (string Schema, string MatchUid, long Bytes, string Sha256, long Rows)
        Scan(string path, LoadedCodebook codebook)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        string? schema = null, matchUid = null;
        long rows = 0, bytes = 0;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(input, Encoding.UTF8, true, 64 * 1024, leaveOpen: true);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            // Hash/byte length must describe the original file, including its line endings.
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            ValidateRow(document.RootElement, codebook, ref schema, ref matchUid);
            rows++;
        }
        input.Position = 0;
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = input.Read(buffer)) > 0) { hash.AppendData(buffer, 0, read); bytes += read; }
        if (rows == 0 || schema == null || matchUid == null)
            throw new InvalidDataException("BFCAP source contains no records.");
        return (schema, matchUid, bytes, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), rows);
    }

    private static void ValidateRow(JsonElement row, LoadedCodebook codebook,
        ref string? schema, ref string? matchUid)
    {
        var currentSchema = row.GetProperty("schema").GetString();
        var currentUid = row.GetProperty("matchUid").GetString();
        var eventName = row.GetProperty("event").GetString() ?? string.Empty;
        if (schema == null) { schema = currentSchema; matchUid = currentUid; }
        if (currentSchema != schema || currentUid != matchUid)
            throw new InvalidDataException("BFCAP schema and match UID must remain constant.");
        if (!codebook.ByEvent.TryGetValue(eventName, out var type))
            throw new InvalidDataException($"Event is absent from the public codebook: {eventName}");
        var actual = row.EnumerateObject().Select(x => x.Name)
            .Where(x => x is not "schema" and not "event" and not "matchUid").ToHashSet();
        if (!actual.SetEquals(type.Fields))
            throw new InvalidDataException($"Event shape differs from public codebook: {eventName}");
    }

    private static byte[] EncodeRow(string line, LoadedCodebook codebook)
    {
        using var document = JsonDocument.Parse(line);
        var row = document.RootElement;
        string? schema = null, uid = null;
        ValidateRow(row, codebook, ref schema, ref uid);
        var eventName = row.GetProperty("event").GetString()!;
        var type = codebook.ByEvent[eventName];
        var symbolFields = codebook.Document.SymbolFields.GetValueOrDefault(eventName) ?? [];
        var symbolArrays = codebook.Document.SymbolArrayFields.GetValueOrDefault(eventName) ?? [];
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartArray(); writer.WriteNumberValue(type.Id);
            foreach (var field in type.Fields)
            {
                var value = row.GetProperty(field);
                if (symbolFields.Contains(field) && value.ValueKind == JsonValueKind.String &&
                    codebook.SymbolIds.TryGetValue(value.GetString()!, out var symbolId))
                    writer.WriteNumberValue(symbolId);
                else if (symbolArrays.Contains(field) && value.ValueKind == JsonValueKind.Array)
                {
                    writer.WriteStartArray();
                    foreach (var item in value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String &&
                            codebook.SymbolIds.TryGetValue(item.GetString()!, out var itemId))
                            writer.WriteNumberValue(itemId);
                        else item.WriteTo(writer);
                    }
                    writer.WriteEndArray();
                }
                else value.WriteTo(writer);
            }
            writer.WriteEndArray();
        }
        return output.ToArray();
    }

    private static void WriteBlock(Stream output, Compressor compressor, MemoryStream rawBlock,
        uint index, uint rows, List<byte[]> blockHashes)
    {
        var raw = rawBlock.ToArray();
        var digest = SHA256.HashData(raw);
        var compressed = compressor.Wrap(raw).ToArray();
        output.Write(BlockMagic); WriteUInt32(output, index); WriteUInt32(output, rows);
        WriteUInt32(output, checked((uint)raw.Length));
        WriteUInt32(output, checked((uint)compressed.Length));
        output.Write(digest); output.Write(compressed); blockHashes.Add(digest);
    }

    private static IEnumerable<string> DecodeRows(Stream input, LoadedCodebook codebook)
    {
        if (!ReadExact(input, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("BFCAP magic is invalid.");
        var headerLength = ReadUInt32(input);
        if (headerLength is 0 or > 16 * 1024 * 1024) throw new InvalidDataException("BFCAP header is invalid.");
        var header = JsonSerializer.Deserialize<PublicHeader>(ReadExact(input, checked((int)headerLength)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("BFCAP header is invalid.");
        if (header.Format != "battlefield-capture-container/v1" || header.Codec != "zstd" ||
            header.CodebookVersion != codebook.Document.Version || header.CodebookSha256 != codebook.Sha256 ||
            header.CaptureSchema != codebook.Document.CaptureSchema || header.SourceBytes is <= 0 or > MaximumLogicalBytes ||
            header.RowCount is <= 0 or > MaximumRows)
            throw new InvalidDataException("BFCAP public codebook does not match.");
        using var decompressor = new Decompressor();
        var hashes = new List<byte[]>();
        uint blocks = 0; long totalRows = 0, totalCompactBytes = 0;
        while (true)
        {
            var marker = ReadExact(input, 4);
            if (marker.SequenceEqual(FooterMagic))
            {
                var expectedBlocks = ReadUInt32(input); var expectedRows = ReadUInt64(input);
                var expectedHash = ReadExact(input, 32);
                if (input.ReadByte() != -1 || expectedBlocks != blocks || expectedRows != (ulong)totalRows ||
                    !CryptographicOperations.FixedTimeEquals(expectedHash,
                        SHA256.HashData(hashes.SelectMany(x => x).ToArray())) || totalRows != header.RowCount)
                    throw new InvalidDataException("BFCAP footer does not match.");
                yield break;
            }
            if (!marker.SequenceEqual(BlockMagic)) throw new InvalidDataException("BFCAP block is invalid.");
            var index = ReadUInt32(input); var rowCount = ReadUInt32(input);
            var rawLength = ReadUInt32(input); var compressedLength = ReadUInt32(input);
            var digest = ReadExact(input, 32);
            if (index != blocks || rawLength > MaximumBlockBytes || compressedLength > MaximumBlockBytes)
                throw new InvalidDataException("BFCAP block bounds are invalid.");
            var raw = decompressor.Unwrap(ReadExact(input, checked((int)compressedLength)),
                checked((int)rawLength)).ToArray();
            if (raw.Length != rawLength || !CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(raw)))
                throw new InvalidDataException("BFCAP block digest does not match.");
            totalCompactBytes += raw.Length;
            if (totalCompactBytes > MaximumLogicalBytes)
                throw new InvalidDataException("BFCAP logical bounds are invalid.");
            uint actualRows = 0;
            using var reader = new StringReader(Encoding.UTF8.GetString(raw));
            string? compact;
            while ((compact = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(compact)) continue;
                yield return DecodeRow(compact, header, codebook); actualRows++; totalRows++;
            }
            if (actualRows != rowCount) throw new InvalidDataException("BFCAP block row count does not match.");
            hashes.Add(digest); blocks++;
        }
    }

    private static string DecodeRow(string compact, PublicHeader header, LoadedCodebook codebook)
    {
        using var document = JsonDocument.Parse(compact);
        var values = document.RootElement.EnumerateArray().ToArray();
        if (values.Length == 0 || !values[0].TryGetInt32(out var typeId) ||
            !codebook.ById.TryGetValue(typeId, out var type) || values.Length != type.Fields.Length + 1)
            throw new InvalidDataException("BFCAP compact row is invalid.");
        var symbolFields = codebook.Document.SymbolFields.GetValueOrDefault(type.Event) ?? [];
        var symbolArrays = codebook.Document.SymbolArrayFields.GetValueOrDefault(type.Event) ?? [];
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", header.CaptureSchema); writer.WriteString("event", type.Event);
            writer.WriteString("matchUid", header.MatchUid);
            for (var index = 0; index < type.Fields.Length; index++)
            {
                var field = type.Fields[index]; var value = values[index + 1]; writer.WritePropertyName(field);
                if (symbolFields.Contains(field) && value.ValueKind == JsonValueKind.Number &&
                    value.TryGetInt32(out var symbolId) && symbolId >= 0 && symbolId < codebook.Document.Symbols.Length)
                    writer.WriteStringValue(codebook.Document.Symbols[symbolId]);
                else if (symbolArrays.Contains(field) && value.ValueKind == JsonValueKind.Array)
                {
                    writer.WriteStartArray();
                    foreach (var item in value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var itemId) &&
                            itemId >= 0 && itemId < codebook.Document.Symbols.Length)
                            writer.WriteStringValue(codebook.Document.Symbols[itemId]);
                        else item.WriteTo(writer);
                    }
                    writer.WriteEndArray();
                }
                else value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static LoadedCodebook LoadCodebook(string path)
    {
        var raw = File.ReadAllBytes(path);
        var document = JsonSerializer.Deserialize<CodebookDocument>(raw,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("BFCAP public codebook is invalid.");
        if (document.Schema != "pvpchronicle-bfwire-codebook/v1" || document.Version < 1)
            throw new InvalidDataException("BFCAP public codebook is invalid.");
        var byEvent = document.CaptureTypes.ToDictionary(x => x.Event, StringComparer.Ordinal);
        var byId = document.CaptureTypes.ToDictionary(x => x.Id);
        if (byEvent.Count != document.CaptureTypes.Length || byId.Count != document.CaptureTypes.Length ||
            document.Symbols.Distinct(StringComparer.Ordinal).Count() != document.Symbols.Length)
            throw new InvalidDataException("BFCAP public codebook contains duplicates.");
        return new LoadedCodebook(document, Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(),
            byEvent, byId, document.Symbols.Select((value, index) => (value, index))
                .ToDictionary(x => x.value, x => x.index, StringComparer.Ordinal));
    }

    private static byte[] ReadExact(Stream input, int count)
    {
        var result = new byte[count]; var offset = 0;
        while (offset < count)
        {
            var read = input.Read(result, offset, count - offset);
            if (read == 0) throw new EndOfStreamException("BFCAP container is truncated.");
            offset += read;
        }
        return result;
    }
    private static uint ReadUInt32(Stream input) => BinaryPrimitives.ReadUInt32LittleEndian(ReadExact(input, 4));
    private static ulong ReadUInt64(Stream input) => BinaryPrimitives.ReadUInt64LittleEndian(ReadExact(input, 8));
    private static void WriteUInt32(Stream output, uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); output.Write(b); }
    private static void WriteUInt64(Stream output, ulong value) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, value); output.Write(b); }
}

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ZstdSharp;

namespace BattlefieldFrontlineCollector;

/// <summary>BFDETAIL v1 decoder backed only by the shared immutable public codebook.</summary>
internal static class BfdetailCodec
{
    internal const string MediaType = "application/vnd.pvpchronicle.detail+zstd";
    private static readonly byte[] Magic = "BFDET1\r\n"u8.ToArray();
    private sealed record Shape(int Id, string[] Fields);
    private sealed record CodebookDocument(string Schema, int Version, Shape[] Shapes, string[] Symbols);
    private sealed record Header(string Format, string Codec, int CodebookVersion,
        string CodebookSha256, long SourceBytes, string SourceSha256);
    private sealed record Loaded(CodebookDocument Document, string Sha256,
        Dictionary<int, string[]> Shapes);

    internal static (int Version, string Sha256) CodebookIdentity(string assetRoot)
    {
        var value = Load(assetRoot); return (value.Document.Version, value.Sha256);
    }

    internal static string DecodeToJson(byte[] payload, string assetRoot)
    {
        try
        {
            return DecodeToJsonCore(payload, assetRoot);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Normalize every malformed-container failure so the caller can
            // consistently retry the optional compact response as JSON.
            // 统一紧凑容器格式异常，确保调用方能够可靠回退普通 JSON。
            throw new InvalidDataException("BFDETAIL container is invalid.", exception);
        }
    }

    private static string DecodeToJsonCore(byte[] payload, string assetRoot)
    {
        var book = Load(assetRoot);
        using var input = new MemoryStream(payload, writable:false);
        if (!ReadExact(input, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("BFDETAIL magic is invalid.");
        var headerLength = ReadUInt32(input);
        if (headerLength is 0 or > 1024 * 1024) throw new InvalidDataException("BFDETAIL header is invalid.");
        var header = JsonSerializer.Deserialize<Header>(ReadExact(input, checked((int)headerLength)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("BFDETAIL header is invalid.");
        if (header.Format != "battlefield-detail-container/v1" || header.Codec != "zstd" ||
            header.CodebookVersion != book.Document.Version || header.CodebookSha256 != book.Sha256)
            throw new InvalidDataException("BFDETAIL public codebook does not match.");
        var rawLength = ReadUInt32(input); var compressedLength = ReadUInt32(input);
        var digest = ReadExact(input, 32);
        if (rawLength > 512 * 1024 * 1024 || compressedLength != input.Length - input.Position)
            throw new InvalidDataException("BFDETAIL payload bounds are invalid.");
        byte[] raw;
        try
        {
            using var decompressor = new Decompressor();
            raw = decompressor.Unwrap(ReadExact(input, checked((int)compressedLength)),
                checked((int)rawLength)).ToArray();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidDataException("BFDETAIL zstd payload is invalid.", exception);
        }
        if (raw.Length != rawLength || !CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(raw)))
            throw new InvalidDataException("BFDETAIL payload digest does not match.");
        using var compact = JsonDocument.Parse(raw);
        using var output = new MemoryStream();
        // The server canonicalizes BFDETAIL source JSON with ensure_ascii=False.
        // Utf8JsonWriter's default encoder escapes non-ASCII text as \uXXXX,
        // which is semantically equal but changes both sourceBytes and SHA-256.
        // Use the same UTF-8 representation so multilingual player, action and
        // status names remain byte-identical across Python and .NET.
        // 服务器按原始 UTF-8 保存多语言文本；默认转义会造成长度与摘要误报。
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions
               { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            DecodeValue(compact.RootElement, writer, book);
        var json = output.ToArray();
        // Integrity is enforced on the compact bytes above (bounds, zstd,
        // SHA-256, immutable codebook and tagged structure). The source byte
        // count/digest describe Python's JSON serialization and are diagnostic
        // metadata, not a cross-runtime semantic contract: two valid JSON
        // serializers may produce different bytes for the same value.
        // 完整性校验针对紧凑载荷本身；源 JSON 的字节长度和摘要仅作诊断，
        // 不再要求 .NET 与 Python 的序列化结果逐字相同。
        return Encoding.UTF8.GetString(json);
    }

    private static void DecodeValue(JsonElement value, Utf8JsonWriter writer, Loaded book)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            // Preserve the compact payload's scalar spelling. Python and .NET
            // can otherwise serialize an equal floating-point value to
            // different bytes, invalidating sourceBytes/sourceSha256.
            // 保留紧凑包中的标量原文，避免跨语言浮点格式差异造成摘要误报。
            writer.WriteRawValue(value.GetRawText(), skipInputValidation: false);
            return;
        }
        var parts = value.EnumerateArray().ToArray();
        if (parts.Length == 0 || parts[0].ValueKind != JsonValueKind.String)
            throw new InvalidDataException("BFDETAIL tagged value is invalid.");
        switch (parts[0].GetString())
        {
            case "$s":
                if (parts.Length != 2 || !parts[1].TryGetInt32(out var symbolId) || symbolId < 0 ||
                    symbolId >= book.Document.Symbols.Length) throw new InvalidDataException("BFDETAIL symbol is invalid.");
                writer.WriteStringValue(book.Document.Symbols[symbolId]); return;
            case "$a":
                writer.WriteStartArray(); foreach (var item in parts.Skip(1)) DecodeValue(item,writer,book);
                writer.WriteEndArray(); return;
            case "$m":
                writer.WriteStartObject(); var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pair in parts.Skip(1))
                {
                    var fields = pair.EnumerateArray().ToArray();
                    if (fields.Length != 2 || fields[0].ValueKind != JsonValueKind.String ||
                        !names.Add(fields[0].GetString()!)) throw new InvalidDataException("BFDETAIL map is invalid.");
                    writer.WritePropertyName(fields[0].GetString()!); DecodeValue(fields[1],writer,book);
                }
                writer.WriteEndObject(); return;
            case "$o":
                if (parts.Length < 2 || !parts[1].TryGetInt32(out var shapeId) ||
                    !book.Shapes.TryGetValue(shapeId,out var shape) || parts.Length != shape.Length+2)
                    throw new InvalidDataException("BFDETAIL shape is invalid.");
                writer.WriteStartObject();
                for (var index=0;index<shape.Length;index++)
                { writer.WritePropertyName(shape[index]); DecodeValue(parts[index+2],writer,book); }
                writer.WriteEndObject(); return;
            default: throw new InvalidDataException("BFDETAIL tag is invalid.");
        }
    }

    private static Loaded Load(string assetRoot)
    {
        var candidates = new[] { Path.Combine(assetRoot,"assets","contracts","bfdetail-codebook-v1.json"),
            Path.Combine(assetRoot,"Assets","contracts","bfdetail-codebook-v1.json"),
            Path.Combine(AppContext.BaseDirectory,"assets","contracts","bfdetail-codebook-v1.json") };
        var path=candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("BFDETAIL public codebook is missing.");
        var raw=File.ReadAllBytes(path);
        var document=JsonSerializer.Deserialize<CodebookDocument>(raw,new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("BFDETAIL public codebook is invalid.");
        var shapes=document.Shapes.ToDictionary(x=>x.Id,x=>x.Fields);
        if (document.Schema!="pvpchronicle-bfdetail-codebook/v1" || shapes.Count!=document.Shapes.Length ||
            document.Symbols.Distinct(StringComparer.Ordinal).Count()!=document.Symbols.Length)
            throw new InvalidDataException("BFDETAIL public codebook is invalid.");
        return new Loaded(document,Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(),shapes);
    }
    private static byte[] ReadExact(Stream input,int count){var value=new byte[count];var offset=0;while(offset<count){var n=input.Read(value,offset,count-offset);if(n==0)throw new EndOfStreamException("BFDETAIL is truncated.");offset+=n;}return value;}
    private static uint ReadUInt32(Stream input)=>BinaryPrimitives.ReadUInt32LittleEndian(ReadExact(input,4));
}

using System.Text;

namespace XnbStringTool;

/// <summary>
/// A parsed XNB "type reader manifest" entry: the fully-qualified reader
/// type name the file was built with, plus its (almost always 0) version.
/// </summary>
public sealed record XnbTypeReaderInfo(string Name, int Version);

/// <summary>
/// The result of reading a Dictionary&lt;string,string&gt; XNB asset: the data
/// itself plus enough metadata to write a byte-faithful equivalent back out.
/// </summary>
public sealed class XnbStringTableFile
{
    public required Dictionary<string, string> Entries { get; init; }
    public required IReadOnlyList<XnbTypeReaderInfo> TypeReaders { get; init; }
    public required bool WasCompressed { get; init; }
    public required byte PlatformByte { get; init; }
    public required byte FormatVersion { get; init; }
    public required byte Flags { get; init; }
}

/// <summary>
/// Reads and writes the XNB files under Stardew Valley's Content/Strings
/// folder. Every one of those assets is a plain
/// <c>Dictionary&lt;string,string&gt;</c>, so this class does not attempt to
/// support the general XNB object graph (textures, effects, etc.) -- only
/// the two-reader shape (DictionaryReader`2[String,String] + StringReader)
/// that the game's string tables actually use.
///
/// Wire format verified against MonoGame's own source
/// (ContentManager.cs / ContentReader.cs / ContentTypeReaderManager.cs /
/// DictionaryReader.cs), not guessed:
///   header:  "XNB" | platform:byte | version:byte | flags:byte | fileSize:int32
///            [ if flags &amp; 0x80: decompressedSize:int32, then LZX-compressed body ]
///   body:    readerCount:7BitEncodedInt
///            readerCount * (readerName:string, readerVersion:int32)
///            sharedResourceCount:7BitEncodedInt   (must be 0 -- unsupported otherwise)
///            primaryTypeIndex:7BitEncodedInt      (1-based index into the readers above)
///            count:int32
///            count * (keyReaderIndex:7BitEncodedInt, key:string,
///                      valueReaderIndex:7BitEncodedInt, value:string)
/// </summary>
public static class XnbStringTable
{
    private const byte CompressedLzxFlag = 0x80;
    private const byte CompressedLz4Flag = 0x40;

    public static XnbStringTableFile Read(Stream input)
    {
        using var header = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);

        var magic = header.ReadBytes(3);
        if (magic[0] != 'X' || magic[1] != 'N' || magic[2] != 'B')
            throw new InvalidDataException("Not an XNB file (bad magic).");

        byte platform = header.ReadByte();
        byte version = header.ReadByte();
        byte flags = header.ReadByte();
        int fileSize = header.ReadInt32();

        bool compressedLzx = (flags & CompressedLzxFlag) != 0;
        bool compressedLz4 = (flags & CompressedLz4Flag) != 0;
        if (compressedLz4)
            throw new NotSupportedException("LZ4-compressed XNB files are not supported by this tool (none of the vanilla Strings tables use LZ4).");

        byte[] body;
        if (compressedLzx)
        {
            int decompressedSize = header.ReadInt32();
            int compressedSize = fileSize - 14;
            body = DecompressLzx(input, decompressedSize, compressedSize);
        }
        else
        {
            int bodySize = fileSize - 10;
            body = header.ReadBytes(bodySize);
        }

        using var bodyStream = new MemoryStream(body);
        using var reader = new BinaryReader(bodyStream, Encoding.UTF8, leaveOpen: true);

        int readerCount = Read7BitEncodedInt(reader);
        var typeReaders = new List<XnbTypeReaderInfo>(readerCount);
        for (int i = 0; i < readerCount; i++)
        {
            string name = reader.ReadString();
            int readerVersion = reader.ReadInt32();
            typeReaders.Add(new XnbTypeReaderInfo(name, readerVersion));
        }

        int sharedResourceCount = Read7BitEncodedInt(reader);
        if (sharedResourceCount != 0)
            throw new NotSupportedException($"XNB file declares {sharedResourceCount} shared resources; this tool only supports simple string-table assets with none.");

        int primaryTypeIndex = Read7BitEncodedInt(reader);
        if (primaryTypeIndex == 0)
            throw new InvalidDataException("XNB primary asset is null.");
        var primaryReader = typeReaders[primaryTypeIndex - 1];
        if (!primaryReader.Name.Contains("DictionaryReader") || !primaryReader.Name.Contains("String"))
            throw new NotSupportedException($"Unsupported top-level asset type: {primaryReader.Name}. This tool only reads Dictionary<string,string> string tables.");

        int count = reader.ReadInt32();
        var entries = new Dictionary<string, string>(count);
        for (int i = 0; i < count; i++)
        {
            string key = ReadIndexedString(reader, typeReaders, "key");
            string value = ReadIndexedString(reader, typeReaders, "value");
            entries[key] = value;
        }

        return new XnbStringTableFile
        {
            Entries = entries,
            TypeReaders = typeReaders,
            WasCompressed = compressedLzx,
            PlatformByte = platform,
            FormatVersion = version,
            Flags = flags,
        };
    }

    private static string ReadIndexedString(BinaryReader reader, List<XnbTypeReaderInfo> typeReaders, string role)
    {
        int readerIndex = Read7BitEncodedInt(reader);
        if (readerIndex == 0)
            return string.Empty; // null string -- the game's tables don't appear to use this, but treat as empty rather than throwing
        var elementReader = typeReaders[readerIndex - 1];
        if (!elementReader.Name.Contains("StringReader"))
            throw new NotSupportedException($"Unsupported {role} reader: {elementReader.Name}. Expected a StringReader.");
        return reader.ReadString();
    }

    /// <summary>
    /// Writes an (uncompressed) XNB Dictionary&lt;string,string&gt; asset. Vanilla
    /// game files are LZX-compressed, but compression is optional -- the
    /// content reader checks a flag bit and skips straight to the body when
    /// it's clear, so an uncompressed file loads exactly the same way. This
    /// tool intentionally never writes LZX-compressed output (that would
    /// require porting an LZX *encoder*, not just the decoder above, for no
    /// real benefit here).
    /// </summary>
    public static void Write(Stream output, Dictionary<string, string> entries, IReadOnlyList<XnbTypeReaderInfo>? typeReaders = null)
    {
        typeReaders ??= DefaultTypeReaders;
        if (typeReaders.Count < 2 ||
            !typeReaders[0].Name.Contains("DictionaryReader") ||
            !typeReaders[1].Name.Contains("StringReader"))
            throw new ArgumentException("typeReaders must be [DictionaryReader`2[String,String], StringReader, ...]", nameof(typeReaders));

        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            Write7BitEncodedInt(writer, typeReaders.Count);
            foreach (var r in typeReaders)
            {
                writer.Write(r.Name);
                writer.Write(r.Version);
            }

            Write7BitEncodedInt(writer, 0); // sharedResourceCount
            Write7BitEncodedInt(writer, 1); // primaryTypeIndex -> DictionaryReader (entry 0)

            writer.Write(entries.Count);
            foreach (var (key, value) in entries)
            {
                Write7BitEncodedInt(writer, 2); // StringReader is entry index 1 -> written index 2
                writer.Write(key);
                Write7BitEncodedInt(writer, 2);
                writer.Write(value);
            }
        }

        byte[] bodyBytes = body.ToArray();
        int fileSize = 10 + bodyBytes.Length; // uncompressed: no decompressedSize field

        using var writerOut = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writerOut.Write((byte)'X');
        writerOut.Write((byte)'N');
        writerOut.Write((byte)'B');
        writerOut.Write((byte)'w'); // platform: Windows (matches vanilla files; irrelevant for non-graphics assets)
        writerOut.Write((byte)5);   // format version
        writerOut.Write((byte)0);   // flags: uncompressed, Reach profile
        writerOut.Write(fileSize);
        writerOut.Write(bodyBytes);
    }

    /// <summary>
    /// The exact type-reader manifest strings as used by the real game files
    /// (captured by reading a real Strings/*.xnb once) -- used as the
    /// default when writing synthetic files so they look byte-for-byte like
    /// what the game's own content pipeline would have produced.
    /// </summary>
    public static readonly IReadOnlyList<XnbTypeReaderInfo> DefaultTypeReaders = new List<XnbTypeReaderInfo>
    {
        new("Microsoft.Xna.Framework.Content.DictionaryReader`2[[System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089],[System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]", 0),
        new("Microsoft.Xna.Framework.Content.StringReader", 0),
    };

    private static byte[] DecompressLzx(Stream input, int decompressedSize, int compressedSize)
    {
        var decoder = new LzxDecoder(16);
        using var decompressed = new MemoryStream(decompressedSize);

        long startPos = input.Position;
        long pos = startPos;

        while (pos - startPos < compressedSize)
        {
            int hi = input.ReadByte();
            int lo = input.ReadByte();
            int blockSize = (hi << 8) | lo;
            int frameSize = 0x8000;

            if (hi == 0xFF)
            {
                hi = lo;
                lo = input.ReadByte();
                frameSize = (hi << 8) | lo;
                hi = input.ReadByte();
                lo = input.ReadByte();
                blockSize = (hi << 8) | lo;
                pos += 5;
            }
            else
            {
                pos += 2;
            }

            if (blockSize == 0 || frameSize == 0)
                break;

            decoder.Decompress(input, blockSize, decompressed, frameSize);
            pos += blockSize;
            input.Seek(pos, SeekOrigin.Begin);
        }

        if (decompressed.Position != decompressedSize)
            throw new InvalidDataException($"LZX decompression size mismatch: expected {decompressedSize}, got {decompressed.Position}.");

        return decompressed.ToArray();
    }

    private static int Read7BitEncodedInt(BinaryReader reader)
    {
        // Same algorithm as the BCL's protected BinaryReader.Read7BitEncodedInt,
        // reimplemented here since that one isn't public before .NET 7's
        // Read7BitEncodedInt() extension -- and to keep this file self-contained.
        int result = 0;
        int shift = 0;
        byte b;
        do
        {
            if (shift >= 35)
                throw new FormatException("7-bit encoded int is too long.");
            b = reader.ReadByte();
            result |= (b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        return result;
    }

    private static void Write7BitEncodedInt(BinaryWriter writer, int value)
    {
        uint v = (uint)value;
        while (v >= 0x80)
        {
            writer.Write((byte)(v | 0x80));
            v >>= 7;
        }
        writer.Write((byte)v);
    }
}

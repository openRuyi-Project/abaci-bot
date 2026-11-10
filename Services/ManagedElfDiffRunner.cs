using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using abaci_bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Pure managed C# runner that performs binary ABI analysis by extracting RPM payloads (cpio/gzip/zstd)
/// and inspecting ELF64 shared object (.so) dynamic symbols (.dynsym) and sonames (.dynamic).
/// Serves as a robust cross-platform fallback when native abipkgdiff is unavailable or lacks RPM support.
/// </summary>
public class ManagedElfDiffRunner : IAbiDiffRunner
{
    private readonly ILogger<ManagedElfDiffRunner>? _logger;
    private readonly AbiComplianceOptions _options;

    public ManagedElfDiffRunner(
        IOptions<AbiComplianceOptions>? options = null,
        ILogger<ManagedElfDiffRunner>? logger = null)
    {
        _options = options?.Value ?? new AbiComplianceOptions();
        _logger = logger;
    }

    public ManagedElfDiffRunner(ILogger<ManagedElfDiffRunner>? logger)
        : this(null, logger)
    {
    }

    public async Task<AbiDiffResult> RunDiffAsync(string oldRpmPath, string newRpmPath, CancellationToken ct = default)
    {
        if (!File.Exists(oldRpmPath) || !File.Exists(newRpmPath))
        {
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: string.Empty,
                ErrorMessage: $"Target RPM file not found: old='{oldRpmPath}', new='{newRpmPath}'");
        }

        try
        {
            var oldLibraries = await ExtractSharedLibrariesAsync(oldRpmPath, _options.MaxTotalPayloadBytes, ct);
            var newLibraries = await ExtractSharedLibrariesAsync(newRpmPath, _options.MaxTotalPayloadBytes, ct);

            return CompareLibraries(oldLibraries, newLibraries);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Managed ELF ABI analysis failed for '{Old}' vs '{New}'", oldRpmPath, newRpmPath);
            return new AbiDiffResult(
                Succeeded: false,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: string.Empty,
                ErrorMessage: $"Managed ELF analysis error: {ex.Message}");
        }
    }

    internal static AbiDiffResult CompareLibraries(
        IReadOnlyDictionary<string, ParsedSharedLibrary> oldLibs,
        IReadOnlyDictionary<string, ParsedSharedLibrary> newLibs)
    {
        if (oldLibs.Count == 0 && newLibs.Count == 0)
        {
            return new AbiDiffResult(
                Succeeded: true,
                HasAbiChange: false,
                IsBreakingChange: false,
                Details: "[Managed ELF Engine] No shared libraries (*.so) found in compared RPM packages.");
        }

        var details = new StringBuilder();
        details.AppendLine("[Managed ELF Engine] Binary comparison summary:");

        var isBreaking = false;
        var hasChange = false;

        foreach (var (oldKey, oldLib) in oldLibs)
        {
            // Match in newLibs: first by SONAME, then by library base name / key
            ParsedSharedLibrary? matchedNewLib = null;
            if (!string.IsNullOrEmpty(oldLib.Soname))
            {
                matchedNewLib = newLibs.Values.FirstOrDefault(l => string.Equals(l.Soname, oldLib.Soname, StringComparison.Ordinal));
            }
            if (matchedNewLib == null && newLibs.TryGetValue(oldKey, out var byKey))
            {
                matchedNewLib = byKey;
            }

            if (matchedNewLib == null)
            {
                isBreaking = true;
                details.AppendLine(CultureInfo.InvariantCulture, $"* Breaking: Library '{oldKey}' (SONAME '{oldLib.Soname}') was removed.");
                continue;
            }

            // Check SONAME change
            if (!string.IsNullOrEmpty(oldLib.Soname) &&
                !string.IsNullOrEmpty(matchedNewLib.Soname) &&
                !string.Equals(oldLib.Soname, matchedNewLib.Soname, StringComparison.Ordinal))
            {
                isBreaking = true;
                details.AppendLine(CultureInfo.InvariantCulture, $"* Breaking: SONAME changed from '{oldLib.Soname}' to '{matchedNewLib.Soname}' for '{oldKey}'.");
            }

            // Check exported symbol differences
            var missingSymbols = oldLib.ExportedSymbols.Except(matchedNewLib.ExportedSymbols, StringComparer.Ordinal).ToList();
            var addedSymbols = matchedNewLib.ExportedSymbols.Except(oldLib.ExportedSymbols, StringComparer.Ordinal).ToList();

            if (missingSymbols.Count > 0)
            {
                isBreaking = true;
                var sample = string.Join(", ", missingSymbols.Take(5));
                if (missingSymbols.Count > 5) sample += $", ... (+{missingSymbols.Count - 5} more)";
                details.AppendLine(CultureInfo.InvariantCulture, $"* Breaking: {missingSymbols.Count} exported symbol(s) removed in '{oldKey}': {sample}");
            }

            if (addedSymbols.Count > 0)
            {
                hasChange = true;
                var sample = string.Join(", ", addedSymbols.Take(5));
                if (addedSymbols.Count > 5) sample += $", ... (+{addedSymbols.Count - 5} more)";
                details.AppendLine(CultureInfo.InvariantCulture, $"* ABI Change: {addedSymbols.Count} exported symbol(s) added in '{oldKey}': {sample}");
            }
        }

        // Check for completely new libraries added
        foreach (var (newKey, newLib) in newLibs)
        {
            var existsInOld = oldLibs.ContainsKey(newKey) ||
                (!string.IsNullOrEmpty(newLib.Soname) && oldLibs.Values.Any(l => string.Equals(l.Soname, newLib.Soname, StringComparison.Ordinal)));
            if (!existsInOld)
            {
                hasChange = true;
                details.AppendLine(CultureInfo.InvariantCulture, $"* ABI Change: New shared library added: '{newKey}' (SONAME '{newLib.Soname}').");
            }
        }

        if (isBreaking)
        {
            return new AbiDiffResult(
                Succeeded: true,
                HasAbiChange: true,
                IsBreakingChange: true,
                Details: "[Symbol-Only Managed Analysis] " + details.ToString().TrimEnd());
        }

        if (hasChange)
        {
            return new AbiDiffResult(
                Succeeded: true,
                HasAbiChange: true,
                IsBreakingChange: false,
                Details: "[Symbol-Only Managed Analysis] " + details.ToString().TrimEnd());
        }

        return new AbiDiffResult(
            Succeeded: true,
            HasAbiChange: false,
            IsBreakingChange: false,
            Details: "[Symbol-Only Managed Analysis] All shared libraries and exported symbols are identical (without DWARF type layout validation).");
    }

    internal sealed class RpmFileEntry
    {
        public string Path { get; }
        public int Mode { get; }
        public long Size { get; }
        public string? Digest { get; }

        public RpmFileEntry(string path, int mode, long size, string? digest)
        {
            Path = path;
            Mode = mode;
            Size = size;
            Digest = digest;
        }
    }

    internal sealed class RpmPackageMetadata
    {
        private const int RPMTAG_FILEMODES = 1030;
        private const int RPMTAG_FILESIZES = 1028;
        private const int RPMTAG_LONGFILESIZES = 5008;
        private const int RPMTAG_DIRINDEXES = 1116;
        private const int RPMTAG_BASENAMES = 1117;
        private const int RPMTAG_DIRNAMES = 1118;
        private const int RPMTAG_FILEDIGESTS = 1035;

        public IReadOnlyList<RpmFileEntry> Files { get; }

        public RpmPackageMetadata(IReadOnlyList<RpmFileEntry> files)
        {
            Files = files;
        }

        public static RpmPackageMetadata Parse(byte[] indexBytes, int nindex, byte[] data)
        {
            int[]? dirIndexes = null;
            string[]? baseNames = null;
            string[]? dirNames = null;
            int[]? fileModes = null;
            long[]? fileSizes = null;
            long[]? longFileSizes = null;
            string[]? fileDigests = null;

            for (var j = 0; j < nindex; j++)
            {
                var entryOffset = j * 16;
                if (entryOffset + 16 > indexBytes.Length)
                    break;

                var tag = BinaryPrimitives.ReadInt32BigEndian(indexBytes.AsSpan(entryOffset, 4));
                var type = BinaryPrimitives.ReadInt32BigEndian(indexBytes.AsSpan(entryOffset + 4, 4));
                var offset = BinaryPrimitives.ReadInt32BigEndian(indexBytes.AsSpan(entryOffset + 8, 4));
                var count = BinaryPrimitives.ReadInt32BigEndian(indexBytes.AsSpan(entryOffset + 12, 4));

                if (offset < 0 || offset >= data.Length || count <= 0)
                    continue;

                switch (tag)
                {
                    case RPMTAG_DIRINDEXES when type == 4 && offset + count * 4 <= data.Length:
                        dirIndexes = new int[count];
                        for (var k = 0; k < count; k++)
                            dirIndexes[k] = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset + k * 4, 4));
                        break;

                    case RPMTAG_FILEMODES when type == 3 && offset + count * 2 <= data.Length:
                        fileModes = new int[count];
                        for (var k = 0; k < count; k++)
                            fileModes[k] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + k * 2, 2));
                        break;

                    case RPMTAG_FILESIZES when type == 4 && offset + count * 4 <= data.Length:
                        fileSizes = new long[count];
                        for (var k = 0; k < count; k++)
                            fileSizes[k] = (long)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + k * 4, 4));
                        break;

                    case RPMTAG_LONGFILESIZES when type == 5 && offset + count * 8 <= data.Length:
                        longFileSizes = new long[count];
                        for (var k = 0; k < count; k++)
                            longFileSizes[k] = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(offset + k * 8, 8));
                        break;

                    case RPMTAG_BASENAMES when (type == 8 || type == 6):
                        baseNames = ParseStringArray(data, offset, count);
                        break;

                    case RPMTAG_DIRNAMES when (type == 8 || type == 6):
                        dirNames = ParseStringArray(data, offset, count);
                        break;

                    case RPMTAG_FILEDIGESTS when (type == 8 || type == 6):
                        fileDigests = ParseStringArray(data, offset, count);
                        break;
                }
            }

            var files = new List<RpmFileEntry>();
            if (baseNames != null && baseNames.Length > 0)
            {
                for (var i = 0; i < baseNames.Length; i++)
                {
                    var dir = (dirIndexes != null && dirNames != null && i < dirIndexes.Length && dirIndexes[i] >= 0 && dirIndexes[i] < dirNames.Length)
                        ? dirNames[dirIndexes[i]]
                        : string.Empty;

                    var rawPath = (dir.TrimStart('/') + baseNames[i]).TrimStart('/');
                    var mode = fileModes != null && i < fileModes.Length ? fileModes[i] : 0;
                    var size = longFileSizes != null && i < longFileSizes.Length
                        ? longFileSizes[i]
                        : (fileSizes != null && i < fileSizes.Length ? fileSizes[i] : 0L);
                    var digest = fileDigests != null && i < fileDigests.Length ? fileDigests[i] : null;

                    files.Add(new RpmFileEntry(rawPath, mode, size, digest));
                }
            }

            return new RpmPackageMetadata(files);
        }

        private static string[] ParseStringArray(byte[] data, int offset, int count)
        {
            var result = new string[count];
            var cur = offset;
            for (var i = 0; i < count; i++)
            {
                if (cur >= data.Length)
                {
                    result[i] = string.Empty;
                    continue;
                }
                var nullIndex = Array.IndexOf(data, (byte)0, cur);
                if (nullIndex < 0)
                {
                    nullIndex = data.Length;
                }
                result[i] = Encoding.UTF8.GetString(data, cur, nullIndex - cur);
                cur = nullIndex + 1;
            }
            return result;
        }
    }

    internal static async Task<Dictionary<string, ParsedSharedLibrary>> ExtractSharedLibrariesAsync(
        string rpmPath,
        long maxTotalBytes,
        CancellationToken ct)
    {
        var result = new Dictionary<string, ParsedSharedLibrary>(StringComparer.OrdinalIgnoreCase);

        await using var rpmFileStream = new FileStream(rpmPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);

        // Read Lead (96 bytes)
        var lead = new byte[96];
        var leadRead = await rpmFileStream.ReadAsync(lead.AsMemory(0, 96), ct);
        if (leadRead < 96)
            throw new InvalidDataException($"Invalid RPM file: truncated lead (read {leadRead} bytes).");

        if (lead[0] != 0xED || lead[1] != 0xAB || lead[2] != 0xEE || lead[3] != 0xDB)
            throw new InvalidDataException("Invalid RPM magic number in lead.");

        // Read Signature Header (skip)
        await SkipHeaderStructureAsync(rpmFileStream, alignTo8Bytes: true, ct);

        // Read Main Header (parse metadata tags: DIRINDEXES, BASENAMES, etc.)
        var metadata = await ReadMainHeaderMetadataAsync(rpmFileStream, ct);

        // Current stream position is now at the beginning of payload
        await using var decompressedStream = CreateDecompressionStream(rpmFileStream);

        // Probe CPIO magic (first 6 bytes)
        var initialMagic = new byte[6];
        var readMagic = await ReadExactAsync(decompressedStream, initialMagic, 6, ct);
        if (readMagic < 6)
            throw new InvalidDataException("CPIO payload archive is empty or truncated.");

        var magicStr = Encoding.ASCII.GetString(initialMagic);
        if (magicStr == "070701" || magicStr == "070702")
        {
            await ExtractNewcCpioLibrariesAsync(decompressedStream, initialMagic, result, maxTotalBytes, ct);
        }
        else if (magicStr == "07070X")
        {
            await ExtractStrippedCpioLibrariesAsync(decompressedStream, initialMagic, metadata.Files, result, maxTotalBytes, ct);
        }
        else
        {
            throw new InvalidDataException($"Unsupported or unrecognized CPIO payload header magic: '{magicStr}'.");
        }

        return result;
    }

    private static async Task<RpmPackageMetadata> ReadMainHeaderMetadataAsync(Stream stream, CancellationToken ct)
    {
        var intro = new byte[16];
        var introRead = await ReadExactAsync(stream, intro, 16, ct);
        if (introRead < 16)
            throw new InvalidDataException("Truncated RPM main header intro structure.");

        if (intro[0] != 0x8E || intro[1] != 0xAD || intro[2] != 0xE8)
            throw new InvalidDataException("Invalid RPM main header magic.");

        var nindex = BinaryPrimitives.ReadInt32BigEndian(intro.AsSpan(8, 4));
        var hsize = BinaryPrimitives.ReadInt32BigEndian(intro.AsSpan(12, 4));

        if (nindex < 0 || hsize < 0 || nindex > 100_000 || hsize > 100_000_000)
            throw new InvalidDataException($"Corrupted RPM main header dimensions: index={nindex}, dataSize={hsize}");

        var indexBytes = new byte[nindex * 16];
        var indexRead = await ReadExactAsync(stream, indexBytes, indexBytes.Length, ct);
        if (indexRead < indexBytes.Length)
            throw new InvalidDataException("Truncated RPM main header index entries.");

        var dataBytes = new byte[hsize];
        var dataRead = await ReadExactAsync(stream, dataBytes, hsize, ct);
        if (dataRead < hsize)
            throw new InvalidDataException("Truncated RPM main header data section.");

        return RpmPackageMetadata.Parse(indexBytes, nindex, dataBytes);
    }

    internal static Task<Dictionary<string, ParsedSharedLibrary>> ExtractSharedLibrariesAsync(
        string rpmPath,
        CancellationToken ct) => ExtractSharedLibrariesAsync(rpmPath, 0, ct);

    /// <summary>
    /// Checks whether an RPM package contains at least one parseable 64-bit ELF shared library binary.
    /// Useful for quickly filtering out auxiliary packages (e.g. docs, headers, tools) from ABI diff pipelines.
    /// Throws InvalidDataException or NotSupportedException if the RPM package cannot be parsed.
    /// </summary>
    public static async Task<bool> ContainsSharedLibrariesAsync(string rpmPath, CancellationToken ct = default)
    {
        if (!File.Exists(rpmPath))
            return false;

        var libs = await ExtractSharedLibrariesAsync(rpmPath, ct);
        return libs.Count > 0;
    }

    private static async Task SkipHeaderStructureAsync(Stream stream, bool alignTo8Bytes, CancellationToken ct)
    {
        var intro = new byte[16];
        var introRead = await stream.ReadAsync(intro.AsMemory(0, 16), ct);
        if (introRead < 16)
            throw new InvalidDataException("Truncated RPM header intro structure.");

        if (intro[0] != 0x8E || intro[1] != 0xAD || intro[2] != 0xE8)
            throw new InvalidDataException("Invalid RPM header magic.");

        var nindex = BinaryPrimitives.ReadInt32BigEndian(intro.AsSpan(8, 4));
        var hsize = BinaryPrimitives.ReadInt32BigEndian(intro.AsSpan(12, 4));

        if (nindex < 0 || hsize < 0 || nindex > 100_000 || hsize > 100_000_000)
            throw new InvalidDataException($"Corrupted RPM header dimensions: index={nindex}, dataSize={hsize}");

        long bytesToSkip = (long)nindex * 16 + hsize;
        if (alignTo8Bytes)
        {
            var totalWithIntro = 16 + bytesToSkip;
            var pad = (8 - (totalWithIntro % 8)) % 8;
            bytesToSkip += pad;
        }

        if (stream.CanSeek)
        {
            stream.Seek(bytesToSkip, SeekOrigin.Current);
        }
        else
        {
            var buffer = new byte[8192];
            while (bytesToSkip > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, bytesToSkip);
                var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct);
                if (read <= 0) break;
                bytesToSkip -= read;
            }
        }
    }

    internal static Stream CreateDecompressionStream(Stream stream)
    {
        // Probe first 4 bytes to detect compression format
        var magic = new byte[4];
        var read = stream.Read(magic, 0, 4);
        if (read < 4)
            throw new InvalidDataException("Payload stream truncated before compression header.");

        // Rewind 4 bytes
        if (stream.CanSeek)
        {
            stream.Seek(-read, SeekOrigin.Current);
        }

        // GZip: 1F 8B
        if (magic[0] == 0x1F && magic[1] == 0x8B)
        {
            return new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
        }

        // ZStandard: 28 B5 2F FD
        if (magic[0] == 0x28 && magic[1] == 0xB5 && magic[2] == 0x2F && magic[3] == 0xFD)
        {
            return new ZstdSharp.DecompressionStream(stream, leaveOpen: true);
        }

        // Uncompressed cpio: magic "070701" or "070702"
        if (magic[0] == (byte)'0' && magic[1] == (byte)'7' && magic[2] == (byte)'0' && magic[3] == (byte)'7')
        {
            return stream;
        }

        // xz magic: FD 37 7A 58 5A 00
        if (magic[0] == 0xFD && magic[1] == 0x37 && magic[2] == 0x7A && magic[3] == 0x58)
        {
            throw new NotSupportedException("Unsupported RPM payload compression format: xz. Managed engine currently supports gzip and zstd payloads.");
        }

        throw new NotSupportedException(
            string.Format(CultureInfo.InvariantCulture, "Unsupported or unrecognized RPM payload compression format (magic: {0:X2} {1:X2} {2:X2} {3:X2}).",
                magic[0], magic[1], magic[2], magic[3]));
    }

    internal static async Task ExtractNewcCpioLibrariesAsync(
        Stream cpioStream,
        byte[] initialMagic,
        Dictionary<string, ParsedSharedLibrary> result,
        long maxTotalBytes,
        CancellationToken ct)
    {
        const int maxEntryCount = 10_000;
        const int maxNameSize = 4096;
        const long maxSingleFileSize = 500L * 1024 * 1024; // 500 MB
        var effectiveMaxTotalBytes = maxTotalBytes > 0 ? maxTotalBytes : 2L * 1024 * 1024 * 1024; // default: 2 GB

        var totalEntries = 0;
        long totalBytes = 0;
        var seenSoCount = 0;
        var parsedSoCount = 0;
        var sawTrailer = false;

        var headerBuf = new byte[110];
        var first = true;

        while (true)
        {
            if (first)
            {
                Array.Copy(initialMagic, 0, headerBuf, 0, 6);
                var rem = await ReadExactAsync(cpioStream, headerBuf.AsMemory(6, 104), ct);
                if (rem < 104)
                    break;
                first = false;
            }
            else
            {
                var read = await ReadExactAsync(cpioStream, headerBuf, 110, ct);
                if (read < 110)
                    break;
            }

            var magic = Encoding.ASCII.GetString(headerBuf, 0, 6);
            if (magic != "070701" && magic != "070702")
                break; // Not newc header or reached end/padding

            var modeHex = Encoding.ASCII.GetString(headerBuf, 14, 8);
            var fileSizeHex = Encoding.ASCII.GetString(headerBuf, 54, 8);
            var nameSizeHex = Encoding.ASCII.GetString(headerBuf, 94, 8);

            var mode = Convert.ToInt32(modeHex, 16);
            var fileSize = Convert.ToInt64(fileSizeHex, 16);
            var nameSize = Convert.ToInt32(nameSizeHex, 16);

            if (nameSize < 0 || nameSize > maxNameSize)
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "CPIO entry name size out of bounds: {0} bytes (max: {1}).", nameSize, maxNameSize));

            if (fileSize < 0 || fileSize > maxSingleFileSize)
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "CPIO file size out of bounds: {0} bytes (max: {1}).", fileSize, maxSingleFileSize));

            totalEntries++;
            if (totalEntries > maxEntryCount)
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "CPIO entry count exceeded safety limit of {0}.", maxEntryCount));

            totalBytes += fileSize;
            if (totalBytes > effectiveMaxTotalBytes)
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "CPIO total payload uncompressed bytes exceeded safety limit of {0} bytes.", effectiveMaxTotalBytes));

            var nameBuf = new byte[nameSize];
            var nameRead = await ReadExactAsync(cpioStream, nameBuf, nameSize, ct);
            if (nameRead < nameSize)
                throw new InvalidDataException("Truncated CPIO entry name.");

            // Read name padding to 4-byte boundary: (110 + nameSize) aligned to 4
            var namePad = (4 - ((110 + nameSize) % 4)) % 4;
            if (namePad > 0)
            {
                var padBuf = new byte[namePad];
                var padRead = await ReadExactAsync(cpioStream, padBuf, namePad, ct);
                if (padRead < namePad)
                    throw new InvalidDataException("Truncated CPIO name padding.");
            }

            var entryName = Encoding.ASCII.GetString(nameBuf).TrimEnd('\0');
            if (entryName == "TRAILER!!!")
            {
                sawTrailer = true;
                break;
            }

            var isRegularFile = (mode & 0xF000) == 0x8000;
            var isSharedLibName = entryName.EndsWith(".so", StringComparison.OrdinalIgnoreCase) ||
                                  entryName.Contains(".so.", StringComparison.OrdinalIgnoreCase);

            if (isRegularFile && isSharedLibName && fileSize > 0)
            {
                var elfBytes = new byte[fileSize];
                var elfRead = await ReadExactAsync(cpioStream, elfBytes, (int)fileSize, ct);
                if (elfRead < fileSize)
                    throw new InvalidDataException($"Truncated CPIO file payload for '{entryName}': expected {fileSize} bytes, read {elfRead}.");

                // Check for ELF magic header (0x7F, 'E', 'L', 'F').
                if (elfBytes.Length >= 4 &&
                    elfBytes[0] == 0x7F && elfBytes[1] == 0x45 && elfBytes[2] == 0x4C && elfBytes[3] == 0x46)
                {
                    seenSoCount++;
                    var parsed = ParseElf64(elfBytes, entryName);
                    if (parsed != null)
                    {
                        var libName = Path.GetFileName(entryName);
                        result[libName] = parsed;
                        parsedSoCount++;
                    }
                }
            }
            else
            {
                // Skip non-library files, directories, symlink destinations, or empty entries
                await SkipBytesAsync(cpioStream, fileSize, ct);
            }

            // Read data padding to 4-byte boundary
            var dataPad = (4 - ((int)(fileSize % 4))) % 4;
            if (dataPad > 0)
            {
                var padBuf = new byte[dataPad];
                var padRead = await ReadExactAsync(cpioStream, padBuf, dataPad, ct);
                if (padRead < dataPad)
                    throw new InvalidDataException("Truncated CPIO data padding.");
            }
        }

        if (totalEntries == 0)
        {
            throw new InvalidDataException("CPIO payload archive is empty or does not contain valid newc headers.");
        }

        if (!sawTrailer)
        {
            throw new InvalidDataException("CPIO archive truncated: missing TRAILER!!! entry.");
        }

        if (seenSoCount > 0 && parsedSoCount < seenSoCount)
        {
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                "Found {0} shared library file(s) in RPM package, but only {1} could be parsed as valid 64-bit ELF binaries.",
                seenSoCount, parsedSoCount));
        }
    }

    internal static async Task ExtractStrippedCpioLibrariesAsync(
        Stream cpioStream,
        byte[] initialMagic,
        IReadOnlyList<RpmFileEntry> files,
        Dictionary<string, ParsedSharedLibrary> result,
        long maxTotalBytes,
        CancellationToken ct)
    {
        const int maxEntryCount = 50_000;
        const long maxSingleFileSize = 500L * 1024 * 1024; // 500 MB
        var effectiveMaxTotalBytes = maxTotalBytes > 0 ? maxTotalBytes : 2L * 1024 * 1024 * 1024; // default: 2 GB

        var totalEntries = 0;
        long totalBytes = 0;
        var seenSoCount = 0;
        var parsedSoCount = 0;
        var digestToLib = new Dictionary<string, ParsedSharedLibrary>(StringComparer.Ordinal);

        var first = true;
        var headerBuf = new byte[16];

        while (true)
        {
            if (first)
            {
                Array.Copy(initialMagic, 0, headerBuf, 0, 6);
                var rem = await ReadExactAsync(cpioStream, headerBuf.AsMemory(6, 10), ct);
                if (rem < 10)
                    throw new InvalidDataException("Truncated stripped CPIO first entry header.");
                first = false;
            }
            else
            {
                var read = await ReadExactAsync(cpioStream, headerBuf, 16, ct);
                if (read == 0)
                    break;
                if (read < 16)
                {
                    var allZeros = true;
                    for (var z = 0; z < read; z++)
                    {
                        if (headerBuf[z] != 0) { allZeros = false; break; }
                    }
                    if (allZeros) break;
                    throw new InvalidDataException("Truncated stripped CPIO entry header.");
                }
            }

            var magicStr = Encoding.ASCII.GetString(headerBuf, 0, 6);
            if (magicStr == "070701" || magicStr == "070702")
            {
                var restOfHeader = new byte[94];
                var restRead = await ReadExactAsync(cpioStream, restOfHeader, 94, ct);
                if (restRead == 94)
                {
                    var nameSizeHex = Encoding.ASCII.GetString(restOfHeader, 80, 8);
                    if (int.TryParse(nameSizeHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var nameSize) && nameSize > 0 && nameSize < 100)
                    {
                        var nameBytes = new byte[nameSize];
                        await ReadExactAsync(cpioStream, nameBytes, nameSize, ct);
                        var entryName = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
                        if (entryName == "TRAILER!!!")
                        {
                            break;
                        }
                    }
                }
                break;
            }

            if (headerBuf[0] == 0)
            {
                var nonZeroIdx = -1;
                for (var z = 0; z < 16; z++)
                {
                    if (headerBuf[z] != 0) { nonZeroIdx = z; break; }
                }

                if (nonZeroIdx < 0)
                {
                    continue;
                }
                else
                {
                    var shift = 16 - nonZeroIdx;
                    Array.Copy(headerBuf, nonZeroIdx, headerBuf, 0, shift);
                    var fillRead = await ReadExactAsync(cpioStream, headerBuf.AsMemory(shift, nonZeroIdx), ct);
                    if (fillRead < nonZeroIdx)
                        break;
                    magicStr = Encoding.ASCII.GetString(headerBuf, 0, 6);
                }
            }

            if (magicStr != "07070X")
            {
                break;
            }

            var fileIdxHex = Encoding.ASCII.GetString(headerBuf, 6, 8);
            if (!int.TryParse(fileIdxHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var fileIdx))
            {
                throw new InvalidDataException($"Invalid hex file index in stripped CPIO header: '{fileIdxHex}'");
            }

            if (fileIdx < 0 || fileIdx >= files.Count)
            {
                throw new InvalidDataException($"Stripped CPIO file index {fileIdx} exceeds package file table count {files.Count}.");
            }

            var entry = files[fileIdx];
            var fileSize = entry.Size;

            if (fileSize < 0 || fileSize > maxSingleFileSize)
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "File size out of bounds: {0} bytes (max: {1}).", fileSize, maxSingleFileSize));

            totalEntries++;
            if (totalEntries > maxEntryCount)
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "Entry count exceeded safety limit of {0}.", maxEntryCount));

            totalBytes += fileSize;
            if (totalBytes > effectiveMaxTotalBytes)
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "Total payload uncompressed bytes exceeded safety limit of {0} bytes.", effectiveMaxTotalBytes));

            var isRegularFile = (entry.Mode & 0xF000) == 0x8000;
            var isSharedLibName = entry.Path.EndsWith(".so", StringComparison.OrdinalIgnoreCase) ||
                                  entry.Path.Contains(".so.", StringComparison.OrdinalIgnoreCase);

            if (isRegularFile && isSharedLibName && fileSize > 0)
            {
                var elfBytes = new byte[fileSize];
                var readBytes = await ReadExactAsync(cpioStream, elfBytes, (int)fileSize, ct);
                if (readBytes < fileSize)
                    throw new InvalidDataException($"Truncated file payload for {entry.Path}: expected {fileSize} bytes, read {readBytes}.");

                if (elfBytes.Length >= 4 &&
                    elfBytes[0] == 0x7F && elfBytes[1] == 0x45 && elfBytes[2] == 0x4C && elfBytes[3] == 0x46)
                {
                    seenSoCount++;
                    var parsed = ParseElf64(elfBytes, entry.Path);
                    if (parsed != null)
                    {
                        var libName = Path.GetFileName(entry.Path);
                        result[libName] = parsed;
                        parsedSoCount++;

                        if (!string.IsNullOrEmpty(entry.Digest))
                        {
                            digestToLib[entry.Digest] = parsed;
                        }
                    }
                }
            }
            else
            {
                await SkipBytesAsync(cpioStream, fileSize, ct);
            }

            var dataPad = (4 - ((int)(fileSize % 4))) % 4;
            if (dataPad > 0)
            {
                var padBuf = new byte[dataPad];
                var padRead = await ReadExactAsync(cpioStream, padBuf, dataPad, ct);
                if (padRead < dataPad)
                    throw new InvalidDataException("Truncated stripped CPIO data padding.");
            }
        }

        foreach (var file in files)
        {
            var isRegularFile = (file.Mode & 0xF000) == 0x8000;
            var isSharedLibName = file.Path.EndsWith(".so", StringComparison.OrdinalIgnoreCase) ||
                                  file.Path.Contains(".so.", StringComparison.OrdinalIgnoreCase);
            if (isRegularFile && isSharedLibName && !string.IsNullOrEmpty(file.Digest))
            {
                var libName = Path.GetFileName(file.Path);
                if (!result.ContainsKey(libName) && digestToLib.TryGetValue(file.Digest, out var existingLib))
                {
                    result[libName] = existingLib;
                }
            }
        }

        if (totalEntries == 0)
        {
            throw new InvalidDataException("CPIO payload archive is empty or does not contain valid entries.");
        }

        if (seenSoCount > 0 && parsedSoCount < seenSoCount)
        {
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                "Found {0} shared library file(s) in RPM package, but only {1} could be parsed as valid 64-bit ELF binaries.",
                seenSoCount, parsedSoCount));
        }
    }

    internal static ParsedSharedLibrary? ParseElf64(byte[] elf, string filename)
    {
        if (elf.Length < 64)
            return null;

        // Check magic: \x7fELF
        if (elf[0] != 0x7F || elf[1] != 0x45 || elf[2] != 0x4C || elf[3] != 0x46)
            return null;

        var elfClass = elf[4]; // 1 = 32-bit, 2 = 64-bit
        if (elfClass != 2) // Support 64-bit ELF
            return null;

        var isLittleEndian = elf[5] == 1;
        if (!isLittleEndian)
            return null;

        var shoff = BinaryPrimitives.ReadInt64LittleEndian(elf.AsSpan(40, 8));
        var shentsize = BinaryPrimitives.ReadInt16LittleEndian(elf.AsSpan(58, 2));
        var shnum = BinaryPrimitives.ReadInt16LittleEndian(elf.AsSpan(60, 2));

        if (shoff <= 0 || shnum <= 0 || shentsize < 64 || shoff + (shnum * shentsize) > elf.Length)
            return null;

        // Section header structures
        int? dynamicSecIdx = null;
        int? dynsymSecIdx = null;

        var sectionHeaders = new ElfSectionHeader[shnum];
        for (int i = 0; i < shnum; i++)
        {
            var offset = (int)(shoff + (i * shentsize));
            var sh = new ElfSectionHeader
            {
                NameOffset = BinaryPrimitives.ReadInt32LittleEndian(elf.AsSpan(offset, 4)),
                Type = BinaryPrimitives.ReadInt32LittleEndian(elf.AsSpan(offset + 4, 4)),
                Flags = BinaryPrimitives.ReadInt64LittleEndian(elf.AsSpan(offset + 8, 8)),
                Addr = BinaryPrimitives.ReadInt64LittleEndian(elf.AsSpan(offset + 16, 8)),
                Offset = BinaryPrimitives.ReadInt64LittleEndian(elf.AsSpan(offset + 24, 8)),
                Size = BinaryPrimitives.ReadInt64LittleEndian(elf.AsSpan(offset + 32, 8)),
                Link = BinaryPrimitives.ReadInt32LittleEndian(elf.AsSpan(offset + 40, 4)),
                Info = BinaryPrimitives.ReadInt32LittleEndian(elf.AsSpan(offset + 44, 4)),
                EntSize = BinaryPrimitives.ReadInt64LittleEndian(elf.AsSpan(offset + 56, 8))
            };
            sectionHeaders[i] = sh;

            // SHT_DYNAMIC = 6
            if (sh.Type == 6)
                dynamicSecIdx = i;
            // SHT_DYNSYM = 11
            else if (sh.Type == 11)
                dynsymSecIdx = i;
        }

        string? soname = null;
        var exportedSymbols = new HashSet<string>(StringComparer.Ordinal);

        // Parse .dynamic for DT_SONAME (tag 14)
        if (dynamicSecIdx.HasValue)
        {
            var dynSh = sectionHeaders[dynamicSecIdx.Value];
            var strTabIdx = dynSh.Link;
            byte[]? dynStr = null;
            if (strTabIdx >= 0 && strTabIdx < shnum)
            {
                var strSh = sectionHeaders[strTabIdx];
                if (strSh.Offset + strSh.Size <= elf.Length)
                {
                    dynStr = new byte[strSh.Size];
                    Array.Copy(elf, (int)strSh.Offset, dynStr, 0, (int)strSh.Size);
                }
            }

            if (dynStr != null && dynSh.Offset + dynSh.Size <= elf.Length)
            {
                var entryCount = (int)(dynSh.Size / 16);
                for (int e = 0; e < entryCount; e++)
                {
                    var entryOffset = (int)(dynSh.Offset + (e * 16));
                    var tag = BinaryPrimitives.ReadInt64LittleEndian(elf.AsSpan(entryOffset, 8));
                    var val = BinaryPrimitives.ReadUInt64LittleEndian(elf.AsSpan(entryOffset + 8, 8));

                    if (tag == 0) // DT_NULL
                        break;

                    if (tag == 14) // DT_SONAME
                    {
                        soname = ReadNullTerminatedString(dynStr, (int)val);
                    }
                }
            }
        }

        // Parse .dynsym for exported symbols
        if (dynsymSecIdx.HasValue)
        {
            var symSh = sectionHeaders[dynsymSecIdx.Value];
            var strTabIdx = symSh.Link;
            byte[]? dynStr = null;
            if (strTabIdx >= 0 && strTabIdx < shnum)
            {
                var strSh = sectionHeaders[strTabIdx];
                if (strSh.Offset + strSh.Size <= elf.Length)
                {
                    dynStr = new byte[strSh.Size];
                    Array.Copy(elf, (int)strSh.Offset, dynStr, 0, (int)strSh.Size);
                }
            }

            if (dynStr != null && symSh.Offset + symSh.Size <= elf.Length)
            {
                var symEntSize = symSh.EntSize > 0 ? (int)symSh.EntSize : 24;
                var symCount = (int)(symSh.Size / symEntSize);

                for (int s = 0; s < symCount; s++)
                {
                    var sOffset = (int)(symSh.Offset + (s * symEntSize));
                    if (sOffset + 24 > elf.Length)
                        break;

                    var stName = BinaryPrimitives.ReadInt32LittleEndian(elf.AsSpan(sOffset, 4));
                    var stInfo = elf[sOffset + 4];
                    var stOther = elf[sOffset + 5];
                    var stShndx = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(sOffset + 6, 2));

                    var bind = (byte)(stInfo >> 4); // STB_GLOBAL = 1, STB_WEAK = 2
                    var visibility = (byte)(stOther & 0x03); // STV_DEFAULT = 0, STV_PROTECTED = 3

                    // Defined symbol in shared object (not SHN_UNDEF = 0)
                    if (stShndx != 0 && (bind == 1 || bind == 2) && (visibility == 0 || visibility == 3))
                    {
                        var symName = ReadNullTerminatedString(dynStr, stName);
                        if (!string.IsNullOrEmpty(symName))
                        {
                            exportedSymbols.Add(symName);
                        }
                    }
                }
            }
        }

        return new ParsedSharedLibrary(filename, soname, exportedSymbols);
    }

    private static string? ReadNullTerminatedString(byte[] buffer, int offset)
    {
        if (offset < 0 || offset >= buffer.Length)
            return null;

        var end = offset;
        while (end < buffer.Length && buffer[end] != 0)
        {
            end++;
        }

        return Encoding.UTF8.GetString(buffer, offset, end - offset);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, count - totalRead), ct);
            if (read <= 0)
                break;
            totalRead += read;
        }
        return totalRead;
    }

    private static async Task<int> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var totalRead = 0;
        var count = buffer.Length;
        while (totalRead < count)
        {
            var read = await stream.ReadAsync(buffer.Slice(totalRead, count - totalRead), ct);
            if (read <= 0)
                break;
            totalRead += read;
        }
        return totalRead;
    }

    private static async Task SkipBytesAsync(Stream stream, long count, CancellationToken ct)
    {
        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Current);
        }
        else
        {
            var buffer = new byte[8192];
            var remaining = count;
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct);
                if (read <= 0)
                {
                    throw new InvalidDataException("Unexpected end of CPIO archive while skipping entry data.");
                }
                remaining -= read;
            }
        }
    }

    private class ElfSectionHeader
    {
        public int NameOffset { get; set; }
        public int Type { get; set; }
        public long Flags { get; set; }
        public long Addr { get; set; }
        public long Offset { get; set; }
        public long Size { get; set; }
        public int Link { get; set; }
        public int Info { get; set; }
        public long EntSize { get; set; }
    }
}

/// <summary>
/// Metadata model representing an inspected ELF shared library and its public ABI footprint.
/// </summary>
public record ParsedSharedLibrary(
    string FilePath,
    string? Soname,
    IReadOnlySet<string> ExportedSymbols);

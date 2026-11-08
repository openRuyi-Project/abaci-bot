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
                Details: details.ToString().TrimEnd());
        }

        if (hasChange)
        {
            return new AbiDiffResult(
                Succeeded: true,
                HasAbiChange: true,
                IsBreakingChange: false,
                Details: details.ToString().TrimEnd());
        }

        return new AbiDiffResult(
            Succeeded: true,
            HasAbiChange: false,
            IsBreakingChange: false,
            Details: "[Managed ELF Engine] All shared libraries and exported symbols are identical.");
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

        // Read Signature Header
        await SkipHeaderStructureAsync(rpmFileStream, alignTo8Bytes: true, ct);

        // Read Main Header
        await SkipHeaderStructureAsync(rpmFileStream, alignTo8Bytes: false, ct);

        // Current stream position is now at the beginning of payload
        await using var decompressedStream = CreateDecompressionStream(rpmFileStream);

        // Parse CPIO (newc format)
        await ExtractCpioLibrariesAsync(decompressedStream, result, maxTotalBytes, ct);

        return result;
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

    private static async Task ExtractCpioLibrariesAsync(
        Stream cpioStream,
        Dictionary<string, ParsedSharedLibrary> result,
        long maxTotalBytes,
        CancellationToken ct)
    {
        const int maxEntryCount = 10_000;
        const int maxNameSize = 4096;
        const long maxSingleFileSize = 256 * 1024 * 1024; // 256 MB
        var effectiveMaxTotalBytes = maxTotalBytes > 0 ? maxTotalBytes : 1024L * 1024 * 1024; // default: 1 GB

        var totalEntries = 0;
        long totalBytes = 0;
        var seenSoCount = 0;
        var parsedSoCount = 0;

        var headerBuf = new byte[110];
        while (true)
        {
            var read = await ReadExactAsync(cpioStream, headerBuf, 110, ct);
            if (read < 110)
                break;

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
            await ReadExactAsync(cpioStream, nameBuf, nameSize, ct);

            // Read name padding to 4-byte boundary: (110 + nameSize) aligned to 4
            var namePad = (4 - ((110 + nameSize) % 4)) % 4;
            if (namePad > 0)
            {
                var padBuf = new byte[namePad];
                await ReadExactAsync(cpioStream, padBuf, namePad, ct);
            }

            var entryName = Encoding.ASCII.GetString(nameBuf).TrimEnd('\0');
            if (entryName == "TRAILER!!!")
                break;

            var isRegularFile = (mode & 0xF000) == 0x8000;
            var isSharedLibName = entryName.EndsWith(".so", StringComparison.OrdinalIgnoreCase) ||
                                  entryName.Contains(".so.", StringComparison.OrdinalIgnoreCase);

            if (isRegularFile && isSharedLibName && fileSize > 0)
            {
                var elfBytes = new byte[fileSize];
                await ReadExactAsync(cpioStream, elfBytes, (int)fileSize, ct);

                // Check for ELF magic header (0x7F, 'E', 'L', 'F').
                // Non-binary files (e.g. GNU ld scripts like "GROUP ( libdemo.so.1 )") are ignored.
                if (elfBytes.Length >= 4 &&
                    elfBytes[0] == 0x7F && elfBytes[1] == 0x45 && elfBytes[2] == 0x4C && elfBytes[3] == 0x46)
                {
                    seenSoCount++;
                    try
                    {
                        var parsed = ParseElf64(elfBytes, entryName);
                        if (parsed != null)
                        {
                            var libName = Path.GetFileName(entryName);
                            result[libName] = parsed;
                            parsedSoCount++;
                        }
                    }
                    catch
                    {
                        // Unparsed entry will be caught by validation below
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
                await ReadExactAsync(cpioStream, padBuf, dataPad, ct);
            }
        }

        if (totalEntries == 0)
        {
            throw new InvalidDataException("CPIO payload archive is empty or does not contain valid newc headers.");
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

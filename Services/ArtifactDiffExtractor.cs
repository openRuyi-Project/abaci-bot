using System.IO.Compression;
using System.Text;

namespace abaci_bot.Services;

/// <summary>
/// Deep module abstracting workflow artifact diff extraction.
/// Detects ZIP stream magic numbers (0x50, 0x4B, 0x03, 0x04) vs raw text patch streams,
/// preventing InvalidDataException when downloading single uncompressed artifacts from GitHub Actions.
/// </summary>
public static class ArtifactDiffExtractor
{
    private static readonly byte[] ZipMagic = { 0x50, 0x4B, 0x03, 0x04 };

    public static async Task<string> ExtractDiffContentAsync(Stream stream, CancellationToken ct = default)
    {
        if (stream == null)
            return string.Empty;

        // Ensure we can inspect the first 4 bytes
        if (!stream.CanSeek)
        {
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            ms.Position = 0;
            stream = ms;
        }

        var header = new byte[4];
        var bytesRead = await stream.ReadAsync(header.AsMemory(0, 4), ct);
        stream.Position = 0;

        bool isZip = bytesRead >= 4 &&
                     header[0] == ZipMagic[0] &&
                     header[1] == ZipMagic[1] &&
                     header[2] == ZipMagic[2] &&
                     header[3] == ZipMagic[3];

        if (isZip)
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var diffEntry = archive.Entries.FirstOrDefault(e =>
                e.Name.EndsWith(".diff", StringComparison.OrdinalIgnoreCase) ||
                e.Name.EndsWith(".patch", StringComparison.OrdinalIgnoreCase));

            if (diffEntry != null)
            {
                using var reader = new StreamReader(diffEntry.Open(), Encoding.UTF8);
                return await reader.ReadToEndAsync(ct);
            }

            return string.Empty;
        }

        // Raw text stream (.diff / .patch)
        using var textReader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        return await textReader.ReadToEndAsync(ct);
    }
}

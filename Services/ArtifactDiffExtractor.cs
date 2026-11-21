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
    private static readonly string[] LineSeparators = { "\r\n", "\r", "\n" };

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
                // Max 10 MiB safety limit against decompression bomb
                if (diffEntry.Length > 10 * 1024 * 1024)
                    return string.Empty;

                using var reader = new StreamReader(diffEntry.Open(), Encoding.UTF8);
                return await reader.ReadToEndAsync(ct);
            }

            return string.Empty;
        }

        // Raw text stream (.diff / .patch)
        using var textReader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        return await textReader.ReadToEndAsync(ct);
    }

    /// <summary>
    /// Parses a unified diff and extracts paired (-#!RemoteAsset, +#!RemoteAsset) checksum changes
    /// strictly belonging to the specified target file path.
    /// Prevents cross-file corruption when a diff contains changes across multiple files.
    /// </summary>
    public static List<(string OldLine, string NewLine)> ExtractFileRemoteAssetChanges(string diffContent, string targetPath)
    {
        var results = new List<(string OldLine, string NewLine)>();
        if (string.IsNullOrWhiteSpace(diffContent) || string.IsNullOrWhiteSpace(targetPath))
            return results;

        var normalizedTarget = targetPath.Trim().Replace('\\', '/');
        if (normalizedTarget.StartsWith('/'))
            normalizedTarget = normalizedTarget.TrimStart('/');

        // Split diff into per-file chunks by "diff --git " or lines starting with "--- "
        var lines = diffContent.Split(LineSeparators, StringSplitOptions.None);
        bool inTargetFile = false;
        var removedInHunk = new List<string>();
        var addedInHunk = new List<string>();

        void FlushHunk()
        {
            if (inTargetFile && removedInHunk.Count > 0 && removedInHunk.Count == addedInHunk.Count)
            {
                for (int i = 0; i < removedInHunk.Count; i++)
                {
                    results.Add((removedInHunk[i], addedInHunk[i]));
                }
            }
            removedInHunk.Clear();
            addedInHunk.Clear();
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal) || line.StartsWith("--- ", StringComparison.Ordinal))
            {
                FlushHunk();
                inTargetFile = false;
            }

            if (line.StartsWith("+++ b/", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var path = line.Substring(line.IndexOf("+++ ", StringComparison.Ordinal) + 4).Trim();
                if (path.StartsWith("b/", StringComparison.Ordinal))
                    path = path.Substring(2);
                path = path.Replace('\\', '/').TrimStart('/');

                inTargetFile = string.Equals(path, normalizedTarget, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                FlushHunk();
                continue;
            }

            if (inTargetFile)
            {
                if (line.StartsWith("-#!RemoteAsset:", StringComparison.Ordinal))
                {
                    removedInHunk.Add(line.Substring(1).Trim());
                }
                else if (line.StartsWith("+#!RemoteAsset:", StringComparison.Ordinal))
                {
                    addedInHunk.Add(line.Substring(1).Trim());
                }
            }
        }

        FlushHunk();
        return results;
    }
}


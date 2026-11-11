using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using abaci_bot.Options;
using abaci_bot.Services;
using Xunit;

namespace abaci_bot.Tests;

public class ManagedElfDiffRunnerTests
{
    [Fact]
    public void CompareLibraries_EmptyLibraries_ReturnsClean()
    {
        var oldLibs = new Dictionary<string, ParsedSharedLibrary>();
        var newLibs = new Dictionary<string, ParsedSharedLibrary>();

        var result = ManagedElfDiffRunner.CompareLibraries(oldLibs, newLibs);

        Assert.True(result.Succeeded);
        Assert.False(result.HasAbiChange);
        Assert.False(result.IsBreakingChange);
        Assert.Contains("No shared libraries", result.Details);
    }

    [Fact]
    public void CompareLibraries_IdenticalSymbolsAndSoname_ReturnsClean()
    {
        var symbols = new HashSet<string> { "foo_init", "foo_compute", "foo_cleanup" };
        var oldLibs = new Dictionary<string, ParsedSharedLibrary>
        {
            ["libfoo.so.1"] = new("usr/lib64/libfoo.so.1", "libfoo.so.1", symbols)
        };
        var newLibs = new Dictionary<string, ParsedSharedLibrary>
        {
            ["libfoo.so.1"] = new("usr/lib64/libfoo.so.1", "libfoo.so.1", new HashSet<string>(symbols))
        };

        var result = ManagedElfDiffRunner.CompareLibraries(oldLibs, newLibs);

        Assert.True(result.Succeeded);
        Assert.False(result.HasAbiChange);
        Assert.False(result.IsBreakingChange);
        Assert.Contains("identical", result.Details);
    }

    [Fact]
    public void CompareLibraries_SonameChanged_ReturnsBreakingChange()
    {
        var symbols = new HashSet<string> { "foo_init", "foo_compute" };
        var oldLibs = new Dictionary<string, ParsedSharedLibrary>
        {
            ["libfoo.so.1"] = new("usr/lib64/libfoo.so.1", "libfoo.so.1", symbols)
        };
        var newLibs = new Dictionary<string, ParsedSharedLibrary>
        {
            ["libfoo.so.2"] = new("usr/lib64/libfoo.so.2", "libfoo.so.2", symbols)
        };

        var result = ManagedElfDiffRunner.CompareLibraries(oldLibs, newLibs);

        Assert.True(result.Succeeded);
        Assert.True(result.HasAbiChange);
        Assert.True(result.IsBreakingChange);
        Assert.Contains("Breaking", result.Details);
    }

    [Fact]
    public void CompareLibraries_SymbolRemoved_ReturnsBreakingChange()
    {
        var oldSymbols = new HashSet<string> { "foo_init", "foo_compute", "foo_deprecated" };
        var newSymbols = new HashSet<string> { "foo_init", "foo_compute" }; // foo_deprecated removed

        var oldLibs = new Dictionary<string, ParsedSharedLibrary>
        {
            ["libfoo.so.1"] = new("usr/lib64/libfoo.so.1", "libfoo.so.1", oldSymbols)
        };
        var newLibs = new Dictionary<string, ParsedSharedLibrary>
        {
            ["libfoo.so.1"] = new("usr/lib64/libfoo.so.1", "libfoo.so.1", newSymbols)
        };

        var result = ManagedElfDiffRunner.CompareLibraries(oldLibs, newLibs);

        Assert.True(result.Succeeded);
        Assert.True(result.HasAbiChange);
        Assert.True(result.IsBreakingChange);
        Assert.Contains("foo_deprecated", result.Details);
        Assert.Contains("removed", result.Details);
    }

    [Fact]
    public void CompareLibraries_SymbolsAddedOnly_ReturnsAbiChangeNotBreaking()
    {
        var oldSymbols = new HashSet<string> { "foo_init", "foo_compute" };
        var newSymbols = new HashSet<string> { "foo_init", "foo_compute", "foo_new_feature" };

        var oldLibs = new Dictionary<string, ParsedSharedLibrary>
        {
            ["libfoo.so.1"] = new("usr/lib64/libfoo.so.1", "libfoo.so.1", oldSymbols)
        };
        var newLibs = new Dictionary<string, ParsedSharedLibrary>
        {
            ["libfoo.so.1"] = new("usr/lib64/libfoo.so.1", "libfoo.so.1", newSymbols)
        };

        var result = ManagedElfDiffRunner.CompareLibraries(oldLibs, newLibs);

        Assert.True(result.Succeeded);
        Assert.True(result.HasAbiChange);
        Assert.False(result.IsBreakingChange);
        Assert.Contains("ABI Change", result.Details);
        Assert.Contains("foo_new_feature", result.Details);
    }

    [Fact]
    public void CompareLibraries_EntireLibraryRemoved_ReturnsBreakingChange()
    {
        var oldLibs = new Dictionary<string, ParsedSharedLibrary>
        {
            ["libfoo.so.1"] = new("usr/lib64/libfoo.so.1", "libfoo.so.1", new HashSet<string> { "foo_a" }),
            ["libbar.so.1"] = new("usr/lib64/libbar.so.1", "libbar.so.1", new HashSet<string> { "bar_a" })
        };
        var newLibs = new Dictionary<string, ParsedSharedLibrary>
        {
            ["libfoo.so.1"] = new("usr/lib64/libfoo.so.1", "libfoo.so.1", new HashSet<string> { "foo_a" })
        };

        var result = ManagedElfDiffRunner.CompareLibraries(oldLibs, newLibs);

        Assert.True(result.Succeeded);
        Assert.True(result.IsBreakingChange);
        Assert.Contains("libbar.so.1", result.Details);
        Assert.Contains("removed", result.Details);
    }

    [Fact]
    public void CompareLibraries_NewLibraryAdded_ReturnsAbiChange()
    {
        var oldLibs = new Dictionary<string, ParsedSharedLibrary>
        {
            ["libfoo.so.1"] = new("usr/lib64/libfoo.so.1", "libfoo.so.1", new HashSet<string> { "foo_a" })
        };
        var newLibs = new Dictionary<string, ParsedSharedLibrary>
        {
            ["libfoo.so.1"] = new("usr/lib64/libfoo.so.1", "libfoo.so.1", new HashSet<string> { "foo_a" }),
            ["libbar.so.1"] = new("usr/lib64/libbar.so.1", "libbar.so.1", new HashSet<string> { "bar_a" })
        };

        var result = ManagedElfDiffRunner.CompareLibraries(oldLibs, newLibs);

        Assert.True(result.Succeeded);
        Assert.True(result.HasAbiChange);
        Assert.False(result.IsBreakingChange);
        Assert.Contains("libbar.so.1", result.Details);
    }

    [Fact]
    public async Task RunDiffAsync_TargetFileNotFound_ReturnsFailure()
    {
        var runner = new ManagedElfDiffRunner();
        var result = await runner.RunDiffAsync("C:/nonexistent/a.rpm", "C:/nonexistent/b.rpm");

        Assert.False(result.Succeeded);
        Assert.Contains("Target RPM file not found", result.ErrorMessage);
    }

    [Fact]
    public async Task RunDiffAsync_CorruptedRpmFile_ReturnsFailureGracefully()
    {
        var tempOld = Path.GetTempFileName();
        var tempNew = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(tempOld, new byte[] { 0x01, 0x02, 0x03 });
            await File.WriteAllBytesAsync(tempNew, new byte[] { 0x01, 0x02, 0x03 });

            var runner = new ManagedElfDiffRunner();
            var result = await runner.RunDiffAsync(tempOld, tempNew);

            Assert.False(result.Succeeded);
            Assert.Contains("Managed ELF analysis error", result.ErrorMessage);
        }
        finally
        {
            if (File.Exists(tempOld)) File.Delete(tempOld);
            if (File.Exists(tempNew)) File.Delete(tempNew);
        }
    }

    [Fact]
    public async Task RunDiffAsync_SyntheticRpmsWithGzipPayload_ComparesSuccessfully()
    {
        var tempOld = Path.GetTempFileName();
        var tempNew = Path.GetTempFileName();

        try
        {
            var oldRpmBytes = CreateSyntheticRpm(new[] { ("usr/lib64/libdemo.so.1", "libdemo.so.1", new[] { "demo_func_a", "demo_func_b" }) });
            var newRpmBytes = CreateSyntheticRpm(new[] { ("usr/lib64/libdemo.so.1", "libdemo.so.1", new[] { "demo_func_a", "demo_func_b", "demo_func_c" }) });

            await File.WriteAllBytesAsync(tempOld, oldRpmBytes);
            await File.WriteAllBytesAsync(tempNew, newRpmBytes);

            var runner = new ManagedElfDiffRunner();
            var result = await runner.RunDiffAsync(tempOld, tempNew);

            Assert.True(result.Succeeded);
            Assert.True(result.HasAbiChange);
            Assert.False(result.IsBreakingChange);
            Assert.Contains("demo_func_c", result.Details);
        }
        finally
        {
            if (File.Exists(tempOld)) File.Delete(tempOld);
            if (File.Exists(tempNew)) File.Delete(tempNew);
        }
    }

    [Fact]
    public void CreateDecompressionStream_ZstdMagic_ReturnsZstdDecompressionStream()
    {
        using var ms = new MemoryStream(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x00, 0x00 });
        using var stream = ManagedElfDiffRunner.CreateDecompressionStream(ms);
        Assert.IsType<ZstdSharp.DecompressionStream>(stream);
    }

    [Fact]
    public void CreateDecompressionStream_UncompressedMagic_ReturnsOriginalStream()
    {
        using var ms = new MemoryStream(Encoding.ASCII.GetBytes("070701sample"));
        using var stream = ManagedElfDiffRunner.CreateDecompressionStream(ms);
        Assert.Same(ms, stream);
    }

    [Fact]
    public void CreateDecompressionStream_TruncatedPayload_ThrowsInvalidDataException()
    {
        using var ms = new MemoryStream(new byte[] { 0x01, 0x02 });
        Assert.Throws<InvalidDataException>(() => ManagedElfDiffRunner.CreateDecompressionStream(ms));
    }

    [Fact]
    public void ParseElf64_InvalidOrUnsupportedInputs_HandlesCorrectly()
    {
        // Truncated buffer
        Assert.Null(ManagedElfDiffRunner.ParseElf64(new byte[20], "short.so"));

        // Invalid ELF magic
        Assert.Null(ManagedElfDiffRunner.ParseElf64(new byte[64], "nomagic.so"));

        // 32-bit ELF (EI_CLASS = 1) -> throws explicit NotSupportedException
        var elf32 = new byte[64];
        elf32[0] = 0x7F; elf32[1] = 0x45; elf32[2] = 0x4C; elf32[3] = 0x46; elf32[4] = 1;
        var ex32 = Assert.Throws<NotSupportedException>(() => ManagedElfDiffRunner.ParseElf64(elf32, "elf32.so"));
        Assert.Contains("32-bit ELF binary 'elf32.so' is not supported", ex32.Message);
        Assert.Contains("Only 64-bit ELF (ELFCLASS64) binaries are supported", ex32.Message);

        // Unrecognized ELF class (EI_CLASS = 3) -> throws explicit InvalidDataException
        var elfBadClass = new byte[64];
        elfBadClass[0] = 0x7F; elfBadClass[1] = 0x45; elfBadClass[2] = 0x4C; elfBadClass[3] = 0x46; elfBadClass[4] = 3;
        var exBadClass = Assert.Throws<InvalidDataException>(() => ManagedElfDiffRunner.ParseElf64(elfBadClass, "badclass.so"));
        Assert.Contains("Unrecognized ELF class '3' in 'badclass.so'", exBadClass.Message);

        // Big-endian ELF (EI_DATA = 2) -> throws explicit NotSupportedException
        var bigEndian = new byte[64];
        bigEndian[0] = 0x7F; bigEndian[1] = 0x45; bigEndian[2] = 0x4C; bigEndian[3] = 0x46;
        bigEndian[4] = 2; bigEndian[5] = 2;
        var exBe = Assert.Throws<NotSupportedException>(() => ManagedElfDiffRunner.ParseElf64(bigEndian, "be.so"));
        Assert.Contains("Big-endian ELF binary 'be.so' is not supported", exBe.Message);
        Assert.Contains("Only little-endian ELF binaries are supported", exBe.Message);

        // Unrecognized ELF data encoding (EI_DATA = 3) -> throws explicit InvalidDataException
        var elfBadEncoding = new byte[64];
        elfBadEncoding[0] = 0x7F; elfBadEncoding[1] = 0x45; elfBadEncoding[2] = 0x4C; elfBadEncoding[3] = 0x46;
        elfBadEncoding[4] = 2; elfBadEncoding[5] = 3;
        var exBadEncoding = Assert.Throws<InvalidDataException>(() => ManagedElfDiffRunner.ParseElf64(elfBadEncoding, "badenc.so"));
        Assert.Contains("Unrecognized ELF data encoding '3' in 'badenc.so'", exBadEncoding.Message);

        // Invalid shoff
        var badShoff = new byte[64];
        badShoff[0] = 0x7F; badShoff[1] = 0x45; badShoff[2] = 0x4C; badShoff[3] = 0x46;
        badShoff[4] = 2; badShoff[5] = 1;
        Assert.Null(ManagedElfDiffRunner.ParseElf64(badShoff, "badshoff.so"));
    }

    [Fact]
    public void CreateDecompressionStream_XzMagic_ReturnsXZStream()
    {
        using var ms = new MemoryStream(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 });
        using var stream = ManagedElfDiffRunner.CreateDecompressionStream(ms);
        Assert.IsType<SharpCompress.Compressors.Xz.XZStream>(stream);
    }

    [Fact]
    public void CreateDecompressionStream_Bzip2Magic_ReturnsBZip2Stream()
    {
        using var rawMs = new MemoryStream();
        using (var bzWriter = SharpCompress.Compressors.BZip2.BZip2Stream.Create(
            rawMs, SharpCompress.Compressors.CompressionMode.Compress, false, true, false))
        {
            bzWriter.Write(Encoding.ASCII.GetBytes("070701test"));
            bzWriter.Finish();
        }

        using var ms = new MemoryStream(rawMs.ToArray());
        using var stream = ManagedElfDiffRunner.CreateDecompressionStream(ms);
        Assert.IsType<SharpCompress.Compressors.BZip2.BZip2Stream>(stream);
    }

    [Fact]
    public void CreateDecompressionStream_LzmaMagic_ReturnsLzmaStream()
    {
        // 5 bytes props + 8 bytes uncompressed size
        var lzmaHeader = new byte[] { 0x5D, 0x00, 0x00, 0x00, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        using var ms = new MemoryStream(lzmaHeader);
        using var stream = ManagedElfDiffRunner.CreateDecompressionStream(ms);
        Assert.IsType<SharpCompress.Compressors.LZMA.LzmaStream>(stream);
    }

    [Fact]
    public void CreateDecompressionStream_LzmaTruncatedHeader_ThrowsInvalidDataException()
    {
        using var ms = new MemoryStream(new byte[] { 0x5D, 0x00, 0x00, 0x00 });
        Assert.Throws<InvalidDataException>(() => ManagedElfDiffRunner.CreateDecompressionStream(ms));
    }

    [Fact]
    public void CreateDecompressionStream_UnknownMagic_ThrowsNotSupportedException()
    {
        using var ms = new MemoryStream(new byte[] { 0x99, 0x88, 0x77, 0x66 });
        var ex = Assert.Throws<NotSupportedException>(() => ManagedElfDiffRunner.CreateDecompressionStream(ms));
        Assert.Contains("Unsupported or unrecognized", ex.Message);
    }

    [Fact]
    public async Task RunDiffAsync_TruncatedXzPayload_ReturnsFailureNotClean()
    {
        var tempOld = Path.GetTempFileName();
        var tempNew = Path.GetTempFileName();

        try
        {
            // Synthetic RPM with truncated XZ payload
            var xzPayloadRpm = CreateSyntheticRpmWithRawPayload(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00, 0x00 });
            await File.WriteAllBytesAsync(tempOld, xzPayloadRpm);
            await File.WriteAllBytesAsync(tempNew, xzPayloadRpm);

            var runner = new ManagedElfDiffRunner();
            var result = await runner.RunDiffAsync(tempOld, tempNew);

            // Fail-closed: Must return failure, NEVER Clean
            Assert.False(result.Succeeded);
            Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        }
        finally
        {
            if (File.Exists(tempOld)) File.Delete(tempOld);
            if (File.Exists(tempNew)) File.Delete(tempNew);
        }
    }

    [Fact]
    public async Task RunDiffAsync_Elf32InPackage_ReturnsExplicitNotSupportedFailure()
    {
        var tempOld = Path.GetTempFileName();
        var tempNew = Path.GetTempFileName();

        try
        {
            using var payloadMs = new MemoryStream();
            using (var gzip = new GZipStream(payloadMs, CompressionLevel.Fastest, leaveOpen: true))
            {
                // Write a 32-bit ELF (EI_CLASS = 1)
                WriteCpioEntry(gzip, "usr/lib64/libcorrupt.so.1", new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0x01, 0x01, 0x01, 0x00 });
                WriteCpioTrailer(gzip);
            }

            var badElfRpm = CreateSyntheticRpmWithRawPayload(payloadMs.ToArray());
            await File.WriteAllBytesAsync(tempOld, badElfRpm);
            await File.WriteAllBytesAsync(tempNew, badElfRpm);

            var runner = new ManagedElfDiffRunner();
            var result = await runner.RunDiffAsync(tempOld, tempNew);

            // Fail-closed: Must fail with explicit 32-bit diagnosis, never silent Clean
            Assert.False(result.Succeeded);
            Assert.Contains("32-bit ELF binary 'usr/lib64/libcorrupt.so.1' is not supported", result.ErrorMessage);
        }
        finally
        {
            if (File.Exists(tempOld)) File.Delete(tempOld);
            if (File.Exists(tempNew)) File.Delete(tempNew);
        }
    }

    [Fact]
    public async Task RunDiffAsync_CorruptedElfInSoFile_ReturnsFailureNotClean()
    {
        var tempOld = Path.GetTempFileName();
        var tempNew = Path.GetTempFileName();

        try
        {
            using var payloadMs = new MemoryStream();
            using (var gzip = new GZipStream(payloadMs, CompressionLevel.Fastest, leaveOpen: true))
            {
                // Write a 64-bit ELF with bad section headers (returns null from ParseElf64)
                var corruptElf = new byte[64];
                corruptElf[0] = 0x7F; corruptElf[1] = 0x45; corruptElf[2] = 0x4C; corruptElf[3] = 0x46;
                corruptElf[4] = 2; corruptElf[5] = 1; // 64-bit little endian
                WriteCpioEntry(gzip, "usr/lib64/libcorrupt.so.1", corruptElf);
                WriteCpioTrailer(gzip);
            }

            var badElfRpm = CreateSyntheticRpmWithRawPayload(payloadMs.ToArray());
            await File.WriteAllBytesAsync(tempOld, badElfRpm);
            await File.WriteAllBytesAsync(tempNew, badElfRpm);

            var runner = new ManagedElfDiffRunner();
            var result = await runner.RunDiffAsync(tempOld, tempNew);

            // Fail-closed: Must fail, not silently ignore corrupt .so and return Clean
            Assert.False(result.Succeeded);
            Assert.Contains("shared library", result.ErrorMessage);
            Assert.Contains("could be parsed as valid 64-bit ELF binaries", result.ErrorMessage);
        }
        finally
        {
            if (File.Exists(tempOld)) File.Delete(tempOld);
            if (File.Exists(tempNew)) File.Delete(tempNew);
        }
    }

    [Fact]
    public async Task RunDiffAsync_PackageWithVersionSymlinkAndLinkerScript_IgnoresNonElfsAndSucceeds()
    {
        var tempOld = Path.GetTempFileName();
        var tempNew = Path.GetTempFileName();

        try
        {
            using var payloadMs = new MemoryStream();
            using (var gzip = new GZipStream(payloadMs, CompressionLevel.Fastest, leaveOpen: true))
            {
                // 1. Real 64-bit ELF library
                var elfBytes = CreateSyntheticElf64("libdemo.so.1", new[] { "demo_function" });
                WriteCpioEntry(gzip, "usr/lib64/libdemo.so.1.0", elfBytes);

                // 2. Version symbolic link: libdemo.so.1 -> libdemo.so.1.0 (mode S_IFLNK = 0xA000)
                var symlinkTarget = Encoding.ASCII.GetBytes("libdemo.so.1.0");
                WriteCpioEntry(gzip, "usr/lib64/libdemo.so.1", symlinkTarget, modeHex: "0000A1FF");

                // 3. GNU ld linker script: libdemo.so (regular file, text script without \x7fELF)
                var scriptContent = Encoding.ASCII.GetBytes("/* GNU ld script */\nGROUP ( libdemo.so.1 )\n");
                WriteCpioEntry(gzip, "usr/lib64/libdemo.so", scriptContent, modeHex: "000081A4");

                WriteCpioTrailer(gzip);
            }

            var rpmBytes = CreateSyntheticRpmWithRawPayload(payloadMs.ToArray());
            await File.WriteAllBytesAsync(tempOld, rpmBytes);
            await File.WriteAllBytesAsync(tempNew, rpmBytes);

            var runner = new ManagedElfDiffRunner();
            var result = await runner.RunDiffAsync(tempOld, tempNew);

            // Must succeed cleanly: symlink and text script are ignored, only real ELF is diffed
            Assert.True(result.Succeeded);
            Assert.False(result.HasAbiChange);
            Assert.False(result.IsBreakingChange);
        }
        finally
        {
            if (File.Exists(tempOld)) File.Delete(tempOld);
            if (File.Exists(tempNew)) File.Delete(tempNew);
        }
    }

    [Fact]
    public async Task RunDiffAsync_CpioEntryNameExceedsLimit_ReturnsFailure()
    {
        var tempOld = Path.GetTempFileName();
        var tempNew = Path.GetTempFileName();

        try
        {
            using var payloadMs = new MemoryStream();
            using (var gzip = new GZipStream(payloadMs, CompressionLevel.Fastest, leaveOpen: true))
            {
                // Header with name size 8192 bytes (exceeds maxNameSize = 4096)
                WriteRawCpioHeader(gzip, "000081A4", "00000000", "00002000", Encoding.ASCII.GetBytes("short_name\0"));
            }

            var rpmBytes = CreateSyntheticRpmWithRawPayload(payloadMs.ToArray());
            await File.WriteAllBytesAsync(tempOld, rpmBytes);
            await File.WriteAllBytesAsync(tempNew, rpmBytes);

            var runner = new ManagedElfDiffRunner();
            var result = await runner.RunDiffAsync(tempOld, tempNew);

            Assert.False(result.Succeeded);
            Assert.Contains("entry name size out of bounds", result.ErrorMessage);
        }
        finally
        {
            if (File.Exists(tempOld)) File.Delete(tempOld);
            if (File.Exists(tempNew)) File.Delete(tempNew);
        }
    }

    [Fact]
    public async Task RunDiffAsync_CpioSingleFileSizeExceedsLimit_ReturnsFailure()
    {
        var tempOld = Path.GetTempFileName();
        var tempNew = Path.GetTempFileName();

        try
        {
            using var payloadMs = new MemoryStream();
            using (var gzip = new GZipStream(payloadMs, CompressionLevel.Fastest, leaveOpen: true))
            {
                // Claimed file size 512 MB (0x20000000) exceeds maxSingleFileSize = 256 MB (0x10000000)
                WriteRawCpioHeader(gzip, "000081A4", "20000000", "00000008", Encoding.ASCII.GetBytes("big\0"));
            }

            var rpmBytes = CreateSyntheticRpmWithRawPayload(payloadMs.ToArray());
            await File.WriteAllBytesAsync(tempOld, rpmBytes);
            await File.WriteAllBytesAsync(tempNew, rpmBytes);

            var runner = new ManagedElfDiffRunner();
            var result = await runner.RunDiffAsync(tempOld, tempNew);

            Assert.False(result.Succeeded);
            Assert.Contains("file size out of bounds", result.ErrorMessage);
        }
        finally
        {
            if (File.Exists(tempOld)) File.Delete(tempOld);
            if (File.Exists(tempNew)) File.Delete(tempNew);
        }
    }

    [Fact]
    public async Task RunDiffAsync_CpioTotalBytesExceedsLimit_ReturnsFailure()
    {
        var tempOld = Path.GetTempFileName();
        var tempNew = Path.GetTempFileName();

        try
        {
            using var payloadMs = new MemoryStream();
            using (var gzip = new GZipStream(payloadMs, CompressionLevel.Fastest, leaveOpen: true))
            {
                // Write two 300-byte entries (total 600 bytes)
                var chunk300 = new byte[300];
                WriteCpioEntry(gzip, "file1.txt", chunk300);
                WriteCpioEntry(gzip, "file2.txt", chunk300);
                WriteCpioTrailer(gzip);
            }

            var rpmBytes = CreateSyntheticRpmWithRawPayload(payloadMs.ToArray());
            await File.WriteAllBytesAsync(tempOld, rpmBytes);
            await File.WriteAllBytesAsync(tempNew, rpmBytes);

            // Configure safety limit of 500 bytes
            var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions { MaxTotalPayloadBytes = 500 });
            var runner = new ManagedElfDiffRunner(options);
            var result = await runner.RunDiffAsync(tempOld, tempNew);

            Assert.False(result.Succeeded);
            Assert.Contains("total payload uncompressed bytes exceeded safety limit", result.ErrorMessage);
        }
        finally
        {
            if (File.Exists(tempOld)) File.Delete(tempOld);
            if (File.Exists(tempNew)) File.Delete(tempNew);
        }
    }

    [Fact]
    public async Task RunDiffAsync_CpioEntryCountExceedsLimit_ReturnsFailure()
    {
        var tempOld = Path.GetTempFileName();
        var tempNew = Path.GetTempFileName();

        try
        {
            using var payloadMs = new MemoryStream();
            using (var gzip = new GZipStream(payloadMs, CompressionLevel.Fastest, leaveOpen: true))
            {
                // 10,001 entries with 0 bytes content, 4-byte name "xyz\0"
                var nameBytes = Encoding.ASCII.GetBytes("xyz\0");
                for (int i = 0; i <= 10_000; i++)
                {
                    WriteRawCpioHeader(gzip, "000081A4", "00000000", "00000004", nameBytes);
                }
            }

            var rpmBytes = CreateSyntheticRpmWithRawPayload(payloadMs.ToArray());
            await File.WriteAllBytesAsync(tempOld, rpmBytes);
            await File.WriteAllBytesAsync(tempNew, rpmBytes);

            var runner = new ManagedElfDiffRunner();
            var result = await runner.RunDiffAsync(tempOld, tempNew);

            Assert.False(result.Succeeded);
            Assert.Contains("entry count exceeded safety limit", result.ErrorMessage);
        }
        finally
        {
            if (File.Exists(tempOld)) File.Delete(tempOld);
            if (File.Exists(tempNew)) File.Delete(tempNew);
        }
    }

    [Fact]
    public async Task ContainsSharedLibrariesAsync_WithElfSharedLibrary_ReturnsTrue()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var rpmBytes = CreateSyntheticRpm(new[]
            {
                ("usr/lib64/libcalc.so.1", "libcalc.so.1", new[] { "add" })
            });
            await File.WriteAllBytesAsync(tempFile, rpmBytes);

            var containsLibs = await ManagedElfDiffRunner.ContainsSharedLibrariesAsync(tempFile);
            Assert.True(containsLibs);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ContainsSharedLibrariesAsync_WithoutElfSharedLibrary_ReturnsFalse()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            using var payloadMs = new MemoryStream();
            using (var gzip = new GZipStream(payloadMs, CompressionLevel.Fastest, leaveOpen: true))
            {
                WriteCpioEntry(gzip, "usr/include/calc.h", Encoding.UTF8.GetBytes("#pragma once\n"));
                WriteCpioTrailer(gzip);
            }

            var rpmBytes = CreateSyntheticRpmWithRawPayload(payloadMs.ToArray());
            await File.WriteAllBytesAsync(tempFile, rpmBytes);

            var containsLibs = await ManagedElfDiffRunner.ContainsSharedLibrariesAsync(tempFile);
            Assert.False(containsLibs);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ContainsSharedLibrariesAsync_WithCorruptOrNonRpmFile_ThrowsInvalidDataException()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "corrupted-rpm-data");
            await Assert.ThrowsAsync<InvalidDataException>(() => ManagedElfDiffRunner.ContainsSharedLibrariesAsync(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ContainsSharedLibrariesAsync_WithXzCompressedPayload_ReturnsTrue()
    {
        var xzRpm = Path.Combine(AppContext.BaseDirectory, "fixtures", "rpm", "acl-xz.rpm");
        var hasLibs = await ManagedElfDiffRunner.ContainsSharedLibrariesAsync(xzRpm);
        Assert.True(hasLibs);
    }

    [Fact]
    public async Task ContainsSharedLibrariesAsync_WithTruncatedXzPayload_ThrowsFailClosed()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var xzPayload = new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00, 0x00, 0x00 };
            var xzRpm = CreateSyntheticRpmWithRawPayload(xzPayload);
            await File.WriteAllBytesAsync(tempFile, xzRpm);

            var ex = await Assert.ThrowsAsync<InvalidDataException>(
                () => ManagedElfDiffRunner.ContainsSharedLibrariesAsync(tempFile));
            Assert.Contains("Corrupted or truncated RPM payload stream", ex.Message);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ContainsSharedLibrariesAsync_NonExistentFile_ReturnsFalse()
    {
        var result = await ManagedElfDiffRunner.ContainsSharedLibrariesAsync("/path/to/nonexistent/file.rpm");
        Assert.False(result);
    }

    internal static byte[] CreateSyntheticRpmWithRawPayload(byte[] rawPayload)
    {
        using var ms = new MemoryStream();
        var lead = new byte[96];
        lead[0] = 0xED; lead[1] = 0xAB; lead[2] = 0xEE; lead[3] = 0xDB;
        ms.Write(lead);

        var sigIntro = new byte[16];
        sigIntro[0] = 0x8E; sigIntro[1] = 0xAD; sigIntro[2] = 0xE8; sigIntro[3] = 0x01;
        ms.Write(sigIntro);

        var mainIntro = new byte[16];
        mainIntro[0] = 0x8E; mainIntro[1] = 0xAD; mainIntro[2] = 0xE8; mainIntro[3] = 0x01;
        ms.Write(mainIntro);

        ms.Write(rawPayload);
        return ms.ToArray();
    }

    internal static byte[] CreateSyntheticRpm(IEnumerable<(string Path, string Soname, string[] Symbols)> libraries)
    {
        using var ms = new MemoryStream();

        // 1. Lead: 96 bytes
        var lead = new byte[96];
        lead[0] = 0xED; lead[1] = 0xAB; lead[2] = 0xEE; lead[3] = 0xDB; // Magic
        ms.Write(lead);

        // 2. Signature Header: 16 bytes intro + 0 entries, padded to 8 bytes
        var sigIntro = new byte[16];
        sigIntro[0] = 0x8E; sigIntro[1] = 0xAD; sigIntro[2] = 0xE8; sigIntro[3] = 0x01;
        ms.Write(sigIntro);
        // pad = (8 - 16 % 8) % 8 = 0

        // 3. Main Header: 16 bytes intro + 0 entries
        var mainIntro = new byte[16];
        mainIntro[0] = 0x8E; mainIntro[1] = 0xAD; mainIntro[2] = 0xE8; mainIntro[3] = 0x01;
        ms.Write(mainIntro);

        // 4. Payload: GZip compressed cpio archive
        using var payloadMs = new MemoryStream();
        using (var gzip = new GZipStream(payloadMs, CompressionLevel.Fastest, leaveOpen: true))
        {
            // Non-so file entry to exercise cpio skipping
            WriteCpioEntry(gzip, "etc/sample.conf", Encoding.UTF8.GetBytes("setting=1\n"));

            foreach (var (path, soname, symbols) in libraries)
            {
                var elfBytes = CreateSyntheticElf64(soname, symbols);
                WriteCpioEntry(gzip, path, elfBytes);
            }
            WriteCpioTrailer(gzip);
        }

        var payloadBytes = payloadMs.ToArray();
        ms.Write(payloadBytes);

        return ms.ToArray();
    }

    private static void WriteCpioEntry(Stream stream, string filename, byte[] content, string modeHex = "000081A4")
    {
        var nameBytes = Encoding.ASCII.GetBytes(filename + "\0");
        var header = new StringBuilder();
        header.Append("070701"); // Magic
        header.Append("00000000"); // ino
        header.Append(modeHex);    // mode
        header.Append("00000000"); // uid
        header.Append("00000000"); // gid
        header.Append("00000001"); // nlink
        header.Append("00000000"); // mtime
        header.Append(content.Length.ToString("X8", CultureInfo.InvariantCulture)); // filesize
        header.Append("00000000"); // maj
        header.Append("00000000"); // min
        header.Append("00000000"); // rmaj
        header.Append("00000000"); // rmin
        header.Append(nameBytes.Length.ToString("X8", CultureInfo.InvariantCulture)); // namesize
        header.Append("00000000"); // chksum

        var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
        stream.Write(headerBytes);
        stream.Write(nameBytes);

        var namePad = (4 - ((110 + nameBytes.Length) % 4)) % 4;
        if (namePad > 0) stream.Write(new byte[namePad]);

        stream.Write(content);
        var dataPad = (4 - (content.Length % 4)) % 4;
        if (dataPad > 0) stream.Write(new byte[dataPad]);
    }

    private static void WriteRawCpioHeader(Stream stream, string modeHex, string fileSizeHex, string nameSizeHex, byte[] nameBytes)
    {
        var header = new StringBuilder();
        header.Append("070701"); // Magic
        header.Append("00000000"); // ino
        header.Append(modeHex);    // mode
        header.Append("00000000"); // uid
        header.Append("00000000"); // gid
        header.Append("00000001"); // nlink
        header.Append("00000000"); // mtime
        header.Append(fileSizeHex); // filesize
        header.Append("00000000"); // maj
        header.Append("00000000"); // min
        header.Append("00000000"); // rmaj
        header.Append("00000000"); // rmin
        header.Append(nameSizeHex); // namesize
        header.Append("00000000"); // chksum

        var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
        stream.Write(headerBytes);
        stream.Write(nameBytes);

        var namePad = (4 - ((110 + nameBytes.Length) % 4)) % 4;
        if (namePad > 0) stream.Write(new byte[namePad]);
    }

    private static void WriteCpioTrailer(Stream stream)
    {
        WriteCpioEntry(stream, "TRAILER!!!", Array.Empty<byte>());
    }

    private static byte[] CreateSyntheticElf64(string soname, string[] exportedSymbols)
    {
        // Build minimal ELF64 binary with .dynstr, .dynamic, and .dynsym sections
        using var ms = new MemoryStream();

        // Build string table
        using var strMs = new MemoryStream();
        strMs.WriteByte(0); // Index 0 is null
        var sonameOffset = (int)strMs.Position;
        var sonameBytes = Encoding.UTF8.GetBytes(soname + "\0");
        strMs.Write(sonameBytes);

        var symbolOffsets = new List<int>();
        foreach (var sym in exportedSymbols)
        {
            symbolOffsets.Add((int)strMs.Position);
            strMs.Write(Encoding.UTF8.GetBytes(sym + "\0"));
        }
        var dynStrBytes = strMs.ToArray();

        // Build .dynamic section (DT_SONAME = 14, DT_NULL = 0)
        using var dynMs = new MemoryStream();
        // Entry 1: DT_SONAME
        var tag14 = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(tag14.AsSpan(0, 8), 14); // DT_SONAME
        BinaryPrimitives.WriteUInt64LittleEndian(tag14.AsSpan(8, 8), (ulong)sonameOffset);
        dynMs.Write(tag14);
        // Entry 2: DT_NULL
        var tag0 = new byte[16];
        dynMs.Write(tag0);
        var dynBytes = dynMs.ToArray();

        // Build .dynsym section (Elf64_Sym is 24 bytes)
        using var symMs = new MemoryStream();
        // Symbol 0 is UNDEF
        symMs.Write(new byte[24]);
        // Exported symbols
        for (int i = 0; i < exportedSymbols.Length; i++)
        {
            var symEntry = new byte[24];
            BinaryPrimitives.WriteInt32LittleEndian(symEntry.AsSpan(0, 4), symbolOffsets[i]); // st_name
            symEntry[4] = 0x12; // st_info: bind=STB_GLOBAL(1), type=STT_FUNC(2) -> (1 << 4) | 2 = 0x12
            symEntry[5] = 0;    // st_other: STV_DEFAULT
            BinaryPrimitives.WriteUInt16LittleEndian(symEntry.AsSpan(6, 2), 1); // st_shndx = 1 (defined!)
            BinaryPrimitives.WriteUInt64LittleEndian(symEntry.AsSpan(8, 8), 0x1000); // st_value
            BinaryPrimitives.WriteUInt64LittleEndian(symEntry.AsSpan(16, 8), 32);     // st_size
            symMs.Write(symEntry);
        }
        var symBytes = symMs.ToArray();

        // Layout:
        // 0..63: ELF Header (64 bytes)
        // 64..: dynStrBytes
        // ..: dynBytes
        // ..: symBytes
        // ..: Section Headers
        var elfHeader = new byte[64];
        elfHeader[0] = 0x7F; elfHeader[1] = 0x45; elfHeader[2] = 0x4C; elfHeader[3] = 0x46; // \x7fELF
        elfHeader[4] = 2; // Class = 64-bit
        elfHeader[5] = 1; // Endian = Little
        elfHeader[6] = 1; // Version
        BinaryPrimitives.WriteInt16LittleEndian(elfHeader.AsSpan(16, 2), 3); // ET_DYN

        var strOffset = 64;
        var dynOffset = strOffset + dynStrBytes.Length;
        var symOffset = dynOffset + dynBytes.Length;
        var shOffset = symOffset + symBytes.Length;

        BinaryPrimitives.WriteInt64LittleEndian(elfHeader.AsSpan(40, 8), shOffset); // e_shoff
        BinaryPrimitives.WriteInt16LittleEndian(elfHeader.AsSpan(58, 2), 64);       // e_shentsize
        BinaryPrimitives.WriteInt16LittleEndian(elfHeader.AsSpan(60, 2), 4);        // e_shnum (4 sections)
        BinaryPrimitives.WriteInt16LittleEndian(elfHeader.AsSpan(62, 2), 0);        // e_shstrndx

        ms.Write(elfHeader);
        ms.Write(dynStrBytes);
        ms.Write(dynBytes);
        ms.Write(symBytes);

        // Section Headers:
        // Sec 0: NULL
        ms.Write(new byte[64]);

        // Sec 1 (.dynstr): type = 3 (SHT_STRTAB)
        var sh1 = new byte[64];
        BinaryPrimitives.WriteInt32LittleEndian(sh1.AsSpan(4, 4), 3); // SHT_STRTAB
        BinaryPrimitives.WriteInt64LittleEndian(sh1.AsSpan(24, 8), strOffset);
        BinaryPrimitives.WriteInt64LittleEndian(sh1.AsSpan(32, 8), dynStrBytes.Length);
        ms.Write(sh1);

        // Sec 2 (.dynamic): type = 6 (SHT_DYNAMIC), link = 1 (points to .dynstr)
        var sh2 = new byte[64];
        BinaryPrimitives.WriteInt32LittleEndian(sh2.AsSpan(4, 4), 6); // SHT_DYNAMIC
        BinaryPrimitives.WriteInt64LittleEndian(sh2.AsSpan(24, 8), dynOffset);
        BinaryPrimitives.WriteInt64LittleEndian(sh2.AsSpan(32, 8), dynBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(sh2.AsSpan(40, 4), 1); // link -> sec 1
        ms.Write(sh2);

        // Sec 3 (.dynsym): type = 11 (SHT_DYNSYM), link = 1 (points to .dynstr), entsize = 24
        var sh3 = new byte[64];
        BinaryPrimitives.WriteInt32LittleEndian(sh3.AsSpan(4, 4), 11); // SHT_DYNSYM
        BinaryPrimitives.WriteInt64LittleEndian(sh3.AsSpan(24, 8), symOffset);
        BinaryPrimitives.WriteInt64LittleEndian(sh3.AsSpan(32, 8), symBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(sh3.AsSpan(40, 4), 1); // link -> sec 1
        BinaryPrimitives.WriteInt64LittleEndian(sh3.AsSpan(56, 8), 24); // entsize = 24
        ms.Write(sh3);

        return ms.ToArray();
    }

    [Fact]
    public async Task ExtractLibrariesAsync_RpmV6RealFixture_ExtractsSharedLibraries()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "rpm", "v6demo-1.0-1.x86_64.rpm");
        Assert.True(File.Exists(fixturePath), $"Fixture file missing at {fixturePath}");

        var libraries = await ManagedElfDiffRunner.ExtractSharedLibrariesAsync(fixturePath, 0, CancellationToken.None);

        Assert.NotEmpty(libraries);
        var libKey = libraries.Keys.FirstOrDefault(k => k.Contains("libv6demo"));
        Assert.NotNull(libKey);
        var lib = libraries[libKey!];
        Assert.NotEmpty(lib.ExportedSymbols);
    }

    [Fact]
    public async Task ContainsSharedLibrariesAsync_RpmV6RealFixture_ReturnsTrue()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "rpm", "v6demo-1.0-1.x86_64.rpm");
        Assert.True(File.Exists(fixturePath));

        var hasLibs = await ManagedElfDiffRunner.ContainsSharedLibrariesAsync(fixturePath);

        Assert.True(hasLibs);
    }

    [Fact]
    public async Task RunDiffAsync_RpmV6RealFixtures_DetectsSymbolChangesAndIncludesManagedPrefix()
    {
        var v1Path = Path.Combine(AppContext.BaseDirectory, "fixtures", "rpm", "v6demo-1.0-1.x86_64.rpm");
        var v2Path = Path.Combine(AppContext.BaseDirectory, "fixtures", "rpm", "v6demo-2.0-1.x86_64.rpm");
        Assert.True(File.Exists(v1Path));
        Assert.True(File.Exists(v2Path));

        var runner = new ManagedElfDiffRunner(Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions()));
        var result = await runner.RunDiffAsync(v1Path, v2Path);

        Assert.True(result.Succeeded);
        Assert.StartsWith("[Symbol-Only Managed Analysis]", result.Details);
        Assert.True(result.HasAbiChange);
    }

    [Fact]
    public async Task ExtractLibrariesAsync_NewcCpioMissingTrailer_ThrowsInvalidDataException()
    {
        // Construct newc CPIO payload without TRAILER!!!
        // 110-byte header + 2-byte name ("a\0") = 112 bytes (4-byte aligned, namesize = 2, filesize = 0)
        using var cpioMs = new MemoryStream();
        var header = Encoding.ASCII.GetBytes("07070100000000000081A40000000000000000000000010000000000000000000000000000000000000000000000000000000200000000a\0");
        cpioMs.Write(header);

        using var gzipMs = new MemoryStream();
        using (var gz = new GZipStream(gzipMs, CompressionMode.Compress, leaveOpen: true))
        {
            gz.Write(cpioMs.ToArray());
        }

        var rpmBytes = CreateSyntheticRpmWithRawPayload(gzipMs.ToArray());
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(tempFile, rpmBytes);
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ManagedElfDiffRunner.ExtractSharedLibrariesAsync(tempFile, 0, CancellationToken.None));
            Assert.Contains("TRAILER", ex.Message);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ExtractStrippedCpioLibrariesAsync_TruncatedFirstHeader_ThrowsInvalidDataException()
    {
        using var ms = new MemoryStream(new byte[5]);
        var magic = Encoding.ASCII.GetBytes("07070X");
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ManagedElfDiffRunner.ExtractStrippedCpioLibrariesAsync(ms, magic, Array.Empty<ManagedElfDiffRunner.RpmFileEntry>(), new(), 0, CancellationToken.None));
        Assert.Contains("first entry header", ex.Message);
    }

    [Fact]
    public async Task ExtractStrippedCpioLibrariesAsync_TruncatedEntryHeader_ThrowsInvalidDataException()
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("0000000000")); // first entry 10 bytes (fileIdx 0)
        ms.Write(new byte[] { 1, 2, 3, 4, 5 }); // truncated second entry (< 16 bytes)
        ms.Position = 0;

        var magic = Encoding.ASCII.GetBytes("07070X");
        var files = new[] { new ManagedElfDiffRunner.RpmFileEntry("/usr/lib64/libempty.so.1", 0x81A4, 0, "") };
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ManagedElfDiffRunner.ExtractStrippedCpioLibrariesAsync(ms, magic, files, new(), 0, CancellationToken.None));
        Assert.Contains("entry header", ex.Message);
    }

    [Fact]
    public async Task ExtractStrippedCpioLibrariesAsync_TruncatedPayload_ThrowsInvalidDataException()
    {
        using var ms = new MemoryStream();
        // 10 bytes: fileIdx 0 -> "0000000000"
        ms.Write(Encoding.ASCII.GetBytes("0000000000"));
        ms.Write(new byte[10]); // only 10 bytes written, but 100 expected
        ms.Position = 0;

        var magic = Encoding.ASCII.GetBytes("07070X");
        var files = new[] { new ManagedElfDiffRunner.RpmFileEntry("/usr/lib64/libtest.so.1", 0x81A4, 100, "digest1") };
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ManagedElfDiffRunner.ExtractStrippedCpioLibrariesAsync(ms, magic, files, new(), 0, CancellationToken.None));
        Assert.Contains("payload", ex.Message);
    }

    [Fact]
    public async Task ExtractStrippedCpioLibrariesAsync_Trailer070701_TerminatesSuccessfully()
    {
        using var ms = new MemoryStream();
        // Entry 1: 10 bytes (fileIdx 0, size 0)
        ms.Write(Encoding.ASCII.GetBytes("0000000000"));
        // Entry 2: Magic "070701" + 104 bytes header + "TRAILER!!!\0"
        var trailer = "070701" + new string('0', 88) + "0000000B" + "00000000" + "TRAILER!!!\0";
        ms.Write(Encoding.ASCII.GetBytes(trailer));
        ms.Position = 0;

        var magic = Encoding.ASCII.GetBytes("07070X");
        var files = new[] { new ManagedElfDiffRunner.RpmFileEntry("/usr/lib64/libempty.so.1", 0x81A4, 0, "") };
        var result = new Dictionary<string, ParsedSharedLibrary>();
        await ManagedElfDiffRunner.ExtractStrippedCpioLibrariesAsync(ms, magic, files, result, 0, CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async Task ExtractStrippedCpioLibrariesAsync_HardlinkWithSameDigest_ReusesParsedLibrary()
    {
        var dummyElf = CreateSyntheticElf64("libfoo.so.1", new[] { "foo_init" });
        using var ms = new MemoryStream();

        // Entry 1: fileIdx 0 ("0000000000")
        ms.Write(Encoding.ASCII.GetBytes("0000000000"));
        ms.Write(dummyElf);
        // Pad entry 1 payload to 4 bytes if needed
        var rem = dummyElf.Length % 4;
        if (rem != 0) ms.Write(new byte[4 - rem]);

        // Entry 2 (Hardlink): fileIdx 1 ("0000000100")
        ms.Write(Encoding.ASCII.GetBytes("07070X0000000100"));

        // Entry 3: Trailer 070701
        var trailer = "070701" + new string('0', 88) + "0000000B" + "00000000" + "TRAILER!!!\0";
        ms.Write(Encoding.ASCII.GetBytes(trailer));
        ms.Position = 0;

        var files = new[]
        {
            new ManagedElfDiffRunner.RpmFileEntry("/usr/lib64/libfoo.so.1", 0x81A4, dummyElf.Length, "same-digest-123"),
            new ManagedElfDiffRunner.RpmFileEntry("/usr/lib64/libfoo.so.1.0.0", 0x81A4, 0, "same-digest-123")
        };

        var magic = Encoding.ASCII.GetBytes("07070X");
        var result = new Dictionary<string, ParsedSharedLibrary>();
        await ManagedElfDiffRunner.ExtractStrippedCpioLibrariesAsync(ms, magic, files, result, 0, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.True(result.ContainsKey("libfoo.so.1"));
        Assert.True(result.ContainsKey("libfoo.so.1.0.0"));
        Assert.Equal(result["libfoo.so.1"].ExportedSymbols, result["libfoo.so.1.0.0"].ExportedSymbols);
    }

    [Theory]
    [InlineData("acl-zstd.rpm")]
    [InlineData("acl-gzip.rpm")]
    [InlineData("acl-xz.rpm")]
    [InlineData("acl-bzip2.rpm")]
    [InlineData("acl-lzma.rpm")]
    public async Task ExtractSharedLibrariesAsync_MatrixPayloadFormats_SuccessfullyExtractsAclSymbols(string rpmFileName)
    {
        var rpmPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "rpm", rpmFileName);
        Assert.True(File.Exists(rpmPath), $"RPM fixture '{rpmFileName}' must exist.");

        var libs = await ManagedElfDiffRunner.ExtractSharedLibrariesAsync(rpmPath, 0, CancellationToken.None);

        Assert.Single(libs);
        var lib = libs.Values.First();
        Assert.Equal("libacl.so.1.1.2301", Path.GetFileName(lib.FilePath));
        Assert.Equal("libacl.so.1", lib.Soname);
        Assert.Equal(44, lib.ExportedSymbols.Count);
        Assert.Contains("acl_init", lib.ExportedSymbols);
        Assert.Contains("acl_get_file", lib.ExportedSymbols);
        Assert.Contains("acl_set_file", lib.ExportedSymbols);
    }

    [Fact]
    public async Task RunDiffAsync_CrossFormatComparisons_AllMatchAndReturnClean()
    {
        var formats = new[] { "acl-zstd.rpm", "acl-gzip.rpm", "acl-xz.rpm", "acl-bzip2.rpm", "acl-lzma.rpm" };
        var runner = new ManagedElfDiffRunner();

        foreach (var oldFile in formats)
        {
            var oldPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "rpm", oldFile);
            foreach (var newFile in formats)
            {
                var newPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "rpm", newFile);
                var diff = await runner.RunDiffAsync(oldPath, newPath);

                Assert.True(diff.Succeeded, $"Diff between '{oldFile}' and '{newFile}' should succeed, but failed: {diff.ErrorMessage}");
                Assert.False(diff.HasAbiChange, $"Diff between '{oldFile}' and '{newFile}' should have no ABI changes.");
                Assert.False(diff.IsBreakingChange, $"Diff between '{oldFile}' and '{newFile}' should not be breaking.");
            }
        }
    }

    [Fact]
    public void CreateDecompressionStream_NonSeekableStream_ThrowsNotSupportedException()
    {
        var original = new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x11, 0x22 };
        using var ms = new MemoryStream(original);
        using var nonSeekable = new NonSeekableReadStream(ms);

        var ex = Assert.Throws<NotSupportedException>(() => ManagedElfDiffRunner.CreateDecompressionStream(nonSeekable));
        Assert.Contains("Payload decompression requires a seekable stream", ex.Message);
    }

    [Fact]
    public async Task ExtractSharedLibrariesAsync_ResultDictionary_UsesOrdinalComparer()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "rpm", "acl-zstd.rpm");
        var libs = await ManagedElfDiffRunner.ExtractSharedLibrariesAsync(fixturePath, 0, CancellationToken.None);
        Assert.Same(StringComparer.Ordinal, libs.Comparer);
    }

    [Fact]
    public async Task ExtractSharedLibrariesAsync_CaseDistinctLibraries_BothPreservedWithoutCollision()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var rpmBytes = CreateSyntheticRpm(new[]
            {
                ("usr/lib64/libfoo.so.1", "libfoo.so.1", new[] { "func_a" }),
                ("usr/lib64/libFoo.so.1", "libFoo.so.1", new[] { "func_b" })
            });
            await File.WriteAllBytesAsync(tempFile, rpmBytes);

            var libs = await ManagedElfDiffRunner.ExtractSharedLibrariesAsync(tempFile, 0, CancellationToken.None);

            Assert.Equal(2, libs.Count);
            Assert.True(libs.ContainsKey("libfoo.so.1"));
            Assert.True(libs.ContainsKey("libFoo.so.1"));
            Assert.NotSame(libs["libfoo.so.1"], libs["libFoo.so.1"]);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    private sealed class NonSeekableReadStream : Stream
    {
        private readonly Stream _inner;
        public NonSeekableReadStream(Stream inner) => _inner = inner;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}

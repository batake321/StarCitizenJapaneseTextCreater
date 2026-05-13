using System.IO.Compression;
using System.Text;
using ZstdSharp;

namespace StarCitizenJapaneseTextCreater;

public class P4kExtractor
{
    public static void Extract(string p4kPath, string targetEntryPath, string outputPath)
    {
        Console.WriteLine($"Extracting {targetEntryPath} from {Path.GetFileName(p4kPath)}...");

        using var fs = File.OpenRead(p4kPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        var entry = zip.Entries.FirstOrDefault(e =>
            e.FullName.Replace('\\', '/').Equals(targetEntryPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

        if (entry == null)
        {
            Console.WriteLine($"  Not found: {targetEntryPath}");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // Try standard extraction first
        try
        {
            using var entryStream = entry.Open();
            using var outFs = File.Create(outputPath);
            entryStream.CopyTo(outFs);

            if (new FileInfo(outputPath).Length > 0)
            {
                Console.WriteLine($"  Extracted: {outputPath} ({new FileInfo(outputPath).Length:N0} bytes)");
                return;
            }
        }
        catch (InvalidDataException)
        {
            // Compression method not supported, fall through to manual ZStd
        }

        // Manual ZStd extraction for method 100
        ExtractZstd(fs, entry, outputPath);
    }

    private static void ExtractZstd(FileStream fs, ZipArchiveEntry entry, string outputPath)
    {
        // Read local file header to get actual data offset
        var headerOffset = (long)typeof(ZipArchiveEntry)
            .GetField("_offsetOfLocalHeader", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
            .GetValue(entry)!;

        // Fallback: scan for the entry by reading central directory info
        if (headerOffset == 0)
        {
            // Try alternative field names
            var prop = entry.GetType().GetProperty("OffsetOfLocalHeader",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (prop != null)
                headerOffset = (long)prop.GetValue(entry)!;
        }

        fs.Seek(headerOffset, SeekOrigin.Begin);
        using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

        var sig = br.ReadUInt32();
        if (sig != 0x04034b50) // PK\x03\x04
            throw new InvalidDataException("Bad local file header signature");

        br.ReadBytes(22); // skip to filename length
        var fnLen = br.ReadUInt16();
        var extraLen = br.ReadUInt16();
        br.ReadBytes(fnLen + extraLen); // skip filename and extra

        var compressedData = br.ReadBytes((int)entry.CompressedLength);

        using var decompressor = new Decompressor();
        var decompressed = decompressor.Unwrap(compressedData);

        File.WriteAllBytes(outputPath, decompressed.ToArray());
        Console.WriteLine($"  Extracted (ZStd): {outputPath} ({decompressed.Length:N0} bytes)");
    }

    public static (string englishPath, string japanesePath) ExtractLocalization(string gamePath, string workDir)
    {
        var p4kPath = Path.Combine(gamePath, "Data.p4k");
        if (!File.Exists(p4kPath))
            throw new FileNotFoundException($"Data.p4k not found at {p4kPath}");

        var enPath = Path.Combine(workDir, "english", "global.ini");
        var jaPath = Path.Combine(workDir, "japanese_(japan)", "global.ini");

        Extract(p4kPath, "Data/Localization/english/global.ini", enPath);
        Extract(p4kPath, "Data/Localization/japanese_(japan)/global.ini", jaPath);

        return (enPath, jaPath);
    }
}

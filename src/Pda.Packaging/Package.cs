using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Pda.Packaging;

internal sealed class Package : IDisposable
{
    private readonly ZipArchive zip;
    internal Manifest Manifest { get; }
    internal Release Release { get; }
    internal byte[] ManifestBytes { get; }
    private readonly Dictionary<string, ZipArchiveEntry> files =
        new(StringComparer.Ordinal);

    internal Package(FileStream stream, string expected)
    {
        Format.Require(Format.Hash(expected), "An external SHA-256 is required.");
        Format.Require(stream.Length is > 0 and <= Format.MaxArchive,
            "Package archive exceeds size limit.");
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        Format.Require(Format.Match(actual, expected), "Package SHA-256 mismatch.");
        CheckDirectory(stream);
        stream.Position = 0;
        zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        try
        {
            Format.Require(zip.Entries.Count <= Format.MaxFiles * 2 + 1,
                "Too many package entries.");
            var manifests = zip.Entries.Where(e => e.FullName == "manifest.json").ToList();
            Format.Require(manifests.Count == 1, "Exactly one manifest is required.");
            using (var content = manifests[0].Open())
                ManifestBytes = Format.Limited(content, manifests[0].Length,
                    Format.MaxManifest);
            Manifest = Format.ReadManifest(ManifestBytes);
            Release = new(Manifest.Version, actual, Manifest.EntryPoint,
                Convert.ToHexStringLower(SHA256.HashData(ManifestBytes)));
            Index();
        }
        catch { zip.Dispose(); throw; }
    }

    private static void CheckDirectory(FileStream stream)
    {
        // Only ordinary single-disk ZIP is needed for bounded releases.
        int length = (int)Math.Min(65557, stream.Length);
        stream.Position = stream.Length - length;
        var tail = new byte[length];
        stream.ReadExactly(tail);
        for (int i = length - 22; i >= 0; i--)
        {
            var e = tail.AsSpan(i);
            if (BinaryPrimitives.ReadUInt32LittleEndian(e) != 0x06054b50 ||
                BinaryPrimitives.ReadUInt16LittleEndian(e[20..]) != e.Length - 22)
                continue;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(e[16..]);
            var count = BinaryPrimitives.ReadUInt16LittleEndian(e[10..]);
            Format.Require(BinaryPrimitives.ReadUInt32LittleEndian(e[4..]) == 0 &&
                BinaryPrimitives.ReadUInt16LittleEndian(e[8..]) == count &&
                count <= 2 * Format.MaxFiles + 1 && size <= 8 * 1024 * 1024 &&
                (long)offset + size == stream.Length - e.Length,
                "Unsupported or oversized ZIP directory.");
            return;
        }
        throw new InvalidDataException("Missing ZIP directory.");
    }

    private void Index()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declared = Manifest.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal) {"app/"};
        foreach (var file in Manifest.Files)
        {
            var path = "app/" + file.Path;
            for (int i = path.IndexOf('/'); i >= 0; i = path.IndexOf('/', i + 1))
                directories.Add(path[..(i + 1)]);
        }
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            string name = entry.FullName;
            Format.Require(names.Add(name), "Duplicate archive member.");
            bool directory = name.EndsWith('/');
            int mode = (entry.ExternalAttributes >> 16) & 0xf000;
            Format.Require((entry.ExternalAttributes & 0x400) == 0 &&
                (mode == 0 || mode == (directory ? 0x4000 : 0x8000)),
                "Linked or unsupported archive member.");
            if (directory)
            {
                Format.Require(entry.Length == 0 && directories.Contains(name),
                    "Unexpected archive directory.");
                continue;
            }
            if (name == "manifest.json") continue;
            Format.Require(name.StartsWith("app/", StringComparison.Ordinal) &&
                declared.ContainsKey(name[4..]), "Undeclared archive member.");
            Format.Require(entry.Length >= 0 && entry.Length <= Format.MaxFile &&
                (total += entry.Length) <= Format.MaxExpanded, "Payload too large.");
            files.Add(name[4..], entry);
        }
        Format.Require(files.Count == declared.Count && files["pda.exe"].Length > 0,
            "Missing or empty package executable or payload.");
    }

    internal void Extract(string stage)
    {
        foreach (var item in Manifest.Files)
        {
            var entry = files[item.Path];
            string path = Path.Combine(stage, item.Path);
            Paths.MakeDirectory(Path.GetDirectoryName(path)!);
            Paths.Check(path);
            using var source = entry.Open();
            using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int count;
            while ((count = source.Read(buffer)) > 0)
            {
                total += count;
                Format.Require(total <= entry.Length, "Expanded ZIP member exceeds limit.");
                hash.AppendData(buffer, 0, count);
                target.Write(buffer, 0, count);
            }
            Format.Require(total == entry.Length &&
                Format.Match(Convert.ToHexString(hash.GetHashAndReset()), item.Sha256),
                "Installed payload hash mismatch.");
            target.Flush(flushToDisk: true);
        }
        using var metadata = new FileStream(Path.Combine(stage, Format.SavedManifest),
            FileMode.CreateNew, FileAccess.Write);
        metadata.Write(ManifestBytes);
        metadata.Flush(flushToDisk: true);
    }

    public void Dispose() => zip.Dispose();
}

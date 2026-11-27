using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Pda.Packaging;

public sealed record Installation(string Root, string Version, string Executable,
    string? PreviousVersion, string PackageSha256);

internal sealed record PayloadFile(string Path, string Sha256);
internal sealed record Manifest(int FormatVersion, string Version,
    string EntryPoint, PayloadFile[] Files);
internal sealed record Release(string Version, string PackageSha256,
    string EntryPoint, string ManifestSha256);
internal sealed record Pointer(int FormatVersion, Release Current, Release? Previous);

internal static class Format
{
    internal const int MaxFiles = 8192;
    internal const int MaxManifest = 2 * 1024 * 1024;
    internal const long MaxFile = 512L * 1024 * 1024;
    internal const long MaxExpanded = 2L * 1024 * 1024 * 1024;
    internal const long MaxArchive = 1024L * 1024 * 1024;
    internal const string SavedManifest = ".pda-manifest.json";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false, MaxDepth = 12, WriteIndented = true
    };

    internal static bool Hash(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    internal static bool Match(string hash, string other) =>
        hash.Equals(other, StringComparison.OrdinalIgnoreCase);

    internal static void Require(bool valid, string reason)
    {
        if (!valid) throw new InvalidDataException(reason);
    }

    internal static void Version(string? value) =>
        Require(value is { Length: > 0 and <= 80 } && Regex.IsMatch(value,
            @"\A(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)" +
            @"(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?" +
            @"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
            RegexOptions.CultureInvariant | RegexOptions.ECMAScript),
            "Invalid package version.");

    internal static void Relative(string path)
    {
        Require(path.Length is > 0 and <= 768 && path.IsNormalized(),
            "Invalid package member name.");
        foreach (var part in path.Split('/'))
        {
            Require(part.Length is > 0 and <= 255 && part.TrimEnd(' ', '.') == part &&
                !part.Any(c => char.IsControl(c) || "\\:<>|?\"*~".Contains(c)) &&
                !Regex.IsMatch(part,
                @"\A(?:\.git|con|prn|aux|nul|conin\$|conout\$|com[0-9¹²³]|lpt[0-9¹²³])(?:\.|\z)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
                !part.Equals(SavedManifest, StringComparison.OrdinalIgnoreCase),
                "Unsafe package member name.");
        }
    }

    internal static T Parse<T>(byte[] bytes)
    {
        ReadOnlySpan<byte> json = bytes;
        if (json.StartsWith((ReadOnlySpan<byte>)[0xef, 0xbb, 0xbf])) json = json[3..];
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json)
                ?? throw new InvalidDataException("Missing package metadata.");
        }
        catch (JsonException e) { throw new InvalidDataException("Invalid package metadata.", e); }
    }

    internal static Manifest ReadManifest(byte[] bytes)
    {
        var manifest = Parse<Manifest>(bytes);
        Version(manifest.Version);
        Require(manifest.FormatVersion == 1 && manifest.EntryPoint == "pda.exe" &&
            manifest.Files is { Length: > 0 and <= MaxFiles }, "Invalid manifest.");
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            Require(file != null && Hash(file.Sha256), "Invalid payload hash.");
            Relative(file!.Path);
            Require(files.Add(file.Path), "Duplicate payload entry.");
            var current = "";
            foreach (var segment in file.Path.Split('/'))
            {
                current = current.Length == 0 ? segment : current + "/" + segment;
                Require(!paths.TryGetValue(current, out var prior) || prior == current,
                    "Ambiguous package path casing.");
                paths[current] = current;
            }
        }
        Require(files.Contains("pda.exe") && paths["pda.exe"] == "pda.exe",
            "Missing package executable.");
        foreach (var path in paths.Keys)
        {
            var slash = path.LastIndexOf('/');
            if (slash > 0) Require(!files.Contains(path[..slash]),
                "Package file conflicts with a directory.");
        }
        return manifest;
    }

    internal static byte[] Limited(Stream input, long length, int limit)
    {
        Require(length is > 0 && length <= limit, "Metadata exceeds size limit.");
        byte[] bytes = new byte[(int)length];
        input.ReadExactly(bytes);
        Require(input.ReadByte() == -1, "Metadata length changed.");
        return bytes;
    }
}

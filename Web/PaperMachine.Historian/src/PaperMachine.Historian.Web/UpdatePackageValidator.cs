using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace PaperMachine.Historian.Web;

public static class UpdatePackageValidator
{
    public static string ParseSha256(string content)
    {
        var hash = (content ?? string.Empty)
            .Trim()
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (hash is null ||
            hash.Length != 64 ||
            !hash.All(Uri.IsHexDigit))
            throw new InvalidOperationException("O arquivo SHA-256 da Release é inválido.");
        return hash.ToUpperInvariant();
    }

    public static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    public static string ReadManifestVersion(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var manifestEntries = archive.Entries
            .Where(entry =>
                string.Equals(
                    entry.FullName.Replace('\\', '/').Split('/').LastOrDefault(),
                    "deployment-manifest.json",
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (manifestEntries.Length != 1)
            throw new InvalidOperationException(
                "O pacote deve conter exatamente um manifesto de implantação.");

        using var stream = manifestEntries[0].Open();
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("Version", out var version) &&
            !document.RootElement.TryGetProperty("version", out version))
            throw new InvalidOperationException("O manifesto de implantação não possui versão.");
        return version.GetString()
            ?? throw new InvalidOperationException("A versão do manifesto está vazia.");
    }
}

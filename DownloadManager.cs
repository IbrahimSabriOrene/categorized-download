using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CategorizedDownload;

public sealed class DownloadManager
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = true });
    public static string DownloadsRoot { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public static string TemporaryRoot { get; } = Path.Combine(Path.GetTempPath(), "CategorizedDownload");

    public async Task<DownloadRecord> DownloadAsync(
        string url,
        bool categorized,
        string? suggestedName,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var fileName = ChooseFileName(response.Content.Headers.ContentDisposition, suggestedName, new Uri(url));
        var category = CategoryClassifier.ForFile(fileName);
        Directory.CreateDirectory(TemporaryRoot);
        var temporaryPath = Path.Combine(TemporaryRoot, $"{Guid.NewGuid():N}.part");
        AppLog.Write($"Download accepted into app temp: {Path.GetFileName(temporaryPath)}; file={fileName}; autoCategory={categorized}.");
        var total = response.Content.Headers.ContentLength;
        long received = 0;

        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    received += count;
                    progress?.Report(new(fileName, received, total));
                }
            }

            var destination = FinalizeDownload(temporaryPath, fileName, categorized, DownloadsRoot);
            return new(fileName, category, "Complete", received, DateTimeOffset.Now, destination);
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
    }

    private static string FinalizeDownload(string temporaryPath, string fileName, bool categorized, string downloadsRoot)
    {
        var folder = categorized ? Path.Combine(downloadsRoot, CategoryClassifier.ForFile(fileName)) : downloadsRoot;
        Directory.CreateDirectory(folder);
        var destination = UniquePath(folder, fileName);
        File.Move(temporaryPath, destination);
        return destination;
    }

    public static async Task SelfTestAsync()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"categorized-download-test-{Guid.NewGuid():N}");
        var temporaryPath = Path.Combine(sandbox, "transfer.part");
        try
        {
            Directory.CreateDirectory(sandbox);
            await File.WriteAllTextAsync(temporaryPath, "test").ConfigureAwait(false);
            var destination = FinalizeDownload(temporaryPath, "test.xlsx", true, sandbox);
            if (destination != Path.Combine(sandbox, "Spreadsheets", "test.xlsx") || !File.Exists(destination))
                throw new InvalidOperationException("Temporary download pipeline self-test failed.");
        }
        finally
        {
            if (Directory.Exists(sandbox))
                Directory.Delete(sandbox, true);
        }
    }

    private static string ChooseFileName(ContentDispositionHeaderValue? disposition, string? suggestedName, Uri uri)
    {
        var name = disposition?.FileNameStar ?? disposition?.FileName ?? suggestedName ?? Uri.UnescapeDataString(Path.GetFileName(uri.LocalPath));
        name = Path.GetFileName(name?.Trim('"'));
        if (string.IsNullOrWhiteSpace(name))
            name = $"download-{DateTime.Now:yyyyMMdd-HHmmss}";
        return Regex.Replace(name, $"[{Regex.Escape(new string(Path.GetInvalidFileNameChars()))}]", "_");
    }

    private static string UniquePath(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        if (!File.Exists(path) && !File.Exists(path + ".part"))
            return path;

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var number = 1; ; number++)
        {
            path = Path.Combine(folder, $"{stem} ({number}){extension}");
            if (!File.Exists(path) && !File.Exists(path + ".part"))
                return path;
        }
    }
}

public static class CategoryClassifier
{
    private static readonly Dictionary<string, string> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        [".xlsx"] = "Spreadsheets", [".xls"] = "Spreadsheets", [".csv"] = "Spreadsheets", [".ods"] = "Spreadsheets",
        [".pdf"] = "Documents", [".doc"] = "Documents", [".docx"] = "Documents", [".txt"] = "Documents", [".ppt"] = "Documents", [".pptx"] = "Documents",
        [".jpg"] = "Images", [".jpeg"] = "Images", [".png"] = "Images", [".gif"] = "Images", [".webp"] = "Images", [".svg"] = "Images",
        [".mp4"] = "Videos", [".mkv"] = "Videos", [".mov"] = "Videos", [".avi"] = "Videos", [".webm"] = "Videos",
        [".mp3"] = "Audio", [".wav"] = "Audio", [".flac"] = "Audio", [".m4a"] = "Audio", [".ogg"] = "Audio",
        [".zip"] = "Archives", [".rar"] = "Archives", [".7z"] = "Archives", [".tar"] = "Archives", [".gz"] = "Archives",
        [".exe"] = "Applications", [".msi"] = "Applications", [".msix"] = "Applications", [".dmg"] = "Applications", [".apk"] = "Applications"
    };
    private static CategoryRule[] customRules = [];

    public static void UseCustomRules(IEnumerable<CategoryRule> rules) => customRules = rules.ToArray();

    public static string ForFile(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return customRules.FirstOrDefault(rule => rule.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))?.Category
            ?? Categories.GetValueOrDefault(extension, "Other");
    }

    public static void SelfTest()
    {
        var savedRules = customRules;
        try
        {
            customRules = [];
            if (ForFile("budget.XLSX") != "Spreadsheets" || ForFile("photo.png") != "Images" || ForFile("unknown.bin") != "Other" ||
                !CategoryRule.TryCreate("Design", "*.psd, AI", out var custom, out _) || custom is null)
                throw new InvalidOperationException("Category classifier self-test failed.");
            UseCustomRules([custom]);
            if (ForFile("mockup.PSD") != "Design")
                throw new InvalidOperationException("Custom category classifier self-test failed.");
        }
        finally
        {
            customRules = savedRules;
        }
    }
}

public sealed record CategoryRule(string Category, string[] Extensions)
{
    [JsonIgnore]
    public string ExtensionsText => string.Join(", ", Extensions);

    public static bool TryCreate(string? category, string? extensions, out CategoryRule? rule, out string error)
    {
        category = category?.Trim();
        if (string.IsNullOrWhiteSpace(category) || category is "." or ".." ||
            category.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            rule = null;
            error = "Enter a valid category folder name.";
            return false;
        }

        var parsed = (extensions ?? "")
            .Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.TrimStart('*').ToLowerInvariant())
            .Select(value => value.StartsWith('.') ? value : $".{value}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (parsed.Length == 0 || parsed.Any(value => !Regex.IsMatch(value, "^\\.[a-z0-9]+$")))
        {
            rule = null;
            error = "Enter extensions such as .xlsx, .csv or pdf.";
            return false;
        }

        rule = new CategoryRule(category, parsed);
        error = "";
        return true;
    }
}

public static class CategoryRuleStore
{
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CategorizedDownload");
    private static readonly string FilePath = Path.Combine(Folder, "categories.json");

    public static IReadOnlyList<CategoryRule> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<CategoryRule>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static bool Save(IEnumerable<CategoryRule> rules)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(rules, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

public sealed record DownloadProgress(string FileName, long Received, long? Total)
{
    public double? Percent => Total > 0 ? Received * 100d / Total : null;
    public string ReceivedText => FormatBytes(Received) + (Total > 0 ? $" / {FormatBytes(Total.Value)}" : "");

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824d:0.0} GB",
        >= 1_048_576 => $"{bytes / 1_048_576d:0.0} MB",
        >= 1024 => $"{bytes / 1024d:0.0} KB",
        _ => $"{bytes} B"
    };
}

public sealed record DownloadRecord(string FileName, string Category, string Status, long Size, DateTimeOffset DownloadedAt, string Destination)
{
    public string SizeText => new DownloadProgress(FileName, Size, Size).ReceivedText.Split(" / ")[0];
    public string SizeDetailsText => $"{SizeText} ({Size:N0} bytes)";
    public string DownloadedAtText => DownloadedAt.ToLocalTime().ToString("g");
}

public static class HistoryStore
{
    public const int MaximumEntries = 100;
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CategorizedDownload");
    private static readonly string FilePath = Path.Combine(Folder, "downloads.json");

    public static IReadOnlyList<DownloadRecord> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<DownloadRecord>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static bool Save(IEnumerable<DownloadRecord> records)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(records.Take(MaximumEntries), new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

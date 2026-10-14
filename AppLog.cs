using System.IO;

namespace CategorizedDownload;

internal static class AppLog
{
    private static readonly object Sync = new();
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CategorizedDownload",
        "app.log");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, $"{DateTimeOffset.Now:O} [PID {Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never interrupt a download.
        }
    }

    public static string DescribeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Host} ({Path.GetExtension(uri.AbsolutePath) switch { "" => "no extension", var extension => extension }})"
            : "invalid URL";
}

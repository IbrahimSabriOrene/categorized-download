using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Text.Json;
using System.Windows;
using DevExpress.Xpf.Core;

namespace CategorizedDownload;

public partial class App : System.Windows.Application
{
    private const string PipeName = "CategorizedDownload.App";
    private const string ChromeOrigin = "chrome-extension://iclppjbhmhkpinkednfkokinenijlalh";
    private const string ChromeBridgeKey = "c794607f-9571-41ea-9f83-383f75ee2ff6";
    private Mutex? instanceMutex;
    private bool ownsInstanceMutex;
    private CancellationTokenSource? pipeCancellation;

    static App()
    {
        CompatibilitySettings.UseLightweightThemes = true;
        ApplicationThemeHelper.Preload(PreloadCategories.Core);
        ApplicationThemeHelper.ApplicationThemeName = Theme.Win11Light.Name;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLog.Write($"Application started ({string.Join(", ", e.Args.Select(arg => arg.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? AppLog.DescribeUrl(arg) : arg))}).");

        if (e.Args.Contains("--self-test"))
        {
            CategoryClassifier.SelfTest();
            DownloadActivity.SelfTest();
            DownloadManager.SelfTestAsync().GetAwaiter().GetResult();
            Shutdown();
            return;
        }

        if (e.Args.Contains("--native-host") || e.Args.FirstOrDefault()?.StartsWith("chrome-extension://", StringComparison.Ordinal) == true)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            NativeMessagingHost.RunOnceAsync().GetAwaiter().GetResult();
            Shutdown();
            return;
        }

        var request = DownloadRequest.FromArgs(e.Args);
        instanceMutex = new Mutex(true, @"Local\CategorizedDownload.App", out var isFirstInstance);
        ownsInstanceMutex = isFirstInstance;
        if (!isFirstInstance)
        {
            try
            {
                ForwardToRunningInstance(request);
                AppLog.Write("Forwarded request to the running application instance.");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"The running download manager could not be reached.\n\n{ex.Message}",
                    "Categorized Download", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        pipeCancellation = new CancellationTokenSource();
        _ = ListenForDownloadsAsync(window, pipeCancellation.Token);
        _ = ListenForChromeHttpAsync(window, pipeCancellation.Token);

        if (request.Url is not null)
            window.ContentRendered += (_, _) =>
                window.TryBeginIncomingDownload(request.Url, request.Categorized ?? window.AutoCategorize, request.Filename);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        pipeCancellation?.Cancel();
        if (ownsInstanceMutex)
            instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void ForwardToRunningInstance(DownloadRequest request)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
        pipe.Connect(3000);
        using var writer = new StreamWriter(pipe) { AutoFlush = true };
        writer.WriteLine(JsonSerializer.Serialize(request));
    }

    private async Task ListenForDownloadsAsync(MainWindow window, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(pipe);
                var json = await reader.ReadLineAsync(cancellationToken);
                var request = json is null ? null : JsonSerializer.Deserialize<DownloadRequest>(json);
                if (request is null)
                    continue;
                AppLog.Write(request.Url is null ? "Received window activation request." : $"Received queued download: {AppLog.DescribeUrl(request.Url)}.");

                await Dispatcher.InvokeAsync(() =>
                {
                    if (request.Url is not null)
                        window.TryBeginIncomingDownload(request.Url, request.Categorized ?? window.AutoCategorize, request.Filename);
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ListenForChromeHttpAsync(MainWindow window, CancellationToken cancellationToken)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:17852/");
        try
        {
            listener.Start();
            AppLog.Write("Chrome URL interceptor listening on 127.0.0.1:17852.");
            while (!cancellationToken.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
                _ = HandleChromeHttpRequestAsync(window, context);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Write($"Chrome URL interceptor stopped: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task HandleChromeHttpRequestAsync(MainWindow window, HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            context.Response.Headers["Access-Control-Allow-Origin"] = ChromeOrigin;
            context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-CategorizedDownload-Key";
            context.Response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";

            if (request.HttpMethod == "OPTIONS")
            {
                context.Response.StatusCode = 204;
                return;
            }

            if (request.HttpMethod != "POST" || request.Url?.AbsolutePath != "/download" ||
                request.Headers["X-CategorizedDownload-Key"] != ChromeBridgeKey ||
                request.ContentLength64 is < 0 or > 1_048_576)
            {
                context.Response.StatusCode = 403;
                return;
            }

            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            var message = JsonSerializer.Deserialize<ChromeBridgeMessage>(await reader.ReadToEndAsync(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (message is null || !Uri.TryCreate(message.Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                context.Response.StatusCode = 400;
                return;
            }

            var fileName = Path.GetFileName(message.Filename);
            AppLog.Write($"Chrome URL handoff received: {AppLog.DescribeUrl(message.Url)}; filename={fileName}.");
            var accepted = await Dispatcher.InvokeAsync(() =>
                window.TryBeginIncomingDownload(message.Url, window.AutoCategorize, fileName));
            await WriteChromeHttpResponseAsync(context.Response, accepted ? 202 : 200,
                accepted ? "{\"ok\":true}" : "{\"ok\":false,\"blocked\":true}");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Chrome URL handoff failed: {ex.GetType().Name}: {ex.Message}");
            if (!context.Response.OutputStream.CanWrite)
                return;
            await WriteChromeHttpResponseAsync(context.Response, 500, "{\"ok\":false}");
        }
        finally
        {
            context.Response.Close();
        }
    }

    private static async Task WriteChromeHttpResponseAsync(HttpListenerResponse response, int statusCode, string json)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }
}

internal sealed record ChromeBridgeMessage(string Url, string? Filename);

internal sealed record DownloadRequest(string? Url, string? Filename, bool? Categorized)
{
    public static DownloadRequest FromArgs(string[] args)
    {
        var urlIndex = Array.IndexOf(args, "--download");
        var fileIndex = Array.IndexOf(args, "--filename");
        return new(
            urlIndex >= 0 && urlIndex + 1 < args.Length ? args[urlIndex + 1] : null,
            fileIndex >= 0 && fileIndex + 1 < args.Length ? args[fileIndex + 1] : null,
            args.Contains("--chrome") ? null : !args.Contains("--direct"));
    }
}

internal static class NativeMessagingHost
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task RunOnceAsync()
    {
        Stream? output = null;
        try
        {
            using var input = Console.OpenStandardInput();
            output = Console.OpenStandardOutput();
            var lengthBytes = new byte[4];
            await input.ReadExactlyAsync(lengthBytes).ConfigureAwait(false);
            var length = BitConverter.ToInt32(lengthBytes);
            if (length is <= 0 or > 1024 * 1024)
                throw new InvalidDataException("Invalid native message length.");

            var payload = new byte[length];
            await input.ReadExactlyAsync(payload).ConfigureAwait(false);
            var message = JsonSerializer.Deserialize<NativeMessage>(payload, JsonOptions)
                ?? throw new InvalidDataException("Chrome sent an empty message.");
            if (!Uri.TryCreate(message.Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidDataException("Only HTTP and HTTPS URLs are supported.");
            AppLog.Write($"Chrome native handoff received: {AppLog.DescribeUrl(message.Url)}; filename={Path.GetFileName(message.Filename)}.");

            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true };
            start.ArgumentList.Add("--download");
            start.ArgumentList.Add(message.Url);
            start.ArgumentList.Add("--chrome");
            if (!string.IsNullOrWhiteSpace(message.Filename))
            {
                start.ArgumentList.Add("--filename");
                start.ArgumentList.Add(Path.GetFileName(message.Filename));
            }
            Process.Start(start);
            AppLog.Write("Chrome native handoff accepted and forwarded to the desktop application.");
            await ReplyAsync(output, new { ok = true }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Chrome native handoff failed: {ex.Message}");
            output ??= Console.OpenStandardOutput();
            await ReplyAsync(output, new { ok = false, error = ex.Message }).ConfigureAwait(false);
        }
    }

    private static async Task ReplyAsync(Stream output, object value)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value);
        await output.WriteAsync(BitConverter.GetBytes(json.Length)).ConfigureAwait(false);
        await output.WriteAsync(json).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    private sealed record NativeMessage(string Url, string? Filename);
}

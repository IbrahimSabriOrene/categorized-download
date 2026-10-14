using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using DevExpress.Xpf.Bars;
using DevExpress.Xpf.Editors;
using DevExpress.Xpf.Grid;
using Forms = System.Windows.Forms;

namespace CategorizedDownload;

public partial class MainWindow
{
    private readonly DownloadManager manager = new();
    private readonly Forms.NotifyIcon trayIcon;
    private readonly DownloadActivity activity = new();

    public ObservableCollection<DownloadRecord> Downloads { get; } = new(HistoryStore.Load());
    public ObservableCollection<CategoryRule> CategoryRules { get; } = new(CategoryRuleStore.Load());
    public DownloadRecord? SelectedDownload { get; set; }
    public CategoryRule? SelectedCategoryRule { get; set; }
    public bool AutoCategorize => CategorizeToggle.IsChecked == true;
    public bool AcceptIncomingDownloads => activity.AcceptIncoming;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        CategoryClassifier.UseCustomRules(CategoryRules);
        trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Categorized Download",
            Visible = true
        };
        trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
                Hide();
        };
        Closing += (_, _) => trayIcon.Dispose();
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e) =>
        await BeginDownloadAsync(UrlEditor.EditValue?.ToString() ?? "", CategorizeToggle.IsChecked == true);

    private async void UrlEditor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
            await BeginDownloadAsync(UrlEditor.EditValue?.ToString() ?? "", CategorizeToggle.IsChecked == true);
    }

    public async Task BeginDownloadAsync(string url, bool categorized, string? suggestedName = null)
    {
        ShowFromTray();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            StatusText.Text = "Enter a valid HTTP or HTTPS address.";
            return;
        }

        activity.Start();
        AppLog.Write($"Download started: {AppLog.DescribeUrl(url)}; categorized={categorized}; suggested={Path.GetFileName(suggestedName)}.");
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.StyleSettings = new ProgressBarMarqueeStyleSettings();
        StatusText.Text = activity.ActiveCount == 1 ? "Connecting…" : $"Connecting… ({activity.ActiveCount} downloads active)";
        UrlEditor.EditValue = url;

        var progress = new Progress<DownloadProgress>(value =>
        {
            if (activity.ActiveCount == 1 && value.Percent is not null)
            {
                if (DownloadProgress.StyleSettings is ProgressBarMarqueeStyleSettings)
                    DownloadProgress.StyleSettings = new ProgressBarStyleSettings();
                DownloadProgress.Value = value.Percent.Value;
            }
            else if (activity.ActiveCount > 1 && DownloadProgress.StyleSettings is not ProgressBarMarqueeStyleSettings)
                DownloadProgress.StyleSettings = new ProgressBarMarqueeStyleSettings();
            StatusText.Text = $"Downloading {value.FileName} — {value.ReceivedText}" +
                              (activity.ActiveCount > 1 ? $" ({activity.ActiveCount} active)" : "");
        });

        try
        {
            var record = await manager.DownloadAsync(url, categorized, suggestedName, progress);
            Downloads.Insert(0, record);
            DownloadsGrid.SelectedItem = record;
            ShowDownloadDetails(record);
            while (Downloads.Count > HistoryStore.MaximumEntries)
                Downloads.RemoveAt(Downloads.Count - 1);
            StatusText.Text = HistoryStore.Save(Downloads)
                ? $"Saved to {record.Destination}"
                : $"Saved to {record.Destination}, but the history log could not be updated.";
            AppLog.Write($"Download completed: {record.FileName}; category={record.Category}; bytes={record.Size}; destination={record.Destination}.");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Download failed: {ex.Message}";
            AppLog.Write($"Download failed: {AppLog.DescribeUrl(url)}; {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            activity.Complete();
            DownloadProgress.Visibility = activity.ActiveCount == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (activity.ActiveCount > 0)
                StatusText.Text = $"{activity.ActiveCount} download(s) active.";
        }
    }

    public bool TryBeginIncomingDownload(string url, bool categorized, string? suggestedName = null)
    {
        if (!activity.AcceptIncoming)
        {
            StatusText.Text = "Incoming downloads are stopped.";
            AppLog.Write($"Incoming download blocked: {AppLog.DescribeUrl(url)}.");
            return false;
        }

        _ = BeginDownloadAsync(url, categorized, suggestedName);
        return true;
    }

    private void IncomingDownloadsButton_Click(object sender, RoutedEventArgs e)
    {
        activity.ToggleIncoming();
        IncomingDownloadsButton.Content = AcceptIncomingDownloads ? "Stop incoming" : "Allow incoming";
        StatusText.Text = AcceptIncomingDownloads ? "Incoming downloads allowed." : "Incoming downloads stopped.";
    }

    public void ShowFromTray()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        if (!IsVisible)
            Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e) => OpenContainingFolder(SelectedDownload);

    private void OpenFileContextMenuItem_Click(object sender, ItemClickEventArgs e)
    {
        var record = ContextDownload();
        if (record is null || !File.Exists(record.Destination))
        {
            StatusText.Text = "The selected download no longer exists.";
            return;
        }

        Process.Start(new ProcessStartInfo(record.Destination) { UseShellExecute = true });
    }

    private void OpenFolderContextMenuItem_Click(object sender, ItemClickEventArgs e) => OpenContainingFolder(ContextDownload());

    private DownloadRecord? ContextDownload() =>
        DownloadsView.GridMenu.MenuInfo is GridCellMenuInfo menuInfo
            ? menuInfo.OriginalRow as DownloadRecord
            : SelectedDownload;

    private void OpenContainingFolder(DownloadRecord? record)
    {
        var path = record?.Destination;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (record is not null)
            StatusText.Text = "The selected download no longer exists.";
        else
            Process.Start("explorer.exe", DownloadManager.DownloadsRoot);
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        Downloads.Clear();
        SelectedDownload = null;
        DownloadDetailsPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = HistoryStore.Save(Downloads) ? "Download log cleared." : "The history log could not be cleared.";
    }

    private void DownloadsGrid_SelectedItemChanged(object sender, SelectedItemChangedEventArgs e)
    {
        SelectedDownload = e.NewItem as DownloadRecord;
        ShowDownloadDetails(SelectedDownload);
    }

    private void ShowDownloadDetails(DownloadRecord? record)
    {
        DownloadDetailsPanel.Visibility = record is null ? Visibility.Collapsed : Visibility.Visible;
        if (record is null)
            return;

        SelectedFileNameText.Text = record.FileName;
        SelectedFileSizeText.Text = $"{record.Category} · {record.SizeDetailsText}";
        SelectedDestinationText.Text = record.Destination;
    }

    private void ToggleCategoryBuilderButton_Click(object sender, RoutedEventArgs e) =>
        CategoryBuilderPanel.Visibility = CategoryBuilderPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

    private void SaveCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CategoryRule.TryCreate(
                CategoryNameEditor.EditValue?.ToString(),
                CategoryExtensionsEditor.EditValue?.ToString(),
                out var rule,
                out var error))
        {
            StatusText.Text = error;
            return;
        }

        var existing = CategoryRules.FirstOrDefault(item => item.Category.Equals(rule!.Category, StringComparison.OrdinalIgnoreCase));
        var conflict = CategoryRules
            .Where(item => !ReferenceEquals(item, existing))
            .FirstOrDefault(item => item.Extensions.Intersect(rule!.Extensions, StringComparer.OrdinalIgnoreCase).Any());
        if (conflict is not null)
        {
            StatusText.Text = $"An extension is already assigned to {conflict.Category}.";
            return;
        }

        if (existing is null)
            CategoryRules.Add(rule!);
        else
            CategoryRules[CategoryRules.IndexOf(existing)] = rule!;

        CategoryClassifier.UseCustomRules(CategoryRules);
        StatusText.Text = CategoryRuleStore.Save(CategoryRules)
            ? $"Category template saved: {rule!.Category}."
            : "The category template could not be saved.";
        CategoryNameEditor.EditValue = null;
        CategoryExtensionsEditor.EditValue = null;
    }

    private void DeleteCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCategoryRule is null)
        {
            StatusText.Text = "Select a category template to delete.";
            return;
        }

        var category = SelectedCategoryRule.Category;
        CategoryRules.Remove(SelectedCategoryRule);
        SelectedCategoryRule = null;
        CategoryClassifier.UseCustomRules(CategoryRules);
        StatusText.Text = CategoryRuleStore.Save(CategoryRules)
            ? $"Category template deleted: {category}."
            : "The category template was removed, but the saved file could not be updated.";
    }
}

internal sealed class DownloadActivity
{
    public bool AcceptIncoming { get; private set; } = true;
    public int ActiveCount { get; private set; }

    public void ToggleIncoming() => AcceptIncoming = !AcceptIncoming;
    public void Start() => ActiveCount++;
    public void Complete() => ActiveCount--;

    public static void SelfTest()
    {
        var activity = new DownloadActivity();
        if (!activity.AcceptIncoming)
            throw new InvalidOperationException("Incoming downloads should be enabled by default.");

        activity.Start();
        activity.Start();
        activity.ToggleIncoming();
        if (activity.AcceptIncoming || activity.ActiveCount != 2)
            throw new InvalidOperationException("Stopping incoming downloads must not stop active downloads.");

        activity.Complete();
        activity.Complete();
        activity.ToggleIncoming();
        if (activity.ActiveCount != 0 || !activity.AcceptIncoming)
            throw new InvalidOperationException("Concurrent download tracking self-test failed.");
    }
}

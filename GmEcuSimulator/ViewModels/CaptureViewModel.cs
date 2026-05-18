using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Core.Bus;

namespace GmEcuSimulator.ViewModels;

// Backs the Captures tab. The bus always writes captured $36 payloads +
// flash regions to disk - the user-facing tab is just a browser/launcher
// for the files. The previous Enabled checkbox was removed when
// Service36Handler moved to unconditional address-anchoring (so the old
// "capture off = NRC $31 on absolute addresses" branch no longer exists;
// capture is always on, but writes only happen when a directory is
// configured, which WPF startup unconditionally does).
public sealed class CaptureViewModel : NotifyPropertyChangedBase
{
    private readonly CaptureSettings settings;

    public ObservableCollection<CapturedFile> Captures { get; } = new();

    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public CaptureViewModel(CaptureSettings settings)
    {
        this.settings = settings;
        settings.CaptureWritten += OnCaptureWritten;

        OpenFolderCommand = new RelayCommand(OpenFolder);
        RefreshCommand    = new RelayCommand(Refresh);

        Refresh();
    }

    public string CaptureDirectory => settings.CaptureDirectory ?? "(not set)";

    private void OnCaptureWritten(string path)
    {
        // Marshal to the UI thread - the event fires from whatever thread
        // EcuExitLogic ran on (TesterPresentTicker P3C timeout / IPC).
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            // Refresh wholesale - new file might also bump the timestamps of
            // others (e.g. a session rebuild) and the list is small.
            Refresh();
        }));
    }

    public void Refresh()
    {
        Captures.Clear();
        if (string.IsNullOrEmpty(settings.CaptureDirectory)) return;
        if (!Directory.Exists(settings.CaptureDirectory)) return;
        // Recurse: capture writers group bins under per-session subfolders
        // (e.g. {dir}/{ecu}-{utc}/cal01_3FAFE0.bin), so a top-level scan
        // misses everything. SearchOption.AllDirectories walks the whole
        // tree and the grid groups visually by Modified-desc.
        var infos = new DirectoryInfo(settings.CaptureDirectory)
            .GetFiles("*.bin", SearchOption.AllDirectories)
            .OrderByDescending(f => f.LastWriteTimeUtc);
        foreach (var fi in infos)
            Captures.Add(new CapturedFile(fi.FullName, fi.Length, fi.LastWriteTime));
    }

    private void OpenFolder()
    {
        if (string.IsNullOrEmpty(settings.CaptureDirectory)) return;
        try
        {
            Directory.CreateDirectory(settings.CaptureDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = settings.CaptureDirectory,
                UseShellExecute = true,
            });
        }
        catch { /* user-visible failure goes to status bar by other means */ }
    }
}

public sealed record CapturedFile(string FullPath, long SizeBytes, DateTime Modified)
{
    public string FileName => Path.GetFileName(FullPath);
    public string SizeText => SizeBytes < 1024 ? $"{SizeBytes} B"
                            : SizeBytes < 1024 * 1024 ? $"{SizeBytes / 1024.0:N1} KB"
                            : $"{SizeBytes / 1024.0 / 1024.0:N2} MB";
}

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SapTextureTool.Models;
using SapTextureTool.Services;
using System.Collections.ObjectModel;
using System.IO;

namespace SapTextureTool;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<TextureEntry> _allEntries = new();
    private readonly ObservableCollection<TextureEntry> _filteredEntries = new();
    private TextureEntry? _selected;
    private string _gameDir = "";
    private string? _activePackDir;
    private string? _activePackName;

    public MainWindow()
    {
        InitializeComponent();
        TextureList.ItemsSource = _filteredEntries;

        ReplacementDropZone.AddHandler(DragDrop.DropEvent, OnReplacementDrop);
        ReplacementDropZone.AddHandler(DragDrop.DragOverEvent, OnReplacementDragOver);

        LoadSavedState();
        this.Opened += (_, _) => OnScanClick(null, null!);
    }

    private void LoadSavedState()
    {
        var saved = PackService.LoadSavedGameDir();
        _gameDir = saved ?? BundleService.GetDefaultGameDir();
        GameDirBox.Text = _gameDir;
    }

    // ── Game dir / scan ───────────────────────────────────────────────────────

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Super Auto Pets game folder",
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(_gameDir),
        });
        if (result.Count == 0) return;
        _gameDir = result[0].Path.LocalPath;
        GameDirBox.Text = _gameDir;
        PackService.SaveGameDir(_gameDir);
    }

    private async void OnScanClick(object? sender, RoutedEventArgs e)
    {
        SetStatus("Scanning...");
        _allEntries.Clear();
        _filteredEntries.Clear();

        var entries = await Task.Run(() =>
            BundleService.ScanBundles(_gameDir, new Progress<string>(msg =>
                Dispatcher.UIThread.Post(() => SetStatus(msg)))));

        foreach (var e2 in entries.OrderBy(x => x.Name))
            _allEntries.Add(e2);

        ApplyFilter(SearchBox.Text ?? "");
        SetStatus($"Found {_allEntries.Count} textures.");
        UpdateStagedCount();
    }

    // ── Search ────────────────────────────────────────────────────────────────

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) =>
        ApplyFilter(SearchBox.Text ?? "");

    private void ApplyFilter(string term)
    {
        _filteredEntries.Clear();
        var lower = term.Trim().ToLowerInvariant();
        foreach (var entry in _allEntries)
        {
            if (!entry.IsAudio && entry.Name.EndsWith("_2x", StringComparison.OrdinalIgnoreCase)) continue;
            if (lower.Length == 0 || entry.Name.ToLowerInvariant().Contains(lower))
                _filteredEntries.Add(entry);
        }
    }

    // ── Texture selection & preview ──────────────────────────────────────────

    private async void OnTextureSelected(object? sender, SelectionChangedEventArgs e)
    {
        _selected = TextureList.SelectedItem as TextureEntry;
        if (_selected == null)
        {
            ClearCurrentPreview();
            ClearReplacementPreviewUi();
            TextureInfo.Text = "No asset selected";
            ImportButton.Content = "Import PNG";
            return;
        }

        if (_selected.IsAudio)
        {
            TextureInfo.Text = $"{_selected.Name}   {_selected.AudioChannels}ch  {_selected.AudioFrequency} Hz  {_selected.AudioLength:F1}s   {AudioFormatName(_selected.AudioCompressionFormat)}   {Path.GetFileName(_selected.BundlePath)}";
            CurrentPreview.IsVisible = false;
            CurrentPlaceholder.Text = $"♪  {_selected.Name}\n{_selected.AudioChannels}ch  ·  {_selected.AudioFrequency} Hz  ·  {_selected.AudioLength:F1}s";
            CurrentPlaceholder.IsVisible = true;
            ImportButton.Content = "Import Audio";
            SetStatus("");

            if (_selected.HasReplacement)
                ShowAudioReplacementInfo(_selected.ReplacementPath!);
            else
                ClearReplacementPreviewUi();
        }
        else
        {
            TextureInfo.Text = $"{_selected.Name}   {_selected.Width}×{_selected.Height}   format {_selected.TextureFormat}   {Path.GetFileName(_selected.BundlePath)}";
            ImportButton.Content = "Import PNG";

            CurrentPlaceholder.Text = "Select a texture";
            CurrentPlaceholder.IsVisible = false;
            CurrentPreview.Source = null;
            CurrentPreview.IsVisible = false;
            SetStatus("Loading preview...");

            var (bmp, err) = await Task.Run(() => BundleService.ExtractPreview(_selected));
            CurrentPreview.Source = bmp;
            CurrentPreview.IsVisible = bmp != null;
            CurrentPlaceholder.IsVisible = bmp == null;
            SetStatus(err != null ? $"Preview failed: {err}" : "");

            if (_selected.HasReplacement)
                ShowReplacementPreview(BundleService.LoadPngPreview(_selected.ReplacementPath!));
            else
                ClearReplacementPreviewUi();
        }

        UpdateAddToPackButton();
    }

    private static string AudioFormatName(int fmt) => fmt switch
    {
        0 => "PCM",
        1 => "Vorbis",
        2 => "MP3",
        _ => $"format {fmt}",
    };

    // ── Import / clear replacement ────────────────────────────────────────────

    private async void OnImportPngClick(object? sender, RoutedEventArgs e)
    {
        if (_selected == null) return;

        IReadOnlyList<IStorageFile> files;
        if (_selected.IsAudio)
        {
            files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select replacement audio",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Audio Files") { Patterns = new[] { "*.wav", "*.ogg", "*.mp3" } } },
            });
        }
        else
        {
            files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select replacement PNG",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("PNG Image") { Patterns = new[] { "*.png" } } },
            });
        }
        if (files.Count == 0) return;
        SetReplacement(_selected, files[0].Path.LocalPath);
    }

    private void OnClearReplacementClick(object? sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        _selected.ReplacementPath = null;
        ClearReplacementPreviewUi();
        UpdateStagedCount();
        UpdateAddToPackButton();
    }

    private void OnReplacementDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnReplacementDrop(object? sender, DragEventArgs e)
    {
        if (_selected == null) return;
        if (!e.Data.Contains(DataFormats.Files)) return;
        var files = e.Data.GetFiles()?.ToList();
        if (files == null || files.Count == 0) return;
        var path = files[0].Path.LocalPath;
        var ext = Path.GetExtension(path);
        if (_selected.IsAudio)
        {
            if (ext.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
                SetReplacement(_selected, path);
        }
        else
        {
            if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase))
                SetReplacement(_selected, path);
        }
    }

    private void SetReplacement(TextureEntry entry, string filePath)
    {
        entry.ReplacementPath = filePath;
        if (entry.IsAudio)
            ShowAudioReplacementInfo(filePath);
        else
            ShowReplacementPreview(BundleService.LoadPngPreview(filePath));
        UpdateStagedCount();
        UpdateAddToPackButton();
    }

    private void ShowAudioReplacementInfo(string path)
    {
        ReplacementPreview.Source = null;
        ReplacementPreview.IsVisible = false;
        ReplacementPlaceholder.Text = $"♪  {Path.GetFileName(path)}";
        ReplacementPlaceholder.IsVisible = true;
    }

    private void ShowReplacementPreview(Bitmap? bmp)
    {
        ReplacementPreview.Source = bmp;
        ReplacementPreview.IsVisible = bmp != null;
        ReplacementPlaceholder.IsVisible = bmp == null;
    }

    private void ClearCurrentPreview()
    {
        CurrentPreview.Source = null;
        CurrentPreview.IsVisible = false;
        CurrentPlaceholder.Text = "Select a texture";
        CurrentPlaceholder.IsVisible = true;
    }

    private void ClearReplacementPreviewUi()
    {
        ReplacementPreview.Source = null;
        ReplacementPreview.IsVisible = false;
        ReplacementPlaceholder.Text = _selected?.IsAudio == true
            ? "Drop audio file here or click Import"
            : "Drop PNG here or click Import";
        ReplacementPlaceholder.IsVisible = true;
    }

    // ── Apply / restore ───────────────────────────────────────────────────────

    private async void OnApplyAllClick(object? sender, RoutedEventArgs e)
    {
        var staged = _allEntries.Where(x => x.HasReplacement).ToList();
        if (staged.Count == 0) { SetStatus("Nothing staged."); return; }

        SetStatus("Applying replacements...");
        try
        {
            await Task.Run(() => BundleService.ApplyReplacements(staged, new Progress<string>(msg =>
                Dispatcher.UIThread.Post(() => SetStatus(msg)))));
            SetStatus($"Applied {staged.Count} replacement(s). Restart SAP to see changes.");
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
    }

    private async void OnRestoreClick(object? sender, RoutedEventArgs e)
    {
        SetStatus("Restoring backups...");
        try
        {
            await Task.Run(() => BundleService.RestoreBackups(_gameDir));
            SetStatus("Backups restored. Restart SAP to see changes.");
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
    }

    // ── Pack management ───────────────────────────────────────────────────────

    private async void OnManagePackClick(object? sender, RoutedEventArgs e)
    {
        var mgr = new PackManagerWindow(_allEntries, _activePackDir);
        await mgr.ShowDialog(this);
        _activePackDir = mgr.ActivePackDir;
        _activePackName = mgr.ActivePackName;
        UpdateStagedCount();
        UpdateAddToPackButton();
        ApplyFilter(SearchBox.Text ?? "");
    }

    private async void OnLoadPackClick(object? sender, RoutedEventArgs e)
    {
        if (_allEntries.Count == 0) { SetStatus("Scan bundles first before loading a pack."); return; }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open pack.json",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Pack manifest") { Patterns = new[] { "pack.json" } } },
        });
        if (files.Count == 0) return;

        var result = PackService.LoadPack(files[0].Path.LocalPath);
        if (result == null) { SetStatus("Failed to read pack.json."); return; }

        var (manifest, packDir) = result.Value;

        foreach (var entry in _allEntries) { entry.IncludeInPack = false; entry.ReplacementPath = null; }

        var count = PackService.ApplyPackToEntries(manifest, packDir, _allEntries);
        _activePackDir = packDir;
        _activePackName = manifest.Name;
        PackService.AddOrUpdateInLibrary(new SapTextureTool.Models.SavedPackRef
            { Name = manifest.Name, Version = manifest.Version, PackDir = packDir });

        UpdateStagedCount();
        UpdateAddToPackButton();
        ApplyFilter(SearchBox.Text ?? "");
        SetStatus($"Loaded '{manifest.Name}' — {count} texture(s) staged.");
    }

    private void OnToggleInPackClick(object? sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        _selected.IncludeInPack = !_selected.IncludeInPack;
        UpdateAddToPackButton();
        UpdateStagedCount();
    }

    private void UpdateAddToPackButton()
    {
        if (_selected?.HasReplacement == true)
        {
            AddToPackButton.IsVisible = true;
            AddToPackButton.Content = _selected.IncludeInPack ? "Remove from Pack" : "Add to Pack";
        }
        else
        {
            AddToPackButton.IsVisible = false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetStatus(string msg) => StatusText.Text = msg;

    private async void OnStatusClick(object? sender, PointerPressedEventArgs e)
    {
        var text = StatusText.Text;
        if (!string.IsNullOrEmpty(text))
            await (TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask);
    }

    private void UpdateStagedCount()
    {
        var staged = _allEntries.Count(x => x.HasReplacement);
        var inPack = _allEntries.Count(x => x.IncludeInPack);
        StagedCount.Text = staged > 0 ? $"{staged} staged" : "";
        PackCount.Text = inPack > 0
            ? (_activePackName != null ? $"{_activePackName} ({inPack})" : $"{inPack} in pack")
            : (_activePackName != null ? _activePackName : "");
    }
}

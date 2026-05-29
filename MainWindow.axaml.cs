using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Fmod5Sharp;
using NAudio.Vorbis;
using NAudio.Wave;
using NLayer.NAudioSupport;
using SapTextureTool.Models;
using SapTextureTool.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SapTextureTool;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<TextureEntry> _allEntries = new();
    private readonly ObservableCollection<TextureEntry> _filteredEntries = new();
    private TextureEntry? _selected;
    private string _gameDir = "";
    private string? _activePackDir;
    private string? _activePackName;
    private string? _activePackVersion = "1.0";
    // In-memory state of every known pack, persisted across pack swaps and app restarts.
    private Dictionary<string, PackDraft> _drafts = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
        TextureList.ItemsSource = _filteredEntries;

        ReplacementDropZone.AddHandler(DragDrop.DropEvent, OnReplacementDrop);
        ReplacementDropZone.AddHandler(DragDrop.DragOverEvent, OnReplacementDragOver);

        LoadSavedState();
        this.Opened += (_, _) => OnScanClick(null, null!);
        this.Closing += (_, _) => { PersistActiveDraft(); StopPlayback(); };
    }

    private void LoadSavedState()
    {
        var saved = PackService.LoadSavedGameDir();
        _gameDir = saved ?? BundleService.GetDefaultGameDir();
        GameDirBox.Text = _gameDir;
        _drafts = DraftService.Load();
        Logger.Info($"Loaded {_drafts.Count} pack draft(s) from disk");
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

        AssignDuplicateLabels(_allEntries);

        // Re-apply the active pack's draft so an in-progress pack survives the scan.
        if (_activePackDir != null && _drafts.TryGetValue(_activePackDir, out var draft))
            PackService.ApplyDraftToEntries(draft, _allEntries);

        ApplyFilter(SearchBox.Text ?? "");
        SetStatus($"Found {_allEntries.Count} textures.");
        UpdateStagedCount();
    }

    // Tags textures sharing a Name with a 1/N badge so the user can tell duplicates apart.
    // Audio entries are excluded — an audio clip that happens to share a sprite's name
    // shouldn't inflate the count or show a badge.
    // Sort within group: bundle entries first (likely current art), then by area descending.
    private static void AssignDuplicateLabels(IEnumerable<TextureEntry> entries)
    {
        var textures = entries.Where(e => e.Kind == AssetKind.Texture);
        foreach (var group in textures.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var list = group.ToList();
            if (list.Count <= 1)
            {
                list[0].DuplicateIndex = 0;
                list[0].DuplicateCount = 0;
                continue;
            }
            var ordered = list
                .OrderBy(e => e.IsBundle ? 0 : 1)
                .ThenByDescending(e => (long)e.Width * e.Height)
                .ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].DuplicateIndex = i + 1;
                ordered[i].DuplicateCount = ordered.Count;
            }
        }
    }

    // ── Search ────────────────────────────────────────────────────────────────

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) =>
        ApplyFilter(SearchBox.Text ?? "");

    private void OnShow2xChanged(object? sender, RoutedEventArgs e) =>
        ApplyFilter(SearchBox.Text ?? "");

    private void ApplyFilter(string term)
    {
        _filteredEntries.Clear();
        var lower = term.Trim().ToLowerInvariant();
        var show2x = Show2xCheckBox.IsChecked == true;
        foreach (var entry in _allEntries)
        {
            if (!show2x && !entry.IsAudio && entry.Name.EndsWith("_2x", StringComparison.OrdinalIgnoreCase)) continue;
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
            var info = $"{_selected.Name}   {_selected.Width}×{_selected.Height}   format {_selected.TextureFormat}   {Path.GetFileName(_selected.BundlePath)}";
            if (_selected.Name.EndsWith("_2x", StringComparison.OrdinalIgnoreCase))
                info += "   ·  staging this _2x makes it manual (base sprite won't override)";
            TextureInfo.Text = info;
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

        StopPlayback();
        UpdateAddToPackButton();
        UpdatePlayButtons();
        UpdateBorderControls();
        UpdateRestoreEntryButton();
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
        StopPlayback();
        _selected.ReplacementPath = null;
        ClearReplacementPreviewUi();
        UpdateStagedCount();
        UpdateAddToPackButton();
        UpdatePlayButtons();
        UpdateBorderControls();
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
        // Fresh user import — drop any prior border state so the UI starts from "no border".
        entry.OriginalReplacementPath = null;
        entry.Border = null;
        entry.ReplacementPath = filePath;
        if (entry.IsAudio)
            ShowAudioReplacementInfo(filePath);
        else
            ShowReplacementPreview(BundleService.LoadPngPreview(filePath));
        UpdateStagedCount();
        UpdateAddToPackButton();
        UpdatePlayButtons();
        UpdateBorderControls();
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

        // Auto-find _2x counterparts for staged textures that aren't already staged.
        // These may live in a different bundle from the base texture, so we can't rely
        // on AutoApplyX2 (which only searches the current bundle CAB entry).
        var stagedNames = new HashSet<string>(staged.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        var autoX2 = new List<TextureEntry>();
        var addedX2 = new HashSet<TextureEntry>();
        foreach (var entry in staged.Where(e => e.Kind == AssetKind.Texture))
        {
            var x2Name = entry.Name + "_2x";
            if (stagedNames.Contains(x2Name)) continue;
            foreach (var x2 in _allEntries.Where(e =>
                e.Kind == AssetKind.Texture &&
                e.Name.Equals(x2Name, StringComparison.OrdinalIgnoreCase)))
            {
                if (!addedX2.Add(x2)) continue;
                StageAutoX2(entry, x2);
                autoX2.Add(x2);
            }
        }

        var allToApply = staged.Concat(autoX2).ToList();
        SetStatus(autoX2.Count > 0
            ? $"Applying {staged.Count} replacement(s) + {autoX2.Count} auto-_2x ({string.Join(", ", autoX2.Select(x => x.Name))})..."
            : "Applying replacements (no _2x counterparts found)...");
        try
        {
            await Task.Run(() => BundleService.ApplyReplacements(allToApply, new Progress<string>(msg =>
                Dispatcher.UIThread.Post(() => SetStatus(msg)))));
            SetStatus($"Applied {staged.Count} replacement(s). Restart SAP to see changes.");
            UpdateRestoreEntryButton();
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
        finally
        {
            foreach (var x2 in autoX2)
            {
                x2.ReplacementPath = null;
                x2.IsAutoX2 = false;
            }
        }
    }

    // Stages an auto-_2x with the same art as its base. If the base had a SAP border applied,
    // re-render the ORIGINAL un-bordered PNG with the _2x default widths so the _2x ends up
    // with the thicker style instead of inheriting the 1x widths from the base's cached PNG.
    private void StageAutoX2(TextureEntry baseEntry, TextureEntry x2)
    {
        if (baseEntry.HasBorder
            && baseEntry.OriginalReplacementPath is { } origPath
            && File.Exists(origPath))
        {
            try
            {
                var srcBytes = File.ReadAllBytes(origPath);
                var x2cfg = DefaultBorderConfig(x2.Name);
                var x2Path = RenderBorderedToCache(srcBytes, x2cfg);
                x2.ReplacementPath = x2Path;
                x2.OriginalReplacementPath = origPath;
                x2.Border = x2cfg;
                x2.IsAutoX2 = true;
                return;
            }
            catch (Exception ex)
            {
                Services.Logger.Error($"Auto-_2x border render failed for {x2.Name}; falling back to base PNG", ex);
            }
        }
        x2.ReplacementPath = baseEntry.ReplacementPath;
        x2.IsAutoX2 = true;
    }

    private async void OnRestoreClick(object? sender, RoutedEventArgs e)
    {
        SetStatus("Restoring backups...");
        try
        {
            await Task.Run(() => BundleService.RestoreBackups(_gameDir));
            SetStatus("Backups restored. Restart SAP to see changes.");
            UpdateRestoreEntryButton();
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
    }

    private async void OnRestoreEntryClick(object? sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        if (!File.Exists(_selected.BundlePath + ".bak"))
        {
            SetStatus("No backup exists for this bundle yet — apply something first.");
            return;
        }

        var toRestore = new List<TextureEntry> { _selected };
        // Convenience: restoring a base texture also reverts its _2x counterpart, since
        // Apply auto-stages the _2x. If the user wants to keep a manual _2x, they can
        // re-import it afterward.
        if (_selected.Kind == AssetKind.Texture
            && !_selected.Name.EndsWith("_2x", StringComparison.OrdinalIgnoreCase))
        {
            var x2Name = _selected.Name + "_2x";
            foreach (var x2 in _allEntries.Where(en =>
                en.Kind == AssetKind.Texture &&
                en.Name.Equals(x2Name, StringComparison.OrdinalIgnoreCase)))
            {
                toRestore.Add(x2);
            }
        }

        var label = string.Join(", ", toRestore.Select(t => t.Name));
        SetStatus($"Restoring {label}...");
        try
        {
            await Task.Run(() => BundleService.RestoreEntries(toRestore, new Progress<string>(msg =>
                Dispatcher.UIThread.Post(() => SetStatus(msg)))));
            SetStatus($"Restored {label}. Restart SAP to see changes.");
            // Refresh the current preview from the just-restored bundle
            OnTextureSelected(null, null!);
        }
        catch (Exception ex)
        {
            Logger.Error("Restore entry failed", ex);
            SetStatus($"Restore error: {ex.Message}");
        }
    }

    private void UpdateRestoreEntryButton()
    {
        RestoreEntryButton.IsVisible = _selected != null
            && File.Exists(_selected.BundlePath + ".bak");
    }

    private void OnOpenLogClick(object? sender, RoutedEventArgs e)
    {
        var path = Logger.LogPath;
        var dir = Logger.LogDir;
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                SetStatus($"Log not initialized. Expected: {Path.Combine(dir, "log.txt")}");
                return;
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start(new ProcessStartInfo("open", $"-R \"{path}\"") { UseShellExecute = false });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{dir}\"") { UseShellExecute = false });
            }
            SetStatus($"Log: {path}");
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't open log location ({ex.Message}). Path: {path}");
        }
    }

    // ── Pack management ───────────────────────────────────────────────────────

    private async void OnManagePackClick(object? sender, RoutedEventArgs e)
    {
        // Snapshot any unsaved active-pack work before opening the manager (the manager
        // may swap to another pack, which would otherwise discard the in-memory state).
        PersistActiveDraft();
        var mgr = new PackManagerWindow(_allEntries, _drafts, _activePackDir, _activePackName, _activePackVersion, _gameDir);
        await mgr.ShowDialog(this);
        _activePackDir = mgr.ActivePackDir;
        _activePackName = mgr.ActivePackName;
        _activePackVersion = mgr.ActivePackVersion;
        // Ensure the (possibly new) active pack still has a draft entry, then persist.
        PersistActiveDraft();
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

        // Snapshot the outgoing pack's unsaved changes before we clear entries.
        PersistActiveDraft();
        foreach (var entry in _allEntries) { entry.IncludeInPack = false; entry.ReplacementPath = null; }

        // Prefer a draft for this pack if we have one — it carries unsaved changes from prior
        // sessions. Otherwise fall back to disk pack.json.
        int count;
        try
        {
            if (_drafts.TryGetValue(packDir, out var draft))
                count = PackService.ApplyDraftToEntries(draft, _allEntries);
            else
                count = PackService.ApplyPackToEntries(manifest, packDir, _allEntries);
        }
        catch (Exception ex)
        {
            SetStatus($"Error loading pack: {ex.Message}");
            return;
        }

        _activePackDir = packDir;
        _activePackName = manifest.Name;
        _activePackVersion = manifest.Version;
        PackService.AddOrUpdateInLibrary(new SapTextureTool.Models.SavedPackRef
            { Name = manifest.Name, Version = manifest.Version, PackDir = packDir });
        // Ensure the new active pack always has a draft (so the Add to Pack picker can see it).
        PersistActiveDraft();

        UpdateStagedCount();
        UpdateAddToPackButton();
        ApplyFilter(SearchBox.Text ?? "");
        SetStatus($"Loaded '{manifest.Name}' — {count} texture(s) staged.");
    }

    private void OnToggleInPackClick(object? sender, RoutedEventArgs e)
    {
        if (_selected == null) return;

        // Toggling off is always immediate (active pack).
        if (_selected.IncludeInPack)
        {
            _selected.IncludeInPack = false;
            PersistActiveDraft();
            UpdateAddToPackButton();
            UpdateStagedCount();
            return;
        }

        var entry = _selected;
        // When there are 0 or 1 drafts, no picker is useful — just add to active (or set the
        // flag with no draft binding if no pack is active yet).
        if (_drafts.Count <= 1)
        {
            AddEntryToActivePack(entry);
            return;
        }

        // Multi-draft: show a popup picker. Active draft floats to the top.
        var ordered = _drafts.Values
            .OrderBy(d => string.Equals(d.PackDir, _activePackDir, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(d => d.Name)
            .ToList();

        var flyout = new MenuFlyout();
        foreach (var d in ordered)
        {
            var isActive = string.Equals(d.PackDir, _activePackDir, StringComparison.OrdinalIgnoreCase);
            var item = new MenuItem { Header = isActive ? $"{d.Name} (active)" : d.Name };
            var draftRef = d;
            item.Click += (_, _) => AddEntryToTargetDraft(entry, draftRef);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(AddToPackButton);
    }

    private void AddEntryToActivePack(TextureEntry entry)
    {
        entry.IncludeInPack = true;
        PersistActiveDraft();
        UpdateAddToPackButton();
        UpdateStagedCount();
        if (_activePackName != null)
            SetStatus($"Added '{entry.Name}' to '{_activePackName}'.");
    }

    private void AddEntryToTargetDraft(TextureEntry entry, PackDraft draft)
    {
        var isActive = string.Equals(draft.PackDir, _activePackDir, StringComparison.OrdinalIgnoreCase);
        if (isActive)
        {
            AddEntryToActivePack(entry);
            return;
        }
        // Non-active: write the entry into that pack's draft directly. Entry state stays
        // bound to the active pack (so the green dot/list filter still reflects active).
        PackService.AddEntryToDraft(draft, entry);
        DraftService.Save(_drafts);
        SetStatus($"Added '{entry.Name}' to '{draft.Name}' (not active — switch to it to see).");
    }

    // Snapshot the active pack's current state into its draft and persist to disk. Safe
    // to call when there's no active pack (no-op).
    private void PersistActiveDraft()
    {
        if (_activePackDir == null) return;
        PackService.SnapshotToDraft(_drafts, _activePackDir, _activePackName, _activePackVersion, _allEntries);
        DraftService.Save(_drafts);
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

    // ── Audio playback ────────────────────────────────────────────────────────

    private IWavePlayer? _wavePlayer;
    private WaveStream? _waveStream;
    private MemoryStream? _audioMemStream;
    private string _playingWhich = "";
    // Mac playback uses afplay because WaveOutEvent is Windows-only.
    private Process? _macPlayer;
    private string? _macTempPath;

    private bool IsPlaying() =>
        (_wavePlayer?.PlaybackState == PlaybackState.Playing)
        || (_macPlayer != null && !_macPlayer.HasExited);

    private async void OnPlayCurrentClick(object? sender, RoutedEventArgs e)
    {
        if (IsPlaying() && _playingWhich == "current")
        { StopPlayback(); return; }

        StopPlayback();
        if (_selected == null || !_selected.IsAudio) return;

        SetStatus("Loading audio...");
        var entry = _selected;
        var (data, err) = await Task.Run(() => BundleService.ExtractAudioBytes(entry));
        if (data == null) { SetStatus($"Audio error: {err}"); return; }
        SetStatus("");

        try
        {
            var playData = UnwrapFsb5IfNeeded(data);
            _audioMemStream = new MemoryStream(playData);
            _waveStream = DetectAudioFormat(playData, _audioMemStream, entry);
            BeginPlayback("current");
        }
        catch (Exception ex) { SetStatus($"Playback error: {ex.Message}"); StopPlayback(); }
    }

    private void OnPlayReplacementClick(object? sender, RoutedEventArgs e)
    {
        if (IsPlaying() && _playingWhich == "replacement")
        { StopPlayback(); return; }

        StopPlayback();
        if (_selected?.ReplacementPath == null) return;

        try
        {
            var ext = Path.GetExtension(_selected.ReplacementPath).ToLowerInvariant();
            _waveStream = ext switch
            {
                ".wav" => new WaveFileReader(_selected.ReplacementPath),
                ".ogg" => new VorbisWaveReader(_selected.ReplacementPath),
                // NLayer-backed decoder so MP3 playback works on macOS (no Msacm32.dll dependency).
                ".mp3" => new Mp3FileReaderBase(File.OpenRead(_selected.ReplacementPath),
                                                wf => new Mp3FrameDecompressor(wf)),
                _ => throw new NotSupportedException($"Format {ext} not supported"),
            };
            BeginPlayback("replacement");
        }
        catch (Exception ex) { SetStatus($"Playback error: {ex.Message}"); StopPlayback(); }
    }

    private static byte[] UnwrapFsb5IfNeeded(byte[] data)
    {
        if (data.Length < 4 || data[0]!='F' || data[1]!='S' || data[2]!='B' || data[3]!='5')
            return data;

        if (!FsbLoader.TryLoadFsbFromByteArray(data, out var bank) || bank == null || bank.Samples.Count == 0)
            throw new NotSupportedException("FSB5 container could not be parsed");

        var sample = bank.Samples[0];
        if (!sample.RebuildAsStandardFileFormat(out var rebuilt, out _) || rebuilt == null)
            throw new NotSupportedException("FSB5 audio format could not be rebuilt (unsupported codec)");

        return rebuilt;
    }

    private WaveStream DetectAudioFormat(byte[] data, Stream stream, TextureEntry entry)
    {
        if (data.Length >= 4)
        {
            // OGG Vorbis
            if (data[0] == 'O' && data[1] == 'g' && data[2] == 'g' && data[3] == 'S')
                return new VorbisWaveReader(stream);
            // RIFF WAV
            if (data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F')
                return new WaveFileReader(stream);
            // MP3 (ID3 tag or sync word). NLayer-backed so it works on Mac too.
            if ((data[0] == 'I' && data[1] == 'D' && data[2] == '3') ||
                (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0))
                return new Mp3FileReaderBase(stream, wf => new Mp3FrameDecompressor(wf));
        }
        // Raw PCM fallback
        return new RawSourceWaveStream(stream,
            new WaveFormat(entry.AudioFrequency, entry.AudioBitsPerSample, entry.AudioChannels));
    }

    private void BeginPlayback(string which)
    {
        _playingWhich = which;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            BeginMacPlayback(which);
            return;
        }
        try { _wavePlayer = new WaveOutEvent(); }
        catch (Exception ex)
        {
            Logger.Error("WaveOutEvent init failed", ex);
            SetStatus("Audio preview is not supported on this platform. Replacement still works — restart the game to hear changes.");
            _waveStream?.Dispose(); _waveStream = null;
            return;
        }
        _wavePlayer.Init(_waveStream!);
        _wavePlayer.PlaybackStopped += (_, _) =>
            Dispatcher.UIThread.Post(() => { _wavePlayer = null; ResetPlayButtons(); });
        _wavePlayer.Play();
        if (which == "current") PlayCurrentButton.Content = "■ Stop";
        else PlayReplacementButton.Content = "■ Stop";
    }

    // macOS branch: transcode the current WaveStream to a PCM-WAV temp file, then run afplay.
    // afplay does not handle raw Vorbis even with a .wav extension, so all formats go through
    // the NAudio PCM-16 conversion for consistency.
    private void BeginMacPlayback(string which)
    {
        if (_waveStream == null) return;
        try
        {
            var temp = Path.Combine(Path.GetTempPath(), $"sap_play_{Guid.NewGuid():N}.wav");
            var pcm16 = _waveStream.ToSampleProvider().ToWaveProvider16();
            using (var fs = File.Create(temp))
            using (var writer = new WaveFileWriter(fs, pcm16.WaveFormat))
            {
                var buf = new byte[8192];
                int n;
                while ((n = pcm16.Read(buf, 0, buf.Length)) > 0)
                    writer.Write(buf, 0, n);
            }
            _waveStream.Dispose(); _waveStream = null;
            _audioMemStream?.Dispose(); _audioMemStream = null;

            _macTempPath = temp;
            _macPlayer = new Process
            {
                StartInfo = new ProcessStartInfo("afplay", $"\"{temp}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
                EnableRaisingEvents = true,
            };
            _macPlayer.Exited += (_, _) =>
                Dispatcher.UIThread.Post(() =>
                {
                    CleanupMacPlayer();
                    ResetPlayButtons();
                });
            _macPlayer.Start();
            if (which == "current") PlayCurrentButton.Content = "■ Stop";
            else PlayReplacementButton.Content = "■ Stop";
            Logger.Info($"Mac playback start: {temp}");
        }
        catch (Exception ex)
        {
            Logger.Error("Mac playback failed", ex);
            SetStatus($"Playback error: {ex.Message}");
            CleanupMacPlayer();
        }
    }

    private void CleanupMacPlayer()
    {
        if (_macPlayer != null)
        {
            try { if (!_macPlayer.HasExited) _macPlayer.Kill(); } catch { }
            try { _macPlayer.Dispose(); } catch { }
            _macPlayer = null;
        }
        if (_macTempPath != null)
        {
            try { File.Delete(_macTempPath); } catch { }
            _macTempPath = null;
        }
    }

    private void StopPlayback()
    {
        CleanupMacPlayer();
        _wavePlayer?.Stop();
        _wavePlayer?.Dispose();
        _wavePlayer = null;
        _waveStream?.Dispose();
        _waveStream = null;
        _audioMemStream?.Dispose();
        _audioMemStream = null;
        ResetPlayButtons();
    }

    private void ResetPlayButtons()
    {
        PlayCurrentButton.Content = "▶ Play";
        PlayReplacementButton.Content = "▶ Play";
    }

    private void UpdatePlayButtons()
    {
        PlayCurrentButton.IsVisible = _selected?.IsAudio == true;
        PlayReplacementButton.IsVisible = _selected?.IsAudio == true && _selected.HasReplacement;
    }

    // ── SAP-style border tool ─────────────────────────────────────────────────

    private void UpdateBorderControls()
    {
        var show = _selected != null && _selected.Kind == AssetKind.Texture && _selected.HasReplacement;
        BorderControls.IsVisible = show;
        if (!show) return;
        if (_selected!.HasBorder)
        {
            BorderButton.Content = "Remove Border";
            BorderInfo.Text = "border: " + DescribeBorder(_selected.Border!);
        }
        else
        {
            BorderButton.Content = "Add SAP Border";
            BorderInfo.Text = "default: " + DescribeBorder(DefaultBorderConfig(_selected.Name));
        }
    }

    // Defaults match the measured in-game style:
    //   1x sprites: black 5→11 px (top→bot), white 4→9 px
    //   _2x sprites: black 7→15 px,           white 6→14 px
    private static BorderConfig DefaultBorderConfig(string name) =>
        name.EndsWith("_2x", StringComparison.OrdinalIgnoreCase)
            ? new BorderConfig(BlackPxTop: 7, BlackPxBot: 15, WhitePxTop: 6, WhitePxBot: 14)
            : new BorderConfig(BlackPxTop: 5, BlackPxBot: 11, WhitePxTop: 4, WhitePxBot: 9);

    private static string DescribeBorder(BorderConfig c) =>
        $"black {c.BlackPxTop}→{c.BlackPxBot}px, white {c.WhitePxTop}→{c.WhitePxBot}px (top→bot)";

    private void OnBorderClick(object? sender, RoutedEventArgs e)
    {
        if (_selected == null || _selected.Kind != AssetKind.Texture || !_selected.HasReplacement) return;

        if (_selected.HasBorder)
        {
            var original = _selected.OriginalReplacementPath;
            if (original != null && File.Exists(original))
            {
                _selected.OriginalReplacementPath = null;
                _selected.Border = null;
                _selected.ReplacementPath = original;
                ShowReplacementPreview(BundleService.LoadPngPreview(original));
            }
            else
            {
                _selected.OriginalReplacementPath = null;
                _selected.Border = null;
            }
            UpdateBorderControls();
            return;
        }

        try
        {
            var sourcePath = _selected.OriginalReplacementPath ?? _selected.ReplacementPath!;
            var sourceBytes = File.ReadAllBytes(sourcePath);
            var cfg = DefaultBorderConfig(_selected.Name);
            var cachePath = RenderBorderedToCache(sourceBytes, cfg);

            _selected.OriginalReplacementPath ??= _selected.ReplacementPath;
            _selected.Border = cfg;
            _selected.ReplacementPath = cachePath;
            ShowReplacementPreview(BundleService.LoadPngPreview(cachePath));
            UpdateBorderControls();
            SetStatus($"Border applied: {DescribeBorder(cfg)}");
        }
        catch (Exception ex)
        {
            Services.Logger.Error("Border apply failed", ex);
            SetStatus($"Border error: {ex.Message}");
        }
    }

    private static string RenderBorderedToCache(byte[] sourceBytes, BorderConfig cfg)
    {
        var bordered = BorderRenderer.AddBorder(sourceBytes, cfg);
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SapTextureTool", "cache", "borders");
        Directory.CreateDirectory(cacheDir);
        using var sha1 = System.Security.Cryptography.SHA1.Create();
        var hash = Convert.ToHexString(sha1.ComputeHash(sourceBytes)).ToLowerInvariant();
        var cachePath = Path.Combine(cacheDir,
            $"{hash}_b{cfg.BlackPxTop}-{cfg.BlackPxBot}_w{cfg.WhitePxTop}-{cfg.WhitePxBot}.png");
        File.WriteAllBytes(cachePath, bordered);
        return cachePath;
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

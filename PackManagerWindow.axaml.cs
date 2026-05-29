using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SapTextureTool.Models;
using SapTextureTool.Services;
using System.Collections.ObjectModel;
using System.IO;

namespace SapTextureTool;

public partial class PackManagerWindow : Window
{
    private readonly IList<TextureEntry> _allEntries;
    private readonly Dictionary<string, PackDraft> _drafts;
    private readonly string _gameDir;
    private readonly ObservableCollection<PackEntryViewModel> _items = new();
    private readonly ObservableCollection<SavedPackRef> _library = new();
    private bool _blockSwap;

    // Read by MainWindow after ShowDialog to sync active pack state.
    public string? ActivePackDir { get; private set; }
    public string? ActivePackName { get; private set; }
    public string? ActivePackVersion { get; private set; }

    public PackManagerWindow(IList<TextureEntry> allEntries, Dictionary<string, PackDraft> drafts,
        string? activePackDir, string? activePackName, string? activePackVersion, string gameDir)
    {
        InitializeComponent();
        _allEntries = allEntries;
        _drafts = drafts;
        _gameDir = gameDir;
        ActivePackDir = activePackDir;
        ActivePackName = activePackName;
        ActivePackVersion = activePackVersion;

        EntriesList.ItemsSource = _items;
        LibraryList.ItemsSource = _library;

        PackNameBox.Text = activePackName ?? "MySapPack";
        VersionBox.Text = activePackVersion ?? "1.0";

        LoadLibrary(activePackDir);
        RefreshPackList();

        this.Opened += async (_, _) => await LoadBitmapsAsync();
    }

    // ── Library ───────────────────────────────────────────────────────────────

    private void LoadLibrary(string? activePackDir)
    {
        _library.Clear();
        foreach (var p in PackService.LoadPackLibrary())
            _library.Add(p);

        LibraryEmptyHint.IsVisible = _library.Count == 0;

        if (activePackDir == null) return;
        var active = _library.FirstOrDefault(p => p.PackDir == activePackDir);
        if (active == null) return;

        _blockSwap = true;
        LibraryList.SelectedItem = active;
        PackNameBox.Text = active.Name;
        VersionBox.Text = active.Version;
        ActivePackName = active.Name;
        _blockSwap = false;
    }

    private void OnLibrarySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_blockSwap) return;
        if (LibraryList.SelectedItem is SavedPackRef packRef)
            SwapToPack(packRef);
    }

    private void OnRemoveFromLibraryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not SavedPackRef packRef) return;

        _blockSwap = true;
        _library.Remove(packRef);
        PackService.SavePackLibrary(_library.ToList());
        LibraryEmptyHint.IsVisible = _library.Count == 0;

        // Removing from the library also drops the draft — orphan drafts would clutter the
        // Add-to-Pack picker forever.
        if (_drafts.Remove(packRef.PackDir))
            DraftService.Save(_drafts);

        if (ActivePackDir == packRef.PackDir)
        {
            ActivePackDir = null;
            ActivePackName = null;
            ActivePackVersion = null;
            LibraryList.SelectedItem = null;
        }
        else
        {
            LibraryList.SelectedItem = _library.FirstOrDefault(p => p.PackDir == ActivePackDir);
        }
        _blockSwap = false;

        StatusText.Text = $"Removed '{packRef.Name}' from library.";
    }

    // Restores every bundle to backup state, then applies only this pack's textures.
    // After the operation the named pack is the sole modded content in the game.
    private async void OnActivatePackClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not SavedPackRef packRef) return;

        // Snapshot the outgoing pack's unsaved state before we switch to the target.
        PackService.SnapshotToDraft(_drafts, ActivePackDir, ActivePackName, ActivePackVersion, _allEntries);
        DraftService.Save(_drafts);

        // Load the target pack into entries — draft is preferred over disk if it exists.
        ClearAllReplacements();
        int count;
        string name, version;
        if (_drafts.TryGetValue(packRef.PackDir, out var draft))
        {
            count = PackService.ApplyDraftToEntries(draft, _allEntries);
            name = draft.Name;
            version = draft.Version;
        }
        else
        {
            var packJsonPath = Path.Combine(packRef.PackDir, "pack.json");
            var result = PackService.LoadPack(packJsonPath);
            if (result == null) { StatusText.Text = $"Could not read pack at {packRef.PackDir}"; return; }
            var (manifest, packDir) = result.Value;
            count = PackService.ApplyPackToEntries(manifest, packDir, _allEntries);
            name = manifest.Name;
            version = manifest.Version;
        }

        PackNameBox.Text = name;
        VersionBox.Text = version;
        ActivePackDir = packRef.PackDir;
        ActivePackName = name;
        ActivePackVersion = version;
        PackService.SnapshotToDraft(_drafts, ActivePackDir, ActivePackName, ActivePackVersion, _allEntries);
        DraftService.Save(_drafts);
        RefreshPackList();
        _ = LoadBitmapsAsync();

        var entriesToApply = _allEntries
            .Where(en => en.IncludeInPack && en.HasReplacement)
            .ToList();

        StatusText.Text = $"Activating '{name}': restoring backups...";
        try
        {
            Logger.Info($"Activate pack: '{name}' ({entriesToApply.Count} entries)");
            await Task.Run(() => BundleService.RestoreBackups(_gameDir));
            StatusText.Text = $"Activating '{name}': applying {entriesToApply.Count} texture(s)...";
            await Task.Run(() => BundleService.ApplyReplacements(entriesToApply,
                new Progress<string>(msg => Dispatcher.UIThread.Post(() => StatusText.Text = msg))));
            StatusText.Text = $"Activated '{name}' — restart SAP to see changes.";
            Logger.Info($"Activate pack done: '{name}'");
        }
        catch (Exception ex)
        {
            Logger.Error("Activate pack failed", ex);
            StatusText.Text = $"Activate error: {ex.Message}";
        }
    }

    private void SwapToPack(SavedPackRef packRef)
    {
        // Snapshot the outgoing pack into its draft so unsaved changes survive the swap.
        PackService.SnapshotToDraft(_drafts, ActivePackDir, ActivePackName, ActivePackVersion, _allEntries);
        DraftService.Save(_drafts);

        ClearAllReplacements();

        // Prefer a draft when we have one — it carries any unsaved state. Fall back to disk.
        int count;
        string name, version;
        if (_drafts.TryGetValue(packRef.PackDir, out var draft))
        {
            count = PackService.ApplyDraftToEntries(draft, _allEntries);
            name = draft.Name;
            version = draft.Version;
        }
        else
        {
            var packJsonPath = Path.Combine(packRef.PackDir, "pack.json");
            var result = PackService.LoadPack(packJsonPath);
            if (result == null)
            {
                StatusText.Text = $"Could not read pack at {packRef.PackDir}";
                return;
            }
            var (manifest, packDir) = result.Value;
            count = PackService.ApplyPackToEntries(manifest, packDir, _allEntries);
            name = manifest.Name;
            version = manifest.Version;
        }

        PackNameBox.Text = name;
        VersionBox.Text = version;
        ActivePackDir = packRef.PackDir;
        ActivePackName = name;
        ActivePackVersion = version;
        // Ensure the new active pack has a draft for future Add-to-Pack picker visibility.
        PackService.SnapshotToDraft(_drafts, ActivePackDir, ActivePackName, ActivePackVersion, _allEntries);
        DraftService.Save(_drafts);

        RefreshPackList();
        _ = LoadBitmapsAsync();
        StatusText.Text = $"Swapped to '{name}' — {count} texture(s) loaded.";
    }

    // ── Pack contents ─────────────────────────────────────────────────────────

    private void RefreshPackList()
    {
        _items.Clear();
        foreach (var entry in _allEntries.Where(e => e.IncludeInPack))
            _items.Add(new PackEntryViewModel(entry));
        var n = _items.Count;
        CountText.Text = $"{n} texture{(n == 1 ? "" : "s")} in pack";
        EmptyText.IsVisible = n == 0;
    }

    private async Task LoadBitmapsAsync()
    {
        foreach (var vm in _items.ToList())
        {
            if (vm.Entry.IsAudio) continue;
            if (vm.Entry.ReplacementPath != null)
                vm.Bitmap = await Task.Run(() => BundleService.LoadPngPreview(vm.Entry.ReplacementPath));
        }
    }

    private void ClearAllReplacements()
    {
        foreach (var entry in _allEntries)
        {
            entry.IncludeInPack = false;
            entry.ReplacementPath = null;
        }
    }

    private void OnRemoveEntryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not PackEntryViewModel vm) return;
        vm.Entry.IncludeInPack = false;
        _items.Remove(vm);
        var n = _items.Count;
        CountText.Text = $"{n} texture{(n == 1 ? "" : "s")} in pack";
        EmptyText.IsVisible = n == 0;
        PackService.SnapshotToDraft(_drafts, ActivePackDir, ActivePackName, ActivePackVersion, _allEntries);
        DraftService.Save(_drafts);
    }

    private void OnAddAllStagedClick(object? sender, RoutedEventArgs e)
    {
        var added = 0;
        foreach (var entry in _allEntries.Where(x => x.HasReplacement && !x.IncludeInPack))
        {
            entry.IncludeInPack = true;
            added++;
        }
        RefreshPackList();
        if (added > 0)
        {
            _ = LoadBitmapsAsync();
            // Sync the bulk additions into the active pack's draft.
            PackService.SnapshotToDraft(_drafts, ActivePackDir, ActivePackName, ActivePackVersion, _allEntries);
            DraftService.Save(_drafts);
        }
        StatusText.Text = added > 0 ? $"Added {added} staged texture(s) to pack." : "No new staged textures to add.";
    }

    // ── Save / Load ───────────────────────────────────────────────────────────

    private async void OnSavePackClick(object? sender, RoutedEventArgs e)
    {
        var packItems = _allEntries.Where(x => x.IncludeInPack && x.HasReplacement).ToList();
        if (packItems.Count == 0) { StatusText.Text = "No textures in pack to save."; return; }

        var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose output folder for pack"
        });
        if (folder.Count == 0) return;

        var name = PackNameBox.Text?.Trim() is { Length: > 0 } n ? n : "MySapPack";
        var version = VersionBox.Text?.Trim() is { Length: > 0 } v ? v : "1.0";
        var outDir = Path.Combine(folder[0].Path.LocalPath, name);
        Directory.CreateDirectory(outDir);
        PackService.SavePack(name, version, packItems, outDir);

        var packRef = UpsertInLibrary(name, version, outDir);
        ActivePackDir = outDir;
        ActivePackName = name;
        ActivePackVersion = version;

        // The pack is now on disk; sync the draft so its in-memory state matches.
        PackService.SnapshotToDraft(_drafts, ActivePackDir, ActivePackName, ActivePackVersion, _allEntries);
        DraftService.Save(_drafts);

        _blockSwap = true;
        LibraryList.SelectedItem = packRef;
        _blockSwap = false;

        StatusText.Text = $"Saved {packItems.Count} texture(s) to {outDir}";
    }

    private async void OnLoadPackClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open pack.json",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Pack manifest") { Patterns = new[] { "pack.json" } } },
        });
        if (files.Count == 0) return;

        var result = PackService.LoadPack(files[0].Path.LocalPath);
        if (result == null) { StatusText.Text = "Failed to read pack.json."; return; }

        var (manifest, packDir) = result.Value;

        // Preserve unsaved active-pack state.
        PackService.SnapshotToDraft(_drafts, ActivePackDir, ActivePackName, ActivePackVersion, _allEntries);
        DraftService.Save(_drafts);

        ClearAllReplacements();

        int count;
        string name, version;
        if (_drafts.TryGetValue(packDir, out var draft))
        {
            count = PackService.ApplyDraftToEntries(draft, _allEntries);
            name = draft.Name;
            version = draft.Version;
        }
        else
        {
            count = PackService.ApplyPackToEntries(manifest, packDir, _allEntries);
            name = manifest.Name;
            version = manifest.Version;
        }

        PackNameBox.Text = name;
        VersionBox.Text = version;
        ActivePackDir = packDir;
        ActivePackName = name;
        ActivePackVersion = version;
        PackService.SnapshotToDraft(_drafts, ActivePackDir, ActivePackName, ActivePackVersion, _allEntries);
        DraftService.Save(_drafts);

        RefreshPackList();
        await LoadBitmapsAsync();

        var packRef = UpsertInLibrary(name, version, packDir);
        _blockSwap = true;
        LibraryList.SelectedItem = packRef;
        _blockSwap = false;

        StatusText.Text = $"Loaded '{name}' — {count} texture(s) staged.";
    }

    // Removes any existing library entry for packDir, adds a fresh one, persists, returns it.
    private SavedPackRef UpsertInLibrary(string name, string version, string packDir)
    {
        var existing = _library.FirstOrDefault(p => p.PackDir == packDir);
        if (existing != null) _library.Remove(existing);

        var packRef = new SavedPackRef { Name = name, Version = version, PackDir = packDir };
        _library.Add(packRef);
        PackService.SavePackLibrary(_library.ToList());
        LibraryEmptyHint.IsVisible = false;
        return packRef;
    }
}

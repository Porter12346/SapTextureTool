using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SapTextureTool.Models;
using SapTextureTool.Services;
using System.Collections.ObjectModel;
using System.IO;

namespace SapTextureTool;

public partial class PackManagerWindow : Window
{
    private readonly IList<TextureEntry> _allEntries;
    private readonly ObservableCollection<PackEntryViewModel> _items = new();
    private readonly ObservableCollection<SavedPackRef> _library = new();
    private bool _blockSwap;

    // Read by MainWindow after ShowDialog to sync active pack state.
    public string? ActivePackDir { get; private set; }
    public string? ActivePackName { get; private set; }

    public PackManagerWindow(IList<TextureEntry> allEntries, string? activePackDir = null)
    {
        InitializeComponent();
        _allEntries = allEntries;
        ActivePackDir = activePackDir;

        EntriesList.ItemsSource = _items;
        LibraryList.ItemsSource = _library;

        PackNameBox.Text = "MySapPack";
        VersionBox.Text = "1.0";

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

        if (ActivePackDir == packRef.PackDir)
        {
            ActivePackDir = null;
            ActivePackName = null;
            LibraryList.SelectedItem = null;
        }
        else
        {
            LibraryList.SelectedItem = _library.FirstOrDefault(p => p.PackDir == ActivePackDir);
        }
        _blockSwap = false;

        StatusText.Text = $"Removed '{packRef.Name}' from library.";
    }

    private void SwapToPack(SavedPackRef packRef)
    {
        var packJsonPath = Path.Combine(packRef.PackDir, "pack.json");
        var result = PackService.LoadPack(packJsonPath);
        if (result == null)
        {
            StatusText.Text = $"Could not read pack at {packRef.PackDir}";
            return;
        }

        ClearAllReplacements();

        var (manifest, packDir) = result.Value;
        PackNameBox.Text = manifest.Name;
        VersionBox.Text = manifest.Version;
        ActivePackDir = packRef.PackDir;
        ActivePackName = manifest.Name;

        var count = PackService.ApplyPackToEntries(manifest, packDir, _allEntries);
        RefreshPackList();
        _ = LoadBitmapsAsync();
        StatusText.Text = $"Swapped to '{manifest.Name}' — {count} texture(s) loaded.";
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
        if (added > 0) _ = LoadBitmapsAsync();
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

        ClearAllReplacements();
        PackNameBox.Text = manifest.Name;
        VersionBox.Text = manifest.Version;
        ActivePackDir = packDir;
        ActivePackName = manifest.Name;

        var count = PackService.ApplyPackToEntries(manifest, packDir, _allEntries);
        RefreshPackList();
        await LoadBitmapsAsync();

        var packRef = UpsertInLibrary(manifest.Name, manifest.Version, packDir);
        _blockSwap = true;
        LibraryList.SelectedItem = packRef;
        _blockSwap = false;

        StatusText.Text = $"Loaded '{manifest.Name}' — {count} texture(s) staged.";
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

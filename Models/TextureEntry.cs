using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SapTextureTool.Models;

public class TextureEntry : INotifyPropertyChanged
{
    public AssetKind Kind { get; init; } = AssetKind.Texture;
    public string Name { get; init; } = "";
    // For bundles: path to the .bundle file. For .assets files: path to the .assets file.
    public string BundlePath { get; init; } = "";
    // Only used for bundles — the internal CAB entry name. Empty for .assets files.
    public string BundleEntryName { get; init; } = "";
    public bool IsBundle { get; init; } = true;
    public long PathId { get; init; }

    // Texture-specific
    public int Width { get; set; }
    public int Height { get; set; }
    public int TextureFormat { get; set; }

    // Audio-specific
    public int AudioChannels { get; init; }
    public int AudioFrequency { get; init; }
    public float AudioLength { get; init; }
    public int AudioCompressionFormat { get; init; }

    public bool IsAudio => Kind == AssetKind.Audio;
    public string BundleFileName => System.IO.Path.GetFileName(BundlePath);

    private string? _replacementPath;
    public string? ReplacementPath
    {
        get => _replacementPath;
        set
        {
            _replacementPath = value;
            if (value == null) _includeInPack = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasReplacement));
            OnPropertyChanged(nameof(HasReplacementOnly));
            OnPropertyChanged(nameof(IncludeInPack));
        }
    }

    private bool _includeInPack;
    public bool IncludeInPack
    {
        get => _includeInPack;
        set
        {
            _includeInPack = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasReplacementOnly));
        }
    }

    public bool HasReplacement => _replacementPath != null;
    public bool HasReplacementOnly => HasReplacement && !_includeInPack;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

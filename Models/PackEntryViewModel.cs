using Avalonia.Media.Imaging;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SapTextureTool.Models;

public class PackEntryViewModel : INotifyPropertyChanged
{
    public TextureEntry Entry { get; }

    private Bitmap? _bitmap;
    public Bitmap? Bitmap
    {
        get => _bitmap;
        set { _bitmap = value; OnPropertyChanged(); }
    }

    public PackEntryViewModel(TextureEntry entry) => Entry = entry;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

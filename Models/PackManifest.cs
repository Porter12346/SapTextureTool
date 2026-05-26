namespace SapTextureTool.Models;

public class PackManifest
{
    public string Name { get; set; } = "My Pack";
    public string Version { get; set; } = "1.0";
    // Maps sprite name → relative PNG path within the pack folder
    public Dictionary<string, string> Assets { get; set; } = new();
}

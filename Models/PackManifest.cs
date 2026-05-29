namespace SapTextureTool.Models;

public class PackAssetEntry
{
    public string Png { get; set; } = "";
    // "<bundleFileName>#<pathId>" — disambiguates same-Name entries from different bundles.
    public string? SourceTag { get; set; }
    public BorderConfig? Border { get; set; }
}

public class PackManifest
{
    public string Name { get; set; } = "My Pack";
    public string Version { get; set; } = "1.0";
    public int SchemaVersion { get; set; } = 1;

    // v1 (legacy). Always written so older tool versions still partially load.
    public Dictionary<string, string> Assets { get; set; } = new();

    // v2 (preferred when present). Carries source disambiguation + border config.
    public Dictionary<string, PackAssetEntry>? AssetsV2 { get; set; }
}

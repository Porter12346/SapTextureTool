using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Avalonia.Media.Imaging;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using NAudio.Vorbis;
using NAudio.Wave;
using SapTextureTool.Models;
using SkiaSharp;
using System.IO;
using System.Runtime.InteropServices;

namespace SapTextureTool.Services;

public static class BundleService
{
    public static string GetDefaultGameDir()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library/Application Support/Steam/steamapps/common/Super Auto Pets");
        return @"C:\Program Files (x86)\Steam\steamapps\common\Super Auto Pets";
    }

    public static string GetBundleDir(string gameDir)
    {
        var candidates = new[]
        {
            // Windows — game dir is the Steam game folder
            Path.Combine(gameDir, "Super Auto Pets_Data", "StreamingAssets", "aa", "StandaloneWindows64"),
            // Mac — game dir is the Steam game folder (contains Super Auto Pets.app)
            Path.Combine(gameDir, "Super Auto Pets.app", "Contents", "Resources", "Data", "StreamingAssets", "aa", "StandaloneOSX"),
            Path.Combine(gameDir, "Super Auto Pets.app", "Contents", "Resources", "Data", "StreamingAssets", "aa", "StandaloneOSX64"),
            // Mac — game dir is the .app bundle itself
            Path.Combine(gameDir, "Contents", "Resources", "Data", "StreamingAssets", "aa", "StandaloneOSX"),
            Path.Combine(gameDir, "Contents", "Resources", "Data", "StreamingAssets", "aa", "StandaloneOSX64"),
            // Mac — game dir is the Data folder itself
            Path.Combine(gameDir, "StreamingAssets", "aa", "StandaloneOSX"),
            Path.Combine(gameDir, "StreamingAssets", "aa", "StandaloneOSX64"),
            // Windows fallbacks
            Path.Combine(gameDir, "Contents", "Resources", "Data", "StreamingAssets", "aa", "StandaloneWindows64"),
            Path.Combine(gameDir, "StandaloneWindows64"),
            gameDir,
        };
        foreach (var c in candidates)
            if (Directory.Exists(c) && Directory.GetFiles(c, "*.bundle").Length > 0)
                return c;

        // Last resort: find any child of a StreamingAssets/aa/ folder that has .bundle files
        var aaDir = FindDescendantDirectory(gameDir, "aa", maxDepth: 7);
        if (aaDir != null)
        {
            try
            {
                foreach (var sub in Directory.GetDirectories(aaDir))
                    if (Directory.GetFiles(sub, "*.bundle").Length > 0)
                        return sub;
            }
            catch { }
        }

        return candidates[0];
    }

    // Returns the directory that contains sharedassets*.assets files.
    public static string GetDataDir(string gameDir)
    {
        var candidates = new[]
        {
            // Windows
            Path.Combine(gameDir, "Super Auto Pets_Data"),
            // Mac — game dir is the Steam game folder
            Path.Combine(gameDir, "Super Auto Pets.app", "Contents", "Resources", "Data"),
            // Mac — game dir is the .app
            Path.Combine(gameDir, "Contents", "Resources", "Data"),
            // game dir IS the Data folder
            gameDir,
        };
        foreach (var c in candidates)
            if (Directory.Exists(c) && Directory.GetFiles(c, "*.assets").Length > 0)
                return c;
        return candidates[0];
    }

    private static string? FindDescendantDirectory(string root, string name, int maxDepth)
    {
        if (maxDepth <= 0) return null;
        try
        {
            foreach (var dir in Directory.GetDirectories(root))
            {
                if (Path.GetFileName(dir).Equals(name, StringComparison.OrdinalIgnoreCase))
                    return dir;
                var found = FindDescendantDirectory(dir, name, maxDepth - 1);
                if (found != null) return found;
            }
        }
        catch { }
        return null;
    }

    // ── AssetsManager factory ─────────────────────────────────────────────────

    // Loads classdata.tpk so GetBaseField works on .assets files that don't embed type trees.
    private static AssetsManager CreateAssetsManager()
    {
        var am = new AssetsManager();
        var tpkPath = Path.Combine(AppContext.BaseDirectory, "classdata.tpk");
        if (File.Exists(tpkPath))
            am.LoadClassPackage(tpkPath);
        return am;
    }

    // ── Scan ─────────────────────────────────────────────────────────────────

    public static List<TextureEntry> ScanBundles(string gameDir, IProgress<string>? progress = null)
    {
        var results = new List<TextureEntry>();
        var am = CreateAssetsManager();

        // 1. Scan Addressable .bundle files
        var bundleDir = GetBundleDir(gameDir);
        if (Directory.Exists(bundleDir))
        {
            foreach (var bundlePath in Directory.GetFiles(bundleDir, "*.bundle"))
            {
                progress?.Report($"Scanning {Path.GetFileName(bundlePath)}...");
                BundleFileInstance? bun = null;
                try
                {
                    bun = am.LoadBundleFile(bundlePath, true);
                    var fileNames = bun.file.GetAllFileNames();
                    for (var fi = 0; fi < fileNames.Count; fi++)
                    {
                        if (!bun.file.IsAssetsFile(fi)) continue;
                        AssetsFileInstance? afile = null;
                        try
                        {
                            afile = am.LoadAssetsFileFromBundle(bun, fi, false);
                            ScanAssetsFile(am, afile, bundlePath, afile.name, isBundle: true, results);
                        }
                        catch { }
                        finally { if (afile != null) am.UnloadAssetsFile(afile); }
                    }
                }
                catch { }
                finally { if (bun != null) am.UnloadBundleFile(bun); }
            }
        }

        // 2. Scan standalone .assets files (sharedassets0.assets, sharedassets1.assets, etc.)
        var dataDir = GetDataDir(gameDir);
        if (Directory.Exists(dataDir))
        {
            foreach (var assetsPath in Directory.GetFiles(dataDir, "*.assets"))
            {
                progress?.Report($"Scanning {Path.GetFileName(assetsPath)}...");
                AssetsFileInstance? afile = null;
                try
                {
                    afile = am.LoadAssetsFile(assetsPath, false);
                    am.LoadClassDatabaseFromPackage(afile.file.Metadata.UnityVersion);
                    ScanAssetsFile(am, afile, assetsPath, "", isBundle: false, results);
                }
                catch { }
                finally { if (afile != null) am.UnloadAssetsFile(afile); }
            }
        }

        am.UnloadAll();
        return results;
    }

    private static void ScanAssetsFile(AssetsManager am, AssetsFileInstance afile,
        string sourcePath, string bundleEntryName, bool isBundle, List<TextureEntry> results)
    {
        foreach (var info in afile.file.GetAssetsOfType(AssetClassID.Texture2D))
        {
            try
            {
                var bf = am.GetBaseField(afile, info);
                results.Add(new TextureEntry
                {
                    Kind = AssetKind.Texture,
                    Name = bf["m_Name"].AsString,
                    BundlePath = sourcePath,
                    BundleEntryName = bundleEntryName,
                    IsBundle = isBundle,
                    PathId = info.PathId,
                    Width = bf["m_Width"].AsInt,
                    Height = bf["m_Height"].AsInt,
                    TextureFormat = bf["m_TextureFormat"].AsInt,
                });
            }
            catch { }
        }

        foreach (var info in afile.file.GetAssetsOfType(AssetClassID.AudioClip))
        {
            try
            {
                var bf = am.GetBaseField(afile, info);

                // Skip stubs: clips with no inline audio data and no streaming resource.
                // These are metadata-only entries (e.g. in sharedassets*.assets) that the
                // game loads from elsewhere; we can't read or write them here.
                var adField = bf["m_AudioData"];
                var hasInlineData = adField?.IsDummy == false && adField.AsByteArray?.Length > 0;
                var resField = bf["m_Resource"];
                var hasStreamedData = resField?.IsDummy == false && resField["m_Size"].AsULong > 0;
                if (!hasInlineData && !hasStreamedData) continue;

                var bpsField = bf["m_BitsPerSample"];
                results.Add(new TextureEntry
                {
                    Kind = AssetKind.Audio,
                    Name = bf["m_Name"].AsString,
                    BundlePath = sourcePath,
                    BundleEntryName = bundleEntryName,
                    IsBundle = isBundle,
                    PathId = info.PathId,
                    AudioChannels = bf["m_Channels"].AsInt,
                    AudioFrequency = bf["m_Frequency"].AsInt,
                    AudioLength = bf["m_Length"].AsFloat,
                    AudioCompressionFormat = bf["m_CompressionFormat"].AsInt,
                    AudioBitsPerSample = (bpsField?.IsDummy == false && bpsField.AsInt > 0) ? bpsField.AsInt : 16,
                });
            }
            catch { }
        }
    }

    // ── Preview extraction ────────────────────────────────────────────────────

    public static (Bitmap? bitmap, string? error) ExtractPreview(TextureEntry entry)
    {
        var am = CreateAssetsManager();
        try
        {
            if (entry.IsBundle)
                return ExtractPreviewFromBundle(am, entry);
            else
                return ExtractPreviewFromAssetsFile(am, entry);
        }
        catch (Exception ex) { return (null, ex.Message); }
        finally { am.UnloadAll(); }
    }

    private static (Bitmap?, string?) ExtractPreviewFromBundle(AssetsManager am, TextureEntry entry)
    {
        var bun = am.LoadBundleFile(entry.BundlePath, true);
        var afile = am.LoadAssetsFileFromBundle(bun, entry.BundleEntryName, false);
        var info = afile.file.GetAssetInfo(entry.PathId);
        if (info == null) return (null, "Asset not found in bundle");

        var bf = am.GetBaseField(afile, info);
        var (raw, err) = GetTextureBytesFromBundle(bf, bun);
        return DecodeAndFlip(raw, err, entry);
    }

    private static (Bitmap?, string?) ExtractPreviewFromAssetsFile(AssetsManager am, TextureEntry entry)
    {
        var afile = am.LoadAssetsFile(entry.BundlePath, false);
        am.LoadClassDatabaseFromPackage(afile.file.Metadata.UnityVersion);
        var info = afile.file.GetAssetInfo(entry.PathId);
        if (info == null) return (null, "Asset not found in .assets file");

        var bf = am.GetBaseField(afile, info);
        var (raw, err) = GetTextureBytesFromAssetsFile(bf, entry.BundlePath);
        return DecodeAndFlip(raw, err, entry);
    }

    private static (Bitmap?, string?) DecodeAndFlip(byte[]? raw, string? err, TextureEntry entry)
    {
        if (raw == null || raw.Length == 0) return (null, err ?? "No texture data");
        var rgba = DecodeToRgba32(raw, entry.TextureFormat, entry.Width, entry.Height);
        if (rgba == null) return (null, $"Format {entry.TextureFormat} not supported for preview");
        FlipVertical(rgba, entry.Width, entry.Height);
        return (RgbaToAvaloniaBitmap(rgba, entry.Width, entry.Height), null);
    }

    // ── Audio extraction for playback ─────────────────────────────────────────

    public static (byte[]? data, string? error) ExtractAudioBytes(TextureEntry entry)
    {
        var am = CreateAssetsManager();
        try
        {
            if (entry.IsBundle)
            {
                var bun = am.LoadBundleFile(entry.BundlePath, true);
                var afile = am.LoadAssetsFileFromBundle(bun, entry.BundleEntryName, false);
                var info = afile.file.GetAssetInfo(entry.PathId);
                if (info == null) return (null, "Asset not found");
                var bf = am.GetBaseField(afile, info);
                return GetAudioBytesFromBundle(bf, bun);
            }
            else
            {
                var afile = am.LoadAssetsFile(entry.BundlePath, false);
                am.LoadClassDatabaseFromPackage(afile.file.Metadata.UnityVersion);
                var info = afile.file.GetAssetInfo(entry.PathId);
                if (info == null) return (null, "Asset not found");
                var bf = am.GetBaseField(afile, info);
                return GetAudioBytesFromAssetsFile(bf, entry.BundlePath);
            }
        }
        catch (Exception ex) { return (null, ex.Message); }
        finally { am.UnloadAll(); }
    }

    private static (byte[]? data, string? error) GetAudioBytesFromBundle(
        AssetTypeValueField bf, BundleFileInstance bun)
    {
        var audioData = bf["m_AudioData"];
        if (audioData?.IsDummy == false)
        {
            var bytes = audioData.AsByteArray;
            if (bytes != null && bytes.Length > 0) return (bytes, null);
        }

        var res = bf["m_Resource"];
        if (res == null || res.IsDummy) return (null, "No audio data");

        var src = res["m_Source"].AsString;
        var offset = (long)res["m_Offset"].AsULong;
        var size = (long)res["m_Size"].AsULong;

        if (size == 0) return (null, "Empty resource");

        string entryName;
        if (string.IsNullOrEmpty(src))
        {
            // Streaming clip with blank m_Source — scan for any .resS entry in the bundle
            var allDirNames = bun.file.BlockAndDirInfo.DirectoryInfos.Select(d => d.Name).ToArray();
            var found = allDirNames.FirstOrDefault(n => n.EndsWith(".resS", StringComparison.OrdinalIgnoreCase));
            if (found == null) return (null, "No .resS entry found in bundle");
            entryName = found;
        }
        else
        {
            entryName = src.Contains('/') ? src[(src.LastIndexOf('/') + 1)..] : src;
        }

        var resSIdx = bun.file.GetFileIndex(entryName);
        if (resSIdx < 0) return (null, $"Bundle entry '{entryName}' not found");

        bun.file.GetFileRange(resSIdx, out var bundleOffset, out _);
        var reader = bun.file.DataReader;
        var savedPos = reader.Position;
        try
        {
            reader.Position = bundleOffset + offset;
            return (reader.ReadBytes((int)size), null);
        }
        finally { reader.Position = savedPos; }
    }

    private static (byte[]? data, string? error) GetAudioBytesFromAssetsFile(
        AssetTypeValueField bf, string assetsPath)
    {
        var audioData = bf["m_AudioData"];
        if (audioData?.IsDummy == false)
        {
            var bytes = audioData.AsByteArray;
            if (bytes != null && bytes.Length > 0) return (bytes, null);
        }

        var res = bf["m_Resource"];
        if (res == null || res.IsDummy) return (null, "No audio data");

        var src = res["m_Source"].AsString;
        var offset = (long)res["m_Offset"].AsULong;
        var size = (long)res["m_Size"].AsULong;

        if (size == 0) return (null, "Empty resource");

        var dir = Path.GetDirectoryName(assetsPath) ?? "";
        string resSName;
        if (string.IsNullOrEmpty(src))
        {
            // Blank m_Source — look for a sibling .resS file next to the .assets file
            var sibling = Directory.EnumerateFiles(dir, "*.resS").FirstOrDefault();
            if (sibling == null) return (null, "Streaming audio: no .resS file found alongside assets file");
            resSName = Path.GetFileName(sibling);
        }
        else
        {
            resSName = src.Contains('/') ? src[(src.LastIndexOf('/') + 1)..] : src;
        }
        var resSPath = Path.Combine(dir, resSName);
        if (!File.Exists(resSPath)) return (null, $"Resource file not found: {resSName}");

        using var fs = File.OpenRead(resSPath);
        fs.Seek(offset, SeekOrigin.Begin);
        var bytes2 = new byte[size];
        fs.ReadExactly(bytes2);
        return (bytes2, null);
    }

    public static Bitmap? LoadPngPreview(string pngPath)
    {
        try { return new Bitmap(pngPath); }
        catch { return null; }
    }

    // ── Apply replacements ────────────────────────────────────────────────────

    public static void ApplyReplacements(IEnumerable<TextureEntry> entries, IProgress<string>? progress = null)
    {
        foreach (var group in entries.Where(e => e.HasReplacement).GroupBy(e => e.BundlePath))
        {
            progress?.Report($"Patching {Path.GetFileName(group.Key)}...");
            var bakPath = group.Key + ".bak";
            if (!File.Exists(bakPath))
                File.Copy(group.Key, bakPath);

            var list = group.ToList();
            if (list[0].IsBundle)
                PatchBundle(group.Key, list, progress);
            else
                PatchAssetsFile(group.Key, list, progress);
        }
    }

    private static void PatchBundle(string bundlePath, List<TextureEntry> entries, IProgress<string>? progress)
    {
        var am = CreateAssetsManager();
        var tempPath = bundlePath + ".tmp";
        try
        {
            var bun = am.LoadBundleFile(bundlePath, true);
            foreach (var entryGroup in entries.GroupBy(e => e.BundleEntryName))
            {
                var afile = am.LoadAssetsFileFromBundle(bun, entryGroup.Key, false);
                var explicitNames = new HashSet<string>(entryGroup.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var entry in entryGroup)
                {
                    if (entry.ReplacementPath == null) continue;
                    progress?.Report($"  Replacing {entry.Name}...");
                    var info = afile.file.GetAssetInfo(entry.PathId);
                    if (info == null) continue;
                    var bf = am.GetBaseField(afile, info);
                    var fileBytes = File.ReadAllBytes(entry.ReplacementPath);
                    if (entry.Kind == AssetKind.Audio)
                    {
                        var audioData = bf["m_AudioData"];
                        bool ok = audioData?.IsDummy == false
                            ? ApplyAudioToField(bf, fileBytes, Path.GetExtension(entry.ReplacementPath))
                            : ApplyStreamingAudioToBundle(bf, bun, fileBytes, Path.GetExtension(entry.ReplacementPath));
                        if (!ok) continue;
                        info.SetNewData(bf);
                    }
                    else
                    {
                        var (ok, newW, newH) = ApplyPngToField(bf, fileBytes);
                        if (!ok) continue;
                        entry.Width = newW; entry.Height = newH; entry.TextureFormat = 4;
                        info.SetNewData(bf);
                        UpdateSpritesForTexture(am, afile, entry.PathId, newW, newH);
                        if (!entry.IsAutoX2)
                            AutoApplyX2(am, afile, entry.Name, fileBytes, explicitNames, progress);
                    }
                }
                var dirInfos = bun.file.BlockAndDirInfo.DirectoryInfos;
                for (var i = 0; i < dirInfos.Count; i++)
                    if (dirInfos[i].Name == afile.name) { dirInfos[i].SetNewData(afile.file); break; }
            }
            using (var outFs = File.Open(tempPath, FileMode.Create, FileAccess.Write))
                bun.file.Write(new AssetsFileWriter(outFs), 0);
        }
        finally { am.UnloadAll(); }
        File.Move(tempPath, bundlePath, overwrite: true);
        PatchCatalogCrc(bundlePath);
    }

    private static void PatchAssetsFile(string assetsPath, List<TextureEntry> entries, IProgress<string>? progress)
    {
        var am = CreateAssetsManager();
        var tempPath = assetsPath + ".tmp";
        try
        {
            var afile = am.LoadAssetsFile(assetsPath, false);
            am.LoadClassDatabaseFromPackage(afile.file.Metadata.UnityVersion);
            var explicitNames = new HashSet<string>(entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (entry.ReplacementPath == null) continue;
                progress?.Report($"  Replacing {entry.Name}...");
                var info = afile.file.GetAssetInfo(entry.PathId);
                if (info == null) continue;
                var bf = am.GetBaseField(afile, info);
                var fileBytes = File.ReadAllBytes(entry.ReplacementPath);
                if (entry.Kind == AssetKind.Audio)
                {
                    var audioData = bf["m_AudioData"];
                    bool ok = audioData?.IsDummy == false
                        ? ApplyAudioToField(bf, fileBytes, Path.GetExtension(entry.ReplacementPath))
                        : ApplyStreamingAudioToResS(bf, fileBytes, Path.GetExtension(entry.ReplacementPath), assetsPath);
                    if (!ok) continue;
                    info.SetNewData(bf);
                }
                else
                {
                    var (ok, newW, newH) = ApplyPngToField(bf, fileBytes);
                    if (!ok) continue;
                    entry.Width = newW; entry.Height = newH; entry.TextureFormat = 4;
                    info.SetNewData(bf);
                    UpdateSpritesForTexture(am, afile, entry.PathId, newW, newH);
                    if (!entry.IsAutoX2)
                        AutoApplyX2(am, afile, entry.Name, fileBytes, explicitNames, progress);
                }
            }
            using (var outFs = File.Open(tempPath, FileMode.Create, FileAccess.Write))
                afile.file.Write(new AssetsFileWriter(outFs), 0);
        }
        finally { am.UnloadAll(); }
        File.Move(tempPath, assetsPath, overwrite: true);
    }

    // Finds the bundle's CRC field in catalog.bin (the aa/ sibling directory of the
    // bundle's platform folder) and zeros it out so Unity skips integrity checking.
    // Unity treats CRC=0 as "no check". The backup file (.bak) must exist already
    // because it provides the original file size used to locate the catalog entry.
    private static void PatchCatalogCrc(string bundlePath)
    {
        // catalog.bin lives two levels up: .../aa/<Platform>/<bundle> → .../aa/catalog.bin
        var platformDir = Path.GetDirectoryName(bundlePath);
        if (platformDir == null) return;
        var aaDir = Path.GetDirectoryName(platformDir);
        if (aaDir == null) return;
        var catalogPath = Path.Combine(aaDir, "catalog.bin");
        if (!File.Exists(catalogPath)) return;

        var bakPath = bundlePath + ".bak";
        if (!File.Exists(bakPath)) return;

        var originalSize = (uint)new FileInfo(bakPath).Length;
        if (originalSize == 0) return;

        var catalog = File.ReadAllBytes(catalogPath);
        byte b0 = (byte)(originalSize & 0xFF);
        byte b1 = (byte)((originalSize >> 8) & 0xFF);
        byte b2 = (byte)((originalSize >> 16) & 0xFF);
        byte b3 = (byte)((originalSize >> 24) & 0xFF);

        bool patched = false;
        for (int i = 4; i < catalog.Length - 8; i++)
        {
            if (catalog[i] != b0 || catalog[i+1] != b1 || catalog[i+2] != b2 || catalog[i+3] != b3)
                continue;
            // The 4 bytes after the size should be a plausible catalog offset
            uint nextVal = catalog[i+4] | ((uint)catalog[i+5] << 8) | ((uint)catalog[i+6] << 16) | ((uint)catalog[i+7] << 24);
            if (nextVal >= (uint)catalog.Length) continue;
            // Zero out the 4 bytes before the size — that's the CRC field
            catalog[i-4] = 0; catalog[i-3] = 0; catalog[i-2] = 0; catalog[i-1] = 0;
            patched = true;
        }

        if (!patched) return;

        var catalogBak = catalogPath + ".bak";
        if (!File.Exists(catalogBak))
            File.Copy(catalogPath, catalogBak);

        File.WriteAllBytes(catalogPath, catalog);
    }

    // ── Restore backups ───────────────────────────────────────────────────────

    public static void RestoreBackups(string gameDir)
    {
        // Restore bundle backups
        var bundleDir = GetBundleDir(gameDir);
        if (Directory.Exists(bundleDir))
            foreach (var bak in Directory.GetFiles(bundleDir, "*.bundle.bak"))
            { File.Copy(bak, bak[..^4], overwrite: true); File.Delete(bak); }

        // Restore .assets and .resS backups
        var dataDir = GetDataDir(gameDir);
        if (Directory.Exists(dataDir))
        {
            foreach (var bak in Directory.GetFiles(dataDir, "*.assets.bak"))
                { File.Copy(bak, bak[..^4], overwrite: true); File.Delete(bak); }
            foreach (var bak in Directory.GetFiles(dataDir, "*.resS.bak"))
                { File.Copy(bak, bak[..^4], overwrite: true); File.Delete(bak); }
        }

        // Restore catalog.bin backup (catalog CRC zeroing is undone here)
        if (Directory.Exists(bundleDir))
        {
            var aaDir = Path.GetDirectoryName(bundleDir);
            if (aaDir != null)
            {
                var catalogBak = Path.Combine(aaDir, "catalog.bin.bak");
                if (File.Exists(catalogBak))
                {
                    File.Copy(catalogBak, Path.Combine(aaDir, "catalog.bin"), overwrite: true);
                    File.Delete(catalogBak);
                }
            }
        }
    }

    // ── Texture data helpers ──────────────────────────────────────────────────

    // For textures inside a .bundle — .resS is an internal bundle entry.
    private static (byte[]? data, string? error) GetTextureBytesFromBundle(
        AssetTypeValueField bf, BundleFileInstance bun)
    {
        var inline = bf["image data"].AsByteArray;
        if (inline != null && inline.Length > 0) return (inline, null);

        var sd = bf["m_StreamData"];
        if (sd == null || sd.IsDummy) return (null, "No stream data");

        var sdPath = sd["path"].AsString;
        var sdOffset = (long)sd["offset"].AsULong;
        var sdSize = (long)sd["size"].AsULong;
        if (string.IsNullOrEmpty(sdPath) || sdSize == 0) return (null, "Empty stream data");

        var entryName = sdPath.Contains('/') ? sdPath[(sdPath.LastIndexOf('/') + 1)..] : sdPath;
        var resSIdx = bun.file.GetFileIndex(entryName);
        if (resSIdx < 0) return (null, $"Bundle entry '{entryName}' not found");

        bun.file.GetFileRange(resSIdx, out var bundleOffset, out _);
        var reader = bun.file.DataReader;
        var savedPos = reader.Position;
        try
        {
            reader.Position = bundleOffset + sdOffset;
            return (reader.ReadBytes((int)sdSize), null);
        }
        finally { reader.Position = savedPos; }
    }

    // For standalone .assets files — .resS is a separate file on disk.
    private static (byte[]? data, string? error) GetTextureBytesFromAssetsFile(
        AssetTypeValueField bf, string assetsPath)
    {
        var inline = bf["image data"].AsByteArray;
        if (inline != null && inline.Length > 0) return (inline, null);

        var sd = bf["m_StreamData"];
        if (sd == null || sd.IsDummy) return (null, "No stream data");

        var sdPath = sd["path"].AsString;
        var sdOffset = (long)sd["offset"].AsULong;
        var sdSize = (long)sd["size"].AsULong;
        if (string.IsNullOrEmpty(sdPath) || sdSize == 0) return (null, "Empty stream data");

        // sdPath is relative to the .assets file, e.g. "sharedassets1.assets.resS"
        var dir = Path.GetDirectoryName(assetsPath) ?? "";
        var resSName = sdPath.Contains('/') ? sdPath[(sdPath.LastIndexOf('/') + 1)..] : sdPath;
        var resSPath = Path.Combine(dir, resSName);
        if (!File.Exists(resSPath)) return (null, $".resS not found: {resSName}");

        using var fs = File.OpenRead(resSPath);
        fs.Seek(sdOffset, SeekOrigin.Begin);
        var bytes = new byte[sdSize];
        fs.ReadExactly(bytes);
        return (bytes, null);
    }

    private static byte[]? DecodeToRgba32(byte[] raw, int format, int w, int h)
    {
        return format switch
        {
            1  => Alpha8ToRgba(raw),
            3  => Rgb24ToRgba(raw),
            4  => (byte[])raw.Clone(),
            5  => Argb32ToRgba(raw),
            14 => BgraToRgba(raw),
            10 => DecodeBc(raw, w, h, CompressionFormat.Bc1), // DXT1
            12 => DecodeBc(raw, w, h, CompressionFormat.Bc3), // DXT5
            26 => DecodeBc(raw, w, h, CompressionFormat.Bc7), // BC7
            _  => null,
        };
    }

    private static byte[] Alpha8ToRgba(byte[] raw)
    {
        var out_ = new byte[raw.Length * 4];
        for (var i = 0; i < raw.Length; i++) out_[i * 4 + 3] = raw[i];
        return out_;
    }

    private static byte[] Rgb24ToRgba(byte[] raw)
    {
        var n = raw.Length / 3;
        var out_ = new byte[n * 4];
        for (var i = 0; i < n; i++)
        { out_[i*4] = raw[i*3]; out_[i*4+1] = raw[i*3+1]; out_[i*4+2] = raw[i*3+2]; out_[i*4+3] = 255; }
        return out_;
    }

    private static byte[] Argb32ToRgba(byte[] raw)
    {
        var out_ = (byte[])raw.Clone();
        for (var i = 0; i < out_.Length; i += 4)
        { var a = out_[i]; out_[i] = out_[i+1]; out_[i+1] = out_[i+2]; out_[i+2] = out_[i+3]; out_[i+3] = a; }
        return out_;
    }

    private static byte[] BgraToRgba(byte[] raw)
    {
        var out_ = (byte[])raw.Clone();
        for (var i = 0; i < out_.Length; i += 4) (out_[i], out_[i+2]) = (out_[i+2], out_[i]);
        return out_;
    }

    private static byte[]? DecodeBc(byte[] raw, int w, int h, CompressionFormat fmt)
    {
        try
        {
            var decoder = new BcDecoder();
            var colors = decoder.DecodeRaw2D(raw, w, h, fmt);
            var span = colors.Span;
            var out_ = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var c = span[y, x];
                    var idx = (y * w + x) * 4;
                    out_[idx] = c.r; out_[idx+1] = c.g; out_[idx+2] = c.b; out_[idx+3] = c.a;
                }
            return out_;
        }
        catch { return null; }
    }

    // ── x2 auto-update ───────────────────────────────────────────────────────

    // After patching a base texture, also patch a same-file `<name>_2x` variant with the same
    // PNG. This is a fallback: OnApplyAllClick normally stages _2x counterparts itself (and
    // handles cross-bundle ones), so this only fires for a same-file _2x that wasn't staged.
    // It rebuilds the _2x sprite as a quad too, to stay consistent with the base texture.
    private static void AutoApplyX2(
        AssetsManager am, AssetsFileInstance afile,
        string baseName, byte[] pngBytes,
        HashSet<string> explicitNames, IProgress<string>? progress)
    {
        var x2Name = baseName + "_2x";
        if (explicitNames.Contains(x2Name)) return; // already staged (by user or UI auto-_2x); don't double-apply
        var x2Info = FindTextureByName(am, afile, x2Name);
        if (x2Info == null) return;
        progress?.Report($"  Auto-updating {x2Name}...");
        var bf = am.GetBaseField(afile, x2Info);
        var (ok, w, h) = ApplyPngToField(bf, pngBytes);
        if (!ok) return;
        x2Info.SetNewData(bf);
        UpdateSpritesForTexture(am, afile, x2Info.PathId, w, h);
    }

    private static AssetFileInfo? FindTextureByName(AssetsManager am, AssetsFileInstance afile, string name)
    {
        foreach (var info in afile.file.GetAssetsOfType(AssetClassID.Texture2D))
        {
            try { if (am.GetBaseField(afile, info)["m_Name"].AsString == name) return info; }
            catch { }
        }
        return null;
    }

    // ── Audio → AudioClip field ───────────────────────────────────────────────

    private static byte[]? DecodeToPcm(byte[] fileBytes, string ext, out int channels, out int frequency)
    {
        channels = 0; frequency = 0;
        var ms = new MemoryStream(fileBytes);
        WaveStream reader;
        try
        {
            reader = ext.ToLowerInvariant() switch
            {
                ".wav" => new WaveFileReader(ms),
                ".ogg" => new VorbisWaveReader(ms),
                ".mp3" => new Mp3FileReader(ms),
                _ => throw new NotSupportedException(ext),
            };
        }
        catch { ms.Dispose(); return null; }

        using (ms) using (reader)
        {
            channels  = reader.WaveFormat.Channels;
            frequency = reader.WaveFormat.SampleRate;
            var pcmProvider = reader.ToSampleProvider().ToWaveProvider16();
            var buf = new MemoryStream();
            var tmp = new byte[8192];
            int n;
            while ((n = pcmProvider.Read(tmp, 0, tmp.Length)) > 0)
                buf.Write(tmp, 0, n);
            var pcm = buf.ToArray();
            return pcm.Length == 0 ? null : pcm;
        }
    }

    private static void SetAudioMetadata(AssetTypeValueField bf, int channels, int frequency, int pcmLen)
    {
        SetFieldIfPresent(bf, "m_Channels", channels);
        SetFieldIfPresent(bf, "m_Frequency", frequency);
        SetFieldIfPresent(bf, "m_BitsPerSample", 16);
        SetFieldIfPresent(bf, "m_LoadType", 0);
        SetFieldIfPresent(bf, "m_CompressionFormat", 0);
        var lenField = bf["m_Length"];
        if (lenField?.IsDummy == false)
            lenField.AsFloat = (float)pcmLen / (channels * 2f * frequency);
    }

    // Inline path: PCM goes directly into m_AudioData. Used for bundle audio clips.
    private static bool ApplyAudioToField(AssetTypeValueField bf, byte[] fileBytes, string ext)
    {
        var pcm = DecodeToPcm(fileBytes, ext, out int channels, out int frequency);
        if (pcm == null) return false;

        SetAudioMetadata(bf, channels, frequency, pcm.Length);

        var audioData = bf["m_AudioData"];
        if (audioData?.IsDummy == false)
            audioData.AsByteArray = pcm;

        var res = bf["m_Resource"];
        if (res?.IsDummy == false)
        {
            var src = res["m_Source"]; if (src?.IsDummy == false) src.AsString = "";
            var off = res["m_Offset"]; if (off?.IsDummy == false) off.AsULong = 0;
            var sz  = res["m_Size"];   if (sz?.IsDummy == false)  sz.AsULong = 0;
        }

        return true;
    }

    // Streaming path for bundle audio: PCM is appended to the bundle's internal .resS entry.
    // Used when m_AudioData is a dummy field — the data lives in a .resS block inside the bundle.
    private static bool ApplyStreamingAudioToBundle(
        AssetTypeValueField bf, BundleFileInstance bun, byte[] fileBytes, string ext)
    {
        var pcm = DecodeToPcm(fileBytes, ext, out int channels, out int frequency);
        if (pcm == null) return false;

        var res = bf["m_Resource"];
        if (res?.IsDummy != false) return false;

        SetAudioMetadata(bf, channels, frequency, pcm.Length);

        // Determine which internal .resS entry to use
        var srcField = res["m_Source"];
        var origSrc = srcField?.IsDummy == false ? srcField.AsString : "";
        string entryName;
        if (string.IsNullOrEmpty(origSrc))
        {
            entryName = bun.file.BlockAndDirInfo.DirectoryInfos
                .Select(d => d.Name)
                .FirstOrDefault(n => n.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)) ?? "";
            if (string.IsNullOrEmpty(entryName)) return false;
        }
        else
        {
            entryName = origSrc.Contains('/') ? origSrc[(origSrc.LastIndexOf('/') + 1)..] : origSrc;
        }

        var resSIdx = bun.file.GetFileIndex(entryName);
        if (resSIdx < 0) return false;

        // Wrap PCM in WAV so FMOD can determine the audio format
        var wavMs = new MemoryStream();
        using (var wfw = new WaveFileWriter(wavMs, new WaveFormat(frequency, 16, channels)))
            wfw.Write(pcm, 0, pcm.Length);
        var wavBytes = wavMs.ToArray();

        // Read the existing .resS block data, append WAV bytes, write back
        bun.file.GetFileRange(resSIdx, out var bundleOffset, out var entrySize);
        var reader = bun.file.DataReader;
        var savedPos = reader.Position;
        byte[] existing;
        try
        {
            reader.Position = bundleOffset;
            existing = reader.ReadBytes((int)entrySize);
        }
        finally { reader.Position = savedPos; }

        var wavOffset = (long)existing.Length;
        var combined = new byte[existing.Length + wavBytes.Length];
        Buffer.BlockCopy(existing, 0, combined, 0, existing.Length);
        Buffer.BlockCopy(wavBytes, 0, combined, existing.Length, wavBytes.Length);
        bun.file.BlockAndDirInfo.DirectoryInfos[resSIdx].SetNewData(combined);

        // Point m_Resource at the appended data
        if (srcField?.IsDummy == false) srcField.AsString = entryName;
        var offField = res["m_Offset"]; if (offField?.IsDummy == false) offField.AsULong = (ulong)wavOffset;
        var szField  = res["m_Size"];   if (szField?.IsDummy  == false) szField.AsULong  = (ulong)wavBytes.Length;

        return true;
    }

    // Streaming path: PCM is appended to the .resS file; m_Resource is updated to point at it.
    // Used for standalone .assets clips where m_AudioData is a dummy field (not writable inline).
    private static bool ApplyStreamingAudioToResS(
        AssetTypeValueField bf, byte[] fileBytes, string ext, string assetsPath)
    {
        var pcm = DecodeToPcm(fileBytes, ext, out int channels, out int frequency);
        if (pcm == null) return false;

        var res = bf["m_Resource"];
        if (res?.IsDummy != false) return false;

        SetAudioMetadata(bf, channels, frequency, pcm.Length);

        // Determine which .resS file to use
        var srcField = res["m_Source"];
        var origSrc = srcField?.IsDummy == false ? srcField.AsString : "";
        string resSName;
        if (!string.IsNullOrEmpty(origSrc))
        {
            var slash = origSrc.LastIndexOf('/');
            resSName = slash >= 0 ? origSrc[(slash + 1)..] : origSrc;
        }
        else
        {
            resSName = Path.GetFileName(assetsPath) + ".resS";
        }

        var dir = Path.GetDirectoryName(assetsPath) ?? "";
        var resSPath = Path.Combine(dir, resSName);

        // Backup .resS alongside the .assets backup (if it exists and isn't already backed up)
        var resSBak = resSPath + ".bak";
        if (File.Exists(resSPath) && !File.Exists(resSBak))
            File.Copy(resSPath, resSBak);

        // Wrap PCM in a WAV container so FMOD can determine the audio format when it reads the file
        var wavMs = new MemoryStream();
        using (var wfw = new WaveFileWriter(wavMs, new WaveFormat(frequency, 16, channels)))
            wfw.Write(pcm, 0, pcm.Length);
        var wavBytes = wavMs.ToArray();

        // Append WAV at the end of the .resS file (creates it if absent)
        long offset = File.Exists(resSPath) ? new FileInfo(resSPath).Length : 0;
        using (var fs = File.Open(resSPath, FileMode.Append, FileAccess.Write))
            fs.Write(wavBytes);

        // Point m_Resource at the new data
        if (srcField?.IsDummy == false) srcField.AsString = resSName;
        var offField = res["m_Offset"]; if (offField?.IsDummy == false) offField.AsULong = (ulong)offset;
        var szField  = res["m_Size"];   if (szField?.IsDummy  == false) szField.AsULong  = (ulong)wavBytes.Length;

        return true;
    }

    private static void SetFieldIfPresent(AssetTypeValueField bf, string name, int value)
    {
        var f = bf[name];
        if (f != null && !f.IsDummy) f.AsInt = value;
    }

    // ── PNG → Texture2D field ─────────────────────────────────────────────────

    private static (bool ok, int w, int h) ApplyPngToField(AssetTypeValueField bf, byte[] pngBytes)
    {
        using var skBmp = SKBitmap.Decode(pngBytes);
        if (skBmp == null) return (false, 0, 0);
        using var rgba = skBmp.Copy(SKColorType.Rgba8888);
        var w = rgba.Width; var h = rgba.Height;
        var pixels = new byte[w * h * 4];
        Marshal.Copy(rgba.GetPixels(), pixels, 0, pixels.Length);
        FlipVertical(pixels, w, h);

        bf["m_Width"].AsInt = w;
        bf["m_Height"].AsInt = h;
        bf["m_TextureFormat"].AsInt = 4; // RGBA32
        bf["m_MipCount"].AsInt = 1;
        bf["m_CompleteImageSize"].AsUInt = (uint)(w * h * 4);
        bf["m_IsReadable"].AsBool = true;
        bf["image data"].AsByteArray = pixels;

        var sd = bf["m_StreamData"];
        if (sd != null && !sd.IsDummy)
        { sd["offset"].AsULong = 0; sd["size"].AsULong = 0; sd["path"].AsString = ""; }

        return (true, w, h);
    }

    private static void UpdateSpritesForTexture(
        AssetsManager am, AssetsFileInstance afile, long texturePathId, int newW, int newH)
    {
        foreach (var spriteInfo in afile.file.GetAssetsOfType(AssetClassID.Sprite).ToList())
        {
            try
            {
                var sbf = am.GetBaseField(afile, spriteInfo);
                var rd  = sbf["m_RD"];
                if (rd.IsDummy) continue;
                var texRef = rd["texture"];
                if (texRef.IsDummy || texRef["m_PathID"].AsLong != texturePathId) continue;

                RebuildSpriteAsQuad(sbf, newW, newH);
                spriteInfo.SetNewData(sbf);
            }
            catch { }
        }
    }

    // Replaces a sprite's mesh with a 4-vertex quad that maps the whole texture onto the
    // sprite's full rect, so an imported image shows completely instead of being clipped to
    // the original silhouette/sub-rect (the mascot bug).
    //
    // SAP sprites bake all-zero vertex UVs; the game derives texture coords as
    // pixel = position * uvTransform.xz + uvTransform.yw. So the corner positions plus a
    // matching uvTransform are what make the full image show — baked UVs are written too but
    // are effectively ignored by the runtime.
    //
    // Mesh buffers go through the field API: m_VertexData.m_DataSize is TypelessData (bytes
    // live on the field itself); m_IndexBuffer is a vector whose bytes live in its "Array" child.
    private static void RebuildSpriteAsQuad(AssetTypeValueField sbf, int w, int h)
    {
        var rd = sbf["m_RD"];
        var vd = rd["m_VertexData"];
        if (vd.IsDummy) return;

        float px = sbf["m_Pivot"]["x"].AsFloat;
        float py = sbf["m_Pivot"]["y"].AsFloat;
        var ppuField = sbf["m_PixelsToUnits"];
        float ppu = (!ppuField.IsDummy && ppuField.AsFloat > 0) ? ppuField.AsFloat : 100f;
        float L = -px*w/ppu, R = (1f-px)*w/ppu, B = -py*h/ppu, T = (1f-py)*h/ppu;

        // Vertex buffer: stream0 = position float3 (4×12=48B); stream1 = uv float2 (4×8=32B)
        // beginning at the 16-byte-aligned end of stream0 (offset 48). Total 80 bytes.
        var vb = new byte[80];
        float[] xs = { L, R, L, R }, ys = { B, B, T, T }, us = { 0, 1, 0, 1 }, vs = { 0, 0, 1, 1 };
        for (int i = 0; i < 4; i++)
        {
            BitConverter.GetBytes(xs[i]).CopyTo(vb, i*12);
            BitConverter.GetBytes(ys[i]).CopyTo(vb, i*12 + 4);
            BitConverter.GetBytes(us[i]).CopyTo(vb, 48 + i*8);
            BitConverter.GetBytes(vs[i]).CopyTo(vb, 48 + i*8 + 4);
        }
        vd["m_VertexCount"].AsInt = 4;
        vd["m_DataSize"].AsByteArray = vb;

        ushort[] qi = { 0, 1, 2, 2, 1, 3 };
        var ib = new byte[12];
        for (int i = 0; i < 6; i++) BitConverter.GetBytes(qi[i]).CopyTo(ib, i*2);
        ByteArrayField(rd["m_IndexBuffer"]).AsByteArray = ib;

        var smArr = rd["m_SubMeshes"]["Array"];
        var subMeshes = (smArr != null && !smArr.IsDummy) ? smArr.Children : rd["m_SubMeshes"].Children;
        if (subMeshes != null && subMeshes.Count > 0)
        {
            var sm = subMeshes[0];
            sm["firstByte"].AsUInt = 0;
            sm["indexCount"].AsUInt = 6;
            sm["topology"].AsInt = 0;
            sm["baseVertex"].AsUInt = 0;
            sm["firstVertex"].AsUInt = 0;
            sm["vertexCount"].AsUInt = 4;
            var aabb = sm["localAABB"];
            if (!aabb.IsDummy)
            {
                aabb["m_Center"]["x"].AsFloat = (L+R)/2f; aabb["m_Center"]["y"].AsFloat = (B+T)/2f; aabb["m_Center"]["z"].AsFloat = 0;
                aabb["m_Extent"]["x"].AsFloat = (R-L)/2f; aabb["m_Extent"]["y"].AsFloat = (T-B)/2f; aabb["m_Extent"]["z"].AsFloat = 0.001f;
            }
        }

        var tr = rd["textureRect"];
        if (!tr.IsDummy) { tr["x"].AsFloat = 0; tr["y"].AsFloat = 0; tr["width"].AsFloat = w; tr["height"].AsFloat = h; }
        var tro = rd["textureRectOffset"]; if (!tro.IsDummy) { tro["x"].AsFloat = 0; tro["y"].AsFloat = 0; }
        var uvt = rd["uvTransform"];
        if (!uvt.IsDummy) { uvt["x"].AsFloat = ppu; uvt["y"].AsFloat = px*w; uvt["z"].AsFloat = ppu; uvt["w"].AsFloat = py*h; }
    }

    // Bytes of a TypelessData / vector<byte> field. A plain TypelessData holds the bytes
    // directly; a vector keeps them in its "Array" child (reading .AsByteArray on the vector
    // itself throws).
    private static AssetTypeValueField ByteArrayField(AssetTypeValueField f)
    {
        if ((f.Value?.ValueType ?? AssetValueType.None) == AssetValueType.ByteArray) return f;
        foreach (var c in f.Children)
            if ((c.Value?.ValueType ?? AssetValueType.None) == AssetValueType.ByteArray) return c;
        return f["Array"];
    }

    // ── Bitmap helpers ────────────────────────────────────────────────────────

    private static void FlipVertical(byte[] rgba, int w, int h)
    {
        var stride = w * 4; var tmp = new byte[stride];
        for (var y = 0; y < h / 2; y++)
        {
            var top = y * stride; var bot = (h - 1 - y) * stride;
            Buffer.BlockCopy(rgba, top, tmp, 0, stride);
            Buffer.BlockCopy(rgba, bot, rgba, top, stride);
            Buffer.BlockCopy(tmp, 0, rgba, bot, stride);
        }
    }

    private static Bitmap RgbaToAvaloniaBitmap(byte[] rgba, int w, int h)
    {
        var skBmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        Marshal.Copy(rgba, 0, skBmp.GetPixels(), rgba.Length);
        using var ms = new MemoryStream();
        skBmp.Encode(ms, SKEncodedImageFormat.Png, 100);
        ms.Position = 0;
        return new Bitmap(ms);
    }
}

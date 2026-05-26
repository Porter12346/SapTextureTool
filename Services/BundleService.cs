using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Avalonia.Media.Imaging;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
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
            Path.Combine(gameDir, "Super Auto Pets_Data", "StreamingAssets", "aa", "StandaloneWindows64"),
            Path.Combine(gameDir, "Contents", "Resources", "Data", "StreamingAssets", "aa", "StandaloneWindows64"),
            Path.Combine(gameDir, "StandaloneWindows64"),
            gameDir,
        };
        foreach (var c in candidates)
            if (Directory.Exists(c) && Directory.GetFiles(c, "*.bundle").Length > 0)
                return c;
        return candidates[0];
    }

    // Returns the Super Auto Pets_Data directory (contains sharedassets*.assets files).
    public static string GetDataDir(string gameDir)
    {
        var candidates = new[]
        {
            Path.Combine(gameDir, "Super Auto Pets_Data"),
            Path.Combine(gameDir, "Contents", "Resources", "Data"),
            gameDir,
        };
        foreach (var c in candidates)
            if (Directory.Exists(c) && Directory.GetFiles(c, "*.assets").Length > 0)
                return c;
        return candidates[0];
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
                        if (!ApplyAudioToField(bf, fileBytes, Path.GetExtension(entry.ReplacementPath))) continue;
                        info.SetNewData(bf);
                    }
                    else
                    {
                        var (ok, newW, newH) = ApplyPngToField(bf, fileBytes);
                        if (!ok) continue;
                        entry.Width = newW; entry.Height = newH; entry.TextureFormat = 4;
                        info.SetNewData(bf);
                        UpdateSpritesForTexture(am, afile, entry.PathId, newW, newH);
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
                    if (!ApplyAudioToField(bf, fileBytes, Path.GetExtension(entry.ReplacementPath))) continue;
                    info.SetNewData(bf);
                }
                else
                {
                    var (ok, newW, newH) = ApplyPngToField(bf, fileBytes);
                    if (!ok) continue;
                    entry.Width = newW; entry.Height = newH; entry.TextureFormat = 4;
                    info.SetNewData(bf);
                    UpdateSpritesForTexture(am, afile, entry.PathId, newW, newH);
                    AutoApplyX2(am, afile, entry.Name, fileBytes, explicitNames, progress);
                }
            }
            using (var outFs = File.Open(tempPath, FileMode.Create, FileAccess.Write))
                afile.file.Write(new AssetsFileWriter(outFs), 0);
        }
        finally { am.UnloadAll(); }
        File.Move(tempPath, assetsPath, overwrite: true);
    }

    // ── Restore backups ───────────────────────────────────────────────────────

    public static void RestoreBackups(string gameDir)
    {
        // Restore bundle backups
        var bundleDir = GetBundleDir(gameDir);
        if (Directory.Exists(bundleDir))
            foreach (var bak in Directory.GetFiles(bundleDir, "*.bundle.bak"))
            { File.Copy(bak, bak[..^4], overwrite: true); File.Delete(bak); }

        // Restore .assets backups
        var dataDir = GetDataDir(gameDir);
        if (Directory.Exists(dataDir))
            foreach (var bak in Directory.GetFiles(dataDir, "*.assets.bak"))
            { File.Copy(bak, bak[..^4], overwrite: true); File.Delete(bak); }
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

    // After patching a base texture, silently patch the _x2 variant in the same
    // assets file using the same PNG. Sprite data is intentionally not updated
    // for x2 — only the Texture2D image needs replacing.
    private static void AutoApplyX2(
        AssetsManager am, AssetsFileInstance afile,
        string baseName, byte[] pngBytes,
        HashSet<string> explicitNames, IProgress<string>? progress)
    {
        var x2Name = baseName + "_2x";
        if (explicitNames.Contains(x2Name)) return; // user staged x2 explicitly; don't override
        var x2Info = FindTextureByName(am, afile, x2Name);
        if (x2Info == null) return;
        progress?.Report($"  Auto-updating {x2Name}...");
        var bf = am.GetBaseField(afile, x2Info);
        var (ok, _, _) = ApplyPngToField(bf, pngBytes);
        if (ok) x2Info.SetNewData(bf);
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

    private static bool ApplyAudioToField(AssetTypeValueField bf, byte[] fileBytes, string ext)
    {
        byte[] audioBytes;
        int compressionFormat;

        if (ext.Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseWav(fileBytes, out audioBytes, out var ch, out var freq, out var bits))
                return false;
            compressionFormat = 0; // PCM
            SetFieldIfPresent(bf, "m_Channels", ch);
            SetFieldIfPresent(bf, "m_Frequency", freq);
            SetFieldIfPresent(bf, "m_BitsPerSample", bits);
            SetFieldIfPresent(bf, "m_LoadType", 0); // DecompressOnLoad
        }
        else
        {
            audioBytes = fileBytes;
            compressionFormat = ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ? 2 : 1; // MP3 or Vorbis
            SetFieldIfPresent(bf, "m_LoadType", 1); // CompressedInMemory
        }

        SetFieldIfPresent(bf, "m_CompressionFormat", compressionFormat);

        var audioData = bf["m_AudioData"];
        if (audioData != null && !audioData.IsDummy)
            audioData.AsByteArray = audioBytes;

        // Clear streaming resource so Unity reads inline data
        var res = bf["m_Resource"];
        if (res != null && !res.IsDummy)
        {
            var src = res["m_Source"]; if (src != null && !src.IsDummy) src.AsString = "";
            var off = res["m_Offset"]; if (off != null && !off.IsDummy) off.AsULong = 0;
            var sz  = res["m_Size"];   if (sz  != null && !sz.IsDummy)  sz.AsULong = 0;
        }

        return true;
    }

    private static void SetFieldIfPresent(AssetTypeValueField bf, string name, int value)
    {
        var f = bf[name];
        if (f != null && !f.IsDummy) f.AsInt = value;
    }

    private static bool TryParseWav(byte[] data, out byte[] pcm,
        out int channels, out int frequency, out int bitsPerSample)
    {
        pcm = Array.Empty<byte>();
        channels = 1; frequency = 44100; bitsPerSample = 16;

        if (data.Length < 44) return false;
        if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F') return false;
        if (data[8] != 'W' || data[9] != 'A' || data[10] != 'V' || data[11] != 'E') return false;

        channels      = BitConverter.ToInt16(data, 22);
        frequency     = BitConverter.ToInt32(data, 24);
        bitsPerSample = BitConverter.ToInt16(data, 34);

        // Walk chunks to find "data"
        var i = 12;
        while (i + 8 <= data.Length)
        {
            var chunkId   = System.Text.Encoding.ASCII.GetString(data, i, 4);
            var chunkSize = BitConverter.ToInt32(data, i + 4);
            if (chunkId == "data")
            {
                var start = i + 8;
                var len   = Math.Min(chunkSize, data.Length - start);
                pcm = new byte[len];
                Buffer.BlockCopy(data, start, pcm, 0, len);
                return true;
            }
            i += 8 + chunkSize;
        }
        return false;
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

    // Finds the Football sprite — exact name preferred, then Football/Soccer substring, then RiceBall.
    // Football has textureRect {7,2,241,240} which covers ~94% of the 256x256 texture.
    private static AssetFileInfo? FindFootballSprite(AssetsManager am, AssetsFileInstance afile)
    {
        AssetFileInfo? footballSubstr = null;
        AssetFileInfo? riceball = null;
        foreach (var s in afile.file.GetAssetsOfType(AssetClassID.Sprite))
        {
            try
            {
                var n = am.GetBaseField(afile, s)["m_Name"].AsString;
                if (n.Equals("Football", StringComparison.OrdinalIgnoreCase))
                    return s; // exact match wins immediately
                if (footballSubstr == null &&
                    (n.IndexOf("Football", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     n.IndexOf("Soccer",   StringComparison.OrdinalIgnoreCase) >= 0))
                    footballSubstr = s;
                else if (riceball == null && n.IndexOf("RiceBall", StringComparison.OrdinalIgnoreCase) >= 0)
                    riceball = s;
            }
            catch { }
        }
        return footballSubstr ?? riceball;
    }

    // For each Sprite that references texturePathId:
    //   Clone the football sprite's full field data (which has a working full-coverage mesh),
    //   swap in the correct name + texture PathID, update the rect for the new size, done.
    private static void UpdateSpritesForTexture(
        AssetsManager am, AssetsFileInstance afile, long texturePathId, int newW, int newH)
    {
        var footballInfo = FindFootballSprite(am, afile);

        foreach (var spriteInfo in afile.file.GetAssetsOfType(AssetClassID.Sprite))
        {
            try
            {
                var sbf = am.GetBaseField(afile, spriteInfo);
                var rd  = sbf["m_RD"];
                if (rd.IsDummy) continue;
                var texRef = rd["texture"];
                if (texRef.IsDummy || texRef["m_PathID"].AsLong != texturePathId) continue;

                var spriteName = sbf["m_Name"].AsString;

                // Build the field to write — start from the football clone if available,
                // otherwise fall back to the sprite's own fields.
                AssetTypeValueField workBf;
                if (footballInfo != null)
                {
                    // Clone the football exactly — change ONLY name and texture PathID,
                    // exactly as the user's manual UABEA approach. Touch nothing else.
                    workBf = am.GetBaseField(afile, footballInfo);
                    workBf["m_Name"].AsString = spriteName;
                    var wrd = workBf["m_RD"];
                    wrd["texture"]["m_PathID"].AsLong = texturePathId;
                    wrd["texture"]["m_FileID"].AsInt  = 0;
                }
                else
                {
                    workBf = sbf;
                    ApplySpriteRect(workBf, newW, newH);
                }

                spriteInfo.SetNewData(workBf);
            }
            catch { }
        }
    }

    private static void ApplySpriteRect(AssetTypeValueField sbf, int newW, int newH)
    {
        sbf["m_Rect"]["x"].AsFloat      = 0;
        sbf["m_Rect"]["y"].AsFloat      = 0;
        sbf["m_Rect"]["width"].AsFloat  = newW;
        sbf["m_Rect"]["height"].AsFloat = newH;

        var rd = sbf["m_RD"];
        rd["textureRect"]["x"].AsFloat      = 0;
        rd["textureRect"]["y"].AsFloat      = 0;
        rd["textureRect"]["width"].AsFloat  = newW;
        rd["textureRect"]["height"].AsFloat = newH;

        var tro = rd["textureRectOffset"];
        if (!tro.IsDummy) { tro["x"].AsFloat = 0; tro["y"].AsFloat = 0; }
        var aro = rd["atlasRectOffset"];
        if (!aro.IsDummy) { aro["x"].AsFloat = 0; aro["y"].AsFloat = 0; }

        var uvt = rd["uvTransform"];
        if (!uvt.IsDummy)
        {
            uvt["x"].AsFloat = newW;
            uvt["y"].AsFloat = newW / 2f;
            uvt["z"].AsFloat = newH;
            uvt["w"].AsFloat = newH / 2f;
        }
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

# SapTextureTool — Claude Code Handoff

## What This Project Is

A cross-platform desktop app (Avalonia / C# / .NET 8) for replacing textures and audio in **Super Auto Pets** (Unity 6, Addressables 2.9.1). Users pick the game directory, browse assets, drag-drop replacement PNGs or audio files, and click Apply. Replacements are written directly into the game's asset bundles.

## Stack

- **UI**: Avalonia 11.2.3 (XAML + code-behind in `MainWindow.axaml` / `MainWindow.axaml.cs`)
- **Asset reading/writing**: AssetsTools.NET 3.0.4
- **Audio decoding**: NAudio + NAudio.Vorbis
- **Game format**: Unity Addressables bundles (`StandaloneWindows64/*.bundle`) + standalone `.assets` files

## Key Files

| File | Purpose |
|------|---------|
| `Services/BundleService.cs` | All Unity asset reading/writing — scan, patch, restore |
| `Services/PackService.cs` | Save/load texture replacement packs (pack.json) |
| `Models/TextureEntry.cs` | Per-asset data model (texture, audio, _2x flag, etc.) |
| `MainWindow.axaml` / `.axaml.cs` | Main UI — search, preview, import, apply |
| `PackManagerWindow.axaml` / `.axaml.cs` | Pack management dialog |

## What Has Been Built / Fixed

### 1. Catalog CRC Patching — working (confirmed in-game)
Unity Addressables 2.9.1 stores a CRC for each bundle in `catalog.bin`. After patching a bundle the CRC no longer matches, causing `CRC Mismatch` errors and Unity refusing to load the bundle.

**Fix**: After writing a patched bundle, `PatchCatalogCrc(bundlePath)` reads `catalog.bin` (two levels up from the bundle file, in `aa/`), finds the bundle's entry using its original file size as the search key (the 4 bytes before the size field = the CRC field), and zeroes it out. CRC=0 means Unity skips checking.

- `catalog.bin.bak` is created before first modification.
- `RestoreBackups` also restores `catalog.bin.bak → catalog.bin`.
- The `.bak` file (created just before patching) provides the original size for the catalog lookup.

Confirmed working: after applying a texture the bundle loads with no CRC error.

### 2. Mascot / pet sprite rendering — FIXED & verified in-game
**Pets and mascots are NOT a shared atlas.** Each pose (`Bee_Build`, `Bee_Battle`, `Bee_Defeat`, `Bee_Draw`, `Bee_Victory`, `*Preview`, …) is its **own 512×512 Texture2D + its own Sprite**. Verified by scanning the bundle: no Texture2D is referenced by more than one Sprite. (The earlier "Bee = 6 sprites on one atlas texture / atlas guard" model was wrong and has been removed.)

Pet/mascot sprites use a **tight silhouette mesh** (~30 verts) whose artwork occupies only a **sub-rectangle** of the texture (`m_RD.textureRect`, e.g. `Bee_Build = (136, 0, 262, 493)` inside 512×512). The baked vertex UVs are **all zero** — the game derives texture coordinates from `uvTransform` applied to the vertex **position**:
```
texPixel = vertexPosition.xy * uvTransform.xz + uvTransform.yw      (÷ textureSize → normalized UV)
```

**The bug**: `UpdateSpritesForTexture` set `uvTransform = (newW, newW/2, newH, newH/2)`. With scale `newW` (512) instead of `ppu` (256), a full-rect quad's corners mapped to pixels `[-220, 803]` instead of `[0, 512]`, so most of the quad sampled **outside** the texture (clamped → solid black/white/striped bands) with only the centre in range — the "8×16 box in the middle + garbage" symptom.

**The fix** (`RebuildSpriteAsQuad`): replace the tight mesh with a 4-vertex quad over the full sprite rect and set `uvTransform = (ppu, pivotX*newW, ppu, pivotY*newH)`, so the four corners map exactly to texture pixels `(0,0)→(newW,newH)` — the whole image shows. Also rewrites the SubMesh (`indexCount→6`, `vertexCount→4`) so Unity doesn't read the old (87) index count from the new 12-byte buffer and crash.

Mesh buffers are written via the **field API** (the old fragile byte-patching `PatchSpriteToQuad` is removed):
- `m_RD.m_VertexData.m_DataSize` is `TypelessData` — bytes are on the field itself (`.AsByteArray`).
- `m_RD.m_IndexBuffer` is a `vector` — its bytes live in the **`["Array"]` child**; calling `.AsByteArray` on the vector itself throws NRE. (That NRE is why a prior author believed the field API couldn't write mesh buffers and resorted to byte-patching.) Use the `ByteArrayField()` helper.

**Consequence**: a replaced image now maps to the **full sprite frame**, not the original silhouette/sub-rect. For a skin that matches the original on-screen size, author full-frame art (e.g. 512×512) with the character centred and transparent padding.

### 3. _2x Auto-Replacement (working)
When applying a base texture, `OnApplyAllClick` searches `_allEntries` for a `_2x` counterpart, stages it as an `IsAutoX2=true` entry with the same PNG, and applies it through the normal patch path. That path runs `UpdateSpritesForTexture` for every entry (`IsAutoX2` only gates the recursive in-file `AutoApplyX2`), so a UI-staged `_2x` sprite **is** rebuilt as a quad — consistent with its base.

Where `_2x` lives: the **pet/item shop ICONS** in the standalone `.assets` files, not the bundle. `sharedassets1.assets` has ≈1,200 `_2x` textures (a few more in sharedassets2/3), 256×256 DXT5, e.g. `Parrot_2x`, `Duck_2x`, `RiceBall_2x`. They are tight-mesh sub-rect sprites just like the bundle pose art, so the §2 quad-rebuild fix applies to them through `PatchAssetsFile`. The battle-pose textures in `defaultlocalgroup_*.bundle` (e.g. `Bee_Build`) have **no** `_2x`. The in-file `AutoApplyX2` helper (BundleService) now also rebuilds the sprite, but is effectively unreachable because the UI stages the `_2x` from `_allEntries` first.

### 4. Show/Hide _2x Checkbox (working)
Checkbox next to search box toggles whether `_2x` texture variants appear in the list.

### 5. Pack Loading Crash (fixed)
`ApplyPackToEntries` used `ToDictionary` which threw on duplicate asset names. Fixed with `TryAdd`.

### 6. Mac Audio Playback (fixed)
`WaveOutEvent` constructor throws on macOS. Wrapped in try-catch with a clear message.

### 7. Streaming Audio Replacement in Bundles (implemented)
`ApplyStreamingAudioToBundle` handles bundle audio clips where `m_AudioData.IsDummy = true` (data lives in internal `.resS` block). Decodes to PCM, wraps in WAV, appends to the resS block, updates `m_Resource` offset/length.

## Important Technical Details

### Bundle Structure
- Textures are in `StandaloneWindows64/defaultlocalgroup_assets_all_*.bundle`
- Each bundle has internal CAB entries (loaded via `LoadAssetsFileFromBundle`)
- Backups: `{bundle}.bak` in same directory; catalog backup: `aa/catalog.bin.bak`

### Catalog CRC Pattern
In `catalog.bin` (binary, ~90KB), each modifiable bundle's entry contains:
```
[4-byte offset][4-byte offset][CRC uint32 LE][size uint32 LE][more offsets...]
```
The size = original file size of the bundle. Zeroing the CRC field disables Unity's check.

### Sprite structure (1 Sprite ↔ 1 Texture2D)
- No shared atlases — 1 Sprite references 1 Texture2D (verified across the bundle; the `.assets` icons follow the same 1:1 pattern, e.g. `Parrot_2x` texture ↔ `Parrot_2x` sprite).
- Asset locations: **battle-pose art** (`*_Build/_Battle/_Defeat/_Draw/_Victory`, `*Preview`) and backgrounds live in `defaultlocalgroup_*.bundle`; **shop/team icons** (including the `_2x` variants) live in the standalone `.assets` files (mostly `sharedassets1.assets`, ~2,500 textures / ~1,200 `_2x`).
- Original formats: pose art 512×512 DXT5 (`m_TextureFormat` 12); backgrounds 2048×1680 DXT1 (fmt 10); previews & icons 256×256 DXT5. `ApplyPngToField` rewrites replacements as RGBA32 (fmt 4): sets dims, `m_MipCount=1`, `m_IsReadable=true`, zeros `m_StreamData`.
- Pose sprites and icon sprites are tight silhouette meshes (~10–45 verts) referencing a **sub-rect** of the texture; backgrounds and some previews are 4-vert quads using the full texture.
- Sprite texture coords are computed at runtime as `uvTransform.xz * vertexPosition + uvTransform.yw` (baked vertex UVs are zero). `RebuildSpriteAsQuad` turns any referencing sprite into a full-texture quad — see §2 for the math and the field-API write details.

### _2x Texture Flag
`TextureEntry.IsAutoX2 = true` marks UI-staged _2x entries. In `PatchBundle`/`PatchAssetsFile`, `IsAutoX2` gates only the recursive in-file `AutoApplyX2` (prevents infinite recursion) — it does **not** skip `UpdateSpritesForTexture`. So every entry that goes through the patch path, including UI-staged `_2x`, gets `RebuildSpriteAsQuad`. The in-file `AutoApplyX2` helper (now also rebuilds the sprite) is a redundant fallback that short-circuits when the `_2x` was already staged. `_2x` variants exist for pet/item icons in `sharedassets1.assets` (the bundle pose textures have none).

### Audio Routing in Bundles
```csharp
bool ok = audioData?.IsDummy == false
    ? ApplyAudioToField(...)           // inline audio
    : ApplyStreamingAudioToBundle(...) // streaming via internal .resS block
```

## Build & Publish

```powershell
# Windows
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# Mac Intel
dotnet publish -c Release -r osx-x64 --self-contained true -p:PublishSingleFile=true

# Mac Apple Silicon
dotnet publish -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true
```

Output: `bin/Release/net8.0/{rid}/publish/SapTextureTool.exe` (Windows) or `SapTextureTool` (Mac).
Mac zips: `SapTextureTool-mac-x64.zip`, `SapTextureTool-mac-arm64.zip`.

### Running the Mac build (Gatekeeper / quarantine)

The published `.app` is ad-hoc/unsigned and arrives quarantined from the downloaded zip, so macOS will refuse to launch it until you fix permissions, self-sign it, and clear the quarantine flag. Run these in Terminal in the folder containing the zip (this is the Apple-Silicon zip; for Intel use `SapTextureTool-mac-x64.zip`):

```bash
# 1. Extract the zip
unzip SapTextureTool-mac-arm64.zip

# 2. Fix permissions
chmod +x SapTextureTool.app/Contents/MacOS/SapTextureTool
chmod +x SapTextureTool.app/Contents/MacOS/*.dylib

# 3. Sign the app (ad-hoc signature)
codesign --deep --force --sign - SapTextureTool.app

# 4. Clear quarantine
xattr -cr SapTextureTool.app

# 5. Open
open SapTextureTool.app
```

## Current Status / What To Do Next

1. **Catalog CRC patch — working** (confirmed in-game; the bundle loads with no CRC mismatch after patching).

2. **Mascot/pet sprite rendering — fixed & verified in-game** (see §2). `UpdateSpritesForTexture` is active and calls `RebuildSpriteAsQuad`; the full imported image now shows on the sprite. The same fix covers pet/item icon `_2x` sprites in `sharedassets1.assets` — worth a quick in-game check on a pet icon replacement (not yet verified).

3. **Mac audio replacement** — implemented but not fully tested on Mac hardware.

4. **Possible: catalog.hash** — `aa/catalog.hash` contains an MD5 of catalog.bin. For this local Steam game it appears unused (not checked on load), but if Unity validates it, modifying catalog.bin would also require updating catalog.hash. Has NOT caused issues yet.

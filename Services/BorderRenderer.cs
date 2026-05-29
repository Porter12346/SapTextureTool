using SapTextureTool.Models;
using SkiaSharp;
using System.IO;
using System.Runtime.InteropServices;

namespace SapTextureTool.Services;

// Adds the SAP-style outline (black ring touching the silhouette + white ring outside the black)
// to a transparent-background sprite PNG. The widths vary linearly with vertical position:
// thinner at the silhouette's top, thicker at the bottom — matches the in-game art style.
//
// Border order around the silhouette: body → black ring → white ring → transparent.
//
// Antialiasing strategy:
//   1. Float-precision chamfer distance with √2 diagonal weight (closer to true Euclidean
//      than 3-4 integer chamfer; sub-pixel precision lets us soften thresholds smoothly).
//   2. 1-pixel feathered transitions at the black↔white and white↔transparent boundaries
//      (linear ramp over distance ∈ [thresh-0.5, thresh+0.5]).
//   3. The source RGBA is composited OVER the ring color, so the input PNG's own anti-aliased
//      silhouette edge fades naturally into the black ring instead of producing a hard step.
public static class BorderRenderer
{
    private const float SQRT2 = 1.41421356f;

    public static byte[] AddBorder(byte[] pngBytes, BorderConfig cfg)
    {
        using var src = SKBitmap.Decode(pngBytes);
        if (src == null) throw new InvalidOperationException("Border: PNG decode failed");
        using var rgba = src.ColorType == SKColorType.Rgba8888 && src.AlphaType == SKAlphaType.Unpremul
            ? src.Copy()
            : src.Copy(SKColorType.Rgba8888);

        var w = rgba.Width; var h = rgba.Height;
        var pixels = new byte[w * h * 4];
        Marshal.Copy(rgba.GetPixels(), pixels, 0, pixels.Length);

        var (bboxTop, bboxBot) = SilhouetteVerticalBounds(pixels, w, h);
        if (bboxBot < bboxTop) { bboxTop = 0; bboxBot = h - 1; }

        var dist = ChamferDistanceF(pixels, w, h);

        // Per-row pixel-space thresholds: blackThresh = end of black ring, whiteThresh = end of white ring.
        var blackThresh = new float[h];
        var whiteThresh = new float[h];
        var rangeRecip = bboxBot > bboxTop ? 1.0f / (bboxBot - bboxTop) : 0f;
        for (var y = 0; y < h; y++)
        {
            var t = Math.Clamp((y - bboxTop) * rangeRecip, 0f, 1f);
            var black = cfg.BlackPxTop + (cfg.BlackPxBot - cfg.BlackPxTop) * t;
            var white = cfg.WhitePxTop + (cfg.WhitePxBot - cfg.WhitePxTop) * t;
            blackThresh[y] = black;
            whiteThresh[y] = black + white;
        }

        var output = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            var bThr = blackThresh[y];
            var wThr = whiteThresh[y];
            var rowBase = y * w;
            for (var x = 0; x < w; x++)
            {
                var i = rowBase + x;
                var d = dist[i];
                var px = i * 4;

                if (d == 0f)
                {
                    // Inside the silhouette mask — pass original through; ring never shows here.
                    output[px]     = pixels[px];
                    output[px + 1] = pixels[px + 1];
                    output[px + 2] = pixels[px + 2];
                    output[px + 3] = pixels[px + 3];
                    continue;
                }

                // Ring color via two 1-pixel-wide linear ramps.
                //   tBW = 0 at bThr-0.5 → 1 at bThr+0.5  (black→white)
                //   tWT = 0 at wThr-0.5 → 1 at wThr+0.5  (opaque→transparent)
                var tBW = Math.Clamp(d - (bThr - 0.5f), 0f, 1f);
                var tWT = Math.Clamp(d - (wThr - 0.5f), 0f, 1f);
                int ringGrey  = (int)(255f * tBW + 0.5f);
                int ringAlpha = (int)(255f * (1f - tWT) + 0.5f);

                // Composite the source PNG OVER the ring. Source pixels with non-zero alpha
                // (feathered silhouette edge) blend smoothly with the ring beneath, eliminating
                // the silhouette→black staircase.
                byte srcA = pixels[px + 3];
                if (srcA == 0)
                {
                    output[px]     = (byte)ringGrey;
                    output[px + 1] = (byte)ringGrey;
                    output[px + 2] = (byte)ringGrey;
                    output[px + 3] = (byte)ringAlpha;
                }
                else
                {
                    float srcAf = srcA / 255f;
                    float dstAf = (ringAlpha / 255f) * (1f - srcAf);
                    float resAf = srcAf + dstAf;
                    if (resAf < 1e-6f)
                    {
                        output[px + 3] = 0;
                    }
                    else
                    {
                        var invRes = 1f / resAf;
                        int r = (int)((pixels[px]     * srcAf + ringGrey * dstAf) * invRes + 0.5f);
                        int g = (int)((pixels[px + 1] * srcAf + ringGrey * dstAf) * invRes + 0.5f);
                        int b = (int)((pixels[px + 2] * srcAf + ringGrey * dstAf) * invRes + 0.5f);
                        output[px]     = (byte)Math.Clamp(r, 0, 255);
                        output[px + 1] = (byte)Math.Clamp(g, 0, 255);
                        output[px + 2] = (byte)Math.Clamp(b, 0, 255);
                        output[px + 3] = (byte)Math.Clamp((int)(resAf * 255f + 0.5f), 0, 255);
                    }
                }
            }
        }

        using var outBmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Marshal.Copy(output, 0, outBmp.GetPixels(), output.Length);
        using var img = SKImage.FromBitmap(outBmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream();
        data.SaveTo(ms);
        return ms.ToArray();
    }

    private static (int top, int bot) SilhouetteVerticalBounds(byte[] rgba, int w, int h)
    {
        int top = h, bot = -1;
        for (var y = 0; y < h; y++)
        {
            var rowBase = y * w * 4;
            for (var x = 0; x < w; x++)
            {
                if (rgba[rowBase + x * 4 + 3] > 128) { if (y < top) top = y; if (y > bot) bot = y; break; }
            }
        }
        return (top, bot);
    }

    // Two-pass chamfer with √2 diagonal weight. Float values give the sub-pixel precision
    // needed for the 1-pixel-wide threshold ramps to look smooth. Orthogonal step = 1.0,
    // diagonal step = √2 ≈ 1.41 — much closer to true Euclidean than the integer 3-4 chamfer.
    private static float[] ChamferDistanceF(byte[] rgba, int w, int h)
    {
        const float MAX = 1e10f;
        var d = new float[w * h];
        for (var i = 0; i < d.Length; i++)
            d[i] = rgba[i * 4 + 3] > 128 ? 0f : MAX;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (d[i] == 0f) continue;
                var v = d[i];
                if (y > 0)
                {
                    if (x > 0)     v = MathF.Min(v, d[i - w - 1] + SQRT2);
                                   v = MathF.Min(v, d[i - w]     + 1f);
                    if (x < w - 1) v = MathF.Min(v, d[i - w + 1] + SQRT2);
                }
                if (x > 0)         v = MathF.Min(v, d[i - 1]     + 1f);
                d[i] = v;
            }
        }
        for (var y = h - 1; y >= 0; y--)
        {
            for (var x = w - 1; x >= 0; x--)
            {
                var i = y * w + x;
                if (d[i] == 0f) continue;
                var v = d[i];
                if (x < w - 1)     v = MathF.Min(v, d[i + 1]     + 1f);
                if (y < h - 1)
                {
                    if (x > 0)     v = MathF.Min(v, d[i + w - 1] + SQRT2);
                                   v = MathF.Min(v, d[i + w]     + 1f);
                    if (x < w - 1) v = MathF.Min(v, d[i + w + 1] + SQRT2);
                }
                d[i] = v;
            }
        }
        return d;
    }
}

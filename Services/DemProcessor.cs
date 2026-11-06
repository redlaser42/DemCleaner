using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using BitMiracle.LibTiff.Classic;
using DemCleaner.Models;

namespace DemCleaner.Services;

public enum TerrainShadingMode
{
    Hypsometric,
    Grayscale,
    Alpine,
    Desert
}

public sealed record Heightmap3DAssets(
    MeshGeometry3D TerrainMesh,
    MeshGeometry3D SkirtMesh,
    BitmapSource TextureBitmap,
    int TextureResolution,
    int MeshGridDimension,
    float MinElevation,
    float MaxElevation);

public sealed class MosaicDescriptor
{
    public required int GridRows { get; init; }
    public required int GridCols { get; init; }
    public required int[] ColWidths { get; init; }
    public required int[] RowHeights { get; init; }
    public required int[] ColOffsets { get; init; }
    public required int[] RowOffsets { get; init; }
    public required int TotalWidth { get; init; }
    public required int TotalHeight { get; init; }
    public required DemTile?[,] TileGrid { get; init; }
    public required float FallbackElevation { get; init; }
    public double? GeoOriginX { get; init; }
    public double? GeoOriginY { get; init; }
    public double? PixelScaleX { get; init; }
    public double? PixelScaleY { get; init; }
    public int? UtmZone { get; init; }
    public bool IsNorthernHemisphere { get; init; } = true;
}

public readonly record struct ExportAreaStats(
    int PixelX,
    int PixelY,
    int PixelWidth,
    int PixelHeight,
    float MinElevation,
    float MaxElevation,
    float MeanElevation);

public static class DemProcessor
{
    public static bool IsVoidPixel(float value, float? gdalNoData, DemProcessingOptions options)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return true;

        // Standard extreme float sentinel values often used by GIS tools (-3.4028235e+38)
        if (value <= -1e30f || value >= 1e30f)
            return true;

        if (options.UseGdalNoDataTag && gdalNoData.HasValue)
        {
            float tol = Math.Max(1e-4f, Math.Abs(gdalNoData.Value) * 1e-5f);
            if (Math.Abs(value - gdalNoData.Value) <= tol)
                return true;
        }

        if (options.UseCustomThreshold)
        {
            return options.CustomComparison switch
            {
                NoDataComparison.LessThanOrEqual => value <= options.CustomNoDataValue,
                NoDataComparison.Equal => Math.Abs(value - options.CustomNoDataValue) <= 1e-4f,
                NoDataComparison.GreaterThanOrEqual => value >= options.CustomNoDataValue,
                _ => false
            };
        }

        return false;
    }

    /// <summary>
    /// Analyzes raw elevation values for all tiles, builds void masks, and computes per-tile and global elevation statistics.
    /// </summary>
    public static (float GlobalMin, float GlobalMax, float GlobalMean, int TotalVoidPixels) AnalyzeTiles(
        IList<DemTile> tiles, DemProcessingOptions options, CancellationToken cancellationToken = default)
    {
        float globalMin = float.MaxValue;
        float globalMax = float.MinValue;
        double globalSum = 0.0;
        long globalValidCount = 0;
        int totalVoids = 0;


        foreach (var tile in tiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int len = tile.RawData.Length;
            bool[] mask = new bool[len];
            float tMin = float.MaxValue;
            float tMax = float.MinValue;
            double tSum = 0.0;
            int tValid = 0;
            int tVoid = 0;

            for (int i = 0; i < len; i++)
            {
                float v = tile.RawData[i];
                if (IsVoidPixel(v, tile.GdalNoDataValue, options))
                {
                    mask[i] = true;
                    tVoid++;
                }
                else
                {
                    mask[i] = false;
                    if (v < tMin) tMin = v;
                    if (v > tMax) tMax = v;
                    tSum += v;
                    tValid++;
                }
            }

            tile.VoidMask = mask;
            tile.VoidPixelCount = tVoid;
            totalVoids += tVoid;

            if (tValid > 0)
            {
                tile.MinElevation = tMin;
                tile.MaxElevation = tMax;
                tile.MeanElevation = (float)(tSum / tValid);

                if (tMin < globalMin) globalMin = tMin;
                if (tMax > globalMax) globalMax = tMax;
                globalSum += tSum;
                globalValidCount += tValid;
            }
            else
            {
                tile.MinElevation = 0f;
                tile.MaxElevation = 0f;
                tile.MeanElevation = 0f;
            }
        }

        if (globalValidCount == 0)
        {
            globalMin = 0f;
            globalMax = 1f;
        }

        float globalMean = globalValidCount > 0 ? (float)(globalSum / globalValidCount) : 0f;
        return (globalMin, globalMax, globalMean, totalVoids);
    }

    /// <summary>
    /// Fills empty/void areas in a single tile according to the chosen FillMethod.
    /// </summary>
    public static void FillTileVoids(
        DemTile tile, DemProcessingOptions options, float globalMin, float globalMax, float globalMean,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int width = tile.Width;
        int height = tile.Height;
        int len = tile.RawData.Length;

        if (tile.VoidMask == null || tile.VoidMask.Length != len)
        {
            AnalyzeTiles(new[] { tile }, options, cancellationToken);
        }

        bool[] mask = tile.VoidMask!;
        float[] filled = new float[len];
        Array.Copy(tile.RawData, filled, len);

        FillMethod effectiveMethod = tile.ResolveEffectiveFillMethod(options.FillMethod);
        string modeSuffix = tile.FillOverride == TileFillModeOverride.UseGlobalDefault
            ? $"{effectiveMethod}"
            : $"{effectiveMethod} [Custom]";

        if (tile.VoidPixelCount == 0)
        {
            tile.BaseFilledData = (float[])filled.Clone();
            tile.FilledData = filled;
            tile.Status = $"Ready (No Voids • {modeSuffix})";
            return;
        }

        if (tile.VoidPixelCount >= len)
        {
            // Entire tile is empty; fill with global stat
            float fallback = effectiveMethod switch
            {
                FillMethod.Minimum => globalMin,
                FillMethod.Maximum => globalMax,
                _ => globalMean
            };
            Array.Fill(filled, fallback);
            tile.BaseFilledData = (float[])filled.Clone();
            tile.FilledData = filled;
            tile.Status = $"Filled ({modeSuffix})";
            return;
        }

        switch (effectiveMethod)
        {
            case FillMethod.Minimum:
            {
                float fillVal = options.FillUsingGlobalStats ? globalMin : tile.MinElevation;
                for (int i = 0; i < len; i++)
                {
                    if (mask[i]) filled[i] = fillVal;
                }
                break;
            }
            case FillMethod.Maximum:
            {
                float fillVal = options.FillUsingGlobalStats ? globalMax : tile.MaxElevation;
                for (int i = 0; i < len; i++)
                {
                    if (mask[i]) filled[i] = fillVal;
                }
                break;
            }
            case FillMethod.Mean:
            {
                float fillVal = options.FillUsingGlobalStats ? globalMean : tile.MeanElevation;
                for (int i = 0; i < len; i++)
                {
                    if (mask[i]) filled[i] = fillVal;
                }
                break;
            }
            case FillMethod.Interpolated:
            {
                InterpolateVoidsInPlace(filled, mask, width, height, options.InterpolationIterations, tile.MeanElevation, cancellationToken);
                break;
            }
        }

        tile.BaseFilledData = (float[])filled.Clone();
        tile.FilledData = filled;
        tile.Status = $"Filled ({modeSuffix})";
    }

    /// <summary>
    /// Smooths elevation seams between adjacent tiles in the 2D grid by applying boundary jump feathering
    /// followed by iterative cross-tile Laplacian smoothing passes across shared borders.
    /// </summary>
    public static void ApplyTileSeamSmoothing(
        IList<DemTile> tiles, int smoothingSteps, CancellationToken cancellationToken = default)
    {
        if (tiles.Count == 0) return;

        // First restore each tile's FilledData from its un-smoothed BaseFilledData
        foreach (var tile in tiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tile.BaseFilledData != null)
            {
                if (tile.FilledData == null || tile.FilledData.Length != tile.BaseFilledData.Length)
                    tile.FilledData = new float[tile.BaseFilledData.Length];
                Array.Copy(tile.BaseFilledData, tile.FilledData, tile.BaseFilledData.Length);
            }
            else if (tile.FilledData == null)
            {
                tile.FilledData = (float[])tile.RawData.Clone();
                tile.BaseFilledData = (float[])tile.RawData.Clone();
            }
        }

        if (smoothingSteps <= 0 || tiles.Count <= 1)
            return;

        int rows = Math.Max(1, tiles.Max(t => t.GridRow) + 1);
        int cols = Math.Max(1, tiles.Max(t => t.GridCol) + 1);

        var grid = new DemTile?[rows, cols];
        foreach (var tile in tiles)
        {
            if (tile.GridRow >= 0 && tile.GridRow < rows && tile.GridCol >= 0 && tile.GridCol < cols)
            {
                grid[tile.GridRow, tile.GridCol] ??= tile;
            }
        }

        int bandWidth = Math.Clamp(smoothingSteps * 3, 2, 64);

        // Pass 1: Smooth out C0 step discontinuities across vertical seams (Left tile <-> Right tile)
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols - 1; c++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DemTile? left = grid[r, c];
                DemTile? right = grid[r, c + 1];
                if (left?.FilledData == null || right?.FilledData == null) continue;

                int sharedH = Math.Min(left.Height, right.Height);
                int wL = left.Width;
                int wR = right.Width;
                int band = Math.Min(bandWidth, Math.Min(wL / 2, wR / 2));
                if (band < 1 || sharedH < 1) continue;

                float[] lData = left.FilledData;
                float[] rData = right.FilledData;

                // Compute boundary difference along the vertical seam and smooth it tangentially
                float[] rawDiff = new float[sharedH];
                for (int y = 0; y < sharedH; y++)
                {
                    int ly = (int)((long)y * left.Height / sharedH);
                    int ry = (int)((long)y * right.Height / sharedH);
                    rawDiff[y] = rData[ry * wR] - lData[ly * wL + (wL - 1)];
                }

                float[] smoothDiff = Smooth1DArray(rawDiff, Math.Clamp(smoothingSteps * 2, 1, 24));

                for (int y = 0; y < left.Height; y++)
                {
                    int sy = Math.Clamp((int)((long)y * sharedH / left.Height), 0, sharedH - 1);
                    float halfDelta = 0.5f * smoothDiff[sy];
                    int rowStart = y * wL;
                    for (int d = 0; d < band; d++)
                    {
                        float t = 1f - (d / (float)band);
                        float weight = t * t * (3f - 2f * t); // Smoothstep falloff
                        lData[rowStart + (wL - 1 - d)] += halfDelta * weight;
                    }
                }

                for (int y = 0; y < right.Height; y++)
                {
                    int sy = Math.Clamp((int)((long)y * sharedH / right.Height), 0, sharedH - 1);
                    float halfDelta = 0.5f * smoothDiff[sy];
                    int rowStart = y * wR;
                    for (int d = 0; d < band; d++)
                    {
                        float t = 1f - (d / (float)band);
                        float weight = t * t * (3f - 2f * t);
                        rData[rowStart + d] -= halfDelta * weight;
                    }
                }
            }
        }

        // Pass 2: Smooth out C0 step discontinuities across horizontal seams (Top tile <-> Bottom tile)
        for (int r = 0; r < rows - 1; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DemTile? top = grid[r, c];
                DemTile? bottom = grid[r + 1, c];
                if (top?.FilledData == null || bottom?.FilledData == null) continue;

                int sharedW = Math.Min(top.Width, bottom.Width);
                int hT = top.Height;
                int hB = bottom.Height;
                int band = Math.Min(bandWidth, Math.Min(hT / 2, hB / 2));
                if (band < 1 || sharedW < 1) continue;

                float[] tData = top.FilledData;
                float[] bData = bottom.FilledData;

                float[] rawDiff = new float[sharedW];
                for (int x = 0; x < sharedW; x++)
                {
                    int tx = (int)((long)x * top.Width / sharedW);
                    int bx = (int)((long)x * bottom.Width / sharedW);
                    rawDiff[x] = bData[bx] - tData[(hT - 1) * top.Width + tx];
                }

                float[] smoothDiff = Smooth1DArray(rawDiff, Math.Clamp(smoothingSteps * 2, 1, 24));

                for (int x = 0; x < top.Width; x++)
                {
                    int sx = Math.Clamp((int)((long)x * sharedW / top.Width), 0, sharedW - 1);
                    float halfDelta = 0.5f * smoothDiff[sx];
                    for (int d = 0; d < band; d++)
                    {
                        float t = 1f - (d / (float)band);
                        float weight = t * t * (3f - 2f * t);
                        tData[(hT - 1 - d) * top.Width + x] += halfDelta * weight;
                    }
                }

                for (int x = 0; x < bottom.Width; x++)
                {
                    int sx = Math.Clamp((int)((long)x * sharedW / bottom.Width), 0, sharedW - 1);
                    float halfDelta = 0.5f * smoothDiff[sx];
                    for (int d = 0; d < band; d++)
                    {
                        float t = 1f - (d / (float)band);
                        float weight = t * t * (3f - 2f * t);
                        bData[d * bottom.Width + x] -= halfDelta * weight;
                    }
                }
            }
        }

        // Pass 3: Iterative cross-boundary Laplacian relaxation along seam neighborhoods for C1 gradient continuity
        int relaxPasses = Math.Clamp(smoothingSteps, 1, 25);
        int relaxRadius = Math.Clamp(smoothingSteps + 2, 2, 28);

        for (int iter = 0; iter < relaxPasses; iter++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Relax vertical seams
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols - 1; c++)
                {
                    DemTile? left = grid[r, c];
                    DemTile? right = grid[r, c + 1];
                    if (left?.FilledData == null || right?.FilledData == null || left.Width != right.Width || left.Height != right.Height)
                        continue;

                    int w = left.Width;
                    int h = left.Height;
                    int rad = Math.Min(relaxRadius, w / 3);
                    if (rad < 1) continue;

                    float[] lData = left.FilledData;
                    float[] rData = right.FilledData;

                    for (int y = 1; y < h - 1; y++)
                    {
                        int rowOff = y * w;
                        for (int k = -rad; k < rad; k++)
                        {
                            float center = GetSeamPixelHoriz(lData, rData, w, y, k);
                            float nLeft = GetSeamPixelHoriz(lData, rData, w, y, k - 1);
                            float nRight = GetSeamPixelHoriz(lData, rData, w, y, k + 1);
                            float nUp = GetSeamPixelHoriz(lData, rData, w, y - 1, k);
                            float nDown = GetSeamPixelHoriz(lData, rData, w, y + 1, k);

                            float avg = 0.25f * (nLeft + nRight + nUp + nDown);
                            float falloff = 1f - (Math.Abs(k + 0.5f) / (rad + 0.5f));
                            float blended = center + 0.5f * falloff * (avg - center);
                            SetSeamPixelHoriz(lData, rData, w, rowOff, k, blended);
                        }
                    }
                }
            }

            // Relax horizontal seams
            for (int r = 0; r < rows - 1; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    DemTile? top = grid[r, c];
                    DemTile? bottom = grid[r + 1, c];
                    if (top?.FilledData == null || bottom?.FilledData == null || top.Width != bottom.Width || top.Height != bottom.Height)
                        continue;

                    int w = top.Width;
                    int h = top.Height;
                    int rad = Math.Min(relaxRadius, h / 3);
                    if (rad < 1) continue;

                    float[] tData = top.FilledData;
                    float[] bData = bottom.FilledData;

                    for (int k = -rad; k < rad; k++)
                    {
                        float falloff = 1f - (Math.Abs(k + 0.5f) / (rad + 0.5f));
                        for (int x = 1; x < w - 1; x++)
                        {
                            float center = GetSeamPixelVert(tData, bData, w, h, x, k);
                            float nUp = GetSeamPixelVert(tData, bData, w, h, x, k - 1);
                            float nDown = GetSeamPixelVert(tData, bData, w, h, x, k + 1);
                            float nLeft = GetSeamPixelVert(tData, bData, w, h, x - 1, k);
                            float nRight = GetSeamPixelVert(tData, bData, w, h, x + 1, k);

                            float avg = 0.25f * (nUp + nDown + nLeft + nRight);
                            float blended = center + 0.5f * falloff * (avg - center);
                            SetSeamPixelVert(tData, bData, w, h, x, k, blended);
                        }
                    }
                }
            }
        }
    }

    private static float GetSeamPixelHoriz(float[] lData, float[] rData, int w, int y, int k)
    {
        int rowOff = y * w;
        return k < 0
            ? lData[rowOff + Math.Clamp(w + k, 0, w - 1)]
            : rData[rowOff + Math.Clamp(k, 0, w - 1)];
    }

    private static void SetSeamPixelHoriz(float[] lData, float[] rData, int w, int rowOff, int k, float val)
    {
        if (k < 0)
            lData[rowOff + Math.Clamp(w + k, 0, w - 1)] = val;
        else
            rData[rowOff + Math.Clamp(k, 0, w - 1)] = val;
    }

    private static float GetSeamPixelVert(float[] tData, float[] bData, int w, int h, int x, int k)
    {
        return k < 0
            ? tData[Math.Clamp(h + k, 0, h - 1) * w + x]
            : bData[Math.Clamp(k, 0, h - 1) * w + x];
    }

    private static void SetSeamPixelVert(float[] tData, float[] bData, int w, int h, int x, int k, float val)
    {
        if (k < 0)
            tData[Math.Clamp(h + k, 0, h - 1) * w + x] = val;
        else
            bData[Math.Clamp(k, 0, h - 1) * w + x] = val;
    }

    private static float[] Smooth1DArray(float[] input, int radius)
    {
        int n = input.Length;
        if (n <= 1 || radius <= 0) return (float[])input.Clone();

        float[] output = new float[n];
        for (int i = 0; i < n; i++)
        {
            int i0 = Math.Max(0, i - radius);
            int i1 = Math.Min(n - 1, i + radius);
            float sum = 0f;
            int cnt = 0;
            for (int j = i0; j <= i1; j++)
            {
                sum += input[j];
                cnt++;
            }
            output[i] = sum / Math.Max(1, cnt);
        }
        return output;
    }

    /// <summary>
    /// Performs wavefront boundary seeding followed by Gauss-Seidel SOR Laplace smoothing on void pixels.
    /// Produces seamless, natural terrain continuation across voids of any size.
    /// </summary>
    private static void InterpolateVoidsInPlace(
        float[] data, bool[] voidMask, int width, int height, int iterations, float fallbackMean,
        CancellationToken cancellationToken)
    {
        int len = width * height;
        bool[] resolved = new bool[len];
        var frontier = new List<int>(Math.Min(len, 4096));

        for (int i = 0; i < len; i++)
        {
            resolved[i] = !voidMask[i];
        }

        int[] dx = { -1, 1, 0, 0, -1, 1, -1, 1 };
        int[] dy = { 0, 0, -1, 1, -1, -1, 1, 1 };
        float[] weights = { 1f, 1f, 1f, 1f, 0.7071f, 0.7071f, 0.7071f, 0.7071f };

        // Step 1: Multi-pass wavefront propagation from valid boundaries inward to initialize all void pixels
        bool anyUnresolved = true;
        int safetyPass = 0;
        while (anyUnresolved && safetyPass < width + height)
        {
            cancellationToken.ThrowIfCancellationRequested();
            anyUnresolved = false;
            frontier.Clear();

            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    int idx = rowOffset + x;
                    if (resolved[idx]) continue;

                    float sum = 0f;
                    float weightSum = 0f;

                    for (int d = 0; d < 8; d++)
                    {
                        int nx = x + dx[d];
                        int ny = y + dy[d];
                        if (nx >= 0 && nx < width && ny >= 0 && ny < height)
                        {
                            int nIdx = ny * width + nx;
                            if (resolved[nIdx])
                            {
                                sum += data[nIdx] * weights[d];
                                weightSum += weights[d];
                            }
                        }
                    }

                    if (weightSum > 0f)
                    {
                        data[idx] = sum / weightSum;
                        frontier.Add(idx);
                    }
                    else
                    {
                        anyUnresolved = true;
                    }
                }
            }

            if (frontier.Count == 0)
            {
                // Remaining unresolved pixels (if isolated) get fallback mean
                for (int i = 0; i < len; i++)
                {
                    if (!resolved[i])
                    {
                        data[i] = fallbackMean;
                        resolved[i] = true;
                    }
                }
                break;
            }

            foreach (int idx in frontier)
            {
                resolved[idx] = true;
            }

            safetyPass++;
        }

        // Collect indices of void pixels for fast Laplace relaxation
        var voidIndices = new List<int>();
        for (int i = 0; i < len; i++)
        {
            if (voidMask[i]) voidIndices.Add(i);
        }

        // Step 2: Iterative Laplace diffusion with Over-Relaxation (SOR) on void pixels
        const float omega = 1.45f;
        int totalIter = Math.Clamp(iterations, 10, 1000);

        for (int iter = 0; iter < totalIter; iter++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float maxDelta = 0f;

            foreach (int idx in voidIndices)
            {
                int y = idx / width;
                int x = idx - y * width;

                float sum = 0f;
                int count = 0;

                if (x > 0) { sum += data[idx - 1]; count++; }
                if (x < width - 1) { sum += data[idx + 1]; count++; }
                if (y > 0) { sum += data[idx - width]; count++; }
                if (y < height - 1) { sum += data[idx + width]; count++; }

                if (count > 0)
                {
                    float avg = sum / count;
                    float oldVal = data[idx];
                    float newVal = oldVal + omega * (avg - oldVal);
                    data[idx] = newVal;

                    float delta = Math.Abs(newVal - oldVal);
                    if (delta > maxDelta) maxDelta = delta;
                }
            }

            if (maxDelta < 1e-4f)
                break;
        }
    }

    /// <summary>
    /// Normalizes a tile's filled elevation data to the [0, 1] range using the total elevation range [globalMin, globalMax]
    /// across all tiles in the set, and exports it as a 16-bit grayscale PNG (0..65535).
    /// </summary>
    public static void ExportNormalized16BitPng(
        DemTile tile, string outputPath, float globalMin, float globalMax, CancellationToken cancellationToken = default)
    {
        float[] source = tile.FilledData ?? tile.RawData;
        ExportNormalized16BitPng(source, tile.Width, tile.Height, outputPath, globalMin, globalMax, cancellationToken);
    }

    public static void ExportNormalized16BitPng(
        float[] source, int width, int height, string outputPath, float globalMin, float globalMax, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        float range = globalMax - globalMin;
        bool flatRange = range <= 1e-9f;

        ushort[] pixels16 = new ushort[width * height];

        for (int i = 0; i < pixels16.Length; i++)
        {
            float elev = source[i];
            float normalized01 = flatRange ? 0.5f : Math.Clamp((elev - globalMin) / range, 0f, 1f);
            pixels16[i] = (ushort)Math.Round(normalized01 * 65535f);
        }

        int stride = width * sizeof(ushort);
        var bitmap = BitmapSource.Create(
            width,
            height,
            96.0,
            96.0,
            PixelFormats.Gray16,
            null,
            pixels16,
            stride);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    /// <summary>
    /// Exports a 32-bit IEEE floating-point GeoTIFF containing real elevation values (in meters)
    /// along with optional GeoTIFF tiepoints, pixel scale, UTM zone, and GDAL_NODATA tags.
    /// </summary>
    public static void ExportFloat32GeoTiff(
        float[] elevationData,
        int width,
        int height,
        string outputPath,
        double? geoOriginX = null,
        double? geoOriginY = null,
        double? pixelScaleX = null,
        double? pixelScaleY = null,
        int? utmZone = null,
        bool isNorthernHemisphere = true,
        float? gdalNoData = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TiffDemReader.EnsureTagsRegistered();

        using Tiff? tif = Tiff.Open(outputPath, "w");
        if (tif == null)
        {
            throw new IOException($"Could not open output TIFF file for writing: {outputPath}");
        }

        int rowsPerStrip = Math.Clamp(65536 / Math.Max(1, width * sizeof(float)), 1, height);

        tif.SetField(TiffTag.IMAGEWIDTH, width);
        tif.SetField(TiffTag.IMAGELENGTH, height);
        tif.SetField(TiffTag.SAMPLESPERPIXEL, 1);
        tif.SetField(TiffTag.BITSPERSAMPLE, 32);
        tif.SetField(TiffTag.SAMPLEFORMAT, SampleFormat.IEEEFP);
        tif.SetField(TiffTag.ORIENTATION, BitMiracle.LibTiff.Classic.Orientation.TOPLEFT);
        tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tif.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);
        tif.SetField(TiffTag.ROWSPERSTRIP, rowsPerStrip);

        if (gdalNoData.HasValue)
        {
            tif.SetField((TiffTag)42113, $"{gdalNoData.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}\0");
        }

        WriteGeoTiffMetadataTags(tif, geoOriginX, geoOriginY, pixelScaleX, pixelScaleY, utmZone, isNorthernHemisphere);

        float[] rowFloats = new float[width];
        byte[] rowBytes = new byte[width * sizeof(float)];
        for (int y = 0; y < height; y++)
        {
            if ((y & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            Array.Copy(elevationData, (long)y * width, rowFloats, 0, width);
            Buffer.BlockCopy(rowFloats, 0, rowBytes, 0, rowBytes.Length);
            tif.WriteScanline(rowBytes, y, 0);
        }

        tif.FlushData();
    }

    /// <summary>
    /// Exports a 16-bit unsigned integer (0..65535) normalized grayscale GeoTIFF mapped from [minElev, maxElev].
    /// </summary>
    public static void ExportNormalized16BitTiff(
        float[] elevationData,
        int width,
        int height,
        string outputPath,
        float minElev,
        float maxElev,
        double? geoOriginX = null,
        double? geoOriginY = null,
        double? pixelScaleX = null,
        double? pixelScaleY = null,
        int? utmZone = null,
        bool isNorthernHemisphere = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TiffDemReader.EnsureTagsRegistered();

        using Tiff? tif = Tiff.Open(outputPath, "w");
        if (tif == null)
        {
            throw new IOException($"Could not open output TIFF file for writing: {outputPath}");
        }

        int rowsPerStrip = Math.Clamp(65536 / Math.Max(1, width * sizeof(ushort)), 1, height);

        tif.SetField(TiffTag.IMAGEWIDTH, width);
        tif.SetField(TiffTag.IMAGELENGTH, height);
        tif.SetField(TiffTag.SAMPLESPERPIXEL, 1);
        tif.SetField(TiffTag.BITSPERSAMPLE, 16);
        tif.SetField(TiffTag.SAMPLEFORMAT, SampleFormat.UINT);
        tif.SetField(TiffTag.ORIENTATION, BitMiracle.LibTiff.Classic.Orientation.TOPLEFT);
        tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tif.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);
        tif.SetField(TiffTag.ROWSPERSTRIP, rowsPerStrip);

        WriteGeoTiffMetadataTags(tif, geoOriginX, geoOriginY, pixelScaleX, pixelScaleY, utmZone, isNorthernHemisphere);

        float range = maxElev - minElev;
        bool flatRange = range <= 1e-9f;

        ushort[] rowUshorts = new ushort[width];
        byte[] rowBytes = new byte[width * sizeof(ushort)];

        for (int y = 0; y < height; y++)
        {
            if ((y & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            long rowOffset = (long)y * width;
            for (int x = 0; x < width; x++)
            {
                float elev = elevationData[rowOffset + x];
                float norm01 = flatRange ? 0.5f : Math.Clamp((elev - minElev) / range, 0f, 1f);
                rowUshorts[x] = (ushort)Math.Round(norm01 * 65535f);
            }

            Buffer.BlockCopy(rowUshorts, 0, rowBytes, 0, rowBytes.Length);
            tif.WriteScanline(rowBytes, y, 0);
        }

        tif.FlushData();
    }

    private static void WriteGeoTiffMetadataTags(
        Tiff tif,
        double? geoOriginX,
        double? geoOriginY,
        double? pixelScaleX,
        double? pixelScaleY,
        int? utmZone,
        bool isNorthernHemisphere)
    {
        if (geoOriginX.HasValue && geoOriginY.HasValue)
        {
            double[] tiepoint = { 0.0, 0.0, 0.0, geoOriginX.Value, geoOriginY.Value, 0.0 };
            tif.SetField((TiffTag)33922, tiepoint.Length, tiepoint);
        }

        if (pixelScaleX.HasValue && pixelScaleY.HasValue && pixelScaleX.Value > 0 && pixelScaleY.Value > 0)
        {
            double[] pixelScale = { pixelScaleX.Value, pixelScaleY.Value, 1.0 };
            tif.SetField((TiffTag)33550, pixelScale.Length, pixelScale);
        }

        if (utmZone.HasValue)
        {
            string hemi = isNorthernHemisphere ? "N" : "S";
            tif.SetField((TiffTag)34737, $"UTM zone {utmZone.Value}{hemi}\0");
        }
    }

    /// <summary>
    /// Builds a unified descriptor of the 2D stitched mosaic grid so sub-regions can be queried or resampled at any resolution.
    /// </summary>
    public static MosaicDescriptor BuildMosaicDescriptor(IList<DemTile> tiles, float fallbackElevation)
    {
        if (tiles.Count == 0)
        {
            return new MosaicDescriptor
            {
                GridRows = 1,
                GridCols = 1,
                ColWidths = new[] { 1 },
                RowHeights = new[] { 1 },
                ColOffsets = new[] { 0 },
                RowOffsets = new[] { 0 },
                TotalWidth = 1,
                TotalHeight = 1,
                TileGrid = new DemTile?[1, 1],
                FallbackElevation = fallbackElevation
            };
        }

        int rows = Math.Max(1, tiles.Max(t => t.GridRow) + 1);
        int cols = Math.Max(1, tiles.Max(t => t.GridCol) + 1);

        int defaultW = Math.Max(1, tiles[0].Width);
        int defaultH = Math.Max(1, tiles[0].Height);

        int[] colWidths = new int[cols];
        int[] colOffsets = new int[cols];
        int accumW = 0;
        for (int c = 0; c < cols; c++)
        {
            colOffsets[c] = accumW;
            int w = tiles.Where(t => t.GridCol == c).Select(t => t.Width).DefaultIfEmpty(defaultW).Max();
            colWidths[c] = Math.Max(1, w);
            accumW += colWidths[c];
        }

        int[] rowHeights = new int[rows];
        int[] rowOffsets = new int[rows];
        int accumH = 0;
        for (int r = 0; r < rows; r++)
        {
            rowOffsets[r] = accumH;
            int h = tiles.Where(t => t.GridRow == r).Select(t => t.Height).DefaultIfEmpty(defaultH).Max();
            rowHeights[r] = Math.Max(1, h);
            accumH += rowHeights[r];
        }

        var grid = new DemTile?[rows, cols];
        foreach (var tile in tiles)
        {
            if (tile.GridRow >= 0 && tile.GridRow < rows && tile.GridCol >= 0 && tile.GridCol < cols)
            {
                grid[tile.GridRow, tile.GridCol] ??= tile;
            }
        }

        // Determine top-left geographic origin of the overall mosaic if GeoTIFF or USGS coordinates are available
        double? mosaicOriginX = null;
        double? mosaicOriginY = null;
        double? mosaicScaleX = null;
        double? mosaicScaleY = null;
        int? sharedUtmZone = tiles.Select(t => t.UtmZone).FirstOrDefault(z => z.HasValue);
        bool isNorth = tiles.FirstOrDefault(t => t.UtmZone.HasValue)?.IsNorthernHemisphere ?? true;

        var refTile = tiles.FirstOrDefault(t => t.GeoOriginX.HasValue && t.GeoOriginY.HasValue);
        if (refTile != null)
        {
            double sx = refTile.PixelScaleX ?? (10000.0 / Math.Max(1, refTile.Width));
            double sy = refTile.PixelScaleY ?? (10000.0 / Math.Max(1, refTile.Height));
            mosaicScaleX = sx;
            mosaicScaleY = sy;
            mosaicOriginX = refTile.GeoOriginX!.Value - colOffsets[refTile.GridCol] * sx;
            mosaicOriginY = refTile.GeoOriginY!.Value + rowOffsets[refTile.GridRow] * sy;
        }
        else
        {
            var usgsTile = tiles.FirstOrDefault(t => t.ParsedFileX.HasValue && t.ParsedFileY.HasValue && sharedUtmZone.HasValue);
            if (usgsTile != null)
            {
                double sx = 10000.0 / Math.Max(1, usgsTile.Width);
                double sy = 10000.0 / Math.Max(1, usgsTile.Height);
                double ox = usgsTile.ParsedFileX!.Value < 1000 ? usgsTile.ParsedFileX.Value * 10000.0 : usgsTile.ParsedFileX.Value;
                double oy = usgsTile.ParsedFileY!.Value < 10000 ? usgsTile.ParsedFileY.Value * 10000.0 : usgsTile.ParsedFileY.Value;
                mosaicScaleX = sx;
                mosaicScaleY = sy;
                mosaicOriginX = ox - colOffsets[usgsTile.GridCol] * sx;
                mosaicOriginY = oy + rowOffsets[usgsTile.GridRow] * sy;
            }
        }

        return new MosaicDescriptor
        {
            GridRows = rows,
            GridCols = cols,
            ColWidths = colWidths,
            RowHeights = rowHeights,
            ColOffsets = colOffsets,
            RowOffsets = rowOffsets,
            TotalWidth = Math.Max(1, accumW),
            TotalHeight = Math.Max(1, accumH),
            TileGrid = grid,
            FallbackElevation = fallbackElevation,
            GeoOriginX = mosaicOriginX,
            GeoOriginY = mosaicOriginY,
            PixelScaleX = mosaicScaleX,
            PixelScaleY = mosaicScaleY,
            UtmZone = sharedUtmZone,
            IsNorthernHemisphere = isNorth
        };
    }

    public static float SampleMosaicPixel(MosaicDescriptor mosaic, int mx, int my, bool showProcessed = true)
    {
        mx = Math.Clamp(mx, 0, mosaic.TotalWidth - 1);
        my = Math.Clamp(my, 0, mosaic.TotalHeight - 1);

        int col = 0;
        for (int c = mosaic.GridCols - 1; c >= 0; c--)
        {
            if (mx >= mosaic.ColOffsets[c])
            {
                col = c;
                break;
            }
        }

        int row = 0;
        for (int r = mosaic.GridRows - 1; r >= 0; r--)
        {
            if (my >= mosaic.RowOffsets[r])
            {
                row = r;
                break;
            }
        }

        DemTile? tile = mosaic.TileGrid[row, col];
        if (tile == null)
            return mosaic.FallbackElevation;

        int localX = mx - mosaic.ColOffsets[col];
        int localY = my - mosaic.RowOffsets[row];

        if (tile.Width != mosaic.ColWidths[col] && mosaic.ColWidths[col] > 0)
            localX = (int)((long)localX * tile.Width / mosaic.ColWidths[col]);
        if (tile.Height != mosaic.RowHeights[row] && mosaic.RowHeights[row] > 0)
            localY = (int)((long)localY * tile.Height / mosaic.RowHeights[row]);

        localX = Math.Clamp(localX, 0, tile.Width - 1);
        localY = Math.Clamp(localY, 0, tile.Height - 1);
        int idx = localY * tile.Width + localX;

        if (!showProcessed)
        {
            if (tile.VoidMask != null && tile.VoidMask[idx])
                return mosaic.FallbackElevation;
            return tile.RawData[idx];
        }

        float[] source = tile.FilledData ?? tile.RawData;
        return source[idx];
    }

    /// <summary>
    /// Computes pixel coordinates and min/max/mean elevation within a normalized viewport selection [normX0..normX1, normY0..normY1].
    /// When exactCalculation is true, every pixel is sampled without striding, preventing peak/valley clipping in 16-bit elevation normalization.
    /// </summary>
    public static ExportAreaStats ComputeExportAreaStats(
        MosaicDescriptor mosaic, double normX0, double normY0, double normX1, double normY1, bool exactCalculation = false)
    {
        normX0 = Math.Clamp(normX0, 0.0, 1.0);
        normY0 = Math.Clamp(normY0, 0.0, 1.0);
        normX1 = Math.Clamp(normX1, 0.0, 1.0);
        normY1 = Math.Clamp(normY1, 0.0, 1.0);

        if (normX1 < normX0) (normX0, normX1) = (normX1, normX0);
        if (normY1 < normY0) (normY0, normY1) = (normY1, normY0);

        int px0 = Math.Clamp((int)Math.Round(normX0 * mosaic.TotalWidth), 0, mosaic.TotalWidth - 1);
        int py0 = Math.Clamp((int)Math.Round(normY0 * mosaic.TotalHeight), 0, mosaic.TotalHeight - 1);
        int px1 = Math.Clamp((int)Math.Round(normX1 * mosaic.TotalWidth), px0 + 1, mosaic.TotalWidth);
        int py1 = Math.Clamp((int)Math.Round(normY1 * mosaic.TotalHeight), py0 + 1, mosaic.TotalHeight);

        int pw = Math.Max(1, px1 - px0);
        int ph = Math.Max(1, py1 - py0);

        // Step stride for fast interactive stats calculation when dragging on huge grids
        int stepX = exactCalculation ? 1 : Math.Max(1, pw / 512);
        int stepY = exactCalculation ? 1 : Math.Max(1, ph / 512);

        float minE = float.MaxValue;
        float maxE = float.MinValue;
        double sumE = 0.0;
        long count = 0;

        if (exactCalculation && (long)pw * ph > 262144L)
        {
            object syncObj = new object();
            Parallel.For(py0, py1, () => (LocalMin: float.MaxValue, LocalMax: float.MinValue, LocalSum: 0.0, LocalCount: 0L),
                (y, loop, local) =>
                {
                    for (int x = px0; x < px1; x++)
                    {
                        float v = SampleMosaicPixel(mosaic, x, y);
                        if (v < local.LocalMin) local.LocalMin = v;
                        if (v > local.LocalMax) local.LocalMax = v;
                        local.LocalSum += v;
                        local.LocalCount++;
                    }
                    return local;
                },
                local =>
                {
                    lock (syncObj)
                    {
                        if (local.LocalMin < minE) minE = local.LocalMin;
                        if (local.LocalMax > maxE) maxE = local.LocalMax;
                        sumE += local.LocalSum;
                        count += local.LocalCount;
                    }
                });
        }
        else
        {
            for (int y = py0; y < py1; y += stepY)
            {
                for (int x = px0; x < px1; x += stepX)
                {
                    float v = SampleMosaicPixel(mosaic, x, y);
                    if (v < minE) minE = v;
                    if (v > maxE) maxE = v;
                    sumE += v;
                    count++;
                }
            }
        }

        if (count == 0)
        {
            minE = mosaic.FallbackElevation;
            maxE = mosaic.FallbackElevation;
        }

        float meanE = count > 0 ? (float)(sumE / count) : mosaic.FallbackElevation;
        return new ExportAreaStats(px0, py0, pw, ph, minE, maxE, meanE);
    }

    /// <summary>
    /// Extracts and resamples a sub-rectangle [subNormX0..subNormX1, subNormY0..subNormY1] of the stitched mosaic
    /// to the exact target resolution (outWidth × outHeight) using bilinear interpolation.
    /// </summary>
    public static float[] ExtractResampledSubTile(
        MosaicDescriptor mosaic,
        double subNormX0,
        double subNormY0,
        double subNormX1,
        double subNormY1,
        int outWidth,
        int outHeight,
        CancellationToken cancellationToken = default)
    {
        outWidth = Math.Max(1, outWidth);
        outHeight = Math.Max(1, outHeight);

        double srcX0 = subNormX0 * mosaic.TotalWidth;
        double srcY0 = subNormY0 * mosaic.TotalHeight;
        double srcW = Math.Max(1e-6, (subNormX1 - subNormX0) * mosaic.TotalWidth);
        double srcH = Math.Max(1e-6, (subNormY1 - subNormY0) * mosaic.TotalHeight);

        float[] result = new float[outWidth * outHeight];

        for (int dy = 0; dy < outHeight; dy++)
        {
            if ((dy & 31) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            double fy = srcY0 + ((dy + 0.5) / outHeight) * srcH - 0.5;
            int y0 = (int)Math.Floor(fy);
            int y1 = y0 + 1;
            float wy = (float)(fy - y0);

            int rowOffset = dy * outWidth;
            for (int dx = 0; dx < outWidth; dx++)
            {
                double fx = srcX0 + ((dx + 0.5) / outWidth) * srcW - 0.5;
                int x0 = (int)Math.Floor(fx);
                int x1 = x0 + 1;
                float wx = (float)(fx - x0);

                float v00 = SampleMosaicPixel(mosaic, x0, y0);
                float v10 = SampleMosaicPixel(mosaic, x1, y0);
                float v01 = SampleMosaicPixel(mosaic, x0, y1);
                float v11 = SampleMosaicPixel(mosaic, x1, y1);

                float top = v00 + (v10 - v00) * wx;
                float bottom = v01 + (v11 - v01) * wx;
                result[rowOffset + dx] = top + (bottom - top) * wy;
            }
        }

        return result;
    }

    /// <summary>
    /// Renders a visual preview BitmapSource for the UI.
    /// For raw preview, empty/void pixels are highlighted in crimson/magenta so the user can clearly spot areas needing correction.
    /// For processed preview, elevations are normalized across [globalMin, globalMax] (matching the 16-bit PNG export).
    /// </summary>
    public static BitmapSource RenderPreviewBitmap(
        DemTile tile, bool showProcessed, float globalMin, float globalMax, bool highlightVoidsInRaw = true, int maxPreviewDim = 512)
    {
        int srcW = tile.Width;
        int srcH = tile.Height;
        if (srcW <= 0 || srcH <= 0)
        {
            return BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr32, null, new byte[4], 4);
        }

        double scale = Math.Min(1.0, (double)maxPreviewDim / Math.Max(srcW, srcH));
        int dstW = Math.Max(1, (int)Math.Round(srcW * scale));
        int dstH = Math.Max(1, (int)Math.Round(srcH * scale));

        float[] data = showProcessed ? (tile.FilledData ?? tile.RawData) : tile.RawData;
        bool[]? mask = tile.VoidMask;
        float range = globalMax - globalMin;
        bool flatRange = range <= 1e-9f;

        byte[] pixels = new byte[dstW * dstH * 4]; // BGRA32

        for (int dy = 0; dy < dstH; dy++)
        {
            int sy = Math.Min(srcH - 1, (int)(dy / scale));
            int srcRowOffset = sy * srcW;
            int dstRowOffset = dy * dstW * 4;

            for (int dx = 0; dx < dstW; dx++)
            {
                int sx = Math.Min(srcW - 1, (int)(dx / scale));
                int srcIdx = srcRowOffset + sx;
                int dstIdx = dstRowOffset + dx * 4;

                bool isVoid = mask != null && mask[srcIdx];

                if (!showProcessed && isVoid)
                {
                    if (highlightVoidsInRaw)
                    {
                        // Vibrant coral-red highlight for void pixels so users immediately see missing DEM data
                        pixels[dstIdx + 0] = 85;  // B
                        pixels[dstIdx + 1] = 60;  // G
                        pixels[dstIdx + 2] = 245; // R
                        pixels[dstIdx + 3] = 255; // A
                    }
                    else
                    {
                        pixels[dstIdx + 0] = 0;
                        pixels[dstIdx + 1] = 0;
                        pixels[dstIdx + 2] = 0;
                        pixels[dstIdx + 3] = 255;
                    }
                }
                else
                {
                    float elev = data[srcIdx];
                    float norm = flatRange ? 0.5f : Math.Clamp((elev - globalMin) / range, 0f, 1f);
                    byte gray = (byte)Math.Round(norm * 255f);

                    pixels[dstIdx + 0] = gray;
                    pixels[dstIdx + 1] = gray;
                    pixels[dstIdx + 2] = gray;
                    pixels[dstIdx + 3] = 255;
                }
            }
        }

        var bmp = BitmapSource.Create(dstW, dstH, 96, 96, PixelFormats.Bgra32, null, pixels, dstW * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// Builds GPU-ready 3D heightmap assets (frozen 1K/2K/4K/8K/16K normal-shaded BitmapSource texture + frozen 3D terrain &amp; skirt MeshGeometry3D)
    /// for hardware-accelerated WPF Viewport3D rendering. Resolution is strictly constrained to 1K (1024), 2K (2048), 4K (4096), 8K (8192), or 16K (16384).
    /// </summary>
    public static Heightmap3DAssets Build3DHeightmapAssets(
        MosaicDescriptor mosaic,
        double normX0,
        double normY0,
        double normX1,
        double normY1,
        int requestedResolution,
        float minElev,
        float maxElev,
        bool showProcessed,
        TerrainShadingMode shadingMode,
        bool simulateWater = true,
        bool highlightQuadEdges = false,
        float waterSlopeLimit = 0.12f,
        float waterAltitudeLimit = 0.24f,
        CancellationToken cancellationToken = default)
    {
        int sampleDim = requestedResolution switch
        {
            <= 1024 => 1024,
            <= 2048 => 2048,
            <= 4096 => 4096,
            <= 8192 => 8192,
            _ => 16384
        };

        int gpuTexDim = sampleDim switch
        {
            <= 1024 => 1024,
            <= 2048 => 2048,
            <= 4096 => 4096,
            _ => 8192
        };

        var (terrainMesh, skirtMesh, meshDim) = Build3DHeightmapMesh(
            mosaic, normX0, normY0, normX1, normY1, requestedResolution, minElev, maxElev, showProcessed, cancellationToken);

        var textureBitmap = Build3DHeightmapTexture(
            mosaic, normX0, normY0, normX1, normY1, requestedResolution, minElev, maxElev, showProcessed, shadingMode, simulateWater, highlightQuadEdges, waterSlopeLimit, waterAltitudeLimit, cancellationToken);

        return new Heightmap3DAssets(
            terrainMesh,
            skirtMesh,
            textureBitmap,
            gpuTexDim,
            meshDim,
            minElev,
            maxElev);
    }

    /// <summary>
    /// Builds displaced 3D terrain and skirting MeshGeometry3D independently of the terrain shading texture.
    /// Allows changing terrain shaders without regenerating the heightfield mesh.
    /// </summary>
    public static (MeshGeometry3D TerrainMesh, MeshGeometry3D SkirtMesh, int MeshDim) Build3DHeightmapMesh(
        MosaicDescriptor mosaic,
        double normX0,
        double normY0,
        double normX1,
        double normY1,
        int requestedResolution,
        float minElev,
        float maxElev,
        bool showProcessed,
        CancellationToken cancellationToken = default)
    {
        int sampleDim = requestedResolution switch
        {
            <= 1024 => 1024,
            <= 2048 => 2048,
            <= 4096 => 4096,
            <= 8192 => 8192,
            _ => 16384
        };

        int meshSegs = sampleDim switch
        {
            <= 1024 => 256,   // 65,536 quads
            <= 2048 => 512,   // 262,144 quads
            <= 4096 => 768,   // 589,824 quads
            _ => 1024         // 1,048,576 quads
        };
        int meshDim = meshSegs + 1;

        var throttledParallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2)
        };

        normX0 = Math.Clamp(normX0, 0.0, 0.999);
        normY0 = Math.Clamp(normY0, 0.0, 0.999);
        normX1 = Math.Clamp(normX1, normX0 + 0.001, 1.0);
        normY1 = Math.Clamp(normY1, normY0 + 0.001, 1.0);

        double srcX0 = normX0 * mosaic.TotalWidth;
        double srcY0 = normY0 * mosaic.TotalHeight;
        double srcW = Math.Max(1.0, (normX1 - normX0) * mosaic.TotalWidth);
        double srcH = Math.Max(1.0, (normY1 - normY0) * mosaic.TotalHeight);

        float elevRange = Math.Max(1e-4f, maxElev - minElev);
        float invRange = 1f / elevRange;

        float[,] gridHeights = new float[meshDim, meshDim];
        const float baseHeightAmplitude = 2.0f;

        Parallel.For(0, meshDim, throttledParallelOptions, (gy, state) =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                state.Stop();
                return;
            }

            double v = gy / (double)(meshDim - 1);
            double fy = Math.Clamp(srcY0 + v * srcH - 0.5, 0.0, mosaic.TotalHeight - 1.0);
            for (int gx = 0; gx < meshDim; gx++)
            {
                double u = gx / (double)(meshDim - 1);
                double fx = Math.Clamp(srcX0 + u * srcW - 0.5, 0.0, mosaic.TotalWidth - 1.0);
                float elev = SampleMosaicPixelBilinear(mosaic, fx, fy, showProcessed, out _);
                float hNorm = Math.Clamp((elev - minElev) * invRange, 0f, 1f);
                gridHeights[gy, gx] = hNorm * baseHeightAmplitude;
            }
        });

        if (cancellationToken.IsCancellationRequested)
        {
            var emptyMesh = new MeshGeometry3D();
            emptyMesh.Freeze();
            return (emptyMesh, emptyMesh, meshDim);
        }

        int vertexCount = meshDim * meshDim;
        int quadCount = (meshDim - 1) * (meshDim - 1);

        var positions = new Point3DCollection(vertexCount);
        var normals = new Vector3DCollection(vertexCount);
        var texCoords = new PointCollection(vertexCount);
        var indices = new Int32Collection(quadCount * 6);

        double invMeshSpan = 1.0 / (meshDim - 1);
        double cellWorldStep = 2.0 * invMeshSpan;

        for (int gy = 0; gy < meshDim; gy++)
        {
            double v = gy * invMeshSpan;
            double worldZ = -1.0 + v * 2.0;

            int gyUp = Math.Max(0, gy - 1);
            int gyDown = Math.Min(meshDim - 1, gy + 1);

            for (int gx = 0; gx < meshDim; gx++)
            {
                double u = gx * invMeshSpan;
                double worldX = -1.0 + u * 2.0;
                float worldY = gridHeights[gy, gx];

                positions.Add(new Point3D(worldX, worldY, worldZ));
                texCoords.Add(new Point(u, v));

                int gxLeft = Math.Max(0, gx - 1);
                int gxRight = Math.Min(meshDim - 1, gx + 1);

                double dhdx = (gridHeights[gy, gxRight] - gridHeights[gy, gxLeft]) / (2.0 * cellWorldStep);
                double dhdz = (gridHeights[gyDown, gx] - gridHeights[gyUp, gx]) / (2.0 * cellWorldStep);

                var n = new Vector3D(-dhdx, 1.0, -dhdz);
                n.Normalize();
                normals.Add(n);
            }
        }

        for (int gy = 0; gy < meshDim - 1; gy++)
        {
            int row0 = gy * meshDim;
            int row1 = (gy + 1) * meshDim;
            for (int gx = 0; gx < meshDim - 1; gx++)
            {
                int i00 = row0 + gx;
                int i10 = row0 + gx + 1;
                int i01 = row1 + gx;
                int i11 = row1 + gx + 1;

                // Counter-clockwise winding when viewed from above (+Y)
                indices.Add(i00);
                indices.Add(i01);
                indices.Add(i10);

                indices.Add(i10);
                indices.Add(i01);
                indices.Add(i11);
            }
        }

        var terrainMesh = new MeshGeometry3D
        {
            Positions = positions,
            Normals = normals,
            TextureCoordinates = texCoords,
            TriangleIndices = indices
        };
        terrainMesh.Freeze();

        var skirtMesh = BuildTerrainSkirtMesh(gridHeights, meshDim);
        skirtMesh.Freeze();

        return (terrainMesh, skirtMesh, meshDim);
    }

    /// <summary>
    /// Builds the GPU terrain shader texture bitmap (albedo, hillshading, water simulation, quad edges)
    /// independently of mesh geometry. Allows fast shader updates without mesh rebuilding.
    /// </summary>
    public static BitmapSource Build3DHeightmapTexture(
        MosaicDescriptor mosaic,
        double normX0,
        double normY0,
        double normX1,
        double normY1,
        int requestedResolution,
        float minElev,
        float maxElev,
        bool showProcessed,
        TerrainShadingMode shadingMode,
        bool simulateWater = true,
        bool highlightQuadEdges = false,
        float waterSlopeLimit = 0.12f,
        float waterAltitudeLimit = 0.24f,
        CancellationToken cancellationToken = default)
    {
        int sampleDim = requestedResolution switch
        {
            <= 1024 => 1024,
            <= 2048 => 2048,
            <= 4096 => 4096,
            <= 8192 => 8192,
            _ => 16384
        };

        int gpuTexDim = sampleDim switch
        {
            <= 1024 => 1024,
            <= 2048 => 2048,
            <= 4096 => 4096,
            _ => 8192
        };

        int meshSegs = sampleDim switch
        {
            <= 1024 => 256,
            <= 2048 => 512,
            <= 4096 => 768,
            _ => 1024
        };
        int texelsPerQuad = Math.Max(1, gpuTexDim / meshSegs);

        var throttledParallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2)
        };

        normX0 = Math.Clamp(normX0, 0.0, 0.999);
        normY0 = Math.Clamp(normY0, 0.0, 0.999);
        normX1 = Math.Clamp(normX1, normX0 + 0.001, 1.0);
        normY1 = Math.Clamp(normY1, normY0 + 0.001, 1.0);

        double srcX0 = normX0 * mosaic.TotalWidth;
        double srcY0 = normY0 * mosaic.TotalHeight;
        double srcW = Math.Max(1.0, (normX1 - normX0) * mosaic.TotalWidth);
        double srcH = Math.Max(1.0, (normY1 - normY0) * mosaic.TotalHeight);

        float elevRange = Math.Max(1e-4f, maxElev - minElev);
        float invRange = 1f / elevRange;

        double waterRadiusMedX = Math.Max(8.0, srcW * 0.012);
        double waterRadiusMedY = Math.Max(8.0, srcH * 0.012);
        double waterRadiusWideX = Math.Max(22.0, srcW * 0.030);
        double waterRadiusWideY = Math.Max(22.0, srcH * 0.030);

        var writeableBmp = new WriteableBitmap(gpuTexDim, gpuTexDim, 96.0, 96.0, PixelFormats.Bgr24, null);
        writeableBmp.Lock();
        try
        {
            IntPtr backBuffer = writeableBmp.BackBuffer;
            int backStride = writeableBmp.BackBufferStride;

            double texStepX = srcW / gpuTexDim;
            double texStepY = srcH / gpuTexDim;

            double gradDeltaX = Math.Max(0.5, srcW / sampleDim);
            double gradDeltaY = Math.Max(0.5, srcH / sampleDim);
            float normalStrengthX = (float)Math.Clamp(0.32 * (srcW / (2.0 * gradDeltaX)), 2.5, 45.0);
            float normalStrengthY = (float)Math.Clamp(0.32 * (srcH / (2.0 * gradDeltaY)), 2.5, 45.0);
            int quadLineWidth = gpuTexDim >= 8192 ? 2 : 1;

            Parallel.For(0, gpuTexDim, throttledParallelOptions, (ty, state) =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    state.Stop();
                    return;
                }

                byte[] rowBytes = new byte[gpuTexDim * 3];
                bool isHorizontalQuadEdge = highlightQuadEdges &&
                    ((ty % texelsPerQuad) < quadLineWidth || ty >= gpuTexDim - quadLineWidth);

                double fy = srcY0 + (ty + 0.5) * texStepY - 0.5;
                double fyUp = Math.Max(0.0, fy - gradDeltaY);
                double fyDown = Math.Min(mosaic.TotalHeight - 1.0, fy + gradDeltaY);
                int iy = Math.Clamp((int)Math.Round(fy), 0, mosaic.TotalHeight - 1);

                for (int tx = 0; tx < gpuTexDim; tx++)
                {
                    int pxOffset = tx * 3;
                    double fx = srcX0 + (tx + 0.5) * texStepX - 0.5;
                    int ix = Math.Clamp((int)Math.Round(fx), 0, mosaic.TotalWidth - 1);

                    float centerElev = SampleMosaicPixelBilinear(mosaic, fx, fy, showProcessed, out bool isVoid);
                    byte finalR, finalG, finalB;

                    if (!showProcessed && isVoid)
                    {
                        finalR = 235;
                        finalG = 55;
                        finalB = 75;
                    }
                    else
                    {
                        float hNorm = Math.Clamp((centerElev - minElev) * invRange, 0f, 1f);

                        double fxLeft = Math.Max(0.0, fx - gradDeltaX);
                        double fxRight = Math.Min(mosaic.TotalWidth - 1.0, fx + gradDeltaX);

                        float elevL = SampleMosaicPixelBilinear(mosaic, fxLeft, fy, showProcessed, out _);
                        float elevR = SampleMosaicPixelBilinear(mosaic, fxRight, fy, showProcessed, out _);
                        float elevU = SampleMosaicPixelBilinear(mosaic, fx, fyUp, showProcessed, out _);
                        float elevD = SampleMosaicPixelBilinear(mosaic, fx, fyDown, showProcessed, out _);

                        float hL = Math.Clamp((elevL - minElev) * invRange, 0f, 1f);
                        float hR = Math.Clamp((elevR - minElev) * invRange, 0f, 1f);
                        float hU = Math.Clamp((elevU - minElev) * invRange, 0f, 1f);
                        float hD = Math.Clamp((elevD - minElev) * invRange, 0f, 1f);

                        float dX = (hR - hL) * normalStrengthX;
                        float dY = (hD - hU) * normalStrengthY;

                        float invLen = 1f / MathF.Sqrt(dX * dX + dY * dY + 1f);
                        float nx = -dX * invLen;
                        float ny = invLen;
                        float nz = -dY * invLen;

                        float microShade = Math.Clamp(0.60f + 0.40f * (nx * -0.55f + ny * 0.68f + nz * -0.48f), 0.30f, 1.20f);
                        float slope = 1f - ny;

                        EvaluateTerrainColorRamp(shadingMode, hNorm, slope, microShade, out finalR, out finalG, out finalB);

                        float maxAltNorm = Math.Clamp(waterAltitudeLimit, 0.005f, 1.0f);
                        float maxSlopeMeters = Math.Clamp(waterSlopeLimit, 0.005f, 5.0f);
                        float localSlopeThreshold = Math.Max(0.003f, maxSlopeMeters * 0.5f);
                        float localNormThreshold = Math.Max(1e-5f, localSlopeThreshold * invRange * 1.2f);

                        if (simulateWater && hNorm <= maxAltNorm)
                        {
                            float localDiffMeters = MathF.Max(MathF.Abs(elevR - elevL), MathF.Abs(elevD - elevU));
                            float localNormGrad = MathF.Max(MathF.Abs(hR - hL), MathF.Abs(hD - hU));
                            float localFlatMetric = MathF.Min(localDiffMeters / localSlopeThreshold, localNormGrad / localNormThreshold);

                            if (localFlatMetric < 1.0f)
                            {
                                int mRx = Math.Max(4, (int)Math.Round(waterRadiusMedX));
                                int mRy = Math.Max(4, (int)Math.Round(waterRadiusMedY));
                                int wRx = Math.Max(10, (int)Math.Round(waterRadiusWideX));
                                int wRy = Math.Max(10, (int)Math.Round(waterRadiusWideY));

                                int mDiagX = (int)(mRx * 0.707);
                                int mDiagY = (int)(mRy * 0.707);
                                int wDiagX = (int)(wRx * 0.707);
                                int wDiagY = (int)(wRy * 0.707);

                                float maxNeighborhoodDiff = 0f;

                                maxNeighborhoodDiff = MathF.Max(maxNeighborhoodDiff, MathF.Abs(SampleMosaicPixel(mosaic, ix - mRx, iy, showProcessed) - centerElev));
                                maxNeighborhoodDiff = MathF.Max(maxNeighborhoodDiff, MathF.Abs(SampleMosaicPixel(mosaic, ix + mRx, iy, showProcessed) - centerElev));
                                maxNeighborhoodDiff = MathF.Max(maxNeighborhoodDiff, MathF.Abs(SampleMosaicPixel(mosaic, ix, iy - mRy, showProcessed) - centerElev));
                                maxNeighborhoodDiff = MathF.Max(maxNeighborhoodDiff, MathF.Abs(SampleMosaicPixel(mosaic, ix, iy + mRy, showProcessed) - centerElev));
                                maxNeighborhoodDiff = MathF.Max(maxNeighborhoodDiff, MathF.Abs(SampleMosaicPixel(mosaic, ix - mDiagX, iy - mDiagY, showProcessed) - centerElev));
                                maxNeighborhoodDiff = MathF.Max(maxNeighborhoodDiff, MathF.Abs(SampleMosaicPixel(mosaic, ix + mDiagX, iy - mDiagY, showProcessed) - centerElev));
                                maxNeighborhoodDiff = MathF.Max(maxNeighborhoodDiff, MathF.Abs(SampleMosaicPixel(mosaic, ix - mDiagX, iy + mDiagY, showProcessed) - centerElev));
                                maxNeighborhoodDiff = MathF.Max(maxNeighborhoodDiff, MathF.Abs(SampleMosaicPixel(mosaic, ix + mDiagX, iy + mDiagY, showProcessed) - centerElev));

                                if (maxNeighborhoodDiff < maxSlopeMeters)
                                {
                                    maxNeighborhoodDiff = MathF.Max(maxNeighborhoodDiff, MathF.Abs(SampleMosaicPixel(mosaic, ix - wRx, iy, showProcessed) - centerElev));
                                    maxNeighborhoodDiff = MathF.Max(maxNeighborhoodDiff, MathF.Abs(SampleMosaicPixel(mosaic, ix + wRx, iy, showProcessed) - centerElev));
                                    maxNeighborhoodDiff = MathF.Max(maxNeighborhoodDiff, MathF.Abs(SampleMosaicPixel(mosaic, ix, iy - wRy, showProcessed) - centerElev));
                                    maxNeighborhoodDiff = MathF.Max(maxNeighborhoodDiff, MathF.Abs(SampleMosaicPixel(mosaic, ix, iy + wRy, showProcessed) - centerElev));
                                    maxNeighborhoodDiff = MathF.Max(maxNeighborhoodDiff, MathF.Abs(SampleMosaicPixel(mosaic, ix - wDiagX, iy - wDiagY, showProcessed) - centerElev));
                                    maxNeighborhoodDiff = MathF.Max(maxNeighborhoodDiff, MathF.Abs(SampleMosaicPixel(mosaic, ix + wDiagX, iy - wDiagY, showProcessed) - centerElev));
                                    maxNeighborhoodDiff = MathF.Max(maxNeighborhoodDiff, MathF.Abs(SampleMosaicPixel(mosaic, ix - wDiagX, iy + wDiagY, showProcessed) - centerElev));
                                    maxNeighborhoodDiff = MathF.Max(maxNeighborhoodDiff, MathF.Abs(SampleMosaicPixel(mosaic, ix + wDiagX, iy + wDiagY, showProcessed) - centerElev));
                                }

                                float wideFlatMetric = maxNeighborhoodDiff / maxSlopeMeters;
                                float combinedFlatMetric = MathF.Max(localFlatMetric, wideFlatMetric);

                                if (combinedFlatMetric < 1.0f)
                                {
                                    float tFlat = Math.Clamp((combinedFlatMetric - 0.25f) / 0.75f, 0f, 1f);
                                    float flatWeight = 1f - (tFlat * tFlat * (3f - 2f * tFlat));

                                    float fadeBand = Math.Max(0.01f, maxAltNorm * 0.25f);
                                    float fadeStart = Math.Max(0f, maxAltNorm - fadeBand);
                                    float tElev = maxAltNorm >= 0.995f
                                        ? 0f
                                        : Math.Clamp((hNorm - fadeStart) / fadeBand, 0f, 1f);
                                    float elevWeight = 1f - (tElev * tElev * (3f - 2f * tElev));

                                    float waterBlend = flatWeight * elevWeight;

                                    float depthTone = Math.Clamp(hNorm / Math.Max(0.05f, maxAltNorm), 0f, 1f);
                                    float wr = (30f + 26f * depthTone + 14f * (1f - waterBlend)) * microShade;
                                    float wg = (100f + 44f * depthTone + 20f * (1f - waterBlend)) * microShade;
                                    float wb = (176f + 40f * depthTone + 12f * (1f - waterBlend)) * microShade;

                                    finalR = (byte)Math.Clamp((int)(finalR * (1f - waterBlend) + wr * waterBlend), 0, 255);
                                    finalG = (byte)Math.Clamp((int)(finalG * (1f - waterBlend) + wg * waterBlend), 0, 255);
                                    finalB = (byte)Math.Clamp((int)(finalB * (1f - waterBlend) + wb * waterBlend), 0, 255);
                                }
                            }
                        }
                    }

                    if (highlightQuadEdges)
                    {
                        bool isVerticalQuadEdge = (tx % texelsPerQuad) < quadLineWidth || tx >= gpuTexDim - quadLineWidth;
                        if (isHorizontalQuadEdge || isVerticalQuadEdge)
                        {
                            finalR = (byte)((finalR * 30 + 235 * 70) / 100);
                            finalG = (byte)((finalG * 30 + 248 * 70) / 100);
                            finalB = (byte)((finalB * 30 + 255 * 70) / 100);
                        }
                    }

                    rowBytes[pxOffset + 0] = finalB;
                    rowBytes[pxOffset + 1] = finalG;
                    rowBytes[pxOffset + 2] = finalR;
                }

                IntPtr rowDest = IntPtr.Add(backBuffer, ty * backStride);
                Marshal.Copy(rowBytes, 0, rowDest, rowBytes.Length);
            });

            if (!cancellationToken.IsCancellationRequested)
            {
                writeableBmp.AddDirtyRect(new Int32Rect(0, 0, gpuTexDim, gpuTexDim));
            }
        }
        finally
        {
            writeableBmp.Unlock();
        }

        writeableBmp.Freeze();
        return writeableBmp;
    }

    private static MeshGeometry3D BuildTerrainSkirtMesh(float[,] gridHeights, int meshDim)
    {
        const double baseBottomY = -0.035;
        double invSpan = 1.0 / (meshDim - 1);

        var pos = new Point3DCollection((meshDim * 4) * 2);
        var norm = new Vector3DCollection((meshDim * 4) * 2);
        var idx = new Int32Collection((meshDim - 1) * 4 * 6);

        void AddEdgeWall(Func<int, Point3D> topPtFunc, Vector3D wallNormal, bool flipWinding)
        {
            int baseVertex = pos.Count;
            for (int i = 0; i < meshDim; i++)
            {
                Point3D topPt = topPtFunc(i);
                Point3D botPt = new(topPt.X, baseBottomY, topPt.Z);
                pos.Add(topPt);
                pos.Add(botPt);
                norm.Add(wallNormal);
                norm.Add(wallNormal);
            }

            for (int i = 0; i < meshDim - 1; i++)
            {
                int t0 = baseVertex + i * 2;
                int b0 = t0 + 1;
                int t1 = baseVertex + (i + 1) * 2;
                int b1 = t1 + 1;

                if (!flipWinding)
                {
                    idx.Add(t0); idx.Add(b0); idx.Add(t1);
                    idx.Add(t1); idx.Add(b0); idx.Add(b1);
                }
                else
                {
                    idx.Add(t0); idx.Add(t1); idx.Add(b0);
                    idx.Add(t1); idx.Add(b1); idx.Add(b0);
                }
            }
        }

        // South Edge (gy = meshDim - 1, Z = +1)
        AddEdgeWall(gx => new Point3D(-1.0 + gx * invSpan * 2.0, gridHeights[meshDim - 1, gx], 1.0), new Vector3D(0, 0, 1), flipWinding: false);
        // North Edge (gy = 0, Z = -1)
        AddEdgeWall(gx => new Point3D(-1.0 + gx * invSpan * 2.0, gridHeights[0, gx], -1.0), new Vector3D(0, 0, -1), flipWinding: true);
        // West Edge (gx = 0, X = -1)
        AddEdgeWall(gy => new Point3D(-1.0, gridHeights[gy, 0], -1.0 + gy * invSpan * 2.0), new Vector3D(-1, 0, 0), flipWinding: false);
        // East Edge (gx = meshDim - 1, X = +1)
        AddEdgeWall(gy => new Point3D(1.0, gridHeights[gy, meshDim - 1], -1.0 + gy * invSpan * 2.0), new Vector3D(1, 0, 0), flipWinding: true);

        return new MeshGeometry3D
        {
            Positions = pos,
            Normals = norm,
            TriangleIndices = idx
        };
    }

    private static float SampleMosaicPixelBilinear(MosaicDescriptor mosaic, double fx, double fy, bool showProcessed, out bool isVoid)
    {
        int x0 = Math.Clamp((int)Math.Floor(fx), 0, mosaic.TotalWidth - 1);
        int y0 = Math.Clamp((int)Math.Floor(fy), 0, mosaic.TotalHeight - 1);
        int x1 = Math.Min(mosaic.TotalWidth - 1, x0 + 1);
        int y1 = Math.Min(mosaic.TotalHeight - 1, y0 + 1);

        isVoid = false;
        if (!showProcessed)
        {
            isVoid = IsMosaicPixelVoid(mosaic, x0, y0);
            if (isVoid) return mosaic.FallbackElevation;
        }

        float wx = (float)Math.Clamp(fx - x0, 0.0, 1.0);
        float wy = (float)Math.Clamp(fy - y0, 0.0, 1.0);

        float v00 = SampleMosaicPixel(mosaic, x0, y0, showProcessed);
        float v10 = SampleMosaicPixel(mosaic, x1, y0, showProcessed);
        float v01 = SampleMosaicPixel(mosaic, x0, y1, showProcessed);
        float v11 = SampleMosaicPixel(mosaic, x1, y1, showProcessed);

        float top = v00 + (v10 - v00) * wx;
        float bottom = v01 + (v11 - v01) * wx;
        return top + (bottom - top) * wy;
    }

    private static bool IsMosaicPixelVoid(MosaicDescriptor mosaic, int mx, int my)
    {
        mx = Math.Clamp(mx, 0, mosaic.TotalWidth - 1);
        my = Math.Clamp(my, 0, mosaic.TotalHeight - 1);

        int col = 0;
        for (int c = mosaic.GridCols - 1; c >= 0; c--)
        {
            if (mx >= mosaic.ColOffsets[c]) { col = c; break; }
        }

        int row = 0;
        for (int r = mosaic.GridRows - 1; r >= 0; r--)
        {
            if (my >= mosaic.RowOffsets[r]) { row = r; break; }
        }

        DemTile? tile = mosaic.TileGrid[row, col];
        if (tile?.VoidMask == null) return false;

        int localX = Math.Clamp(mx - mosaic.ColOffsets[col], 0, tile.Width - 1);
        int localY = Math.Clamp(my - mosaic.RowOffsets[row], 0, tile.Height - 1);
        return tile.VoidMask[localY * tile.Width + localX];
    }

    private static void EvaluateTerrainColorRamp(
        TerrainShadingMode mode, float h, float slope, float microShade, out byte r, out byte g, out byte b)
    {
        float rf, gf, bf;

        switch (mode)
        {
            case TerrainShadingMode.Grayscale:
            {
                float v = (0.12f + 0.88f * h) * microShade;
                byte gray = (byte)Math.Clamp((int)(v * 255f), 0, 255);
                r = gray;
                g = gray;
                b = gray;
                return;
            }
            case TerrainShadingMode.Alpine:
            {
                // Deep fjord slate -> pine -> granite rock -> snow peaks
                if (h < 0.35f)
                {
                    float t = h / 0.35f;
                    rf = 42f + t * (74f - 42f);
                    gf = 68f + t * (105f - 68f);
                    bf = 62f + t * (82f - 62f);
                }
                else if (h < 0.72f)
                {
                    float t = (h - 0.35f) / 0.37f;
                    rf = 74f + t * (148f - 74f);
                    gf = 105f + t * (146f - 105f);
                    bf = 82f + t * (142f - 82f);
                }
                else
                {
                    float t = (h - 0.72f) / 0.28f;
                    rf = 148f + t * (245f - 148f);
                    gf = 146f + t * (248f - 146f);
                    bf = 142f + t * (252f - 142f);
                }
                break;
            }
            case TerrainShadingMode.Desert:
            {
                // Canyon floor -> ochre sandstone -> warm terracotta -> sun-bleached mesa crest
                if (h < 0.4f)
                {
                    float t = h / 0.4f;
                    rf = 92f + t * (168f - 92f);
                    gf = 68f + t * (114f - 68f);
                    bf = 48f + t * (72f - 48f);
                }
                else if (h < 0.78f)
                {
                    float t = (h - 0.4f) / 0.38f;
                    rf = 168f + t * (212f - 168f);
                    gf = 114f + t * (158f - 114f);
                    bf = 72f + t * (106f - 72f);
                }
                else
                {
                    float t = (h - 0.78f) / 0.22f;
                    rf = 212f + t * (238f - 212f);
                    gf = 158f + t * (216f - 158f);
                    bf = 106f + t * (184f - 106f);
                }
                break;
            }
            default: // Hypsometric (Earthy Topo: Valley Olive -> Sage -> Tan Sandstone -> High Peak Snow)
            {
                if (h < 0.28f)
                {
                    float t = h / 0.28f;
                    rf = 46f + t * (86f - 46f);
                    gf = 78f + t * (118f - 78f);
                    bf = 54f + t * (74f - 54f);
                }
                else if (h < 0.62f)
                {
                    float t = (h - 0.28f) / 0.34f;
                    rf = 86f + t * (173f - 86f);
                    gf = 118f + t * (145f - 118f);
                    bf = 74f + t * (104f - 74f);
                }
                else if (h < 0.86f)
                {
                    float t = (h - 0.62f) / 0.24f;
                    rf = 173f + t * (205f - 173f);
                    gf = 145f + t * (182f - 145f);
                    bf = 104f + t * (152f - 104f);
                }
                else
                {
                    float t = (h - 0.86f) / 0.14f;
                    rf = 205f + t * (244f - 205f);
                    gf = 182f + t * (242f - 182f);
                    bf = 152f + t * (238f - 152f);
                }
                break;
            }
        }

        // Darken steep rocky slopes slightly for natural cliff definition
        float cliffFactor = Math.Clamp(slope * 2.2f, 0f, 0.38f);
        rf = (rf * (1f - cliffFactor) + 95f * cliffFactor) * microShade;
        gf = (gf * (1f - cliffFactor) + 88f * cliffFactor) * microShade;
        bf = (bf * (1f - cliffFactor) + 82f * cliffFactor) * microShade;

        r = (byte)Math.Clamp((int)rf, 0, 255);
        g = (byte)Math.Clamp((int)gf, 0, 255);
        b = (byte)Math.Clamp((int)bf, 0, 255);
    }
}

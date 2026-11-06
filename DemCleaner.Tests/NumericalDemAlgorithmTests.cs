using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using DemCleaner.Models;
using DemCleaner.Services;

namespace DemCleaner.Tests;

[TestClass]
public class NumericalDemAlgorithmTests
{
    [TestMethod]
    public void IsVoidPixel_DetectsNaNAndInfinities()
    {
        var options = new DemProcessingOptions
        {
            UseGdalNoDataTag = true,
            UseCustomThreshold = false
        };

        Assert.IsTrue(DemProcessor.IsVoidPixel(float.NaN, null, options));
        Assert.IsTrue(DemProcessor.IsVoidPixel(float.PositiveInfinity, null, options));
        Assert.IsTrue(DemProcessor.IsVoidPixel(float.NegativeInfinity, null, options));
    }

    [TestMethod]
    public void IsVoidPixel_DetectsExtremeSentinels()
    {
        var options = new DemProcessingOptions
        {
            UseGdalNoDataTag = false,
            UseCustomThreshold = false
        };

        Assert.IsTrue(DemProcessor.IsVoidPixel(-3.4028235e+38f, null, options));
        Assert.IsTrue(DemProcessor.IsVoidPixel(3.4028235e+38f, null, options));
        Assert.IsTrue(DemProcessor.IsVoidPixel(-1e31f, null, options));
        Assert.IsTrue(DemProcessor.IsVoidPixel(1e31f, null, options));
        Assert.IsFalse(DemProcessor.IsVoidPixel(1250f, null, options));
    }

    [TestMethod]
    public void IsVoidPixel_RespectsGdalNoDataTag()
    {
        var optionsEnabled = new DemProcessingOptions
        {
            UseGdalNoDataTag = true,
            UseCustomThreshold = false
        };
        var optionsDisabled = new DemProcessingOptions
        {
            UseGdalNoDataTag = false,
            UseCustomThreshold = false
        };

        float gdalNoData = -32767f;

        Assert.IsTrue(DemProcessor.IsVoidPixel(-32767f, gdalNoData, optionsEnabled));
        Assert.IsFalse(DemProcessor.IsVoidPixel(100f, gdalNoData, optionsEnabled));
        Assert.IsFalse(DemProcessor.IsVoidPixel(-32767f, gdalNoData, optionsDisabled));
    }

    [TestMethod]
    public void IsVoidPixel_RespectsCustomThresholdComparisons()
    {
        var lteOptions = new DemProcessingOptions
        {
            UseGdalNoDataTag = false,
            UseCustomThreshold = true,
            CustomComparison = NoDataComparison.LessThanOrEqual,
            CustomNoDataValue = 0f
        };

        Assert.IsTrue(DemProcessor.IsVoidPixel(-10f, null, lteOptions));
        Assert.IsTrue(DemProcessor.IsVoidPixel(0f, null, lteOptions));
        Assert.IsFalse(DemProcessor.IsVoidPixel(10f, null, lteOptions));

        var eqOptions = new DemProcessingOptions
        {
            UseGdalNoDataTag = false,
            UseCustomThreshold = true,
            CustomComparison = NoDataComparison.Equal,
            CustomNoDataValue = -500f
        };

        Assert.IsTrue(DemProcessor.IsVoidPixel(-500f, null, eqOptions));
        Assert.IsFalse(DemProcessor.IsVoidPixel(-501f, null, eqOptions));

        var gteOptions = new DemProcessingOptions
        {
            UseGdalNoDataTag = false,
            UseCustomThreshold = true,
            CustomComparison = NoDataComparison.GreaterThanOrEqual,
            CustomNoDataValue = 5000f
        };

        Assert.IsTrue(DemProcessor.IsVoidPixel(5000f, null, gteOptions));
        Assert.IsTrue(DemProcessor.IsVoidPixel(6000f, null, gteOptions));
        Assert.IsFalse(DemProcessor.IsVoidPixel(4000f, null, gteOptions));
    }

    [TestMethod]
    public void AnalyzeTiles_CalculatesAccurateElevationStatsAndVoidCount()
    {
        // 4x4 grid: values 10, 20, 30, 40, and 4 void pixels (-9999)
        int width = 4;
        int height = 4;
        float[] rawData = new float[]
        {
            10f, 20f, 30f, 40f,
            10f, -9999f, -9999f, 40f,
            10f, -9999f, -9999f, 40f,
            10f, 20f, 30f, 40f
        };

        var tile = new DemTile
        {
            FilePath = "test_tile.tif",
            Width = width,
            Height = height,
            RawData = (float[])rawData.Clone()
        };

        var options = new DemProcessingOptions
        {
            UseGdalNoDataTag = false,
            UseCustomThreshold = true,
            CustomComparison = NoDataComparison.LessThanOrEqual,
            CustomNoDataValue = 0f
        };

        var (min, max, mean, voidCount) = DemProcessor.AnalyzeTiles(new[] { tile }, options);

        Assert.AreEqual(4, voidCount);
        Assert.AreEqual(4, tile.VoidPixelCount);
        Assert.AreEqual(10f, min, 1e-4f);
        Assert.AreEqual(40f, max, 1e-4f);
        // Valid values: 10*4 + 20*2 + 30*2 + 40*4 = 40 + 40 + 60 + 160 = 300 / 12 = 25
        Assert.AreEqual(25f, mean, 1e-4f);
    }

    [TestMethod]
    public void FillTileVoids_ConstantFill_PopulatesCorrectStatAndPreservesRawData()
    {
        int width = 3;
        int height = 3;
        float[] rawData = new float[]
        {
            10f, 20f, 30f,
            40f, -9999f, 60f,
            70f, 80f, 90f
        };

        var tile = new DemTile
        {
            FilePath = "tile.tif",
            Width = width,
            Height = height,
            RawData = (float[])rawData.Clone()
        };

        var options = new DemProcessingOptions
        {
            FillMethod = FillMethod.Minimum,
            UseGdalNoDataTag = false,
            UseCustomThreshold = true,
            CustomComparison = NoDataComparison.LessThanOrEqual,
            CustomNoDataValue = 0f
        };

        // Fill with Minimum (valid min is 10)
        DemProcessor.FillTileVoids(tile, options, globalMin: 10f, globalMax: 90f, globalMean: 50f);

        // RawData must remain unchanged (Invariant #1)
        Assert.AreEqual(-9999f, tile.RawData[4]);

        // BaseFilledData and FilledData should have 10f at index 4
        Assert.IsNotNull(tile.BaseFilledData);
        Assert.IsNotNull(tile.FilledData);
        Assert.AreEqual(10f, tile.BaseFilledData[4]);
        Assert.AreEqual(10f, tile.FilledData[4]);

        // Fill with Maximum
        options.FillMethod = FillMethod.Maximum;
        DemProcessor.FillTileVoids(tile, options, globalMin: 10f, globalMax: 90f, globalMean: 50f);
        Assert.AreEqual(90f, tile.FilledData[4]);

        // Fill with Mean
        options.FillMethod = FillMethod.Mean;
        DemProcessor.FillTileVoids(tile, options, globalMin: 10f, globalMax: 90f, globalMean: 50f);
        // Mean of 10,20,30,40,60,70,80,90 = 400 / 8 = 50
        Assert.AreEqual(50f, tile.FilledData[4]);
    }

    [TestMethod]
    public void FillTileVoids_Interpolated_SmoothlyFillsVoidHole()
    {
        // 5x5 grid with center 3x3 void, surrounded by border of 100f
        int width = 5;
        int height = 5;
        float[] raw = new float[25];
        for (int y = 0; y < 5; y++)
        {
            for (int x = 0; x < 5; x++)
            {
                if (x == 0 || x == 4 || y == 0 || y == 4)
                    raw[y * 5 + x] = 100f;
                else
                    raw[y * 5 + x] = -9999f;
            }
        }

        var tile = new DemTile
        {
            FilePath = "interp.tif",
            Width = width,
            Height = height,
            RawData = raw
        };

        var options = new DemProcessingOptions
        {
            FillMethod = FillMethod.Interpolated,
            InterpolationIterations = 20,
            UseGdalNoDataTag = false,
            UseCustomThreshold = true,
            CustomComparison = NoDataComparison.LessThanOrEqual,
            CustomNoDataValue = 0f
        };

        DemProcessor.FillTileVoids(tile, options, globalMin: 100f, globalMax: 100f, globalMean: 100f);

        Assert.IsNotNull(tile.FilledData);
        // Center pixel (2,2) should converge close to 100f without NaNs
        float centerVal = tile.FilledData[2 * 5 + 2];
        Assert.IsFalse(float.IsNaN(centerVal));
        Assert.IsTrue(centerVal >= 90f && centerVal <= 110f, $"Center value {centerVal} not within expected range");
    }

    [TestMethod]
    public void ApplyTileSeamSmoothing_ReducesEdgeDiscontinuityAndIsIdempotent()
    {
        // Two adjacent 4x4 tiles: Left at 100m elevation, Right at 200m elevation
        int w = 4, h = 4;
        float[] leftData = new float[16];
        float[] rightData = new float[16];
        Array.Fill(leftData, 100f);
        Array.Fill(rightData, 200f);

        var tileLeft = new DemTile
        {
            FilePath = "left.tif",
            Width = w,
            Height = h,
            GridRow = 0,
            GridCol = 0,
            RawData = (float[])leftData.Clone(),
            BaseFilledData = (float[])leftData.Clone(),
            FilledData = (float[])leftData.Clone()
        };

        var tileRight = new DemTile
        {
            FilePath = "right.tif",
            Width = w,
            Height = h,
            GridRow = 0,
            GridCol = 1,
            RawData = (float[])rightData.Clone(),
            BaseFilledData = (float[])rightData.Clone(),
            FilledData = (float[])rightData.Clone()
        };

        var tiles = new List<DemTile> { tileLeft, tileRight };

        // Before smoothing: seam difference at boundary is 100m (left col 3 is 100, right col 0 is 200)
        float initialDiff = tileRight.FilledData[0] - tileLeft.FilledData[3];
        Assert.AreEqual(100f, initialDiff, 1e-4f);

        // Apply seam smoothing (2 steps)
        DemProcessor.ApplyTileSeamSmoothing(tiles, smoothingSteps: 2);

        // After smoothing: seam step difference should be significantly reduced
        float smoothedDiff = tileRight.FilledData[0] - tileLeft.FilledData[3];
        Assert.IsTrue(smoothedDiff < initialDiff, $"Smoothed diff {smoothedDiff} should be less than initial {initialDiff}");

        // Invariant check: Resetting with smoothingSteps = 0 restores BaseFilledData
        DemProcessor.ApplyTileSeamSmoothing(tiles, smoothingSteps: 0);
        Assert.AreEqual(100f, tileLeft.FilledData[3]);
        Assert.AreEqual(200f, tileRight.FilledData[0]);
    }

    [TestMethod]
    public void BilinearResampling_PreservesLinearRamp()
    {
        // 4x4 single tile mosaic with linear horizontal ramp from 0 to 300
        int w = 4, h = 4;
        float[] ramp = new float[16];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                ramp[y * w + x] = x * 100f; // 0, 100, 200, 300
            }
        }

        var tile = new DemTile
        {
            FilePath = "ramp.tif",
            Width = w,
            Height = h,
            GridRow = 0,
            GridCol = 0,
            RawData = ramp,
            BaseFilledData = ramp,
            FilledData = ramp
        };

        var mosaic = DemProcessor.BuildMosaicDescriptor(new[] { tile }, fallbackElevation: 0f);

        // Resample entire tile to 8x8
        float[] resampled = DemProcessor.ExtractResampledSubTile(
            mosaic,
            subNormX0: 0.0,
            subNormY0: 0.0,
            subNormX1: 1.0,
            subNormY1: 1.0,
            outWidth: 8,
            outHeight: 8);

        Assert.AreEqual(64, resampled.Length);

        // Check monotonicity across any row
        for (int row = 0; row < 8; row++)
        {
            for (int col = 0; col < 7; col++)
            {
                float curr = resampled[row * 8 + col];
                float next = resampled[row * 8 + col + 1];
                Assert.IsTrue(next >= curr, $"Resampled values must be monotonic: curr={curr}, next={next}");
            }
        }
    }

    [TestMethod]
    public void UserSettings_RoundTripSerialization_PreservesValues()
    {
        var settings = new UserSettings
        {
            ThemeIndex = 2,
            MapProvider = "Google Maps",
            UseGdalNoData = false,
            UseCustomThreshold = true,
            ComparisonIndex = 1,
            CustomNoDataValue = "-32768",
            FillMethod = 3,
            InterpolationPasses = 45,
            UseGlobalForConstantFill = false,
            EnablePerTileFillOverride = true,
            SeamSmoothingSteps = 7,
            ExportLayout = 2,
            ExportGridSize = "4",
            ResolutionPresetSetIndex = 1,
            ExportWidth = "2048",
            ExportHeight = "4096",
            LockExportAspectRatio = true,
            ExportFileFormatIndex = 1,
            ExportFolder = @"C:\Test\Export",
            ExportStandardXyNaming = false,
            ExportPrefix = "dem_",
            ExportSuffix = "_post"
        };

        string json = System.Text.Json.JsonSerializer.Serialize(settings);
        var restored = System.Text.Json.JsonSerializer.Deserialize<UserSettings>(json);

        Assert.IsNotNull(restored);
        Assert.AreEqual(settings.ThemeIndex, restored.ThemeIndex);
        Assert.AreEqual(settings.MapProvider, restored.MapProvider);
        Assert.AreEqual(settings.UseGdalNoData, restored.UseGdalNoData);
        Assert.AreEqual(settings.UseCustomThreshold, restored.UseCustomThreshold);
        Assert.AreEqual(settings.ComparisonIndex, restored.ComparisonIndex);
        Assert.AreEqual(settings.CustomNoDataValue, restored.CustomNoDataValue);
        Assert.AreEqual(settings.FillMethod, restored.FillMethod);
        Assert.AreEqual(settings.InterpolationPasses, restored.InterpolationPasses);
        Assert.AreEqual(settings.UseGlobalForConstantFill, restored.UseGlobalForConstantFill);
        Assert.AreEqual(settings.EnablePerTileFillOverride, restored.EnablePerTileFillOverride);
        Assert.AreEqual(settings.SeamSmoothingSteps, restored.SeamSmoothingSteps);
        Assert.AreEqual(settings.ExportLayout, restored.ExportLayout);
        Assert.AreEqual(settings.ExportGridSize, restored.ExportGridSize);
        Assert.AreEqual(settings.ResolutionPresetSetIndex, restored.ResolutionPresetSetIndex);
        Assert.AreEqual(settings.ExportWidth, restored.ExportWidth);
        Assert.AreEqual(settings.ExportHeight, restored.ExportHeight);
        Assert.AreEqual(settings.LockExportAspectRatio, restored.LockExportAspectRatio);
        Assert.AreEqual(settings.ExportFileFormatIndex, restored.ExportFileFormatIndex);
        Assert.AreEqual(settings.ExportFolder, restored.ExportFolder);
        Assert.AreEqual(settings.ExportStandardXyNaming, restored.ExportStandardXyNaming);
        Assert.AreEqual(settings.ExportPrefix, restored.ExportPrefix);
        Assert.AreEqual(settings.ExportSuffix, restored.ExportSuffix);
    }
    [TestMethod]
    public void DeriveLocationName_ExtractsUsgsPatternCorrectly()
    {
        string filename = @"C:\Data\USGS_1M_10_x56y475_OR_SouthEast_D22.tif";
        string result = TiffDemReader.DeriveLocationName(filename);
        Assert.AreEqual("OR_SouthEast_D22", result);

        string generic = @"C:\ElevationData\CraterLake\Tile_001.tif";
        string resultGeneric = TiffDemReader.DeriveLocationName(generic);
        Assert.AreEqual("CraterLake", resultGeneric);
    }

    [TestMethod]
    public void App_StartupUri_PointsToExistingViewsMainWindow()
    {
        var app = new DemCleaner.App();
        app.InitializeComponent();
        Assert.IsNotNull(app.StartupUri);
        Assert.AreEqual("Views/MainWindow.xaml", app.StartupUri.OriginalString);
    }

    [TestMethod]
    public void ComputeExportAreaStats_ExactCalculation_CapturesPeaksBetweenStrides()
    {
        int dim = 1024;
        float[] data = new float[dim * dim];
        Array.Fill(data, 100f);

        // Place an isolated sharp peak at (1, 1), which sits between the 2-pixel stride
        data[1 * dim + 1] = 999f;
        // Place an isolated valley at (3, 3)
        data[3 * dim + 3] = -50f;

        var tile = new DemTile
        {
            FilePath = "test_stride.tif",
            Width = dim,
            Height = dim,
            GridRow = 0,
            GridCol = 0,
            RawData = data,
            BaseFilledData = data,
            FilledData = data
        };

        var mosaic = DemProcessor.BuildMosaicDescriptor(new[] { tile }, fallbackElevation: 0f);

        // Strided interactive calculation skips odd pixels
        var stridedStats = DemProcessor.ComputeExportAreaStats(mosaic, 0.0, 0.0, 1.0, 1.0, exactCalculation: false);
        Assert.AreEqual(100f, stridedStats.MaxElevation, 1e-4f, "Strided calculation should miss off-stride peak");
        Assert.AreEqual(100f, stridedStats.MinElevation, 1e-4f, "Strided calculation should miss off-stride valley");

        // Exact calculation scans all pixels without skipping
        var exactStats = DemProcessor.ComputeExportAreaStats(mosaic, 0.0, 0.0, 1.0, 1.0, exactCalculation: true);
        Assert.AreEqual(999f, exactStats.MaxElevation, 1e-4f, "Exact calculation must capture the true peak");
        Assert.AreEqual(-50f, exactStats.MinElevation, 1e-4f, "Exact calculation must capture the true valley");
    }
}

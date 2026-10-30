using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using DemCleaner.Models;
using DemCleaner.Services;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace DemCleaner;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DemTile> _tiles = new();
    private readonly ObservableCollection<ExpectedOutputFileItem> _expectedOutputs = new();
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _cts3D;
    private float _globalMin = 0f;
    private float _globalMax = 1f;
    private float _globalMean = 0.5f;
    private int _totalVoidPixels = 0;
    private bool _isInitializing = true;
    private bool _isBusy = false;
    private bool _isSyncingSelectedTileCombo = false;
    private bool _isSyncingExportResolutionBoxes = false;
    private bool _matchNativeExportResolution = true;
    private bool _isAuto3DZScale = true;

    // Interactive Viewport Export Area state (normalized [0..1] across the stitched mosaic)
    private double _exportNormX0 = 0.0;
    private double _exportNormY0 = 0.0;
    private double _exportNormX1 = 1.0;
    private double _exportNormY1 = 1.0;
    private ExportAreaStats _exportAreaStats = new(0, 0, 1, 1, 0f, 1f, 0.5f);

    // Viewport Mosaic Dragging & Overlay Canvas state
    private Canvas? _mosaicOverlayCanvas;
    private double _mosaicCanvasWidth = 1.0;
    private double _mosaicCanvasHeight = 1.0;
    private double[] _colDispWidths = Array.Empty<double>();
    private double[] _rowDispHeights = Array.Empty<double>();
    private bool _isMouseDownOnMosaic = false;
    private bool _isDraggingExportArea = false;
    private Point _mosaicDragStartPt;

    // 3D GPU Heightmap Camera State
    private double _camYawDeg = -32.0;
    private double _camPitchDeg = 36.0;
    private double _camDistance = 2.65;
    private Point3D _camTarget = new(0.0, 0.12, 0.0);
    private bool _is3DOrbiting = false;
    private bool _is3DPanning = false;
    private Point _last3DMousePt;
    private readonly Stopwatch _camFrameStopwatch = Stopwatch.StartNew();
    private bool _isCamUpdatePending = false;
    private bool _isSettingsOpen = false;
    private DispatcherTimer? _waterSliderDebounceTimer;

    private bool IsCustomExportAreaActive =>
        Math.Abs(_exportNormX0) > 1e-4 ||
        Math.Abs(_exportNormY0) > 1e-4 ||
        Math.Abs(_exportNormX1 - 1.0) > 1e-4 ||
        Math.Abs(_exportNormY1 - 1.0) > 1e-4;

    public MainWindow()
    {
        InitializeComponent();
        DgTiles.ItemsSource = _tiles;
        DgExpectedOutputs.ItemsSource = _expectedOutputs;

        // Set default designated export folder inside BaseDirectory/ExportedPngs
        string defaultExportDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExportedPngs");
        TxtExportFolder.Text = defaultExportDir;

        TxtExportFolder.TextChanged += ExportNaming_TextChanged;
        TxtExportPrefix.TextChanged += ExportNaming_TextChanged;
        TxtExportSuffix.TextChanged += ExportNaming_TextChanged;
        Grd3DVisualizer.SizeChanged += (_, _) => Update3DScaleBar();

        // Populate Mixed Earthy & Classic Themes
        foreach (var theme in ThemeManager.Themes)
        {
            CmbTheme.Items.Add(theme.Name);
        }
        CmbTheme.SelectedIndex = 0; // Default: Sage & Sandstone (Darker Tan BG / Green Panels)
        ThemeManager.ApplyTheme(Resources, ThemeManager.Themes[0]);

        LoadLicenseTooltipText();
        UpdateExportResolutionPresetButtons();
        Update3DCameraTransform();
        Update3DSunLight();
        UpdateNewExportResolutionSummary();

        _isInitializing = false;
    }

    private void LoadLicenseTooltipText()
    {
        if (TxtLicenseTooltip == null) return;

        string[] candidatePaths =
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LICENSE.txt"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "License.txt"),
            Path.Combine(Environment.CurrentDirectory, "LICENSE.txt"),
            Path.Combine(Environment.CurrentDirectory, "License.txt"),
            Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "LICENSE.txt"))
        };

        foreach (string path in candidatePaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    string licenseContent = File.ReadAllText(path).Trim();
                    if (!string.IsNullOrEmpty(licenseContent))
                    {
                        TxtLicenseTooltip.Text = licenseContent;
                        return;
                    }
                }
            }
            catch
            {
                // Try next path
            }
        }

        TxtLicenseTooltip.Text = "PROPRIETARY SOFTWARE LICENSE\nCopyright (c) 2026 Sipp Studio. All Rights Reserved.\n(LICENSE.txt not found in application directory.)";
    }

    private void CmbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbTheme.SelectedIndex >= 0 && CmbTheme.SelectedIndex < ThemeManager.Themes.Count)
        {
            var palette = ThemeManager.Themes[CmbTheme.SelectedIndex];
            ThemeManager.ApplyTheme(Resources, palette);
            if (!_isInitializing)
            {
                RebuildMosaicGrid();
            }
        }
    }

    private DemProcessingOptions ReadOptionsFromUi()
    {
        var options = new DemProcessingOptions
        {
            UseGdalNoDataTag = ChkUseGdalNoData.IsChecked == true,
            UseCustomThreshold = ChkUseCustomThreshold.IsChecked == true,
            CustomComparison = CmbComparison.SelectedIndex switch
            {
                0 => NoDataComparison.LessThanOrEqual,
                1 => NoDataComparison.Equal,
                2 => NoDataComparison.GreaterThanOrEqual,
                _ => NoDataComparison.LessThanOrEqual
            },
            FillUsingGlobalStats = ChkUseGlobalForConstantFill.IsChecked == true,
            InterpolationIterations = (int)Math.Round(SldIterations.Value),
            SeamSmoothingSteps = (int)Math.Round(SldSeamSmoothingSteps?.Value ?? 0)
        };

        if (float.TryParse(TxtCustomNoData.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float customVal))
        {
            options.CustomNoDataValue = customVal;
        }

        if (RbFillMin.IsChecked == true) options.FillMethod = FillMethod.Minimum;
        else if (RbFillMax.IsChecked == true) options.FillMethod = FillMethod.Maximum;
        else if (RbFillMean.IsChecked == true) options.FillMethod = FillMethod.Mean;
        else options.FillMethod = FillMethod.Interpolated;

        return options;
    }

    private void BtnStopProcessing_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy && _cts != null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            TxtStatusBar.Text = "Stopping processing... please wait.";
            BtnStopSidebar.IsEnabled = false;
        }
    }

    private async void BtnImportFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        var dlg = new OpenFileDialog
        {
            Title = "Select DEM TIFF File(s) — or Navigate into a Folder and Click Open to Import Folder",
            Filter = "TIFF Elevation Files or Folder (*.tif;*.tiff)|*.tif;*.tiff|All Files (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = false,
            CheckPathExists = true,
            ValidateNames = false,
            FileName = "[Import Entire Folder]"
        };

        if (dlg.ShowDialog() != true) return;

        var selectedTiffs = dlg.FileNames
            .Where(f => File.Exists(f) &&
                        (f.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) ||
                         f.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (selectedTiffs.Length > 0)
        {
            await LoadAndProcessFilesAsync(selectedTiffs, append: _tiles.Count > 0);
            return;
        }

        // Otherwise, the user navigated into a folder and clicked Open to import the entire folder
        string? folderPath = Directory.Exists(dlg.FileName)
            ? dlg.FileName
            : Path.GetDirectoryName(dlg.FileName);

        if (!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath))
        {
            var folderFiles = Directory.GetFiles(folderPath)
                .Where(f => f.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (folderFiles.Length == 0)
            {
                MessageBox.Show("No .tif or .tiff files were found in the selected folder.", "No TIFF Files Found",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await LoadAndProcessFilesAsync(folderFiles, append: _tiles.Count > 0);
        }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        _tiles.Clear();
        _exportNormX0 = 0.0;
        _exportNormY0 = 0.0;
        _exportNormX1 = 1.0;
        _exportNormY1 = 1.0;
        _isAuto3DZScale = true;
        UpdateGlobalStatsDisplay();
        RebuildMosaicGrid();
        UpdateInspectorView();
        TxtStatusBar.Text = "Cleared all tiles.";
    }

    private async void BtnRemoveSingleTile_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (sender is Button { Tag: DemTile tileToRemove } && _tiles.Contains(tileToRemove))
        {
            _tiles.Remove(tileToRemove);

            if (_tiles.Count == 0)
            {
                UpdateGlobalStatsDisplay();
                RebuildMosaicGrid();
                UpdateInspectorView();
                TxtStatusBar.Text = $"Removed {tileToRemove.FileName}. No tiles remaining.";
                return;
            }

            TiffDemReader.ArrangeTilesInGrid(_tiles);
            SyncGridDimensionsBoxes();
            await RunProcessingPipelineAsync();
            TxtStatusBar.Text = $"Removed {tileToRemove.FileName}. Recalculated global elevation range across {_tiles.Count} tile(s).";
        }
    }

    private async void ChkEnablePerTileFillOverride_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        bool enabled = ChkEnablePerTileFillOverride.IsChecked == true;
        ColTileFillOverride.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        PnlSelectedTileOverride.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        if (!enabled && _tiles.Any(t => t.FillOverride != TileFillModeOverride.UseGlobalDefault))
        {
            // Reset all tiles back to UseGlobalDefault when unchecked
            foreach (var tile in _tiles)
            {
                tile.FillOverride = TileFillModeOverride.UseGlobalDefault;
            }
            SyncSelectedTileOverrideCombo();
            if (!_isBusy && _tiles.Count > 0)
            {
                await RunProcessingPipelineAsync();
            }
        }
        else
        {
            SyncSelectedTileOverrideCombo();
        }
    }

    private async void RowFillOverrideCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isBusy || _isSyncingSelectedTileCombo) return;
        if (sender is ComboBox { Tag: DemTile tile })
        {
            SyncSelectedTileOverrideCombo();
            await ProcessSingleTileOverrideAsync(tile);
        }
    }

    private async void CmbSelectedTileFillOverride_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isBusy || _isSyncingSelectedTileCombo) return;
        if (DgTiles.SelectedItem is DemTile selected && CmbSelectedTileFillOverride.SelectedIndex >= 0)
        {
            var chosen = (TileFillModeOverride)CmbSelectedTileFillOverride.SelectedIndex;
            if (selected.FillOverride != chosen)
            {
                selected.FillOverride = chosen;
                await ProcessSingleTileOverrideAsync(selected);
            }
        }
    }

    private void SyncSelectedTileOverrideCombo()
    {
        _isSyncingSelectedTileCombo = true;
        try
        {
            if (DgTiles.SelectedItem is DemTile selected)
            {
                CmbSelectedTileFillOverride.SelectedIndex = (int)selected.FillOverride;
                CmbSelectedTileFillOverride.IsEnabled = true;
            }
            else
            {
                CmbSelectedTileFillOverride.SelectedIndex = 0;
                CmbSelectedTileFillOverride.IsEnabled = false;
            }
        }
        finally
        {
            _isSyncingSelectedTileCombo = false;
        }
    }

    private async Task ProcessSingleTileOverrideAsync(DemTile tile)
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        try
        {
            SetBusy(true, $"Applying {tile.ResolveEffectiveFillMethod(ReadOptionsFromUi().FillMethod)} fill to {tile.FileName}...");
            var options = ReadOptionsFromUi();
            float gMin = _globalMin;
            float gMax = _globalMax;
            float gMean = _globalMean;

            var tilesList = _tiles.ToList();
            await Task.Run(() =>
            {
                DemProcessor.FillTileVoids(tile, options, gMin, gMax, gMean, token);
                token.ThrowIfCancellationRequested();
                DemProcessor.ApplyTileSeamSmoothing(tilesList, options.SeamSmoothingSteps, token);
                foreach (var t in tilesList)
                {
                    t.ProcessedThumbnail = DemProcessor.RenderPreviewBitmap(t, showProcessed: true, gMin, gMax, highlightVoidsInRaw: false);
                }
            }, token);

            RebuildMosaicGrid();
            UpdateInspectorView();
            if (RbView3D.IsChecked == true)
            {
                await Rebuild3DVisualizerAsync();
            }
            SetBusy(false, $"Updated fill method for {tile.FileName} ({tile.Status}).");
        }
        catch (OperationCanceledException)
        {
            SetBusy(false, "Tile processing stopped by user.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error updating tile fill:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            SetBusy(false, "Error updating tile.");
        }
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (_isBusy) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        if (e.Data.GetData(DataFormats.FileDrop) is string[] droppedPaths && droppedPaths.Length > 0)
        {
            var tiffFiles = new List<string>();
            foreach (string path in droppedPaths)
            {
                if (Directory.Exists(path))
                {
                    tiffFiles.AddRange(Directory.GetFiles(path)
                        .Where(f => f.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase)));
                }
                else if (File.Exists(path) &&
                         (path.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) ||
                          path.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase)))
                {
                    tiffFiles.Add(path);
                }
            }

            if (tiffFiles.Count > 0)
            {
                await LoadAndProcessFilesAsync(tiffFiles, append: _tiles.Count > 0);
            }
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async Task LoadAndProcessFilesAsync(IEnumerable<string> filePaths, bool append)
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        try
        {
            SetBusy(true, "Loading DEM TIFF tiles...");
            if (!append)
            {
                _tiles.Clear();
                _exportNormX0 = 0.0;
                _exportNormY0 = 0.0;
                _exportNormX1 = 1.0;
                _exportNormY1 = 1.0;
            }

            var existingPaths = _tiles.Select(t => t.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pathsToLoad = filePaths.Where(p => !existingPaths.Contains(p)).ToList();

            var loadedTiles = new List<DemTile>();
            int total = pathsToLoad.Count;
            int count = 0;

            foreach (string path in pathsToLoad)
            {
                token.ThrowIfCancellationRequested();
                count++;
                PbProgress.Value = (count * 50.0) / Math.Max(1, total);
                TxtStatusBar.Text = $"Reading TIFF ({count}/{total}): {Path.GetFileName(path)}...";

                DemTile tile = await Task.Run(() => TiffDemReader.LoadTile(path), token);
                loadedTiles.Add(tile);
            }

            foreach (var tile in loadedTiles)
            {
                _tiles.Add(tile);
            }

            // If designated export folder hasn't been customized yet, default to a subfolder next to the first loaded tile
            if (_tiles.Count > 0 && string.IsNullOrWhiteSpace(TxtExportFolder.Text))
            {
                string? firstDir = Path.GetDirectoryName(_tiles[0].FilePath);
                if (!string.IsNullOrEmpty(firstDir))
                {
                    TxtExportFolder.Text = Path.Combine(firstDir, "Exported_16Bit_PNG");
                }
            }

            TiffDemReader.ArrangeTilesInGrid(_tiles);
            SyncGridDimensionsBoxes();

            await RunProcessingPipelineCoreAsync(token);

            if (_tiles.Count > 0 && DgTiles.SelectedItem == null)
            {
                DgTiles.SelectedIndex = 0;
            }
        }
        catch (OperationCanceledException)
        {
            TiffDemReader.ArrangeTilesInGrid(_tiles);
            UpdateGlobalStatsDisplay();
            RebuildMosaicGrid();
            UpdateInspectorView();
            SetBusy(false, "Processing stopped by user.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error loading TIFF files:\n{ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            SetBusy(false, "Failed to load one or more TIFF files.");
        }
    }

    private async Task RunProcessingPipelineAsync()
    {
        if (_tiles.Count == 0)
        {
            UpdateGlobalStatsDisplay();
            RebuildMosaicGrid();
            UpdateInspectorView();
            SetBusy(false, "Ready.");
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            await RunProcessingPipelineCoreAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            RebuildMosaicGrid();
            UpdateInspectorView();
            SetBusy(false, "Processing stopped by user.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error processing DEM tiles:\n{ex.Message}", "Processing Error", MessageBoxButton.OK, MessageBoxImage.Error);
            SetBusy(false, "Error during DEM processing.");
        }
    }

    private async Task RunProcessingPipelineCoreAsync(CancellationToken token)
    {
        if (_tiles.Count == 0)
        {
            UpdateGlobalStatsDisplay();
            RebuildMosaicGrid();
            UpdateInspectorView();
            SetBusy(false, "Ready.");
            return;
        }

        SetBusy(true, "Analyzing elevation range and filling empty areas...");
        DemProcessingOptions options = ReadOptionsFromUi();
        var tilesList = _tiles.ToList();

        // Step 1: Analyze all tiles for global min/max/mean and void masks
        var stats = await Task.Run(() => DemProcessor.AnalyzeTiles(tilesList, options, token), token);
        _globalMin = stats.GlobalMin;
        _globalMax = stats.GlobalMax;
        _globalMean = stats.GlobalMean;
        _totalVoidPixels = stats.TotalVoidPixels;

        UpdateGlobalStatsDisplay();

        // Step 2: Fill voids on each tile
        int total = tilesList.Count;
        float gMin = _globalMin;
        float gMax = _globalMax;
        float gMean = _globalMean;

        for (int i = 0; i < total; i++)
        {
            token.ThrowIfCancellationRequested();

            var tile = tilesList[i];
            FillMethod effective = tile.ResolveEffectiveFillMethod(options.FillMethod);
            TxtStatusBar.Text = $"Filling voids ({effective}) in tile {i + 1}/{total}: {tile.FileName}...";
            PbProgress.Value = 45.0 + ((i + 1) * 40.0) / total;

            await Task.Run(() =>
            {
                DemProcessor.FillTileVoids(tile, options, gMin, gMax, gMean, token);
            }, token);
        }

        // Step 3: Apply between-tile seam smoothing (if steps > 0) and render thumbnails
        if (options.SeamSmoothingSteps > 0 && tilesList.Count > 1)
        {
            TxtStatusBar.Text = $"Applying {options.SeamSmoothingSteps} between-tile seam smoothing step(s)...";
            PbProgress.Value = 90.0;
        }

        await Task.Run(() =>
        {
            DemProcessor.ApplyTileSeamSmoothing(tilesList, options.SeamSmoothingSteps, token);
            foreach (var tile in tilesList)
            {
                token.ThrowIfCancellationRequested();
                tile.RawThumbnail = DemProcessor.RenderPreviewBitmap(tile, showProcessed: false, gMin, gMax, highlightVoidsInRaw: true);
                tile.ProcessedThumbnail = DemProcessor.RenderPreviewBitmap(tile, showProcessed: true, gMin, gMax, highlightVoidsInRaw: false);
            }
        }, token);

        RebuildMosaicGrid();
        UpdateExportAreaMetricsAndUi();
        UpdateInspectorView();
        if (RbView3D.IsChecked == true)
        {
            await Rebuild3DVisualizerAsync();
        }

        SetBusy(false, $"Processed {_tiles.Count} tile(s). Global Elevation Range: [{_globalMin:F2}, {_globalMax:F2}] mapped to [0, 1].");
    }

    private (float Min, float Max) GetEffectiveNormalizationRange()
    {
        if (_tiles.Count > 0 && IsCustomExportAreaActive)
        {
            return (_exportAreaStats.MinElevation, _exportAreaStats.MaxElevation);
        }
        return (_globalMin, _globalMax);
    }

    private void UpdateExportAreaMetricsAndUi()
    {
        if (_tiles.Count == 0)
        {
            _exportAreaStats = new ExportAreaStats(0, 0, 1, 1, 0f, 1f, 0.5f);
            if (BtnResetExportAreaToolbar != null)
            {
                BtnResetExportAreaToolbar.IsEnabled = false;
                BtnResetExportAreaToolbar.Visibility = RbViewGrid?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            }
            if (Btn3DReturnToGlobal != null)
                Btn3DReturnToGlobal.Visibility = Visibility.Collapsed;
            UpdateNewExportResolutionSummary();
            Update2DScaleBar();
            Update3DScaleBar();
            return;
        }

        var mosaic = DemProcessor.BuildMosaicDescriptor(_tiles, _globalMin);
        _exportAreaStats = DemProcessor.ComputeExportAreaStats(mosaic, _exportNormX0, _exportNormY0, _exportNormX1, _exportNormY1);

        bool isCustom = IsCustomExportAreaActive;

        if (BtnResetExportAreaToolbar != null)
        {
            BtnResetExportAreaToolbar.IsEnabled = isCustom;
            BtnResetExportAreaToolbar.Visibility = RbViewGrid?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
        if (Btn3DReturnToGlobal != null)
            Btn3DReturnToGlobal.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;

        if (isCustom)
        {
            int sidePx = Math.Min(_exportAreaStats.PixelWidth, _exportAreaStats.PixelHeight);
            _exportAreaStats = _exportAreaStats with { PixelWidth = sidePx, PixelHeight = sidePx };
        }

        // Update the header GRID SIZE badge dynamically for either the full grid or the custom defined area
        if (TxtGlobalGridWidth != null && TxtGridSizeLabel != null)
        {
            double metersPerPixel = GetMetersPerPixelFromUi();
            TxtGridSizeLabel.Text = isCustom
                ? $"AREA SIZE ({metersPerPixel:0.##}m/px)"
                : $"GRID SIZE ({metersPerPixel:0.##}m/px)";

            double physicalWidthMeters = _exportAreaStats.PixelWidth * metersPerPixel;
            double physicalHeightMeters = _exportAreaStats.PixelHeight * metersPerPixel;
            TxtGlobalGridWidth.Text = $"{physicalWidthMeters:N0}m × {physicalHeightMeters:N0}m";
        }

        if (_matchNativeExportResolution)
        {
            ApplyNativeExportAreaResolutionToBoxes();
        }
        else if (ChkLockExportAspectRatio?.IsChecked == true)
        {
            AdjustExportHeightForLockedAspectRatio();
        }

        if (_isAuto3DZScale)
        {
            UpdateAuto3DZScaleFromActiveZone();
        }

        UpdateNewExportResolutionSummary();
        Update2DScaleBar();
        Update3DScaleBar();
    }

    private void UpdateGlobalStatsDisplay()
    {
        TxtTileCount.Text = _tiles.Count.ToString();
        double metersPerPixel = GetMetersPerPixelFromUi();
        TxtGridSizeLabel.Text = $"GRID SIZE ({metersPerPixel:0.##}m/px)";

        if (_tiles.Count == 0)
        {
            TxtGlobalMin.Text = "—";
            TxtGlobalMax.Text = "—";
            TxtGlobalHeightRange.Text = "—";
            TxtGlobalGridWidth.Text = "—";
            TxtTotalVoids.Text = "0";
            TxtGridCenterCoords.Text = "No geolocated tiles loaded";
            if (BtnOpenInMap != null) BtnOpenInMap.IsEnabled = false;
            PnlEmptyState.Visibility = Visibility.Visible;
            SvGridMosaic.Visibility = Visibility.Collapsed;
            if (Bdr2DScaleBar != null)
                Bdr2DScaleBar.Visibility = Visibility.Collapsed;
            GrdTileInspector.Visibility = Visibility.Collapsed;
            if (Grd3DVisualizer != null)
                Grd3DVisualizer.Visibility = Visibility.Collapsed;
            if (BtnResetExportAreaToolbar != null)
                BtnResetExportAreaToolbar.Visibility = RbViewGrid?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (BdrGridLeftStatsOverlay != null)
                BdrGridLeftStatsOverlay.Visibility = RbViewGrid?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (PnlViewport3DBar != null)
                PnlViewport3DBar.Visibility = RbView3D?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            UpdateExportAreaMetricsAndUi();
        }
        else
        {
            UpdateExportAreaMetricsAndUi();
            var (dispMin, dispMax) = GetEffectiveNormalizationRange();
            float heightRange = Math.Max(0f, dispMax - dispMin);
            TxtGlobalMin.Text = $"{dispMin:F2}m";
            TxtGlobalMax.Text = $"{dispMax:F2}m";
            TxtGlobalHeightRange.Text = $"{heightRange:F2}m";
            TxtTotalVoids.Text = $"{_totalVoidPixels:N0}";

            double physicalWidthMeters = _exportAreaStats.PixelWidth * metersPerPixel;
            double physicalHeightMeters = _exportAreaStats.PixelHeight * metersPerPixel;
            TxtGlobalGridWidth.Text = $"{physicalWidthMeters:N0}m × {physicalHeightMeters:N0}m";
            PnlEmptyState.Visibility = Visibility.Collapsed;

            if (TiffDemReader.TryGetGridCenterLatLon(_tiles, out _, out _, out string desc))
            {
                TxtGridCenterCoords.Text = desc;
                if (BtnOpenInMap != null) BtnOpenInMap.IsEnabled = true;
            }
            else
            {
                TxtGridCenterCoords.Text = "Coordinates unavailable (non-georeferenced TIFFs)";
                if (BtnOpenInMap != null) BtnOpenInMap.IsEnabled = false;
            }

            bool showGrid = RbViewGrid.IsChecked == true;
            bool showInspector = RbViewInspector.IsChecked == true;
            bool show3D = RbView3D?.IsChecked == true;

            SvGridMosaic.Visibility = showGrid ? Visibility.Visible : Visibility.Collapsed;
            if (Bdr2DScaleBar != null)
                Bdr2DScaleBar.Visibility = showGrid ? Visibility.Visible : Visibility.Collapsed;
            if (BdrGridLeftStatsOverlay != null)
                BdrGridLeftStatsOverlay.Visibility = showGrid ? Visibility.Visible : Visibility.Collapsed;
            GrdTileInspector.Visibility = showInspector ? Visibility.Visible : Visibility.Collapsed;
            if (Grd3DVisualizer != null)
                Grd3DVisualizer.Visibility = show3D ? Visibility.Visible : Visibility.Collapsed;

            if (BtnResetExportAreaToolbar != null)
                BtnResetExportAreaToolbar.Visibility = showGrid ? Visibility.Visible : Visibility.Collapsed;
            if (PnlViewport3DBar != null)
                PnlViewport3DBar.Visibility = show3D ? Visibility.Visible : Visibility.Collapsed;

            Update2DScaleBar();
            Update3DScaleBar();
        }
    }

    private double GetMetersPerPixelFromUi()
    {
        if (TxtMetersPerPixel != null &&
            double.TryParse(TxtMetersPerPixel.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double mpp) &&
            mpp > 0)
        {
            return mpp;
        }
        return 1.0;
    }

    private void TxtMetersPerPixel_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _isAuto3DZScale = true;
        UpdateGlobalStatsDisplay();
    }

    private void TxtMetersPerPixel_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_isInitializing)
        {
            _isAuto3DZScale = true;
            UpdateGlobalStatsDisplay();
        }
    }

    private void BtnAutoScaleFromGeoTiff_Click(object sender, RoutedEventArgs e)
    {
        if (_tiles.Count == 0)
        {
            MessageBox.Show("Load one or more GeoTIFF tiles first to read their pixel scale.", "No Tiles Loaded",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var tileWithScale = _tiles.FirstOrDefault(t => t.PixelScaleX.HasValue && t.PixelScaleX.Value > 0);
        if (tileWithScale != null)
        {
            double sx = tileWithScale.PixelScaleX!.Value;
            // If scale is in geographic degrees (e.g. 0.00001 deg), convert approximately to meters (~111,320 m/deg)
            double meters = sx < 0.01 ? sx * 111320.0 : sx;
            TxtMetersPerPixel.Text = meters.ToString("0.###", CultureInfo.InvariantCulture);
            _isAuto3DZScale = true;
            UpdateGlobalStatsDisplay();
            TxtStatusBar.Text = $"Updated scale from GeoTIFF tag: {meters:0.###} meters/pixel.";
        }
        else
        {
            TxtMetersPerPixel.Text = "1.0";
            _isAuto3DZScale = true;
            UpdateGlobalStatsDisplay();
            TxtStatusBar.Text = "No GeoTIFF ModelPixelScaleTag found; defaulted to 1.0 meter/pixel.";
        }
    }

    private void ChkExportStandardXyNaming_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        bool useStandardXy = ChkExportStandardXyNaming.IsChecked == true;
        GrdExportPrefixRow.Visibility = useStandardXy ? Visibility.Visible : Visibility.Collapsed;
        GrdExportSuffixRow.Visibility = useStandardXy ? Visibility.Collapsed : Visibility.Visible;
        UpdateExpectedOutputFilesList();
    }

    private void ExportNaming_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        UpdateExpectedOutputFilesList();
    }

    private void SyncGridDimensionsBoxes()
    {
        if (_tiles.Count == 0)
        {
            TxtGridRows.Text = "1";
            TxtGridCols.Text = "1";
            return;
        }

        int maxRow = _tiles.Max(t => t.GridRow) + 1;
        int maxCol = _tiles.Max(t => t.GridCol) + 1;
        TxtGridRows.Text = Math.Max(1, maxRow).ToString();
        TxtGridCols.Text = Math.Max(1, maxCol).ToString();
    }

    /// <summary>
    /// Builds the 2D Tile Grid Mosaic flush to the left/top edges of the viewport with Global X/Y coordinate
    /// scale legend badges overlaid directly onto the grid cells, plus an interactive overlay canvas
    /// that lets the user drag to redefine the Export Area anywhere over the grid.
    /// </summary>
    private void RebuildMosaicGrid()
    {
        GrdMosaicWithHeaders.Children.Clear();
        GrdMosaicWithHeaders.RowDefinitions.Clear();
        GrdMosaicWithHeaders.ColumnDefinitions.Clear();
        _mosaicOverlayCanvas = null;

        if (_tiles.Count == 0) return;

        var mosaic = DemProcessor.BuildMosaicDescriptor(_tiles, _globalMin);
        int rows = mosaic.GridRows;
        int cols = mosaic.GridCols;

        // Calculate proportional viewport pixel dimensions for each column and row using a uniform scale
        double targetMaxDim = Math.Clamp(Math.Max(rows, cols) * 235.0, 380.0, 960.0);
        int minCellNativeDim = Math.Max(1, Math.Min(mosaic.ColWidths.Min(), mosaic.RowHeights.Min()));
        double displayScale = Math.Max(
            targetMaxDim / Math.Max(1, Math.Max(mosaic.TotalWidth, mosaic.TotalHeight)),
            95.0 / minCellNativeDim);

        _colDispWidths = new double[cols];
        for (int c = 0; c < cols; c++)
        {
            _colDispWidths[c] = Math.Max(1.0, mosaic.ColWidths[c] * displayScale);
        }

        _rowDispHeights = new double[rows];
        for (int r = 0; r < rows; r++)
        {
            _rowDispHeights[r] = Math.Max(1.0, mosaic.RowHeights[r] * displayScale);
        }

        _mosaicCanvasWidth = Math.Max(1.0, _colDispWidths.Sum());
        _mosaicCanvasHeight = Math.Max(1.0, _rowDispHeights.Sum());

        // Rows 0..rows-1 = Tile Rows (flush to top & left with overlaid scale/coordinate legend badges)
        for (int r = 0; r < rows; r++)
        {
            GrdMosaicWithHeaders.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_rowDispHeights[r], GridUnitType.Pixel) });
        }

        // Cols 0..cols-1 = Tile Columns
        for (int c = 0; c < cols; c++)
        {
            GrdMosaicWithHeaders.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_colDispWidths[c], GridUnitType.Pixel) });
        }

        var bgWellBrush = (Brush)Resources["ThemeBgWell"];
        var bgCardBrush = (Brush)Resources["ThemeBgCard"];
        var borderCardBrush = (Brush)Resources["ThemeBorderCard"];
        var accentPrimaryBrush = (Brush)Resources["ThemeAccentPrimary"];
        var textMutedBrush = (Brush)Resources["ThemeTextMuted"];

        bool showProcessed = ChkHighlightVoidsInRed?.IsChecked != true;
        var selectedTile = DgTiles.SelectedItem as DemTile;
        FillMethod globalDefaultFill = ReadOptionsFromUi().FillMethod;

        var lookup = _tiles
            .GroupBy(t => (t.GridRow, t.GridCol))
            .ToDictionary(g => g.Key, g => g.First());

        // Populate Tile Cells directly at [r, c] with overlaid coordinate scale legend badges
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                var cellGrid = new Grid();

                if (lookup.TryGetValue((r, c), out DemTile? tile))
                {
                    bool isSelected = ReferenceEquals(tile, selectedTile);
                    FillMethod effectiveFill = tile.ResolveEffectiveFillMethod(globalDefaultFill);

                    var img = new Image
                    {
                        Source = showProcessed ? tile.ProcessedThumbnail : tile.RawThumbnail,
                        Stretch = Stretch.Fill
                    };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
                    cellGrid.Children.Add(img);

                    var labelBorder = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(195, 16, 22, 18)),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(5, 2, 5, 2),
                        Margin = new Thickness(5, r == 0 ? 26 : 5, 5, 5),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        IsHitTestVisible = false
                    };

                    string fillBadge = tile.FillOverride != TileFillModeOverride.UseGlobalDefault
                        ? $" [{effectiveFill}]"
                        : string.Empty;

                    var labelText = new TextBlock
                    {
                        Text = $"{tile.ColCoordinateLabel}, {tile.RowCoordinateLabel}{fillBadge} • {tile.FileName}",
                        Foreground = Brushes.White,
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold
                    };
                    labelBorder.Child = labelText;
                    cellGrid.Children.Add(labelBorder);

                    // Overlay Column Header Scale Legend on Top Row (r == 0)
                    if (r == 0)
                    {
                        string colLabel = !string.IsNullOrEmpty(tile.ColCoordinateLabel) ? tile.ColCoordinateLabel : $"Col {c} (X)";
                        var colOverlayBadge = new Border
                        {
                            Background = new SolidColorBrush(Color.FromArgb(205, 14, 20, 16)),
                            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                            BorderThickness = new Thickness(0.75),
                            CornerRadius = new CornerRadius(3),
                            Padding = new Thickness(6, 2, 6, 2),
                            Margin = new Thickness(5, 4, 5, 0),
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Top,
                            IsHitTestVisible = false,
                            Child = new TextBlock
                            {
                                Text = $"COL {c} • {colLabel}",
                                Foreground = accentPrimaryBrush,
                                FontSize = 9.5,
                                FontWeight = FontWeights.Bold
                            }
                        };
                        cellGrid.Children.Add(colOverlayBadge);
                    }

                    // Overlay Row Header Scale Legend on Left Column (c == 0)
                    if (c == 0)
                    {
                        string rowLabel = !string.IsNullOrEmpty(tile.RowCoordinateLabel) ? tile.RowCoordinateLabel : $"Row {r} (Y)";
                        var rowOverlayBadge = new Border
                        {
                            Background = new SolidColorBrush(Color.FromArgb(205, 14, 20, 16)),
                            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                            BorderThickness = new Thickness(0.75),
                            CornerRadius = new CornerRadius(3),
                            Padding = new Thickness(6, 2, 6, 2),
                            Margin = new Thickness(5, 0, 0, 5),
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Center,
                            IsHitTestVisible = false,
                            Child = new TextBlock
                            {
                                Text = $"ROW {r} • {rowLabel}",
                                Foreground = accentPrimaryBrush,
                                FontSize = 9.5,
                                FontWeight = FontWeights.Bold
                            }
                        };
                        cellGrid.Children.Add(rowOverlayBadge);
                    }

                    var cellBorder = new Border
                    {
                        Background = bgWellBrush,
                        BorderBrush = isSelected ? accentPrimaryBrush : borderCardBrush,
                        BorderThickness = new Thickness(isSelected ? 2.0 : 0.75),
                        Margin = new Thickness(0),
                        Tag = tile,
                        Child = cellGrid
                    };
                    Grid.SetRow(cellBorder, r);
                    Grid.SetColumn(cellBorder, c);
                    GrdMosaicWithHeaders.Children.Add(cellBorder);
                }
                else
                {
                    var emptyCell = new Border
                    {
                        Background = bgCardBrush,
                        BorderBrush = borderCardBrush,
                        BorderThickness = new Thickness(0.75),
                        Margin = new Thickness(0),
                        Opacity = 0.65,
                        Child = new TextBlock
                        {
                            Text = $"Empty Slot [{r},{c}]",
                            Foreground = textMutedBrush,
                            FontSize = 11,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    };
                    Grid.SetRow(emptyCell, r);
                    Grid.SetColumn(emptyCell, c);
                    GrdMosaicWithHeaders.Children.Add(emptyCell);
                }
            }
        }

        // Interactive Overlay Canvas spanning all tile cells (Rows 0..rows-1, Cols 0..cols-1) for Export Area Dragging & Tile Selection
        _mosaicOverlayCanvas = new Canvas
        {
            Width = _mosaicCanvasWidth,
            Height = _mosaicCanvasHeight,
            Background = Brushes.Transparent,
            Cursor = Cursors.Cross,
            ClipToBounds = true,
            ToolTip = "Click and drag to redefine the Export Area • Click a tile to select • Double-click to open Individual Tile Inspector"
        };

        _mosaicOverlayCanvas.MouseLeftButtonDown += MosaicOverlayCanvas_MouseLeftButtonDown;
        _mosaicOverlayCanvas.MouseMove += MosaicOverlayCanvas_MouseMove;
        _mosaicOverlayCanvas.MouseLeftButtonUp += MosaicOverlayCanvas_MouseLeftButtonUp;

        Grid.SetRow(_mosaicOverlayCanvas, 0);
        Grid.SetColumn(_mosaicOverlayCanvas, 0);
        Grid.SetRowSpan(_mosaicOverlayCanvas, rows);
        Grid.SetColumnSpan(_mosaicOverlayCanvas, cols);
        Panel.SetZIndex(_mosaicOverlayCanvas, 20);
        GrdMosaicWithHeaders.Children.Add(_mosaicOverlayCanvas);

        RenderExportAreaOverlay();
        Update2DScaleBar();
    }

    private void MosaicOverlayCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_mosaicOverlayCanvas == null || _tiles.Count == 0) return;

        if (e.ClickCount >= 2)
        {
            // Double-click opens Before/After Inspector for the clicked tile
            Point clickPt = e.GetPosition(_mosaicOverlayCanvas);
            SelectTileAtCanvasPoint(clickPt);
            RbViewInspector.IsChecked = true;
            e.Handled = true;
            return;
        }

        _mosaicDragStartPt = e.GetPosition(_mosaicOverlayCanvas);
        _mosaicDragStartPt.X = Math.Clamp(_mosaicDragStartPt.X, 0.0, _mosaicCanvasWidth);
        _mosaicDragStartPt.Y = Math.Clamp(_mosaicDragStartPt.Y, 0.0, _mosaicCanvasHeight);
        _isMouseDownOnMosaic = true;
        _isDraggingExportArea = false;
        _mosaicOverlayCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void MosaicOverlayCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isMouseDownOnMosaic || _mosaicOverlayCanvas == null || _tiles.Count == 0) return;

        Point curPt = e.GetPosition(_mosaicOverlayCanvas);
        curPt.X = Math.Clamp(curPt.X, 0.0, _mosaicCanvasWidth);
        curPt.Y = Math.Clamp(curPt.Y, 0.0, _mosaicCanvasHeight);

        double dx = curPt.X - _mosaicDragStartPt.X;
        double dy = curPt.Y - _mosaicDragStartPt.Y;

        if (!_isDraggingExportArea && Math.Sqrt(dx * dx + dy * dy) >= 4.0)
        {
            _isDraggingExportArea = true;
        }

        if (_isDraggingExportArea)
        {
            ApplyDragPointsToExportArea(_mosaicDragStartPt, curPt);
            UpdateExportAreaMetricsAndUi();
            RenderExportAreaOverlay();
        }
    }

    private void MosaicOverlayCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isMouseDownOnMosaic || _mosaicOverlayCanvas == null) return;

        Point releasePt = e.GetPosition(_mosaicOverlayCanvas);
        releasePt.X = Math.Clamp(releasePt.X, 0.0, _mosaicCanvasWidth);
        releasePt.Y = Math.Clamp(releasePt.Y, 0.0, _mosaicCanvasHeight);

        bool wasDragging = _isDraggingExportArea;
        _isMouseDownOnMosaic = false;
        _isDraggingExportArea = false;
        _mosaicOverlayCanvas.ReleaseMouseCapture();

        if (wasDragging)
        {
            _isAuto3DZScale = true;
            ApplyDragPointsToExportArea(_mosaicDragStartPt, releasePt);
            UpdateGlobalStatsDisplay();
            RenderExportAreaOverlay();
            TxtStatusBar.Text =
                $"Redefined Export Area: X={_exportAreaStats.PixelX}..{_exportAreaStats.PixelX + _exportAreaStats.PixelWidth} px, " +
                $"Y={_exportAreaStats.PixelY}..{_exportAreaStats.PixelY + _exportAreaStats.PixelHeight} px " +
                $"({_exportAreaStats.PixelWidth} × {_exportAreaStats.PixelHeight} px) • Elev [{_exportAreaStats.MinElevation:F2}m .. {_exportAreaStats.MaxElevation:F2}m]";
        }
        else
        {
            SelectTileAtCanvasPoint(releasePt);
        }

        e.Handled = true;
    }

    /// <summary>
    /// Applies the user's drag gesture to the Export Area while locking the bounding box to a 1:1 square aspect ratio.
    /// </summary>
    private void ApplyDragPointsToExportArea(Point p0, Point p1)
    {
        double canvasW = Math.Max(1.0, _mosaicCanvasWidth);
        double canvasH = Math.Max(1.0, _mosaicCanvasHeight);

        double dx = p1.X - p0.X;
        double dy = p1.Y - p0.Y;
        double signX = dx >= 0 ? 1.0 : -1.0;
        double signY = dy >= 0 ? 1.0 : -1.0;

        // Lock bounding box to a 1:1 square aspect ratio
        double requestedSide = Math.Max(Math.Abs(dx), Math.Abs(dy));
        double maxSideX = signX >= 0 ? (canvasW - p0.X) : p0.X;
        double maxSideY = signY >= 0 ? (canvasH - p0.Y) : p0.Y;
        double maxAllowedSide = Math.Min(maxSideX, maxSideY);

        double side = Math.Clamp(requestedSide, 4.0, Math.Max(4.0, maxAllowedSide));

        double x0 = signX >= 0 ? p0.X : p0.X - side;
        double x1 = signX >= 0 ? p0.X + side : p0.X;
        double y0 = signY >= 0 ? p0.Y : p0.Y - side;
        double y1 = signY >= 0 ? p0.Y + side : p0.Y;

        // Ensure square fits within [0..canvasW, 0..canvasH]
        if (x0 < 0) { x1 = Math.Min(canvasW, x1 - x0); x0 = 0; }
        if (y0 < 0) { y1 = Math.Min(canvasH, y1 - y0); y0 = 0; }
        if (x1 > canvasW) { x0 = Math.Max(0, x0 - (x1 - canvasW)); x1 = canvasW; }
        if (y1 > canvasH) { y0 = Math.Max(0, y0 - (y1 - canvasH)); y1 = canvasH; }

        double finalSide = Math.Min(x1 - x0, y1 - y0);
        x1 = x0 + finalSide;
        y1 = y0 + finalSide;

        double nx0 = Math.Clamp(x0 / canvasW, 0.0, 0.995);
        double ny0 = Math.Clamp(y0 / canvasH, 0.0, 0.995);
        double nx1 = Math.Clamp(x1 / canvasW, nx0 + 0.005, 1.0);
        double ny1 = Math.Clamp(y1 / canvasH, ny0 + 0.005, 1.0);

        _exportNormX0 = nx0;
        _exportNormY0 = ny0;
        _exportNormX1 = nx1;
        _exportNormY1 = ny1;
    }

    private void SelectTileAtCanvasPoint(Point pt)
    {
        if (_colDispWidths.Length == 0 || _rowDispHeights.Length == 0) return;

        int col = 0;
        double accumX = 0;
        for (int c = 0; c < _colDispWidths.Length; c++)
        {
            accumX += _colDispWidths[c];
            if (pt.X <= accumX) { col = c; break; }
            col = c;
        }

        int row = 0;
        double accumY = 0;
        for (int r = 0; r < _rowDispHeights.Length; r++)
        {
            accumY += _rowDispHeights[r];
            if (pt.Y <= accumY) { row = r; break; }
            row = r;
        }

        var clickedTile = _tiles.FirstOrDefault(t => t.GridRow == row && t.GridCol == col);
        if (clickedTile != null)
        {
            DgTiles.SelectedItem = clickedTile;
            DgTiles.ScrollIntoView(clickedTile);
        }
    }

    /// <summary>
    /// Draws the interactive Export Area selection box, exterior dimming mask, custom R×C output grid subdivision lines,
    /// corner handles, and dimensions badge on top of the 2D Tile Grid Mosaic.
    /// </summary>
    private void RenderExportAreaOverlay()
    {
        if (_mosaicOverlayCanvas == null || _tiles.Count == 0) return;
        _mosaicOverlayCanvas.Children.Clear();

        double w = _mosaicCanvasWidth;
        double h = _mosaicCanvasHeight;
        if (w <= 1 || h <= 1) return;

        double rx0 = _exportNormX0 * w;
        double ry0 = _exportNormY0 * h;
        double rx1 = _exportNormX1 * w;
        double ry1 = _exportNormY1 * h;
        double rw = Math.Max(2.0, rx1 - rx0);
        double rh = Math.Max(2.0, ry1 - ry0);

        bool isCustomArea = IsCustomExportAreaActive;
        var (outRows, outCols) = GetNewExportGridDimensionsFromUi();
        bool showSubGrid = outRows > 1 || outCols > 1;

        // 1. Exterior dimmed shade rectangles when a sub-area is active
        if (isCustomArea)
        {
            var dimBrush = new SolidColorBrush(Color.FromArgb(135, 8, 12, 10));

            if (ry0 > 0)
                AddOverlayRect(0, 0, w, ry0, dimBrush);
            if (ry1 < h)
                AddOverlayRect(0, ry1, w, h - ry1, dimBrush);
            if (rx0 > 0)
                AddOverlayRect(0, ry0, rx0, rh, dimBrush);
            if (rx1 < w)
                AddOverlayRect(rx1, ry0, w - rx1, rh, dimBrush);
        }

        // 2. Interior subdivision grid lines when Custom Grid Size (R × C) is selected for new exports
        if (showSubGrid)
        {
            var subGridStroke = new SolidColorBrush(Color.FromArgb(185, 232, 168, 73));
            for (int c = 1; c < outCols; c++)
            {
                double xLine = rx0 + rw * (c / (double)outCols);
                var vLine = new Line
                {
                    X1 = xLine,
                    Y1 = ry0,
                    X2 = xLine,
                    Y2 = ry1,
                    Stroke = subGridStroke,
                    StrokeThickness = 1.4,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                    IsHitTestVisible = false
                };
                _mosaicOverlayCanvas.Children.Add(vLine);
            }

            for (int r = 1; r < outRows; r++)
            {
                double yLine = ry0 + rh * (r / (double)outRows);
                var hLine = new Line
                {
                    X1 = rx0,
                    Y1 = yLine,
                    X2 = rx1,
                    Y2 = yLine,
                    Stroke = subGridStroke,
                    StrokeThickness = 1.4,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                    IsHitTestVisible = false
                };
                _mosaicOverlayCanvas.Children.Add(hLine);
            }
        }

        // 3. Export Area Border Outline & Corner Handles (drawn when custom area is active or custom export grid > 1x1)
        if (isCustomArea || showSubGrid)
        {
            var accentBrush = (Brush)Resources["ThemeAccentExport"];
            var primaryBrush = (Brush)Resources["ThemeAccentPrimary"];
            Brush outlineBrush = isCustomArea ? primaryBrush : accentBrush;

            var selRect = new Rectangle
            {
                Width = rw,
                Height = rh,
                Stroke = outlineBrush,
                StrokeThickness = 2.5,
                Fill = new SolidColorBrush(Color.FromArgb(18, 232, 168, 73)),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(selRect, rx0);
            Canvas.SetTop(selRect, ry0);
            _mosaicOverlayCanvas.Children.Add(selRect);

            // Corner Handles
            const double handleSize = 8.0;
            Point[] corners =
            {
                new(rx0, ry0),
                new(rx1 - handleSize, ry0),
                new(rx0, ry1 - handleSize),
                new(rx1 - handleSize, ry1)
            };
            foreach (var pt in corners)
            {
                var handle = new Rectangle
                {
                    Width = handleSize,
                    Height = handleSize,
                    Fill = outlineBrush,
                    Stroke = Brushes.White,
                    StrokeThickness = 1,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(handle, Math.Clamp(pt.X, 0, Math.Max(0, w - handleSize)));
                Canvas.SetTop(handle, Math.Clamp(pt.Y, 0, Math.Max(0, h - handleSize)));
                _mosaicOverlayCanvas.Children.Add(handle);
            }

            // Floating Export Area Badge at bottom-right of selection
            var (resW, resH) = GetNewExportFileResolutionFromUi();
            string gridTag = outRows == 1 && outCols == 1 ? "Single Image (1×1)" : $"{outRows}×{outCols} Export Grid";
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(220, 18, 28, 22)),
                BorderBrush = outlineBrush,
                BorderThickness = new Thickness(1.2),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 3, 7, 3),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = $"EXPORT AREA: {_exportAreaStats.PixelWidth}×{_exportAreaStats.PixelHeight} px • {gridTag} ({resW}×{resH} px/file)",
                    Foreground = Brushes.White,
                    FontSize = 10.5,
                    FontWeight = FontWeights.Bold
                }
            };
            Canvas.SetLeft(badge, Math.Max(4, rx0 + 6));
            Canvas.SetTop(badge, Math.Max(4, ry1 - 28));
            _mosaicOverlayCanvas.Children.Add(badge);
        }
    }

    private void AddOverlayRect(double x, double y, double w, double h, Brush fill)
    {
        if (_mosaicOverlayCanvas == null || w <= 0 || h <= 0) return;
        var rect = new Rectangle
        {
            Width = w,
            Height = h,
            Fill = fill,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        _mosaicOverlayCanvas.Children.Add(rect);
    }

    private async void BtnResetExportArea_Click(object sender, RoutedEventArgs e)
    {
        _exportNormX0 = 0.0;
        _exportNormY0 = 0.0;
        _exportNormX1 = 1.0;
        _exportNormY1 = 1.0;
        _isAuto3DZScale = true;
        UpdateGlobalStatsDisplay();
        RenderExportAreaOverlay();
        if (RbView3D?.IsChecked == true && _tiles.Count > 0)
        {
            await Rebuild3DVisualizerAsync();
        }
        TxtStatusBar.Text = "Reset Export Area to Full Global Grid (100% × 100%).";
    }

    private void UpdateInspectorView()
    {
        SyncSelectedTileOverrideCombo();

        if (DgTiles.SelectedItem is DemTile selected)
        {
            FillMethod effective = selected.ResolveEffectiveFillMethod(ReadOptionsFromUi().FillMethod);
            ImgInspectorRaw.Source = selected.RawThumbnail;
            ImgInspectorProcessed.Source = selected.ProcessedThumbnail;
            TxtInspectorAfterHeader.Text =
                $"AFTER: {selected.FileName} — FILLED ({effective.ToString().ToUpperInvariant()}) & NORMALIZED TO GLOBAL [{_globalMin:F2} .. {_globalMax:F2}] → [0 .. 1]";
        }
        else if (_tiles.Count > 0)
        {
            var first = _tiles[0];
            ImgInspectorRaw.Source = first.RawThumbnail;
            ImgInspectorProcessed.Source = first.ProcessedThumbnail;
        }
        else
        {
            ImgInspectorRaw.Source = null;
            ImgInspectorProcessed.Source = null;
        }
    }

    private void BtnOpenInMap_Click(object sender, RoutedEventArgs e)
    {
        if (!TiffDemReader.TryGetGridCenterLatLon(_tiles, out double lat, out double lon, out _))
        {
            MessageBox.Show("Could not determine geographic coordinates from the loaded tiles.",
                "Coordinates Unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string latStr = lat.ToString("F6", CultureInfo.InvariantCulture);
        string lonStr = lon.ToString("F6", CultureInfo.InvariantCulture);
        bool useEarth = RbMapProviderGoogleEarth?.IsChecked == true;
        string url = useEarth
            ? $"https://earth.google.com/web/@{latStr},{lonStr},1500a,25000d,35y,0h,0t,0r"
            : $"https://www.google.com/maps/search/?api=1&query={latStr},{lonStr}";
        OpenUrlInBrowser(url);
    }

    private void MapProvider_Changed(object sender, RoutedEventArgs e)
    {
        if (BtnOpenInMap == null) return;
        bool useEarth = RbMapProviderGoogleEarth?.IsChecked == true;
        BtnOpenInMap.Content = useEarth ? "🌍 Open in Map (Earth)" : "🌐 Open in Map";
    }

    private void BtnToggleSettings_Click(object sender, RoutedEventArgs e)
    {
        AnimateSettingsPopout(!_isSettingsOpen);
    }

    private void BtnCloseSettings_Click(object sender, RoutedEventArgs e)
    {
        AnimateSettingsPopout(false);
    }

    private void AnimateSettingsPopout(bool open)
    {
        if (BdrSettingsPopout == null || ScaleSettingsPopout == null || TransSettingsPopout == null) return;
        _isSettingsOpen = open;

        var duration = new Duration(TimeSpan.FromMilliseconds(180));
        var ease = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn };

        if (open)
        {
            BdrSettingsPopout.Visibility = Visibility.Visible;
            BdrSettingsPopout.IsHitTestVisible = true;
            BdrSettingsPopout.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, duration) { EasingFunction = ease });
            ScaleSettingsPopout.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1.0, duration) { EasingFunction = ease });
            ScaleSettingsPopout.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1.0, duration) { EasingFunction = ease });
            TransSettingsPopout.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-10.0, 0.0, duration) { EasingFunction = ease });
        }
        else
        {
            BdrSettingsPopout.IsHitTestVisible = false;
            var fadeOut = new DoubleAnimation(BdrSettingsPopout.Opacity, 0.0, duration) { EasingFunction = ease };
            fadeOut.Completed += (_, _) =>
            {
                if (!_isSettingsOpen)
                {
                    BdrSettingsPopout.Visibility = Visibility.Collapsed;
                }
            };
            BdrSettingsPopout.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            ScaleSettingsPopout.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 0.92, duration) { EasingFunction = ease });
            ScaleSettingsPopout.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 0.92, duration) { EasingFunction = ease });
            TransSettingsPopout.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0.0, -10.0, duration) { EasingFunction = ease });
        }
    }

    private static void OpenUrlInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open browser:\n{ex.Message}", "Browser Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnProcessAll_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _tiles.Count == 0) return;
        await RunProcessingPipelineAsync();
    }

    private async void DetectionSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isBusy || _tiles.Count == 0) return;
        await RunProcessingPipelineAsync();
    }

    private async void TxtCustomNoData_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isBusy || _tiles.Count == 0) return;
        await RunProcessingPipelineAsync();
    }

    private async void TxtCustomNoData_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_isInitializing && !_isBusy && _tiles.Count > 0)
        {
            await RunProcessingPipelineAsync();
        }
    }

    private async void FillSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (BdrInterpolatedNote != null)
        {
            BdrInterpolatedNote.Visibility = RbFillInterpolated?.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (_isInitializing || _isBusy || _tiles.Count == 0) return;
        await RunProcessingPipelineAsync();
    }

    private void SldIterations_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Users can click "Re-Calculate & Fill All Tiles" or change radio buttons to re-run
    }

    private void SldSeamSmoothingSteps_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtSeamSmoothingStepsLabel == null) return;
        int steps = (int)Math.Round(SldSeamSmoothingSteps.Value);
        TxtSeamSmoothingStepsLabel.Text = steps <= 0 ? "0 steps (Off)" : $"{steps} step{(steps == 1 ? "" : "s")}";
    }

    private async void BtnApplySeamSmoothing_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isBusy || _tiles.Count == 0) return;
        await ApplySeamSmoothingOnlyAsync();
    }

    private async Task ApplySeamSmoothingOnlyAsync()
    {
        if (_tiles.Count == 0) return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        int steps = (int)Math.Round(SldSeamSmoothingSteps?.Value ?? 0);
        var tilesList = _tiles.ToList();
        float gMin = _globalMin;
        float gMax = _globalMax;

        try
        {
            SetBusy(true, steps > 0
                ? $"Applying {steps} between-tile seam smoothing step(s)..."
                : "Restoring un-smoothed tile boundaries...");

            await Task.Run(() =>
            {
                DemProcessor.ApplyTileSeamSmoothing(tilesList, steps, token);
                foreach (var tile in tilesList)
                {
                    token.ThrowIfCancellationRequested();
                    tile.ProcessedThumbnail = DemProcessor.RenderPreviewBitmap(tile, showProcessed: true, gMin, gMax, highlightVoidsInRaw: false);
                }
            }, token);

            RebuildMosaicGrid();
            UpdateExportAreaMetricsAndUi();
            UpdateInspectorView();
            if (RbView3D?.IsChecked == true)
            {
                await Rebuild3DVisualizerAsync();
            }

            SetBusy(false, steps > 0
                ? $"Applied {steps} between-tile seam smoothing step(s) across {_tiles.Count} tile(s)."
                : "Between-tile seam smoothing disabled (0 steps).");
        }
        catch (OperationCanceledException)
        {
            SetBusy(false, "Seam smoothing stopped by user.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error applying between-tile seam smoothing:\n{ex.Message}", "Seam Smoothing Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            SetBusy(false, "Error applying seam smoothing.");
        }
    }

    private async void BtnApplyGridDimensions_Click(object sender, RoutedEventArgs e)
    {
        if (_tiles.Count == 0) return;

        if (int.TryParse(TxtGridRows.Text, out int rows) && rows > 0 &&
            int.TryParse(TxtGridCols.Text, out int cols) && cols > 0)
        {
            for (int i = 0; i < _tiles.Count; i++)
            {
                int r = (i / cols) % Math.Max(rows, (_tiles.Count + cols - 1) / cols);
                int c = i % cols;
                _tiles[i].GridRow = r;
                _tiles[i].GridCol = c;
                if (string.IsNullOrEmpty(_tiles[i].ColCoordinateLabel))
                    _tiles[i].ColCoordinateLabel = $"X: {c}";
                if (string.IsNullOrEmpty(_tiles[i].RowCoordinateLabel))
                    _tiles[i].RowCoordinateLabel = $"Y: {r}";
            }
            SyncGridDimensionsBoxes();
            UpdateGlobalStatsDisplay();
            RebuildMosaicGrid();
            if (RbView3D?.IsChecked == true)
            {
                await Rebuild3DVisualizerAsync();
            }
        }
        else
        {
            MessageBox.Show("Please enter positive integers for Grid Rows and Columns.", "Invalid Grid Dimensions",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void PreviewMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        UpdateGlobalStatsDisplay();
        if (RbView3D?.IsChecked == true && _tiles.Count > 0)
        {
            await Rebuild3DVisualizerAsync();
        }
    }

    private async void PreviewLayer_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        RebuildMosaicGrid();
        if (RbView3D?.IsChecked == true && _tiles.Count > 0)
        {
            await Rebuild3DVisualizerAsync();
        }
    }

    private void DgTiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        UpdateInspectorView();
        RebuildMosaicGrid();
    }

    private void DgTiles_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            SyncGridDimensionsBoxes();
            UpdateGlobalStatsDisplay();
            RebuildMosaicGrid();
            if (RbView3D?.IsChecked == true && _tiles.Count > 0)
            {
                await Rebuild3DVisualizerAsync();
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void BtnSetExportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select Designated Output Folder for Exports"
        };

        if (!string.IsNullOrWhiteSpace(TxtExportFolder.Text) && Directory.Exists(TxtExportFolder.Text))
        {
            dlg.InitialDirectory = TxtExportFolder.Text;
        }

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            TxtExportFolder.Text = dlg.FolderName;
            TxtStatusBar.Text = $"Designated export folder set to: {dlg.FolderName}";
        }
    }

    private void BtnOpenExportFolder_Click(object sender, RoutedEventArgs e)
    {
        string folderPath = TxtExportFolder.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            MessageBox.Show("Please set a designated export folder first.", "No Export Folder Set",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = folderPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open export folder in Windows Explorer:\n{ex.Message}", "Error Opening Folder",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private (int Rows, int Cols) GetNewExportGridDimensionsFromUi()
    {
        if (RbExportSingleImage?.IsChecked == true)
        {
            return (1, 1);
        }

        int gridSize = 2;
        if (TxtNewExportGridSize != null && int.TryParse(TxtNewExportGridSize.Text, out int n) && n > 0)
            gridSize = Math.Clamp(n, 1, 64);

        return (gridSize, gridSize);
    }

    private (int Width, int Height) GetNewExportFileResolutionFromUi()
    {
        int w = 1024;
        int h = 1024;
        if (TxtNewExportWidth != null && int.TryParse(TxtNewExportWidth.Text, out int pw) && pw > 0)
            w = Math.Max(1, pw);
        if (TxtNewExportHeight != null && int.TryParse(TxtNewExportHeight.Text, out int ph) && ph > 0)
            h = Math.Max(1, ph);
        return (w, h);
    }

    private void ApplyNativeExportAreaResolutionToBoxes()
    {
        if (_tiles.Count == 0 || TxtNewExportWidth == null || TxtNewExportHeight == null) return;

        var (outRows, outCols) = GetNewExportGridDimensionsFromUi();
        int fileW = Math.Max(1, (int)Math.Round(_exportAreaStats.PixelWidth / (double)outCols));
        int fileH = Math.Max(1, (int)Math.Round(_exportAreaStats.PixelHeight / (double)outRows));

        _isSyncingExportResolutionBoxes = true;
        try
        {
            TxtNewExportWidth.Text = fileW.ToString(CultureInfo.InvariantCulture);
            TxtNewExportHeight.Text = fileH.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _isSyncingExportResolutionBoxes = false;
        }
    }

    private void AdjustExportHeightForLockedAspectRatio()
    {
        if (_tiles.Count == 0 || TxtNewExportWidth == null || TxtNewExportHeight == null) return;
        if (!int.TryParse(TxtNewExportWidth.Text, out int w) || w <= 0) return;

        var (outRows, outCols) = GetNewExportGridDimensionsFromUi();
        double tileSrcW = Math.Max(1.0, _exportAreaStats.PixelWidth / (double)outCols);
        double tileSrcH = Math.Max(1.0, _exportAreaStats.PixelHeight / (double)outRows);
        int h = Math.Max(1, (int)Math.Round(w * (tileSrcH / tileSrcW)));

        _isSyncingExportResolutionBoxes = true;
        try
        {
            TxtNewExportHeight.Text = h.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _isSyncingExportResolutionBoxes = false;
        }
    }

    private static long ComputeGcd(long a, long b)
    {
        a = Math.Abs(a);
        b = Math.Abs(b);
        while (b != 0)
        {
            long t = b;
            b = a % b;
            a = t;
        }
        return Math.Max(1L, a);
    }

    private static string FormatSimplifiedRatio(long exportPx, long sourcePx)
    {
        if (exportPx <= 0 || sourcePx <= 0) return "1:1";
        if (exportPx == sourcePx) return "1:1";

        long gcd = ComputeGcd(exportPx, sourcePx);
        long simpExp = exportPx / gcd;
        long simpSrc = sourcePx / gcd;

        // If the exact integer ratio simplifies to clean numbers (e.g. 1:1, 1:2, 2:1, 1:4, 3:4), show it directly
        if (simpExp <= 64 && simpSrc <= 64)
        {
            return $"{simpExp}:{simpSrc}";
        }

        // Otherwise normalize the smaller side to 1 so the ratio is always simplified (e.g. 1:14.7 or 2.5:1)
        if (exportPx >= sourcePx)
        {
            double r = exportPx / (double)sourcePx;
            return Math.Abs(r - Math.Round(r)) < 0.05
                ? $"{Math.Round(r):0}:1"
                : $"{r:0.##}:1";
        }
        else
        {
            double inv = sourcePx / (double)exportPx;
            return Math.Abs(inv - Math.Round(inv)) < 0.05
                ? $"1:{Math.Round(inv):0}"
                : $"1:{inv:0.##}";
        }
    }

    private void UpdateNewExportResolutionSummary()
    {
        if (TxtNewExportResolutionSummary == null) return;

        var (rows, cols) = GetNewExportGridDimensionsFromUi();
        var (fileW, fileH) = GetNewExportFileResolutionFromUi();
        int totalFiles = rows * cols;
        long totalPxW = (long)fileW * cols;
        long totalPxH = (long)fileH * rows;

        string ratioText = "1:1";
        if (_tiles.Count > 0 && _exportAreaStats.PixelWidth > 0 && _exportAreaStats.PixelHeight > 0)
        {
            double rx = totalPxW / (double)_exportAreaStats.PixelWidth;
            double ry = totalPxH / (double)_exportAreaStats.PixelHeight;
            string simpW = FormatSimplifiedRatio(totalPxW, _exportAreaStats.PixelWidth);
            string simpH = FormatSimplifiedRatio(totalPxH, _exportAreaStats.PixelHeight);
            ratioText = Math.Abs(rx - ry) < 0.01
                ? $"{simpW} ({rx:0.##}×)"
                : $"{simpW} W / {simpH} H";
        }

        if (totalFiles == 1)
        {
            TxtNewExportResolutionSummary.Text =
                $"Output: 1 Single File • {fileW} × {fileH} px • Export/Source Ratio: {ratioText}";
        }
        else
        {
            TxtNewExportResolutionSummary.Text =
                $"Output: {rows} × {cols} Grid • {fileW} × {fileH} px/file ({totalPxW} × {totalPxH} px total) • Export/Source Ratio: {ratioText}";
        }

        UpdateExpectedOutputFilesList();
    }

    private void BottomTableTab_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || DgTiles == null || DgExpectedOutputs == null) return;

        bool showLoaded = RbTabLoadedTiles?.IsChecked == true;
        DgTiles.Visibility = showLoaded ? Visibility.Visible : Visibility.Collapsed;
        DgExpectedOutputs.Visibility = showLoaded ? Visibility.Collapsed : Visibility.Visible;

        if (TxtBottomTableHint != null)
        {
            TxtBottomTableHint.Text = showLoaded
                ? "Use '✕' to remove a tile • Click a row to inspect"
                : $"{_expectedOutputs.Count} expected output file(s) based on current Export Layout, Resolution & Format";
        }
    }

    private void UpdateExpectedOutputFilesList()
    {
        _expectedOutputs.Clear();

        if (_tiles.Count == 0)
        {
            if (RbTabLoadedTiles != null) RbTabLoadedTiles.Content = "LOADED DEM TILES (0)";
            if (RbTabExpectedOutputs != null) RbTabExpectedOutputs.Content = "EXPECTED OUTPUT FILES (0)";
            if (TxtBottomTableHint != null && RbTabExpectedOutputs?.IsChecked == true)
                TxtBottomTableHint.Text = "0 expected output files (import DEM tiles first)";
            return;
        }

        var (outRows, outCols) = GetNewExportGridDimensionsFromUi();
        var (fileWidth, fileHeight) = GetNewExportFileResolutionFromUi();
        int totalFiles = outRows * outCols;

        int fmtIdx = CmbExportFileFormat?.SelectedIndex ?? 0;
        bool exportAsTiff = fmtIdx == 1 || fmtIdx == 2;
        bool tiffFloat32 = fmtIdx == 1;
        string ext = exportAsTiff ? ".tif" : ".png";
        string formatDesc = exportAsTiff
            ? (tiffFloat32 ? "32-Bit Float GeoTIFF" : "16-Bit Norm GeoTIFF")
            : "16-Bit Norm PNG";

        bool useStandardXy = ChkExportStandardXyNaming?.IsChecked == true;
        string prefix = TxtExportPrefix?.Text?.Trim() ?? string.Empty;
        string suffix = TxtExportSuffix?.Text?.Trim() ?? string.Empty;
        string outputDir = TxtExportFolder?.Text?.Trim() ?? string.Empty;
        string baseStem = _tiles.Count == 1
            ? Path.GetFileNameWithoutExtension(_tiles[0].FilePath)
            : "DEM_Export";

        int srcTilePxW = Math.Max(1, (int)Math.Round(_exportAreaStats.PixelWidth / (double)outCols));
        int srcTilePxH = Math.Max(1, (int)Math.Round(_exportAreaStats.PixelHeight / (double)outRows));
        double rx = fileWidth / (double)srcTilePxW;
        double ry = fileHeight / (double)srcTilePxH;
        string simpTileW = FormatSimplifiedRatio(fileWidth, srcTilePxW);
        string simpTileH = FormatSimplifiedRatio(fileHeight, srcTilePxH);
        string ratioLabel = Math.Abs(rx - ry) < 0.01
            ? $"{simpTileW} ({rx:0.##}×)"
            : $"{simpTileW} W / {simpTileH} H";

        double mpp = GetMetersPerPixelFromUi();
        double physW = srcTilePxW * mpp;
        double physH = srcTilePxH * mpp;
        string physLabel = $"{FormatMetricDistance(physW)} × {FormatMetricDistance(physH)}";

        long estBytesPerFile = exportAsTiff
            ? ((long)fileWidth * fileHeight * (tiffFloat32 ? 4L : 2L) + 4096L)
            : ((long)(fileWidth * fileHeight * 2.0 * 0.65) + 1024L);
        string estSizeLabel = estBytesPerFile >= 1024 * 1024
            ? $"~{estBytesPerFile / (1024.0 * 1024.0):0.1} MB"
            : $"~{Math.Max(1.0, estBytesPerFile / 1024.0):0} KB";

        var mosaic = DemProcessor.BuildMosaicDescriptor(_tiles, _globalMin);
        double areaX0 = _exportNormX0;
        double areaY0 = _exportNormY0;
        double areaX1 = _exportNormX1;
        double areaY1 = _exportNormY1;

        for (int r = 0; r < outRows; r++)
        {
            for (int c = 0; c < outCols; c++)
            {
                double subX0 = areaX0 + (areaX1 - areaX0) * (c / (double)outCols);
                double subX1 = areaX0 + (areaX1 - areaX0) * ((c + 1) / (double)outCols);
                double subY0 = areaY0 + (areaY1 - areaY0) * (r / (double)outRows);
                double subY1 = areaY0 + (areaY1 - areaY0) * ((r + 1) / (double)outRows);

                string outFileName;
                if (totalFiles == 1)
                {
                    if (useStandardXy)
                    {
                        outFileName = $"{prefix}x0_y0{ext}";
                    }
                    else
                    {
                        string safeSuffix = string.IsNullOrEmpty(suffix) ? "_single" : suffix;
                        outFileName = $"{baseStem}{safeSuffix}_{fileWidth}x{fileHeight}{ext}";
                    }
                }
                else
                {
                    if (useStandardXy)
                    {
                        outFileName = $"{prefix}x{c}_y{r}{ext}";
                    }
                    else
                    {
                        string safeSuffix = string.IsNullOrEmpty(suffix) ? "" : suffix;
                        outFileName = $"{baseStem}{safeSuffix}_x{c}_y{r}_{fileWidth}x{fileHeight}{ext}";
                    }
                }

                string fullPath = string.IsNullOrEmpty(outputDir)
                    ? outFileName
                    : Path.Combine(outputDir, outFileName);

                var (subMin, subMax) = ComputeQuickSubTileElevationRange(mosaic, subX0, subY0, subX1, subY1);
                float subRange = Math.Max(0f, subMax - subMin);

                _expectedOutputs.Add(new ExpectedOutputFileItem(
                    OutputFileName: outFileName,
                    GridSlotLabel: totalFiles == 1 ? "Single (1×1)" : $"Col {c}, Row {r} (x{c}_y{r})",
                    FormatDescription: formatDesc,
                    OutputResolutionLabel: $"{fileWidth} × {fileHeight} px",
                    SourceAreaPixelsLabel: $"{srcTilePxW} × {srcTilePxH} px",
                    ResolutionRatioLabel: ratioLabel,
                    PhysicalSizeLabel: physLabel,
                    SubElevationRange: subRange,
                    SubMinElevation: subMin,
                    SubMaxElevation: subMax,
                    EstimatedFileSizeLabel: estSizeLabel,
                    FullOutputPath: fullPath));
            }
        }

        if (RbTabLoadedTiles != null) RbTabLoadedTiles.Content = $"LOADED DEM TILES ({_tiles.Count})";
        if (RbTabExpectedOutputs != null) RbTabExpectedOutputs.Content = $"EXPECTED OUTPUT FILES ({_expectedOutputs.Count})";
        if (TxtBottomTableHint != null && RbTabExpectedOutputs?.IsChecked == true)
        {
            TxtBottomTableHint.Text = $"{_expectedOutputs.Count} expected output file(s) based on current Export Layout, Resolution & Format";
        }
    }

    private (float Min, float Max) ComputeQuickSubTileElevationRange(
        MosaicDescriptor mosaic,
        double normX0,
        double normY0,
        double normX1,
        double normY1)
    {
        if (mosaic.TotalWidth <= 0 || mosaic.TotalHeight <= 0)
            return (_exportAreaStats.MinElevation, _exportAreaStats.MaxElevation);

        int pxX0 = Math.Clamp((int)Math.Round(normX0 * mosaic.TotalWidth), 0, Math.Max(0, mosaic.TotalWidth - 1));
        int pxY0 = Math.Clamp((int)Math.Round(normY0 * mosaic.TotalHeight), 0, Math.Max(0, mosaic.TotalHeight - 1));
        int pxX1 = Math.Clamp((int)Math.Round(normX1 * mosaic.TotalWidth), pxX0 + 1, mosaic.TotalWidth);
        int pxY1 = Math.Clamp((int)Math.Round(normY1 * mosaic.TotalHeight), pxY0 + 1, mosaic.TotalHeight);

        int spanW = Math.Max(1, pxX1 - pxX0);
        int spanH = Math.Max(1, pxY1 - pxY0);
        int stepX = Math.Max(1, spanW / 48);
        int stepY = Math.Max(1, spanH / 48);

        float minV = float.MaxValue;
        float maxV = float.MinValue;

        for (int r = 0; r < mosaic.GridRows; r++)
        {
            int tileY0 = mosaic.RowOffsets[r];
            int tileY1 = tileY0 + mosaic.RowHeights[r];
            int overlapY0 = Math.Max(pxY0, tileY0);
            int overlapY1 = Math.Min(pxY1, tileY1);
            if (overlapY0 >= overlapY1) continue;

            for (int c = 0; c < mosaic.GridCols; c++)
            {
                var tile = mosaic.TileGrid[r, c];
                if (tile == null) continue;
                float[]? data = tile.FilledData is { Length: > 0 } ? tile.FilledData : tile.RawData;
                if (data == null || data.Length == 0) continue;

                int tileX0 = mosaic.ColOffsets[c];
                int tileX1 = tileX0 + mosaic.ColWidths[c];
                int overlapX0 = Math.Max(pxX0, tileX0);
                int overlapX1 = Math.Min(pxX1, tileX1);
                if (overlapX0 >= overlapX1) continue;

                int tw = tile.Width;
                int th = tile.Height;
                for (int gy = overlapY0; gy < overlapY1; gy += stepY)
                {
                    int ly = Math.Clamp(gy - tileY0, 0, th - 1);
                    int rowOff = ly * tw;
                    for (int gx = overlapX0; gx < overlapX1; gx += stepX)
                    {
                        int lx = Math.Clamp(gx - tileX0, 0, tw - 1);
                        float v = data[rowOff + lx];
                        if (float.IsFinite(v))
                        {
                            if (v < minV) minV = v;
                            if (v > maxV) maxV = v;
                        }
                    }
                }
            }
        }

        if (minV == float.MaxValue || maxV == float.MinValue)
            return (_exportAreaStats.MinElevation, _exportAreaStats.MaxElevation);

        return (minV, maxV);
    }

    // =========================================================================
    // 2D & 3D VIEWPORT METRIC SCALE LINES
    // =========================================================================

    private static string FormatMetricDistance(double meters)
    {
        if (meters >= 1000.0)
            return $"{meters / 1000.0:0.##} km";
        return $"{meters:0.#} m";
    }

    private static void FormatNiceScaleBar(double metersPerScreenPixel, double targetBarPx, out double barWidthPx, out string distanceLabel)
    {
        double safeMpp = Math.Max(1e-6, metersPerScreenPixel);
        double rawMeters = safeMpp * targetBarPx;
        double exp = Math.Floor(Math.Log10(Math.Max(1e-3, rawMeters)));
        double baseVal = Math.Pow(10.0, exp);
        double[] niceSteps = { 1.0, 2.0, 2.5, 5.0, 10.0 };

        double niceMeters = baseVal;
        foreach (double s in niceSteps)
        {
            double candidate = s * baseVal;
            if (Math.Abs(candidate - rawMeters) < Math.Abs(niceMeters - rawMeters))
            {
                niceMeters = candidate;
            }
        }

        barWidthPx = Math.Clamp(niceMeters / safeMpp, 36.0, 155.0);
        distanceLabel = niceMeters >= 1000.0
            ? $"{niceMeters / 1000.0:0.##} km"
            : $"{niceMeters:0.##} m";
    }

    private void Update2DScaleBar()
    {
        if (Bdr2DScaleBar == null || Rect2DScaleLine == null || Txt2DScaleBarLabel == null || Txt2DScaleTotalSpan == null)
            return;

        if (_tiles.Count == 0 || RbViewGrid?.IsChecked != true)
        {
            Bdr2DScaleBar.Visibility = Visibility.Collapsed;
            return;
        }

        Bdr2DScaleBar.Visibility = Visibility.Visible;

        var mosaic = DemProcessor.BuildMosaicDescriptor(_tiles, _globalMin);
        double mpp = GetMetersPerPixelFromUi();
        double totalPhysW = Math.Max(1.0, mosaic.TotalWidth * mpp);
        double totalPhysH = Math.Max(1.0, mosaic.TotalHeight * mpp);
        double canvasW = Math.Max(1.0, _mosaicCanvasWidth);

        double metersPerScreenPx = totalPhysW / canvasW;
        FormatNiceScaleBar(metersPerScreenPx, 90.0, out double barPx, out string distLabel);

        Rect2DScaleLine.Width = barPx;
        Txt2DScaleBarLabel.Text = distLabel;

        if (IsCustomExportAreaActive)
        {
            double zonePhysW = _exportAreaStats.PixelWidth * mpp;
            Txt2DScaleTotalSpan.Text = $"Grid: {FormatMetricDistance(totalPhysW)} × {FormatMetricDistance(totalPhysH)} • Zone: {FormatMetricDistance(zonePhysW)}";
        }
        else
        {
            Txt2DScaleTotalSpan.Text = $"Grid Span: {FormatMetricDistance(totalPhysW)} × {FormatMetricDistance(totalPhysH)}";
        }
    }

    private void Update3DScaleBar()
    {
        if (Bdr3DScaleBar == null || Rect3DScaleLine == null || Txt3DScaleBarLabel == null || Txt3DScaleTotalSpan == null)
            return;

        if (_tiles.Count == 0)
            return;

        double mpp = GetMetersPerPixelFromUi();
        double activeZoneWidthMeters = Math.Max(1.0, _exportAreaStats.PixelWidth * mpp);
        double vpWidth = Grd3DVisualizer != null && Grd3DVisualizer.ActualWidth > 40.0
            ? Grd3DVisualizer.ActualWidth
            : 760.0;

        // Terrain spans 2.0 world units across [-1..+1]. Camera FOV is 44 degrees.
        double fovRad = 44.0 * (Math.PI / 180.0);
        double visibleWorldWidth = 2.0 * _camDistance * Math.Tan(fovRad * 0.5);
        double metersPerWorldUnit = activeZoneWidthMeters / 2.0;
        double metersPerScreenPx = (visibleWorldWidth * metersPerWorldUnit) / Math.Max(100.0, vpWidth);

        FormatNiceScaleBar(metersPerScreenPx, 90.0, out double barPx, out string distLabel);

        Rect3DScaleLine.Width = barPx;
        Txt3DScaleBarLabel.Text = distLabel;

        var (dispMin, dispMax) = GetEffectiveNormalizationRange();
        float deltaZ = Math.Max(0f, dispMax - dispMin);
        string modeLabel = IsCustomExportAreaActive ? "Zone Span" : "Global Span";
        Txt3DScaleTotalSpan.Text = $"{modeLabel}: {FormatMetricDistance(activeZoneWidthMeters)} • ΔZ: {deltaZ:F1}m";
    }

    private void CmbExportFileFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        UpdateNewExportResolutionSummary();
    }

    private void NewExportLayout_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        if (GrdNewExportCustomGrid != null)
        {
            GrdNewExportCustomGrid.Visibility = RbExportCustomGrid.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_matchNativeExportResolution)
        {
            ApplyNativeExportAreaResolutionToBoxes();
        }
        else if (ChkLockExportAspectRatio?.IsChecked == true)
        {
            AdjustExportHeightForLockedAspectRatio();
        }

        UpdateNewExportResolutionSummary();
        RenderExportAreaOverlay();
    }

    private void NewExportDimensions_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isSyncingExportResolutionBoxes) return;
        if (_matchNativeExportResolution)
        {
            ApplyNativeExportAreaResolutionToBoxes();
        }
        else if (ChkLockExportAspectRatio?.IsChecked == true)
        {
            AdjustExportHeightForLockedAspectRatio();
        }
        UpdateNewExportResolutionSummary();
        RenderExportAreaOverlay();
    }

    private void NewExportDimensions_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_isInitializing && !_isSyncingExportResolutionBoxes)
        {
            if (ReferenceEquals(sender, TxtNewExportWidth) || ReferenceEquals(sender, TxtNewExportHeight))
            {
                _matchNativeExportResolution = false;
            }

            if (ChkLockExportAspectRatio?.IsChecked == true && ReferenceEquals(sender, TxtNewExportWidth))
            {
                AdjustExportHeightForLockedAspectRatio();
            }

            UpdateNewExportResolutionSummary();
            RenderExportAreaOverlay();
        }
    }

    private void TxtNewExportWidth_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isSyncingExportResolutionBoxes) return;
        _matchNativeExportResolution = false;
        if (ChkLockExportAspectRatio?.IsChecked == true)
            AdjustExportHeightForLockedAspectRatio();
        UpdateNewExportResolutionSummary();
        RenderExportAreaOverlay();
    }

    private void TxtNewExportHeight_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isSyncingExportResolutionBoxes) return;
        _matchNativeExportResolution = false;
        UpdateNewExportResolutionSummary();
        RenderExportAreaOverlay();
    }

    private void CmbExportResolutionPresetSet_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateExportResolutionPresetButtons();
    }

    private void UpdateExportResolutionPresetButtons()
    {
        if (BtnResPreset0 == null || BtnResPreset1 == null || BtnResPreset2 == null ||
            BtnResPreset3 == null || BtnResPreset4 == null || BtnResPreset5 == null)
        {
            return;
        }

        int mode = CmbExportResolutionPresetSet?.SelectedIndex ?? 0;

        // Index 0 = Power of Two (Default), Index 1 = Unreal Engine (Landscape), Index 2 = Unity (Terrain)
        (string Label, string Tag, string Tooltip)[] presets = mode switch
        {
            1 => new[]
            {
                ("505", "505", "Unreal Engine Landscape recommended heightmap resolution: 505 × 505 (8×8 components, 63×63 quads)"),
                ("1009", "1009", "Unreal Engine Landscape recommended heightmap resolution: 1009 × 1009 (16×16 components, 63×63 quads)"),
                ("2017", "2017", "Unreal Engine Landscape recommended heightmap resolution: 2017 × 2017 (16×16 components, 126×126 quads)"),
                ("4033", "4033", "Unreal Engine Landscape recommended heightmap resolution: 4033 × 4033 (32×32 components, 126×126 quads)"),
                ("8129", "8129", "Unreal Engine Landscape recommended heightmap resolution: 8129 × 8129 (32×32 components, 254×254 quads)")
            },
            2 => new[]
            {
                ("257", "257", "Unity Terrain standard heightmap resolution (2^n + 1): 257 × 257"),
                ("513", "513", "Unity Terrain standard heightmap resolution (2^n + 1): 513 × 513"),
                ("1025", "1025", "Unity Terrain standard heightmap resolution (2^n + 1): 1025 × 1025"),
                ("2049", "2049", "Unity Terrain standard heightmap resolution (2^n + 1): 2049 × 2049"),
                ("4097", "4097", "Unity Terrain maximum standard heightmap resolution (2^n + 1): 4097 × 4097")
            },
            _ => new[]
            {
                ("512", "512", "Power-of-Two resolution: 512 × 512 px"),
                ("1024", "1024", "Power-of-Two resolution: 1024 × 1024 px"),
                ("2048", "2048", "Power-of-Two resolution: 2048 × 2048 px"),
                ("4096", "4096", "Power-of-Two resolution: 4096 × 4096 px"),
                ("8192", "8192", "Power-of-Two resolution: 8192 × 8192 px")
            }
        };

        BtnResPreset0.Content = "Native 1:1";
        BtnResPreset0.Tag = "Native";

        Button[] numericButtons = { BtnResPreset1, BtnResPreset2, BtnResPreset3, BtnResPreset4, BtnResPreset5 };
        for (int i = 0; i < numericButtons.Length; i++)
        {
            numericButtons[i].Content = presets[i].Label;
            numericButtons[i].Tag = presets[i].Tag;
            numericButtons[i].ToolTip = presets[i].Tooltip;
        }
    }

    private void BtnPresetResolution_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || sender is not Button { Tag: string tag }) return;

        if (string.Equals(tag, "Native", StringComparison.OrdinalIgnoreCase))
        {
            _matchNativeExportResolution = true;
            ApplyNativeExportAreaResolutionToBoxes();
        }
        else if (int.TryParse(tag, out int presetDim) && presetDim > 0)
        {
            _matchNativeExportResolution = false;
            _isSyncingExportResolutionBoxes = true;
            try
            {
                TxtNewExportWidth.Text = presetDim.ToString(CultureInfo.InvariantCulture);
                TxtNewExportHeight.Text = presetDim.ToString(CultureInfo.InvariantCulture);
            }
            finally
            {
                _isSyncingExportResolutionBoxes = false;
            }

            if (ChkLockExportAspectRatio?.IsChecked == true)
            {
                AdjustExportHeightForLockedAspectRatio();
            }

            // If 1K, 2K, 4K, 8K, or 16K was clicked, also sync the 3D GPU Visualizer resolution dropdown
            if (Cmb3DResolution != null)
            {
                if (presetDim == 1024) Cmb3DResolution.SelectedIndex = 0;
                else if (presetDim == 2048) Cmb3DResolution.SelectedIndex = 1;
                else if (presetDim == 4096) Cmb3DResolution.SelectedIndex = 2;
                else if (presetDim == 8192) Cmb3DResolution.SelectedIndex = 3;
                else if (presetDim == 16384) Cmb3DResolution.SelectedIndex = 4;
            }
        }

        UpdateNewExportResolutionSummary();
        RenderExportAreaOverlay();
    }

    private void ChkLockExportAspectRatio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        if (ChkLockExportAspectRatio.IsChecked == true)
        {
            AdjustExportHeightForLockedAspectRatio();
            UpdateNewExportResolutionSummary();
            RenderExportAreaOverlay();
        }
    }

    private string? EnsureExportFolderSelected(string dialogTitle)
    {
        string outputDir = TxtExportFolder.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            var dlg = new OpenFolderDialog
            {
                Title = dialogTitle
            };

            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.FolderName))
                return null;

            outputDir = dlg.FolderName;
            TxtExportFolder.Text = outputDir;
        }

        Directory.CreateDirectory(outputDir);
        return outputDir;
    }

    private async void BtnExportMain_Click(object sender, RoutedEventArgs e)
    {
        int fmtIdx = CmbExportFileFormat?.SelectedIndex ?? 0;
        bool exportAsTiff = fmtIdx == 1 || fmtIdx == 2;
        await ExportCustomAreaSetAsync(exportAsTiff);
    }

    private async Task ExportCustomAreaSetAsync(bool exportAsTiff)
    {
        if (_isBusy) return;
        if (_tiles.Count == 0)
        {
            MessageBox.Show("Please import one or more DEM .tiff files first.", "No Tiles Loaded",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var (outRows, outCols) = GetNewExportGridDimensionsFromUi();
        var (fileWidth, fileHeight) = GetNewExportFileResolutionFromUi();

        if (!int.TryParse(TxtNewExportWidth.Text, out int parsedW) || parsedW <= 0 ||
            !int.TryParse(TxtNewExportHeight.Text, out int parsedH) || parsedH <= 0)
        {
            MessageBox.Show("Please enter valid positive integer pixel dimensions for the new export file Width and Height.",
                "Invalid Export Resolution", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string? outputDir = EnsureExportFolderSelected(
            exportAsTiff ? "Select Output Folder for .TIFF Export Set" : "Select Output Folder for 16-Bit PNG Export Set");
        if (string.IsNullOrWhiteSpace(outputDir)) return;

        bool useStandardXy = ChkExportStandardXyNaming.IsChecked == true;
        string prefix = TxtExportPrefix.Text?.Trim() ?? string.Empty;
        string suffix = TxtExportSuffix.Text?.Trim() ?? string.Empty;
        bool tiffFloat32 = (CmbExportFileFormat?.SelectedIndex ?? 0) == 1;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        try
        {
            string formatLabel = exportAsTiff
                ? (tiffFloat32 ? "32-Bit Float GeoTIFF" : "16-Bit Normalized GeoTIFF")
                : "16-Bit Normalized PNG";

            SetBusy(true, $"Exporting {formatLabel} set ({outRows}×{outCols} grid @ {fileWidth}×{fileHeight} px)...");

            var mosaic = DemProcessor.BuildMosaicDescriptor(_tiles, _globalMin);
            var (normMin, normMax) = GetEffectiveNormalizationRange();

            double areaX0 = _exportNormX0;
            double areaY0 = _exportNormY0;
            double areaX1 = _exportNormX1;
            double areaY1 = _exportNormY1;

            int totalFiles = outRows * outCols;
            int completed = 0;
            string ext = exportAsTiff ? ".tif" : ".png";
            string baseStem = _tiles.Count == 1
                ? Path.GetFileNameWithoutExtension(_tiles[0].FilePath)
                : "DEM_Export";

            for (int r = 0; r < outRows; r++)
            {
                for (int c = 0; c < outCols; c++)
                {
                    token.ThrowIfCancellationRequested();
                    completed++;

                    double subX0 = areaX0 + (areaX1 - areaX0) * (c / (double)outCols);
                    double subX1 = areaX0 + (areaX1 - areaX0) * ((c + 1) / (double)outCols);
                    double subY0 = areaY0 + (areaY1 - areaY0) * (r / (double)outRows);
                    double subY1 = areaY0 + (areaY1 - areaY0) * ((r + 1) / (double)outRows);

                    string outFileName;
                    if (totalFiles == 1)
                    {
                        if (useStandardXy)
                        {
                            outFileName = $"{prefix}x0_y0{ext}";
                        }
                        else
                        {
                            string safeSuffix = string.IsNullOrEmpty(suffix) ? "_single" : suffix;
                            outFileName = $"{baseStem}{safeSuffix}_{fileWidth}x{fileHeight}{ext}";
                        }
                    }
                    else
                    {
                        if (useStandardXy)
                        {
                            outFileName = $"{prefix}x{c}_y{r}{ext}";
                        }
                        else
                        {
                            string safeSuffix = string.IsNullOrEmpty(suffix) ? "" : suffix;
                            outFileName = $"{baseStem}{safeSuffix}_x{c}_y{r}_{fileWidth}x{fileHeight}{ext}";
                        }
                    }

                    string outPath = Path.Combine(outputDir, outFileName);
                    TxtStatusBar.Text = $"Exporting {formatLabel} ({completed}/{totalFiles}): {outFileName} ({fileWidth}×{fileHeight} px)...";
                    PbProgress.Value = (completed * 100.0) / totalFiles;

                    // Compute GeoTIFF origin and resampled pixel scale for this sub-tile if georeferenced
                    double? subOriginX = null;
                    double? subOriginY = null;
                    double? subScaleX = null;
                    double? subScaleY = null;

                    if (mosaic.GeoOriginX.HasValue && mosaic.GeoOriginY.HasValue &&
                        mosaic.PixelScaleX.HasValue && mosaic.PixelScaleY.HasValue)
                    {
                        double srcPxX0 = subX0 * mosaic.TotalWidth;
                        double srcPxY0 = subY0 * mosaic.TotalHeight;
                        double srcPxW = Math.Max(1e-6, (subX1 - subX0) * mosaic.TotalWidth);
                        double srcPxH = Math.Max(1e-6, (subY1 - subY0) * mosaic.TotalHeight);

                        subOriginX = mosaic.GeoOriginX.Value + srcPxX0 * mosaic.PixelScaleX.Value;
                        subOriginY = mosaic.GeoOriginY.Value - srcPxY0 * mosaic.PixelScaleY.Value;
                        subScaleX = mosaic.PixelScaleX.Value * (srcPxW / fileWidth);
                        subScaleY = mosaic.PixelScaleY.Value * (srcPxH / fileHeight);
                    }

                    await Task.Run(() =>
                    {
                        float[] resampled = DemProcessor.ExtractResampledSubTile(
                            mosaic, subX0, subY0, subX1, subY1, fileWidth, fileHeight, token);

                        if (exportAsTiff)
                        {
                            if (tiffFloat32)
                            {
                                DemProcessor.ExportFloat32GeoTiff(
                                    resampled,
                                    fileWidth,
                                    fileHeight,
                                    outPath,
                                    subOriginX,
                                    subOriginY,
                                    subScaleX,
                                    subScaleY,
                                    mosaic.UtmZone,
                                    mosaic.IsNorthernHemisphere,
                                    gdalNoData: -9999f,
                                    cancellationToken: token);
                            }
                            else
                            {
                                DemProcessor.ExportNormalized16BitTiff(
                                    resampled,
                                    fileWidth,
                                    fileHeight,
                                    outPath,
                                    normMin,
                                    normMax,
                                    subOriginX,
                                    subOriginY,
                                    subScaleX,
                                    subScaleY,
                                    mosaic.UtmZone,
                                    mosaic.IsNorthernHemisphere,
                                    cancellationToken: token);
                            }
                        }
                        else
                        {
                            DemProcessor.ExportNormalized16BitPng(
                                resampled,
                                fileWidth,
                                fileHeight,
                                outPath,
                                normMin,
                                normMax,
                                token);
                        }
                    }, token);
                }
            }

            SetBusy(false, $"Successfully exported {totalFiles} {formatLabel} file(s) ({fileWidth}×{fileHeight} px each) to {outputDir}");

            double mpp = GetMetersPerPixelFromUi();
            double areaPhysW = _exportAreaStats.PixelWidth * mpp;
            double areaPhysH = _exportAreaStats.PixelHeight * mpp;
            float deltaZ = Math.Max(0f, normMax - normMin);

            MessageBox.Show(
                $"Exported {totalFiles} {formatLabel} file(s) to:\n{outputDir}\n\n" +
                $"Export Summary:\n" +
                $"• Layout: {(totalFiles == 1 ? "Single Image (1 × 1)" : $"{outRows} × {outCols} Grid ({totalFiles} files)")}\n" +
                $"• Resolution per File: {fileWidth} × {fileHeight} px (Total: {(long)fileWidth * outCols} × {(long)fileHeight * outRows} px)\n" +
                $"• Source Export Area: {_exportAreaStats.PixelWidth} × {_exportAreaStats.PixelHeight} px ({areaPhysW:N0}m × {areaPhysH:N0}m)\n" +
                $"• Elevation Range: [{normMin:F2}m .. {normMax:F2}m] (ΔZ: {deltaZ:F2}m)",
                "Export Complete — Sipp Studio DEM Cleaner",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            SetBusy(false, "Export stopped by user.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to export files:\n{ex.Message}", "Export Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            SetBusy(false, "Export failed.");
        }
    }

    // =========================================================================
    // 3D GPU HEIGHTMAP VISUALIZER (1K, 2K, 4K, 8K, and 16K Resolution)
    // =========================================================================

    private int GetSelected3DGpuResolution()
    {
        return (Cmb3DResolution?.SelectedIndex ?? 1) switch
        {
            0 => 1024,
            1 => 2048,
            2 => 4096,
            3 => 8192,
            4 => 16384,
            _ => 2048
        };
    }

    private TerrainShadingMode GetSelected3DShadingMode()
    {
        return (Cmb3DShading?.SelectedIndex ?? 0) switch
        {
            1 => TerrainShadingMode.Grayscale,
            2 => TerrainShadingMode.Alpine,
            3 => TerrainShadingMode.Desert,
            _ => TerrainShadingMode.Hypsometric
        };
    }

    private async void Cmb3DResolution_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || RbView3D?.IsChecked != true || _tiles.Count == 0) return;
        await Rebuild3DVisualizerAsync();
    }

    private async void Cmb3DShading_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || RbView3D?.IsChecked != true || _tiles.Count == 0) return;
        await Rebuild3DVisualizerAsync();
    }

    private async void Preview3DOption_Changed(object sender, RoutedEventArgs e)
    {
        if (Bdr3DWaterSliders != null)
        {
            Bdr3DWaterSliders.Visibility = Chk3DSimulateWater?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
        if (_isInitializing || RbView3D?.IsChecked != true || _tiles.Count == 0) return;
        await Rebuild3DVisualizerAsync();
    }

    private void Sld3DWaterParam_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Txt3DWaterSlopeLabel != null && Sld3DWaterSlopeLimit != null)
        {
            Txt3DWaterSlopeLabel.Text = $"{Sld3DWaterSlopeLimit.Value:0.00}m";
        }
        if (Txt3DWaterAltitudeLabel != null && Sld3DWaterAltitudeLimit != null)
        {
            Txt3DWaterAltitudeLabel.Text = $"{Sld3DWaterAltitudeLimit.Value:0}%";
        }

        if (_isInitializing || RbView3D?.IsChecked != true || _tiles.Count == 0) return;

        if (_waterSliderDebounceTimer == null)
        {
            _waterSliderDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(140)
            };
            _waterSliderDebounceTimer.Tick += async (_, _) =>
            {
                _waterSliderDebounceTimer.Stop();
                if (RbView3D?.IsChecked == true && _tiles.Count > 0)
                {
                    await Rebuild3DVisualizerAsync();
                }
            };
        }

        _waterSliderDebounceTimer.Stop();
        _waterSliderDebounceTimer.Start();
    }

    private void RequestThrottled3DCameraUpdate()
    {
        if (_camFrameStopwatch.ElapsedMilliseconds >= 33)
        {
            _camFrameStopwatch.Restart();
            Update3DCameraTransform();
            return;
        }

        if (_isCamUpdatePending) return;
        _isCamUpdatePending = true;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _isCamUpdatePending = false;
            _camFrameStopwatch.Restart();
            Update3DCameraTransform();
        }), DispatcherPriority.Background);
    }

    /// <summary>
    /// Automatically derives the 3D Z-Scale (Height-to-Width ratio = ΔZ / Width) from the physical
    /// width and elevation range of either the global grid or the selected custom export zone.
    /// </summary>
    private void UpdateAuto3DZScaleFromActiveZone()
    {
        if (Scale3DTerrain == null || Scale3DSkirt == null) return;

        if (_tiles.Count == 0)
        {
            Scale3DTerrain.ScaleY = 0.2;
            Scale3DSkirt.ScaleY = 0.2;
            if (Txt3DZScaleRatio != null)
                Txt3DZScaleRatio.Text = "0.200";
            return;
        }

        var (normMin, normMax) = GetEffectiveNormalizationRange();
        double deltaZMeters = Math.Max(0.0, normMax - normMin);
        double widthMeters = Math.Max(1.0, _exportAreaStats.PixelWidth * GetMetersPerPixelFromUi());
        double ratio = deltaZMeters / widthMeters;

        double appliedScaleY = Math.Clamp(ratio, 0.002, 10.0);
        Scale3DTerrain.ScaleY = appliedScaleY;
        Scale3DSkirt.ScaleY = appliedScaleY;
        _camTarget.Y = Math.Clamp(appliedScaleY * 0.45, 0.03, 0.65);
        Update3DCameraTransform();

        if (Txt3DZScaleRatio != null)
        {
            Txt3DZScaleRatio.Text = ratio.ToString("0.0000", CultureInfo.InvariantCulture);
        }
    }

    private void Apply3DZScaleFromTextBox()
    {
        if (Txt3DZScaleRatio == null || Scale3DTerrain == null || Scale3DSkirt == null) return;

        if (double.TryParse(Txt3DZScaleRatio.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double userRatio) &&
            userRatio > 0)
        {
            _isAuto3DZScale = false;
            double clamped = Math.Clamp(userRatio, 0.001, 10.0);
            Scale3DTerrain.ScaleY = clamped;
            Scale3DSkirt.ScaleY = clamped;
            _camTarget.Y = Math.Clamp(clamped * 0.45, 0.03, 0.65);
            Update3DCameraTransform();
            Txt3DZScaleRatio.Text = clamped.ToString("0.0000", CultureInfo.InvariantCulture);
        }
        else
        {
            UpdateAuto3DZScaleFromActiveZone();
        }
    }

    private void Txt3DZScaleRatio_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        Apply3DZScaleFromTextBox();
    }

    private void Txt3DZScaleRatio_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_isInitializing)
        {
            Apply3DZScaleFromTextBox();
            e.Handled = true;
        }
    }

    private void BtnAuto3DZScale_Click(object sender, RoutedEventArgs e)
    {
        _isAuto3DZScale = true;
        UpdateAuto3DZScaleFromActiveZone();
        TxtStatusBar.Text = $"Reset 3D Z-Scale to automatic Height/Width ratio ({Txt3DZScaleRatio?.Text}).";
    }

    private void Sld3DSunAzimuth_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        Update3DSunLight();
    }

    private void Update3DSunLight()
    {
        if (Light3DMain == null || Sld3DSunAzimuth == null) return;
        double azRad = Sld3DSunAzimuth.Value * (Math.PI / 180.0);
        double lx = -Math.Cos(azRad) * 0.72;
        double lz = -Math.Sin(azRad) * 0.72;
        Light3DMain.Direction = new Vector3D(lx, -0.78, lz);
    }

    private void BtnReset3DCamera_Click(object sender, RoutedEventArgs e)
    {
        _camYawDeg = -32.0;
        _camPitchDeg = 36.0;
        _camDistance = 2.65;
        double currentScaleY = Scale3DTerrain?.ScaleY ?? 0.2;
        _camTarget = new Point3D(0.0, Math.Clamp(currentScaleY * 0.45, 0.03, 0.65), 0.0);
        Update3DCameraTransform();
    }

    private void Update3DCameraTransform()
    {
        if (Cam3D == null) return;

        double yawRad = _camYawDeg * (Math.PI / 180.0);
        double pitchRad = Math.Clamp(_camPitchDeg, 6.0, 88.0) * (Math.PI / 180.0);

        double horizRadius = _camDistance * Math.Cos(pitchRad);
        double camX = _camTarget.X + horizRadius * Math.Sin(yawRad);
        double camY = _camTarget.Y + _camDistance * Math.Sin(pitchRad);
        double camZ = _camTarget.Z + horizRadius * Math.Cos(yawRad);

        Point3D pos = new(camX, camY, camZ);
        Vector3D lookDir = _camTarget - pos;
        lookDir.Normalize();

        Cam3D.Position = pos;
        Cam3D.LookDirection = lookDir;
        Cam3D.UpDirection = new Vector3D(0, 1, 0);

        Update3DScaleBar();
    }

    private void Grd3DVisualizer_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Grd3DVisualizer == null) return;
        _last3DMousePt = e.GetPosition(Grd3DVisualizer);

        if (e.ChangedButton == MouseButton.Left)
        {
            _is3DOrbiting = true;
            Grd3DVisualizer.CaptureMouse();
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Right || e.ChangedButton == MouseButton.Middle)
        {
            _is3DPanning = true;
            Grd3DVisualizer.CaptureMouse();
            e.Handled = true;
        }
    }

    private void Grd3DVisualizer_MouseMove(object sender, MouseEventArgs e)
    {
        if ((!_is3DOrbiting && !_is3DPanning) || Grd3DVisualizer == null) return;

        Point curPt = e.GetPosition(Grd3DVisualizer);
        double dx = curPt.X - _last3DMousePt.X;
        double dy = curPt.Y - _last3DMousePt.Y;
        _last3DMousePt = curPt;

        if (_is3DOrbiting)
        {
            _camYawDeg -= dx * 0.42;
            _camPitchDeg = Math.Clamp(_camPitchDeg + dy * 0.35, 6.0, 88.0);
            RequestThrottled3DCameraUpdate();
        }
        else if (_is3DPanning)
        {
            double yawRad = _camYawDeg * (Math.PI / 180.0);
            double panScale = _camDistance * 0.0016;

            // Pan along camera right and forward-horizontal axes
            Vector3D rightVec = new(Math.Cos(yawRad), 0, -Math.Sin(yawRad));
            Vector3D forwardHorizVec = new(-Math.Sin(yawRad), 0, -Math.Cos(yawRad));

            _camTarget -= rightVec * (dx * panScale);
            _camTarget += forwardHorizVec * (dy * panScale);

            _camTarget.X = Math.Clamp(_camTarget.X, -1.6, 1.6);
            _camTarget.Z = Math.Clamp(_camTarget.Z, -1.6, 1.6);
            RequestThrottled3DCameraUpdate();
        }
    }

    private void Grd3DVisualizer_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_is3DOrbiting || _is3DPanning)
        {
            _is3DOrbiting = false;
            _is3DPanning = false;
            Grd3DVisualizer?.ReleaseMouseCapture();
            Update3DCameraTransform();
            e.Handled = true;
        }
    }

    private void Grd3DVisualizer_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        double zoomFactor = e.Delta > 0 ? 0.88 : 1.14;
        _camDistance = Math.Clamp(_camDistance * zoomFactor, 0.32, 7.5);
        RequestThrottled3DCameraUpdate();
        e.Handled = true;
    }

    private async Task Rebuild3DVisualizerAsync()
    {
        if (_tiles.Count == 0 || Model3DTerrain == null || Model3DSkirt == null) return;

        _cts3D?.Cancel();
        _cts3D?.Dispose();
        _cts3D = new CancellationTokenSource();
        CancellationToken token = _cts3D.Token;

        int res = GetSelected3DGpuResolution();
        string resTag = res switch
        {
            1024 => "1K (1024 × 1024)",
            2048 => "2K (2048 × 2048)",
            4096 => "4K (4096 × 4096)",
            8192 => "8K (8192 × 8192)",
            _ => "16K (16384 × 16384)"
        };

        TerrainShadingMode shadingMode = GetSelected3DShadingMode();
        bool showProcessed = ChkHighlightVoidsInRed?.IsChecked != true;
        bool simulateWater = Chk3DSimulateWater?.IsChecked == true;
        bool highlightQuadEdges = Chk3DHighlightQuadEdges?.IsChecked == true;
        float waterSlopeLimit = (float)(Sld3DWaterSlopeLimit?.Value ?? 0.12);
        float waterAltitudeLimit = (float)((Sld3DWaterAltitudeLimit?.Value ?? 24.0) / 100.0);
        var (normMin, normMax) = GetEffectiveNormalizationRange();

        double ax0 = _exportNormX0;
        double ay0 = _exportNormY0;
        double ax1 = _exportNormX1;
        double ay1 = _exportNormY1;

        try
        {
            if (Bdr3DLoadingOverlay != null && Txt3DLoadingMessage != null)
            {
                Txt3DLoadingMessage.Text = $"Rendering GPU 3D Heightmap @ {resTag}...";
                Bdr3DLoadingOverlay.Visibility = Visibility.Visible;
            }

            if (_isAuto3DZScale)
            {
                UpdateAuto3DZScaleFromActiveZone();
            }

            var mosaic = DemProcessor.BuildMosaicDescriptor(_tiles, _globalMin);

            Heightmap3DAssets assets = await Task.Run(() =>
                DemProcessor.Build3DHeightmapAssets(
                    mosaic,
                    ax0,
                    ay0,
                    ax1,
                    ay1,
                    res,
                    normMin,
                    normMax,
                    showProcessed,
                    shadingMode,
                    simulateWater,
                    highlightQuadEdges,
                    waterSlopeLimit,
                    waterAltitudeLimit,
                    token), token);

            token.ThrowIfCancellationRequested();

            var imgBrush = new ImageBrush(assets.TextureBitmap)
            {
                ViewportUnits = BrushMappingMode.Absolute
            };
            RenderOptions.SetBitmapScalingMode(imgBrush, BitmapScalingMode.Linear);
            imgBrush.Freeze();

            var mat = new DiffuseMaterial(imgBrush);
            mat.Freeze();

            var backBrush = new SolidColorBrush(Color.FromRgb(38, 32, 26));
            backBrush.Freeze();
            var backMat = new DiffuseMaterial(backBrush);
            backMat.Freeze();

            Model3DTerrain.Geometry = assets.TerrainMesh;
            Model3DTerrain.Material = mat;
            Model3DTerrain.BackMaterial = backMat;

            Model3DSkirt.Geometry = assets.SkirtMesh;

            string areaTag = IsCustomExportAreaActive ? "Custom Selected Zone" : "Full Global Grid";
            int quadCount = assets.TerrainMesh.TriangleIndices.Count / 6;
            if (Txt3DGpuHudTitle != null)
            {
                Txt3DGpuHudTitle.Text = $"GPU 3D HEIGHTMAP • {resTag} ({quadCount:N0} quads) • {areaTag} [{normMin:F1}m .. {normMax:F1}m]";
            }
            Update3DScaleBar();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer 3D build request
        }
        catch (Exception ex)
        {
            TxtStatusBar.Text = $"3D GPU Visualizer error: {ex.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested && Bdr3DLoadingOverlay != null)
            {
                Bdr3DLoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void SetBusy(bool busy, string statusText)
    {
        _isBusy = busy;
        TxtStatusBar.Text = statusText;
        BtnProcessAll.IsEnabled = !busy;
        if (BtnExportMain != null) BtnExportMain.IsEnabled = !busy;
        if (BtnApplySeamSmoothing != null) BtnApplySeamSmoothing.IsEnabled = !busy;
        BtnStopSidebar.IsEnabled = busy;
        if (!busy)
        {
            PbProgress.Value = 0;
        }
    }
}
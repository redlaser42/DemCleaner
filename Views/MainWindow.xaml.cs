using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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
    private bool _isDraggingGizmo = false;
    private Point _mosaicPanStartPt;
    private double _mosaicPanStartHOffset;
    private double _mosaicPanStartVOffset;
    private bool _isPanningMosaic = false;
    private Point _gizmoDragStartPt;
    private double _gizmoStartNormX0;
    private double _gizmoStartNormY0;
    private double _gizmoStartNormX1;
    private double _gizmoStartNormY1;

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

        Closing += MainWindow_Closing;
        ApplyLoadedSettings(UserSettingsService.Load());

        LoadLicenseTooltipText();
        UpdateExportResolutionPresetButtons();
        Update3DCameraTransform();
        Update3DSunLight();
        UpdateNewExportResolutionSummary();

        _isInitializing = false;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveCurrentSettings();
    }

    private void ApplyLoadedSettings(UserSettings settings)
    {
        if (settings.ThemeIndex >= 0 && settings.ThemeIndex < ThemeManager.Themes.Count)
        {
            CmbTheme.SelectedIndex = settings.ThemeIndex;
            ThemeManager.ApplyTheme(Resources, ThemeManager.Themes[settings.ThemeIndex]);
        }

        if (settings.MapProvider == "Google Maps")
        {
            RbMapProviderGoogleMaps.IsChecked = true;
            RbMapProviderGoogleEarth.IsChecked = false;
        }
        else
        {
            RbMapProviderGoogleEarth.IsChecked = true;
            RbMapProviderGoogleMaps.IsChecked = false;
        }

        ChkUseGdalNoData.IsChecked = settings.UseGdalNoData;
        ChkUseCustomThreshold.IsChecked = settings.UseCustomThreshold;
        if (settings.ComparisonIndex >= 0 && settings.ComparisonIndex < CmbComparison.Items.Count)
        {
            CmbComparison.SelectedIndex = settings.ComparisonIndex;
        }
        if (!string.IsNullOrWhiteSpace(settings.CustomNoDataValue))
        {
            TxtCustomNoData.Text = settings.CustomNoDataValue;
        }

        RbFillMin.IsChecked = settings.FillMethod == 0;
        RbFillMax.IsChecked = settings.FillMethod == 1;
        RbFillMean.IsChecked = settings.FillMethod == 2;
        RbFillInterpolated.IsChecked = settings.FillMethod == 3;
        if (BdrInterpolatedNote != null)
        {
            BdrInterpolatedNote.Visibility = settings.FillMethod == 3 ? Visibility.Visible : Visibility.Collapsed;
        }
        SldIterations.Value = Math.Clamp(settings.InterpolationPasses, 1, 500);

        ChkUseGlobalForConstantFill.IsChecked = settings.UseGlobalForConstantFill;
        ChkEnablePerTileFillOverride.IsChecked = settings.EnablePerTileFillOverride;
        ColTileFillOverride.Visibility = settings.EnablePerTileFillOverride ? Visibility.Visible : Visibility.Collapsed;
        PnlSelectedTileOverride.Visibility = settings.EnablePerTileFillOverride ? Visibility.Visible : Visibility.Collapsed;

        int smoothingSteps = Math.Clamp(settings.SeamSmoothingSteps, 0, 20);
        SldSeamSmoothingSteps.Value = smoothingSteps;
        TxtSeamSmoothingStepsLabel.Text = smoothingSteps == 0 ? "0 steps (Off)" : $"{smoothingSteps} step(s)";

        RbExportSourceGrid.IsChecked = settings.ExportLayout == 0;
        RbExportSingleImage.IsChecked = settings.ExportLayout == 1;
        RbExportCustomGrid.IsChecked = settings.ExportLayout == 2;
        GrdNewExportCustomGrid.Visibility = settings.ExportLayout == 2 ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(settings.ExportGridSize))
        {
            TxtNewExportGridSize.Text = settings.ExportGridSize;
        }

        if (settings.ResolutionPresetSetIndex >= 0 && settings.ResolutionPresetSetIndex < CmbExportResolutionPresetSet.Items.Count)
        {
            CmbExportResolutionPresetSet.SelectedIndex = settings.ResolutionPresetSetIndex;
        }
        if (!string.IsNullOrWhiteSpace(settings.ExportWidth))
        {
            TxtNewExportWidth.Text = settings.ExportWidth;
        }
        if (!string.IsNullOrWhiteSpace(settings.ExportHeight))
        {
            TxtNewExportHeight.Text = settings.ExportHeight;
        }
        ChkLockExportAspectRatio.IsChecked = settings.LockExportAspectRatio;

        if (settings.ExportFileFormatIndex >= 0 && settings.ExportFileFormatIndex < CmbExportFileFormat.Items.Count)
        {
            CmbExportFileFormat.SelectedIndex = settings.ExportFileFormatIndex;
        }

        if (!string.IsNullOrWhiteSpace(settings.ExportFolder))
        {
            TxtExportFolder.Text = settings.ExportFolder;
        }

        ChkExportStandardXyNaming.IsChecked = settings.ExportStandardXyNaming;
        GrdExportPrefixRow.Visibility = settings.ExportStandardXyNaming ? Visibility.Visible : Visibility.Collapsed;
        GrdExportSuffixRow.Visibility = settings.ExportStandardXyNaming ? Visibility.Collapsed : Visibility.Visible;
        TxtExportPrefix.Text = settings.ExportPrefix;
        TxtExportSuffix.Text = settings.ExportSuffix;
    }

    private void SaveCurrentSettings()
    {
        var settings = new UserSettings
        {
            ThemeIndex = CmbTheme.SelectedIndex,
            MapProvider = RbMapProviderGoogleMaps.IsChecked == true ? "Google Maps" : "Google Earth",
            UseGdalNoData = ChkUseGdalNoData.IsChecked == true,
            UseCustomThreshold = ChkUseCustomThreshold.IsChecked == true,
            ComparisonIndex = CmbComparison.SelectedIndex,
            CustomNoDataValue = TxtCustomNoData.Text,
            FillMethod = RbFillInterpolated.IsChecked == true ? 3 :
                         RbFillMean.IsChecked == true ? 2 :
                         RbFillMax.IsChecked == true ? 1 : 0,
            InterpolationPasses = (int)Math.Round(SldIterations.Value),
            UseGlobalForConstantFill = ChkUseGlobalForConstantFill.IsChecked == true,
            EnablePerTileFillOverride = ChkEnablePerTileFillOverride.IsChecked == true,
            SeamSmoothingSteps = (int)Math.Round(SldSeamSmoothingSteps.Value),
            ExportLayout = RbExportCustomGrid.IsChecked == true ? 2 :
                           RbExportSingleImage.IsChecked == true ? 1 : 0,
            ExportGridSize = TxtNewExportGridSize.Text,
            ResolutionPresetSetIndex = CmbExportResolutionPresetSet.SelectedIndex,
            ExportWidth = TxtNewExportWidth.Text,
            ExportHeight = TxtNewExportHeight.Text,
            LockExportAspectRatio = ChkLockExportAspectRatio.IsChecked == true,
            ExportFileFormatIndex = CmbExportFileFormat.SelectedIndex,
            ExportFolder = TxtExportFolder.Text,
            ExportStandardXyNaming = ChkExportStandardXyNaming.IsChecked == true,
            ExportPrefix = TxtExportPrefix.Text,
            ExportSuffix = TxtExportSuffix.Text
        };

        UserSettingsService.Save(settings);
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

    private void RefreshTileIndices()
    {
        for (int idx = 0; idx < _tiles.Count; idx++)
        {
            _tiles[idx].Index = idx + 1;
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
            if (BtnStopMain != null) BtnStopMain.IsEnabled = false;
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
        RefreshTileIndices();
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
            RefreshTileIndices();

            if (_tiles.Count == 0)
            {
                UpdateGlobalStatsDisplay();
                RebuildMosaicGrid();
                UpdateInspectorView();
                TxtStatusBar.Text = $"Removed {tileToRemove.FileName}. No tiles remaining.";
                return;
            }

            TiffDemReader.ArrangeTilesInGrid(_tiles);
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
                RefreshTileIndices();
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
                RefreshTileIndices();
            }

            // If designated export folder hasn't been customized yet, default to a subfolder next to the first loaded tile
            if (_tiles.Count > 0 && string.IsNullOrWhiteSpace(TxtExportFolder.Text))
            {
                string? firstDir = Path.GetDirectoryName(_tiles[0].FilePath);
                if (!string.IsNullOrEmpty(firstDir))
                {
                    string locationName = TiffDemReader.DeriveLocationName(_tiles);
                    TxtExportFolder.Text = Path.Combine(firstDir, locationName);
                }
            }

            TiffDemReader.ArrangeTilesInGrid(_tiles);

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
            if (BtnCenterExportAreaToolbar != null)
            {
                BtnCenterExportAreaToolbar.IsEnabled = false;
                BtnCenterExportAreaToolbar.Visibility = RbViewGrid?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
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
        if (BtnCenterExportAreaToolbar != null)
        {
            BtnCenterExportAreaToolbar.IsEnabled = _tiles.Count > 0;
            BtnCenterExportAreaToolbar.Visibility = RbViewGrid?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
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
            if (isCustom)
            {
                var (fileW, fileH) = GetNewExportFileResolutionFromUi();
                var (gridR, gridC) = GetNewExportGridDimensionsFromUi();
                int totalExportPxW = fileW * gridC;
                double zonePhysicalMetersW = _exportAreaStats.PixelWidth * metersPerPixel;
                double effectiveExportMpp = totalExportPxW > 0 ? (zonePhysicalMetersW / totalExportPxW) : metersPerPixel;
                if (TxtMetersPerPixelLabel != null) TxtMetersPerPixelLabel.Text = "EXPORT AREA METERS/PX";
                if (TxtSourceMetersPerPixel != null) TxtSourceMetersPerPixel.Text = $"{effectiveExportMpp:0.##} m/px (Src: {metersPerPixel:0.##})";
            }
            else
            {
                if (TxtMetersPerPixelLabel != null) TxtMetersPerPixelLabel.Text = "SOURCE DATA METERS/PX";
                if (TxtSourceMetersPerPixel != null) TxtSourceMetersPerPixel.Text = $"{metersPerPixel:0.##} m/px";
            }
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
            if (BtnCenterExportAreaToolbar != null) BtnCenterExportAreaToolbar.Visibility = RbViewGrid?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
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
                BdrGridLeftStatsOverlay.Visibility = (showGrid || showInspector) ? Visibility.Visible : Visibility.Collapsed;
            GrdTileInspector.Visibility = showInspector ? Visibility.Visible : Visibility.Collapsed;
            if (Grd3DVisualizer != null)
                Grd3DVisualizer.Visibility = show3D ? Visibility.Visible : Visibility.Collapsed;

            if (BtnResetExportAreaToolbar != null)
                BtnResetExportAreaToolbar.Visibility = showGrid ? Visibility.Visible : Visibility.Collapsed;
            if (BtnCenterExportAreaToolbar != null) BtnCenterExportAreaToolbar.Visibility = showGrid ? Visibility.Visible : Visibility.Collapsed;
            if (PnlViewport3DBar != null)
                PnlViewport3DBar.Visibility = show3D ? Visibility.Visible : Visibility.Collapsed;

            Update2DScaleBar();
            Update3DScaleBar();
        }
    }

    private double _metersPerPixel = 1.0;

    private double GetMetersPerPixelFromUi()
    {
        var tileWithScale = _tiles.FirstOrDefault(t => t.PixelScaleX.HasValue && t.PixelScaleX.Value > 0);
        if (tileWithScale != null)
        {
            double sx = tileWithScale.PixelScaleX!.Value;
            return sx < 0.01 ? sx * 111320.0 : sx;
        }
        return _metersPerPixel > 0 ? _metersPerPixel : 1.0;
    }

    private void ChkExportStandardXyNaming_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        bool useStandardXy = ChkExportStandardXyNaming.IsChecked == true;
        GrdExportPrefixRow.Visibility = useStandardXy ? Visibility.Visible : Visibility.Collapsed;
        GrdExportSuffixRow.Visibility = useStandardXy ? Visibility.Collapsed : Visibility.Visible;
        HighlightSelectedResolutionButton();
        UpdateExpectedOutputFilesList();
    }

    private void ExportNaming_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        HighlightSelectedResolutionButton();
        UpdateExpectedOutputFilesList();
    }

    /// <summary>
    /// Builds the 2D Tile Grid Mosaic reframed so all tiles fit neatly in the viewport when fully zoomed out,
    /// enclosed by a left and bottom graticule neatline border showing length in pixels and distance in meters at each tile edge,
    /// plus an interactive overlay canvas for custom export regions.
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

        // Calculate available viewport space accounting for ScrollViewer padding and graticule border
        double padH = SvGridMosaic.Padding.Left + SvGridMosaic.Padding.Right;
        double padV = SvGridMosaic.Padding.Top + SvGridMosaic.Padding.Bottom;
        double availW = SvGridMosaic.ActualWidth > 100 ? SvGridMosaic.ActualWidth - padH : 640.0;
        double availH = SvGridMosaic.ActualHeight > 100 ? SvGridMosaic.ActualHeight - padV : 440.0;

        const double graticuleLeftW = 56.0;
        const double graticuleBottomH = 34.0;
        const double frameMargin = 20.0;

        double maxMosaicW = Math.Max(120.0, availW - graticuleLeftW - frameMargin);
        double maxMosaicH = Math.Max(120.0, availH - graticuleBottomH - frameMargin);

        double displayScale = Math.Min(maxMosaicW / Math.Max(1, mosaic.TotalWidth), maxMosaicH / Math.Max(1, mosaic.TotalHeight));
        displayScale = Math.Max(displayScale, 0.0001);

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

        // GrdMosaicWithHeaders: Col 0 = Left Graticule, Col 1 = Mosaic; Row 0 = Mosaic, Row 1 = Bottom Graticule
        GrdMosaicWithHeaders.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(graticuleLeftW, GridUnitType.Pixel) });
        GrdMosaicWithHeaders.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_mosaicCanvasWidth, GridUnitType.Pixel) });

        GrdMosaicWithHeaders.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_mosaicCanvasHeight, GridUnitType.Pixel) });
        GrdMosaicWithHeaders.RowDefinitions.Add(new RowDefinition { Height = new GridLength(graticuleBottomH, GridUnitType.Pixel) });

        var grdTileMosaic = new Grid
        {
            Width = _mosaicCanvasWidth,
            Height = _mosaicCanvasHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        for (int r = 0; r < rows; r++)
        {
            grdTileMosaic.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_rowDispHeights[r], GridUnitType.Pixel) });
        }
        for (int c = 0; c < cols; c++)
        {
            grdTileMosaic.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_colDispWidths[c], GridUnitType.Pixel) });
        }

        _mosaicOverlayCanvas = new Canvas
        {
            Width = _mosaicCanvasWidth,
            Height = _mosaicCanvasHeight,
            Background = Brushes.Transparent,
            Cursor = Cursors.Cross,
            ClipToBounds = true
        };
        _mosaicOverlayCanvas.MouseLeftButtonDown += MosaicOverlayCanvas_MouseLeftButtonDown;
        _mosaicOverlayCanvas.MouseMove += MosaicOverlayCanvas_MouseMove;
        _mosaicOverlayCanvas.MouseLeftButtonUp += MosaicOverlayCanvas_MouseLeftButtonUp;
        _mosaicOverlayCanvas.MouseLeave += MosaicOverlayCanvas_MouseLeave;

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

        // Reset 2D Grid zoom scale when loading or rebuilding mosaic
        if (ScaleGridMosaic != null)
        {
            ScaleGridMosaic.ScaleX = 1.0;
            ScaleGridMosaic.ScaleY = 1.0;
        }

        // Populate Tile Cells directly at [r, c] (Col and Row header badges removed; slot badge retained in corner)
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

                    // Top-Left: Tile Source Filename & Custom Fill Badge
                    string fillBadge = tile.FillOverride != TileFillModeOverride.UseGlobalDefault
                        ? $" [{effectiveFill}]"
                        : string.Empty;

                    var labelBorder = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(195, 14, 20, 16)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                        BorderThickness = new Thickness(0.75),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(5, 2, 5, 2),
                        Margin = new Thickness(5),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        IsHitTestVisible = false
                    };

                    var labelText = new TextBlock
                    {
                        Text = $"{tile.FileName}{fillBadge}",
                        Foreground = Brushes.White,
                        FontSize = 9.5,
                        FontWeight = FontWeights.SemiBold
                    };
                    labelBorder.Child = labelText;
                    cellGrid.Children.Add(labelBorder);

                    // Bottom-Right Corner: 0x0y, 1x0y Tile Coordinate Slot Badge
                    var slotBadge = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(215, 12, 18, 14)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(130, 232, 168, 73)),
                        BorderThickness = new Thickness(0.8),
                        CornerRadius = new CornerRadius(3.5),
                        Padding = new Thickness(5, 1.5, 5, 1.5),
                        Margin = new Thickness(0, 0, 4, 4),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        IsHitTestVisible = false,
                        ToolTip = $"Tile Grid Coordinates: {c}x{r}y (Col {c}, Row {r})",
                        Child = new TextBlock
                        {
                            Text = $"{c}x{r}y",
                            Foreground = accentPrimaryBrush,
                            FontFamily = new FontFamily("Consolas, monospace"),
                            FontSize = 10,
                            FontWeight = FontWeights.Bold
                        }
                    };
                    cellGrid.Children.Add(slotBadge);

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
                    grdTileMosaic.Children.Add(cellBorder);
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
                    grdTileMosaic.Children.Add(emptyCell);
                }
            }
        }

        Grid.SetRow(_mosaicOverlayCanvas, 0);
        Grid.SetColumn(_mosaicOverlayCanvas, 0);
        Grid.SetRowSpan(_mosaicOverlayCanvas, rows);
        Grid.SetColumnSpan(_mosaicOverlayCanvas, cols);
        Panel.SetZIndex(_mosaicOverlayCanvas, 20);
        grdTileMosaic.Children.Add(_mosaicOverlayCanvas);

        Grid.SetRow(grdTileMosaic, 0);
        Grid.SetColumn(grdTileMosaic, 1);
        GrdMosaicWithHeaders.Children.Add(grdTileMosaic);

        double mpp = GetMetersPerPixelFromUi();

        // Left Graticule Neatline Border
        var leftGraticule = BuildLeftGraticule(rows, mosaic, mpp);
        Grid.SetRow(leftGraticule, 0);
        Grid.SetColumn(leftGraticule, 0);
        GrdMosaicWithHeaders.Children.Add(leftGraticule);

        // Bottom Graticule Neatline Border
        var bottomGraticule = BuildBottomGraticule(cols, mosaic, mpp);
        Grid.SetRow(bottomGraticule, 1);
        Grid.SetColumn(bottomGraticule, 1);
        GrdMosaicWithHeaders.Children.Add(bottomGraticule);

        // Bottom-Left Neatline Corner Box
        var cornerBox = BuildGraticuleCornerBox();
        Grid.SetRow(cornerBox, 1);
        Grid.SetColumn(cornerBox, 0);
        GrdMosaicWithHeaders.Children.Add(cornerBox);

        RenderExportAreaOverlay();
        Update2DScaleBar();
    }

    private FrameworkElement BuildLeftGraticule(int rows, MosaicDescriptor mosaic, double mpp)
    {
        var graticuleGrid = new Grid
        {
            Width = 56.0,
            Height = _mosaicCanvasHeight,
            Background = (Brush)Resources["ThemeBgWell"]
        };

        for (int r = 0; r < rows; r++)
        {
            graticuleGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_rowDispHeights[r], GridUnitType.Pixel) });
        }

        var borderBrush = (Brush)Resources["ThemeBorderCard"];
        var accentBrush = (Brush)Resources["ThemeAccentPrimary"];
        var textPrimaryBrush = (Brush)Resources["ThemeTextPrimary"];

        for (int r = 0; r < rows; r++)
        {
            int nativeH = mosaic.RowHeights[r];
            double distMeters = nativeH * mpp;
            string distStr = distMeters >= 1000.0 ? $"{(distMeters / 1000.0):0.#} km" : $"{distMeters:N0} m";

            var cellBorder = new Border
            {
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(0, 0, 1.2, r == rows - 1 ? 0 : 0.8),
                Padding = new Thickness(2, 2, 4, 2)
            };

            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            stack.Children.Add(new TextBlock
            {
                Text = $"{nativeH} px",
                Foreground = accentBrush,
                FontSize = 8.5,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            stack.Children.Add(new TextBlock
            {
                Text = distStr,
                Foreground = textPrimaryBrush,
                FontSize = 8.0,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0)
            });

            cellBorder.Child = stack;
            Grid.SetRow(cellBorder, r);
            graticuleGrid.Children.Add(cellBorder);
        }

        return graticuleGrid;
    }

    private FrameworkElement BuildBottomGraticule(int cols, MosaicDescriptor mosaic, double mpp)
    {
        var graticuleGrid = new Grid
        {
            Width = _mosaicCanvasWidth,
            Height = 34.0,
            Background = (Brush)Resources["ThemeBgWell"]
        };

        for (int c = 0; c < cols; c++)
        {
            graticuleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_colDispWidths[c], GridUnitType.Pixel) });
        }

        var borderBrush = (Brush)Resources["ThemeBorderCard"];
        var accentBrush = (Brush)Resources["ThemeAccentPrimary"];
        var textPrimaryBrush = (Brush)Resources["ThemeTextPrimary"];

        for (int c = 0; c < cols; c++)
        {
            int nativeW = mosaic.ColWidths[c];
            double distMeters = nativeW * mpp;
            string distStr = distMeters >= 1000.0 ? $"{(distMeters / 1000.0):0.#} km" : $"{distMeters:N0} m";

            var cellBorder = new Border
            {
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(0, 1.2, c == cols - 1 ? 0 : 0.8, 0),
                Padding = new Thickness(2, 2, 2, 2)
            };

            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            stack.Children.Add(new TextBlock
            {
                Text = $"{nativeW} px",
                Foreground = accentBrush,
                FontSize = 8.5,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            stack.Children.Add(new TextBlock
            {
                Text = distStr,
                Foreground = textPrimaryBrush,
                FontSize = 8.0,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0)
            });

            cellBorder.Child = stack;
            Grid.SetColumn(cellBorder, c);
            graticuleGrid.Children.Add(cellBorder);
        }

        return graticuleGrid;
    }

    private FrameworkElement BuildGraticuleCornerBox()
    {
        return new Border
        {
            Width = 56.0,
            Height = 34.0,
            Background = (Brush)Resources["ThemeBgWell"],
            BorderBrush = (Brush)Resources["ThemeBorderCard"],
            BorderThickness = new Thickness(0, 1.2, 1.2, 0),
            Child = new TextBlock
            {
                Text = "px â€¢ m",
                Foreground = (Brush)Resources["ThemeTextMuted"],
                FontSize = 8.0,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
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

        Point downPt = e.GetPosition(_mosaicOverlayCanvas);
        downPt.X = Math.Clamp(downPt.X, 0.0, _mosaicCanvasWidth);
        downPt.Y = Math.Clamp(downPt.Y, 0.0, _mosaicCanvasHeight);

        _mosaicDragStartPt = downPt;
        _isMouseDownOnMosaic = true;
        _isDraggingExportArea = false;

        // Check if mouse clicked on the Transform Control Gizmo of an active custom export zone
        if (IsPointOverGizmo(downPt))
        {
            _isDraggingGizmo = true;
            _gizmoDragStartPt = downPt;
            _gizmoStartNormX0 = _exportNormX0;
            _gizmoStartNormY0 = _exportNormY0;
            _gizmoStartNormX1 = _exportNormX1;
            _gizmoStartNormY1 = _exportNormY1;
            _mosaicOverlayCanvas.Cursor = Cursors.SizeAll;
            _mosaicOverlayCanvas.CaptureMouse();
            RenderExportAreaOverlay();
            e.Handled = true;
            return;
        }

        _isDraggingGizmo = false;
        _mosaicOverlayCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void MosaicOverlayCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_mosaicOverlayCanvas == null || _tiles.Count == 0) return;

        Point curPt = e.GetPosition(_mosaicOverlayCanvas);
        curPt.X = Math.Clamp(curPt.X, 0.0, _mosaicCanvasWidth);
        curPt.Y = Math.Clamp(curPt.Y, 0.0, _mosaicCanvasHeight);

        // When mouse button is not held down, update hover cursor based on proximity to the transform gizmo
        if (!_isMouseDownOnMosaic)
        {
            _mosaicOverlayCanvas.Cursor = IsPointOverGizmo(curPt) ? Cursors.SizeAll : Cursors.Cross;
            return;
        }

        if (_isDraggingGizmo)
        {
            double canvasW = Math.Max(1.0, _mosaicCanvasWidth);
            double canvasH = Math.Max(1.0, _mosaicCanvasHeight);

            double deltaNormX = (curPt.X - _gizmoDragStartPt.X) / canvasW;
            double deltaNormY = (curPt.Y - _gizmoDragStartPt.Y) / canvasH;

            double normW = _gizmoStartNormX1 - _gizmoStartNormX0;
            double normH = _gizmoStartNormY1 - _gizmoStartNormY0;

            double newX0 = _gizmoStartNormX0 + deltaNormX;
            double newY0 = _gizmoStartNormY0 + deltaNormY;

            // Clamp so the custom bounding box stays entirely within the mosaic bounds [0..1, 0..1]
            newX0 = Math.Clamp(newX0, 0.0, Math.Max(0.0, 1.0 - normW));
            newY0 = Math.Clamp(newY0, 0.0, Math.Max(0.0, 1.0 - normH));
            double newX1 = newX0 + normW;
            double newY1 = newY0 + normH;

            _exportNormX0 = newX0;
            _exportNormY0 = newY0;
            _exportNormX1 = newX1;
            _exportNormY1 = newY1;

            UpdateExportAreaMetricsAndUi();
            RenderExportAreaOverlay();
            return;
        }

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

            double mpp = GetMetersPerPixelFromUi();
            double wM = _exportAreaStats.PixelWidth * mpp;
            double hM = _exportAreaStats.PixelHeight * mpp;
            TxtStatusBar.Text = $"Export Zone: {_exportAreaStats.PixelWidth} \u00d7 {_exportAreaStats.PixelHeight} px ({wM:N0}m \u00d7 {hM:N0}m) \u2022 Scale: {mpp:0.##} m/px \u2022 Elev [{_exportAreaStats.MinElevation:F1}m .. {_exportAreaStats.MaxElevation:F1}m]";
        }
    }

    private void MosaicOverlayCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isMouseDownOnMosaic || _mosaicOverlayCanvas == null) return;

        Point releasePt = e.GetPosition(_mosaicOverlayCanvas);
        releasePt.X = Math.Clamp(releasePt.X, 0.0, _mosaicCanvasWidth);
        releasePt.Y = Math.Clamp(releasePt.Y, 0.0, _mosaicCanvasHeight);

        bool wasDraggingExportArea = _isDraggingExportArea;
        bool wasDraggingGizmo = _isDraggingGizmo;

        _isMouseDownOnMosaic = false;
        _isDraggingExportArea = false;
        _isDraggingGizmo = false;
        _mosaicOverlayCanvas.ReleaseMouseCapture();

        if (wasDraggingGizmo)
        {
            _isAuto3DZScale = true;
            UpdateGlobalStatsDisplay();
            RenderExportAreaOverlay();
            TxtStatusBar.Text =
                $"Repositioned Export Area: X={_exportAreaStats.PixelX}..{_exportAreaStats.PixelX + _exportAreaStats.PixelWidth} px, " +
                $"Y={_exportAreaStats.PixelY}..{_exportAreaStats.PixelY + _exportAreaStats.PixelHeight} px " +
                $"({_exportAreaStats.PixelWidth} \u00d7 {_exportAreaStats.PixelHeight} px) \u2022 Elev [{_exportAreaStats.MinElevation:F2}m .. {_exportAreaStats.MaxElevation:F2}m]";

            if (RbView3D?.IsChecked == true && _tiles.Count > 0)
            {
                _ = Rebuild3DVisualizerAsync();
            }
        }
        else if (wasDraggingExportArea)
        {
            _isAuto3DZScale = true;
            ApplyDragPointsToExportArea(_mosaicDragStartPt, releasePt);
            UpdateGlobalStatsDisplay();
            RenderExportAreaOverlay();
            TxtStatusBar.Text =
                $"Redefined Export Area: X={_exportAreaStats.PixelX}..{_exportAreaStats.PixelX + _exportAreaStats.PixelWidth} px, " +
                $"Y={_exportAreaStats.PixelY}..{_exportAreaStats.PixelY + _exportAreaStats.PixelHeight} px " +
                $"({_exportAreaStats.PixelWidth} \u00d7 {_exportAreaStats.PixelHeight} px) \u2022 Elev [{_exportAreaStats.MinElevation:F2}m .. {_exportAreaStats.MaxElevation:F2}m]";

            if (RbView3D?.IsChecked == true && _tiles.Count > 0)
            {
                _ = Rebuild3DVisualizerAsync();
            }
        }
        else
        {
            SelectTileAtCanvasPoint(releasePt);
        }

        _mosaicOverlayCanvas.Cursor = IsPointOverGizmo(releasePt) ? Cursors.SizeAll : Cursors.Cross;
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
        // Light size snapping to active export resolution preset family
        if (_tiles.Count > 0)
        {
            var mosaic = DemProcessor.BuildMosaicDescriptor(_tiles, _globalMin);
            if (mosaic.TotalWidth > 0)
            {
                int[] basePresets = (CmbExportResolutionPresetSet?.SelectedIndex ?? 0) switch
                {
                    1 => new[] { 1009, 2017, 4033, 8129, 16129 },
                    2 => new[] { 1025, 2049, 4097, 8193, 16385 },
                    _ => new[] { 512, 1024, 2048, 4096, 8192, 16384 }
                };
                var (_, outC) = GetNewExportGridDimensionsFromUi();
                int mult = Math.Max(1, outC);
                foreach (int preset in basePresets)
                {
                    double snapCanvasSide1 = ((double)preset / mosaic.TotalWidth) * canvasW;
                    if (Math.Abs(requestedSide - snapCanvasSide1) <= 12.0)
                    {
                        requestedSide = snapCanvasSide1;
                        break;
                    }
                    if (mult > 1)
                    {
                        double snapCanvasSideGrid = ((double)(preset * mult) / mosaic.TotalWidth) * canvasW;
                        if (Math.Abs(requestedSide - snapCanvasSideGrid) <= 12.0)
                        {
                            requestedSide = snapCanvasSideGrid;
                            break;
                        }
                    }
                }
            }
        }
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

        // 2. Interior subdivision grid lines when Custom Grid Size (R Ã— C) is selected for new exports
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

            // Main EXPORT ZONE Header Badge
            var (resW, resH) = GetNewExportFileResolutionFromUi();
            double mpp = GetMetersPerPixelFromUi();
            double zoneMW = _exportAreaStats.PixelWidth * mpp;
            double zoneMH = _exportAreaStats.PixelHeight * mpp;
            string zoneMStr = zoneMW >= 10000.0 || zoneMH >= 10000.0
                ? $"{(zoneMW / 1000.0):0.##} × {(zoneMH / 1000.0):0.##} km"
                : $"{zoneMW:N0} × {zoneMH:N0} m";

            int totalExportPxW = resW * outCols;
            double effectiveExportMpp = totalExportPxW > 0 ? (zoneMW / totalExportPxW) : mpp;

            var panelStack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
            panelStack.Children.Add(new TextBlock
            {
                Text = showSubGrid ? $"EXPORT ZONE • {outRows}×{outCols} GRID ({outRows * outCols} TILES)" : "EXPORT ZONE • 1×1 SINGLE FILE",
                Foreground = (Brush)Resources["ThemeAccentExport"],
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 2)
            });
            panelStack.Children.Add(new TextBlock
            {
                Text = $"Ground: {zoneMStr} • {effectiveExportMpp:0.##} m/px",
                Foreground = Brushes.White,
                FontSize = 9.0,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            panelStack.Children.Add(new TextBlock
            {
                Text = showSubGrid ? $"Tile Res: {resW} × {resH} px/file" : $"Resolution: {resW} × {resH} px",
                Foreground = (Brush)Resources["ThemeAccentPrimary"],
                FontSize = 8.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(235, 14, 20, 16)),
                BorderBrush = outlineBrush,
                BorderThickness = new Thickness(1.2),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 4, 8, 4),
                IsHitTestVisible = false,
                Child = panelStack
            };

            badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double badgeW = badge.DesiredSize.Width > 0 ? badge.DesiredSize.Width : 160.0;
            double badgeH = badge.DesiredSize.Height > 0 ? badge.DesiredSize.Height : 52.0;

            double cx = (rx0 + rx1) * 0.5;

            // Horizontally centered on the custom export zone square
            double badgeX = Math.Clamp(cx - badgeW * 0.5, 4.0, Math.Max(4.0, w - badgeW - 4.0));

            // Positioned directly below the outside bottom edge of the custom grid zone
            double badgeY = ry1 + 8.0;

            // If badge would extend past the bottom of the canvas, flip above the zone if space allows, else clamp
            if (badgeY + badgeH > h - 4.0)
            {
                if (ry0 - badgeH - 8.0 >= 4.0)
                {
                    badgeY = ry0 - badgeH - 8.0;
                }
                else
                {
                    badgeY = Math.Max(4.0, h - badgeH - 4.0);
                }
            }

            Canvas.SetLeft(badge, badgeX);
            Canvas.SetTop(badge, badgeY);
            _mosaicOverlayCanvas.Children.Add(badge);

            // Sub-Tile / Region Info Tags in the corner of each export region (Dynamically scaled by cell dimensions and zoom)
            if (showSubGrid || isCustomArea)
            {
                var subCardBg = new SolidColorBrush(Color.FromArgb(215, 12, 18, 14));
                var subCardBorder = new SolidColorBrush(Color.FromArgb(180, 232, 168, 73));
                subCardBg.Freeze();
                subCardBorder.Freeze();

                for (int r = 0; r < outRows; r++)
                {
                    for (int c = 0; c < outCols; c++)
                    {
                        double cellX0 = rx0 + rw * (c / (double)outCols);
                        double cellY0 = ry0 + rh * (r / (double)outRows);
                        double cellW = rw / outCols;
                        double cellH = rh / outRows;

                        // Skip tag if cell is too small to display even a minimal indicator
                        if (cellW < 26.0 || cellH < 16.0) continue;

                        // Normalized scale factor between 30px (minimal) and 200px (full scale)
                        double minDim = Math.Min(cellW, cellH);
                        double t = Math.Clamp((minDim - 30.0) / 170.0, 0.0, 1.0);

                        // Dynamically scale typography, padding, corner radius, and offsets
                        double fontSize = 7.5 + (3.5 * t);          // 7.5pt -> 11.0pt
                        double padH = 3.0 + (4.0 * t);              // 3.0px -> 7.0px
                        double padV = 1.0 + (2.5 * t);              // 1.0px -> 3.5px
                        double cornerOffset = 2.5 + (3.5 * t);      // 2.5px -> 6.0px
                        double cornerRadius = 2.5 + (2.5 * t);      // 2.5px -> 5.0px
                        double borderThickness = 0.7 + (0.5 * t);   // 0.7px -> 1.2px

                        // Select text format according to available cell dimensions (Adaptive LOD)
                        string pillText;
                        if (showSubGrid)
                        {
                            if (cellW > 180.0 && cellH > 46.0)
                            {
                                double subSpanM = zoneMW / outCols;
                                string subSpanStr = subSpanM >= 1000.0 ? $"{subSpanM / 1000.0:0.#}km" : $"{subSpanM:N0}m";
                                pillText = $"x{c}_y{r} • {resW}×{resH} • {subSpanStr}";
                            }
                            else if (cellW > 105.0 && cellH > 32.0)
                            {
                                pillText = $"x{c}_y{r} • {resW}×{resH}";
                            }
                            else if (cellW > 45.0 && cellH > 20.0)
                            {
                                pillText = $"x{c}_y{r}";
                            }
                            else
                            {
                                pillText = $"x{c}y{r}";
                            }
                        }
                        else
                        {
                            if (cellW > 220.0 && cellH > 50.0)
                            {
                                pillText = $"Single File • {resW}×{resH} px";
                            }
                            else if (cellW > 110.0 && cellH > 32.0)
                            {
                                pillText = $"1×1 • {resW}×{resH}";
                            }
                            else
                            {
                                pillText = "1×1";
                            }
                        }

                        var textBlock = new TextBlock
                        {
                            Text = pillText,
                            Foreground = (Brush)Resources["ThemeAccentPrimary"],
                            FontSize = fontSize,
                            FontWeight = FontWeights.SemiBold
                        };

                        var pill = new Border
                        {
                            Background = subCardBg,
                            BorderBrush = subCardBorder,
                            BorderThickness = new Thickness(borderThickness),
                            CornerRadius = new CornerRadius(cornerRadius),
                            Padding = new Thickness(padH, padV, padH, padV),
                            IsHitTestVisible = false,
                            Child = textBlock
                        };

                        // Verify that pill width fits comfortably within cell; downgrade to shorter text if cramped
                        pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        if (pill.DesiredSize.Width > cellW - (cornerOffset * 2.0 + 4.0))
                        {
                            textBlock.Text = showSubGrid ? $"x{c}_y{r}" : "1×1";
                            pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                            if (pill.DesiredSize.Width > cellW - (cornerOffset * 2.0))
                            {
                                textBlock.Text = showSubGrid ? $"x{c}y{r}" : "1";
                            }
                        }

                        Canvas.SetLeft(pill, cellX0 + cornerOffset);
                        Canvas.SetTop(pill, cellY0 + cornerOffset);
                        _mosaicOverlayCanvas.Children.Add(pill);
                    }
                }
            }

            // 4. Center Transform Control Gizmo (Reposition Handle for Custom Zone)
            if (isCustomArea)
            {
                RenderTransformGizmo(rx0, ry0, rx1, ry1, w, h);
            }
        }
    }

    /// <summary>
    /// Renders an interactive Transform Control Gizmo in the center of the custom export zone with X/Y directional arrows and a central move hub.
    /// </summary>
    private void RenderTransformGizmo(double rx0, double ry0, double rx1, double ry1, double canvasW, double canvasH)
    {
        if (_mosaicOverlayCanvas == null) return;

        double cx = (rx0 + rx1) * 0.5;
        double cy = (ry0 + ry1) * 0.5;
        double rw = rx1 - rx0;
        double rh = ry1 - ry0;

        // Scale gizmo slightly if custom area is very small
        double minBoxDim = Math.Min(rw, rh);
        double gizmoScale = Math.Clamp(minBoxDim / 100.0, 0.65, 1.0);

        double armLength = 26.0 * gizmoScale;
        double arrowHeadSize = 6.0 * gizmoScale;
        double hubRadius = 13.0 * gizmoScale;
        double hubDiameter = hubRadius * 2.0;

        // Distinct transform axis brushes (Red for X / East-West, Green for Y / North-South)
        var xBrush = new SolidColorBrush(Color.FromRgb(255, 75, 75));
        var yBrush = new SolidColorBrush(Color.FromRgb(65, 215, 95));
        xBrush.Freeze();
        yBrush.Freeze();

        var hubBorderBrush = _isDraggingGizmo
            ? new SolidColorBrush(Color.FromRgb(255, 215, 60))
            : new SolidColorBrush(Color.FromRgb(232, 168, 73));
        hubBorderBrush.Freeze();

        var hubBgBrush = _isDraggingGizmo
            ? new SolidColorBrush(Color.FromArgb(240, 48, 52, 42))
            : new SolidColorBrush(Color.FromArgb(220, 16, 22, 18));
        hubBgBrush.Freeze();

        // 1. Four Directional Transform Axis Lines with Arrowheads
        // East (X+)
        AddGizmoArrow(cx + hubRadius * 0.7, cy, cx + armLength, cy, xBrush, arrowHeadSize, isHorizontal: true, isPositive: true);
        // West (X-)
        AddGizmoArrow(cx - hubRadius * 0.7, cy, cx - armLength, cy, xBrush, arrowHeadSize, isHorizontal: true, isPositive: false);
        // North (Y-)
        AddGizmoArrow(cx, cy - hubRadius * 0.7, cx, cy - armLength, yBrush, arrowHeadSize, isHorizontal: false, isPositive: false);
        // South (Y+)
        AddGizmoArrow(cx, cy + hubRadius * 0.7, cx, cy + armLength, yBrush, arrowHeadSize, isHorizontal: false, isPositive: true);

        // 2. Center Hub Container with DropShadow
        var hub = new Border
        {
            Width = hubDiameter,
            Height = hubDiameter,
            CornerRadius = new CornerRadius(hubRadius),
            Background = hubBgBrush,
            BorderBrush = hubBorderBrush,
            BorderThickness = new Thickness(_isDraggingGizmo ? 2.2 : 1.6),
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 8,
                ShadowDepth = 2,
                Opacity = 0.75
            }
        };

        // Center 4-way move crosshair / translate icon
        var iconText = new TextBlock
        {
            Text = "\u2725",
            FontFamily = new FontFamily("Segoe UI Symbol, Arial, sans-serif"),
            FontSize = 13.5 * gizmoScale,
            FontWeight = FontWeights.Bold,
            Foreground = _isDraggingGizmo ? new SolidColorBrush(Color.FromRgb(255, 215, 60)) : Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        hub.Child = iconText;

        Canvas.SetLeft(hub, cx - hubRadius);
        Canvas.SetTop(hub, cy - hubRadius);
        _mosaicOverlayCanvas.Children.Add(hub);
    }

    private void AddGizmoArrow(double x1, double y1, double x2, double y2, Brush stroke, double headSize, bool isHorizontal, bool isPositive)
    {
        if (_mosaicOverlayCanvas == null) return;

        var shaft = new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = stroke,
            StrokeThickness = 2.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        _mosaicOverlayCanvas.Children.Add(shaft);

        var head = new Polygon
        {
            Fill = stroke,
            IsHitTestVisible = false
        };

        if (isHorizontal)
        {
            double tipX = x2;
            double tipY = y2;
            double baseX = isPositive ? (x2 - headSize) : (x2 + headSize);
            head.Points = new PointCollection
            {
                new Point(tipX, tipY),
                new Point(baseX, tipY - headSize * 0.6),
                new Point(baseX, tipY + headSize * 0.6)
            };
        }
        else
        {
            double tipX = x2;
            double tipY = y2;
            double baseY = isPositive ? (y2 - headSize) : (y2 + headSize);
            head.Points = new PointCollection
            {
                new Point(tipX, tipY),
                new Point(tipX - headSize * 0.6, baseY),
                new Point(tipX + headSize * 0.6, baseY)
            };
        }

        _mosaicOverlayCanvas.Children.Add(head);
    }

    private bool IsPointOverGizmo(Point pt)
    {
        if (!IsCustomExportAreaActive || _mosaicOverlayCanvas == null || _tiles.Count == 0) return false;

        double w = _mosaicCanvasWidth;
        double h = _mosaicCanvasHeight;
        if (w <= 1 || h <= 1) return false;

        double rx0 = _exportNormX0 * w;
        double ry0 = _exportNormY0 * h;
        double rx1 = _exportNormX1 * w;
        double ry1 = _exportNormY1 * h;

        double cx = (rx0 + rx1) * 0.5;
        double cy = (ry0 + ry1) * 0.5;

        double dx = pt.X - cx;
        double dy = pt.Y - cy;

        double minBoxDim = Math.Min(rx1 - rx0, ry1 - ry0);
        double gizmoScale = Math.Clamp(minBoxDim / 100.0, 0.65, 1.0);
        double hitRadius = 28.0 * gizmoScale;

        return (dx * dx + dy * dy) <= (hitRadius * hitRadius);
    }

    private void SvGridMosaic_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true; // Prevent default vertical scroll / panning
        if (ScaleGridMosaic == null || SvGridMosaic == null) return;

        Point mousePos = e.GetPosition(SvGridMosaic);
        double oldScale = ScaleGridMosaic.ScaleX;
        double zoomFactor = e.Delta > 0 ? 1.15 : (1.0 / 1.15);
        double newScale = Math.Clamp(oldScale * zoomFactor, 1.0, 10.0);

        if (Math.Abs(newScale - oldScale) < 0.0001) return;

        ScaleGridMosaic.ScaleX = newScale;
        ScaleGridMosaic.ScaleY = newScale;
        SvGridMosaic.UpdateLayout();

        if (newScale <= 1.001)
        {
            SvGridMosaic.ScrollToHorizontalOffset(0);
            SvGridMosaic.ScrollToVerticalOffset(0);
        }
        else
        {
            double contentX = (mousePos.X + SvGridMosaic.HorizontalOffset) / oldScale;
            double contentY = (mousePos.Y + SvGridMosaic.VerticalOffset) / oldScale;
            double newHOffset = contentX * newScale - mousePos.X;
            double newVOffset = contentY * newScale - mousePos.Y;
            SvGridMosaic.ScrollToHorizontalOffset(Math.Max(0, newHOffset));
            SvGridMosaic.ScrollToVerticalOffset(Math.Max(0, newVOffset));
        }
    }

    private void SvGridMosaic_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.MiddleButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed)
        {
            _isPanningMosaic = true;
            _mosaicPanStartPt = e.GetPosition(SvGridMosaic);
            _mosaicPanStartHOffset = SvGridMosaic.HorizontalOffset;
            _mosaicPanStartVOffset = SvGridMosaic.VerticalOffset;
            SvGridMosaic.CaptureMouse();
            SvGridMosaic.Cursor = Cursors.SizeAll;
            e.Handled = true;
        }
    }

    private void SvGridMosaic_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isPanningMosaic)
        {
            Point curPt = e.GetPosition(SvGridMosaic);
            double dx = curPt.X - _mosaicPanStartPt.X;
            double dy = curPt.Y - _mosaicPanStartPt.Y;
            SvGridMosaic.ScrollToHorizontalOffset(_mosaicPanStartHOffset - dx);
            SvGridMosaic.ScrollToVerticalOffset(_mosaicPanStartVOffset - dy);
            e.Handled = true;
        }
    }

    private void SvGridMosaic_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isPanningMosaic)
        {
            _isPanningMosaic = false;
            SvGridMosaic.ReleaseMouseCapture();
            SvGridMosaic.Cursor = Cursors.Arrow;
            e.Handled = true;
        }
    }

    private void MosaicOverlayCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isMouseDownOnMosaic && _mosaicOverlayCanvas != null)
        {
            _mosaicOverlayCanvas.Cursor = Cursors.Cross;
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
        if (RbExportSourceGrid?.IsChecked == true)
        {
            if (_tiles.Count > 0)
            {
                int maxRow = _tiles.Max(t => t.GridRow) + 1;
                int maxCol = _tiles.Max(t => t.GridCol) + 1;
                return (Math.Max(1, maxRow), Math.Max(1, maxCol));
            }
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

        HighlightSelectedResolutionButton();
        UpdateExpectedOutputFilesList();
        UpdateDynamicScaleMatrix();
    }

    private void BottomTableTab_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || DgTiles == null || DgExpectedOutputs == null) return;

        bool showLoaded = RbTabLoadedTiles?.IsChecked == true;
        bool showOutputs = RbTabExpectedOutputs?.IsChecked == true;
        bool showMatrix = RbTabScaleMatrix?.IsChecked == true;

        DgTiles.Visibility = showLoaded ? Visibility.Visible : Visibility.Collapsed;
        DgExpectedOutputs.Visibility = showOutputs ? Visibility.Visible : Visibility.Collapsed;
        if (PnlScaleMatrix != null)
            PnlScaleMatrix.Visibility = showMatrix ? Visibility.Visible : Visibility.Collapsed;

        if (TxtBottomTableHint != null)
        {
            if (showLoaded)
            {
                TxtBottomTableHint.Text = "Use '✕' to remove a tile • Click a row to inspect";
            }
            else if (showOutputs)
            {
                TxtBottomTableHint.Text = $"{_expectedOutputs.Count} expected output file(s) based on current Export Layout, Resolution & Format";
            }
            else if (showMatrix)
            {
                TxtBottomTableHint.Text = "Live dynamic telemetry: Interconnected relationship between ground span (km), export resolution (px), ground scale (m/px) and source fidelity";
            }
        }
    }

    private double _lastExportMppForMatrix = 1.0;

    private void CnvHumanScaleReference_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderHumanScaleReference(_lastExportMppForMatrix);
    }

    private void UpdateDynamicScaleMatrix()
    {
        if (PnlScaleMatrix == null || TxtMatrixRegionSpanKm == null) return;

        if (_tiles.Count == 0)
        {
            TxtMatrixRegionSpanKm.Text = "—";
            TxtMatrixRegionSpanMeters.Text = "No tiles loaded";
            TxtMatrixRegionType.Text = "Import DEM files to begin";
            TxtMatrixTotalPixels.Text = "—";
            TxtMatrixPerTileResolution.Text = "—";
            TxtMatrixOutputLayout.Text = "—";
            TxtMatrixGroundScaleMpp.Text = "—";
            TxtMatrixSourceScaleMpp.Text = "Source DEM: —";
            TxtMatrixEstFileSize.Text = "—";
            TxtMatrixFormatDesc.Text = "—";
            TxtMatrixBatchSummary.Text = "Total Files: 0";
            TxtMatrixFidelityRatio.Text = "—";
            TxtMatrixFidelityTitle.Text = "AWAITING DEM DATA";
            TxtMatrixFidelityDesc.Text = "Import DEM GeoTIFF files or select a region of interest to inspect the live scale matrix.";
            TxtMatrixElevationRange.Text = "—";
            if (TxtMatrixHumanRatio != null) TxtMatrixHumanRatio.Text = "1 Step = —";
            CnvHumanScaleReference?.Children.Clear();
            if (BdrMatrixFidelityBadge != null)
                BdrMatrixFidelityBadge.Background = (Brush)Resources["ThemeBorderCard"];
            return;
        }

        var (rows, cols) = GetNewExportGridDimensionsFromUi();
        var (fileW, fileH) = GetNewExportFileResolutionFromUi();
        int totalFiles = rows * cols;
        long totalPxW = (long)fileW * cols;
        long totalPxH = (long)fileH * rows;

        double mpp = GetMetersPerPixelFromUi();
        bool isCustom = IsCustomExportAreaActive;
        double physW = _exportAreaStats.PixelWidth * mpp;
        double physH = _exportAreaStats.PixelHeight * mpp;
        double areaSqKm = (physW / 1000.0) * (physH / 1000.0);

        TxtMatrixRegionSpanKm.Text = $"{FormatMetricDistance(physW)} × {FormatMetricDistance(physH)}";
        TxtMatrixRegionSpanMeters.Text = $"{physW:N0} m × {physH:N0} m ({areaSqKm:0.##} km²)";
        TxtMatrixRegionType.Text = isCustom ? "Custom Crop Selection" : "Full Global Mosaic";

        TxtMatrixTotalPixels.Text = $"{totalPxW:N0} × {totalPxH:N0} px";
        TxtMatrixPerTileResolution.Text = totalFiles == 1 ? "1 Tile @ Full Resolution" : $"{fileW} × {fileH} px per tile";
        TxtMatrixOutputLayout.Text = totalFiles == 1 ? "1 Single Merged File (1×1)" : $"{rows} × {cols} Grid ({totalFiles} Tiles)";

        double effectiveExportMpp = totalPxW > 0 ? (physW / totalPxW) : mpp;
        TxtMatrixGroundScaleMpp.Text = $"{effectiveExportMpp:0.##} m/px";
        TxtMatrixSourceScaleMpp.Text = $"Source DEM: {mpp:0.##} m/px";

        int fmtIdx = CmbExportFileFormat?.SelectedIndex ?? 0;
        bool exportAsTiff = fmtIdx == 1 || fmtIdx == 2;
        bool tiffFloat32 = fmtIdx == 1;
        string formatDesc = exportAsTiff
            ? (tiffFloat32 ? "32-Bit Float GeoTIFF" : "16-Bit Norm GeoTIFF")
            : "16-Bit Norm PNG";

        long estBytesPerFile = exportAsTiff
            ? ((long)fileW * fileH * (tiffFloat32 ? 4L : 2L) + 4096L)
            : ((long)(fileW * fileH * 2.0 * 0.65) + 1024L);
        long totalEstBytes = estBytesPerFile * totalFiles;

        string totalEstSizeStr = totalEstBytes >= 1024 * 1024
            ? $"~{totalEstBytes / (1024.0 * 1024.0):0.1} MB"
            : $"~{Math.Max(1.0, totalEstBytes / 1024.0):0} KB";

        TxtMatrixEstFileSize.Text = totalEstSizeStr;
        TxtMatrixFormatDesc.Text = formatDesc;
        TxtMatrixBatchSummary.Text = totalFiles == 1 ? "1 Output File" : $"Batch: {totalFiles} Files (~{estBytesPerFile / (1024.0 * 1024.0):0.1} MB/file)";

        var (dispMin, dispMax) = GetEffectiveNormalizationRange();
        float heightRange = Math.Max(0f, dispMax - dispMin);
        TxtMatrixElevationRange.Text = $"[{dispMin:F1}m – {dispMax:F1}m]  Δ{heightRange:F1}m";

        // Fidelity Ratio & Qualitative Indicator
        double ratio = _exportAreaStats.PixelWidth > 0 ? (totalPxW / (double)_exportAreaStats.PixelWidth) : 1.0;
        string simpRatio = FormatSimplifiedRatio(totalPxW, Math.Max(1, _exportAreaStats.PixelWidth));

        if (BdrMatrixFidelityBadge != null)
        {
            if (Math.Abs(ratio - 1.0) < 0.05)
            {
                BdrMatrixFidelityBadge.Background = (Brush)Resources["ThemeAccentExport"];
                TxtMatrixFidelityRatio.Text = $"{simpRatio} (1:1 PARITY)";
                TxtMatrixFidelityTitle.Text = "NATIVE PIXEL-TO-METER FIDELITY (EXACT 1:1)";
                TxtMatrixFidelityDesc.Text = "Export resolution matches source DEM data sampling exactly. Optimal fidelity with zero blur or interpolation.";
            }
            else if (ratio < 0.95)
            {
                BdrMatrixFidelityBadge.Background = (Brush)Resources["ThemeAccentPrimary"];
                TxtMatrixFidelityRatio.Text = $"{ratio:0.##}× (DOWNSAMPLED)";
                TxtMatrixFidelityTitle.Text = "DOWNSAMPLED HIGH-DENSITY TERRAIN";
                TxtMatrixFidelityDesc.Text = "Heightmap pixels consolidate multiple source DEM pixels. Produces clean, crisp rendering and compact storage.";
            }
            else if (ratio <= 2.0)
            {
                BdrMatrixFidelityBadge.Background = (Brush)Resources["ThemeAccentWarning"];
                TxtMatrixFidelityRatio.Text = $"{ratio:0.##}× (UPSAMPLED)";
                TxtMatrixFidelityTitle.Text = "INTERPOLATED UPSAMPLED RESOLUTION";
                TxtMatrixFidelityDesc.Text = "Output resolution is finer than source DEM data. Sub-pixel bilinear interpolation smooths high frequencies.";
            }
            else
            {
                BdrMatrixFidelityBadge.Background = (Brush)Resources["ThemeAccentDanger"];
                TxtMatrixFidelityRatio.Text = $"{ratio:0.##}× (OVER-SAMPLED)";
                TxtMatrixFidelityTitle.Text = "HIGH OVERSAMPLING WARNING (>2× SOURCE)";
                TxtMatrixFidelityDesc.Text = "Output resolution heavily exceeds source topographic detail. Results in bloated file sizes without adding new real elevation features.";
            }
        }

        // Pillar 5: Dynamic Human Scale Reference vs Pixel Step Stairs
        _lastExportMppForMatrix = effectiveExportMpp;
        RenderHumanScaleReference(effectiveExportMpp);
    }

    private void RenderHumanScaleReference(double effectiveExportMpp)
    {
        if (CnvHumanScaleReference == null) return;
        CnvHumanScaleReference.Children.Clear();

        double w = CnvHumanScaleReference.ActualWidth;
        double h = CnvHumanScaleReference.ActualHeight;
        if (w < 20.0 || h < 20.0) return;

        double stepM = Math.Max(0.01, effectiveExportMpp);
        const double humanM = 1.75;
        double ratio = stepM / humanM;

        // Telemetry Ratio Header
        if (TxtMatrixHumanRatio != null)
        {
            string stepLabel = stepM >= 1000.0 ? $"{stepM / 1000.0:0.##}km" :
                               stepM >= 10.0 ? $"{stepM:0.#}m" :
                               $"{stepM:0.##}m";

            if (ratio < 0.2)
                TxtMatrixHumanRatio.Text = $"1 Step = {stepLabel} (Ankle)";
            else if (ratio < 0.5)
                TxtMatrixHumanRatio.Text = $"1 Step = {stepLabel} (Knee)";
            else if (ratio < 0.85)
                TxtMatrixHumanRatio.Text = $"1 Step = {stepLabel} (Waist)";
            else if (ratio <= 1.15)
                TxtMatrixHumanRatio.Text = $"1 Step = {stepLabel} (1:1 Human)";
            else if (ratio < 3.0)
                TxtMatrixHumanRatio.Text = $"1 Step = {stepLabel} ({ratio:0.#}× Tall)";
            else if (ratio < 10.0)
                TxtMatrixHumanRatio.Text = $"1 Step = {stepLabel} ({ratio:0.#}× Cliff)";
            else
                TxtMatrixHumanRatio.Text = $"1 Step = {stepLabel} ({ratio:0.#}× Monolith)";
        }

        double groundY = h - 6.0;
        double targetHumanPx;
        double targetStepPx;

        if (ratio <= 1.0)
        {
            // Human is larger than or equal to 1 step
            targetHumanPx = Math.Clamp(h * 0.70, 24.0, 42.0);
            double ppm = targetHumanPx / humanM;
            targetStepPx = Math.Max(2.5, stepM * ppm);
        }
        else if (ratio <= 6.0)
        {
            // Step is 1x to 6x human
            targetStepPx = Math.Clamp(h * 0.76, 26.0, 45.0);
            double ppm = targetStepPx / stepM;
            targetHumanPx = Math.Max(12.0, humanM * ppm);
        }
        else
        {
            // Step is huge (cliff / mountain pixel)
            targetHumanPx = 12.0; // Keep human recognizable
            targetStepPx = h - 10.0;
        }

        var accentBrush = (Brush)Resources["ThemeAccentExport"] ?? Brushes.Goldenrod;
        var primaryBrush = (Brush)Resources["ThemeAccentPrimary"] ?? Brushes.Cyan;
        var textMuted = (Brush)Resources["ThemeTextMuted"] ?? Brushes.Gray;
        var stepFill = new SolidColorBrush(Color.FromArgb(50, 232, 168, 73));
        stepFill.Freeze();

        // Ground baseline
        var groundLine = new Line
        {
            X1 = 4.0,
            Y1 = groundY,
            X2 = w - 4.0,
            Y2 = groundY,
            Stroke = textMuted,
            StrokeThickness = 1.0,
            StrokeDashArray = new DoubleCollection { 3, 2 },
            Opacity = 0.6
        };
        CnvHumanScaleReference.Children.Add(groundLine);

        // Anchor positions: place human on left, stairs in center/right
        double baseX = Math.Clamp(w * 0.22, 28.0, 50.0);
        double stepStartX = baseX + 0.22 * targetHumanPx;

        // Draw Stairs (Pixel Steps)
        if (ratio <= 0.45)
        {
            // Multiple small steps (like a real staircase)
            int numSteps = Math.Clamp((int)Math.Round(1.75 / stepM), 2, 5);
            double curX = stepStartX;
            double curY = groundY;

            var stairGeom = new StreamGeometry();
            using (var ctx = stairGeom.Open())
            {
                ctx.BeginFigure(new Point(curX, groundY), true, true);
                for (int i = 0; i < numSteps; i++)
                {
                    curY -= targetStepPx;
                    ctx.LineTo(new Point(curX, curY), true, false);
                    curX += targetStepPx;
                    ctx.LineTo(new Point(curX, curY), true, false);
                }
                ctx.LineTo(new Point(curX, groundY), true, false);
            }
            stairGeom.Freeze();

            var stairPath = new System.Windows.Shapes.Path
            {
                Data = stairGeom,
                Fill = stepFill,
                Stroke = accentBrush,
                StrokeThickness = 1.2
            };
            CnvHumanScaleReference.Children.Add(stairPath);

            // Step dimension label
            var txtStep = new TextBlock
            {
                Text = $"{stepM:0.##}m / px",
                FontSize = 7.5,
                FontWeight = FontWeights.Bold,
                Foreground = accentBrush
            };
            Canvas.SetLeft(txtStep, Math.Min(w - 55.0, stepStartX + 4.0));
            Canvas.SetTop(txtStep, Math.Max(2.0, curY - 12.0));
            CnvHumanScaleReference.Children.Add(txtStep);
        }
        else
        {
            // 1 or 2 large steps / cliff
            double stepTopY = Math.Max(4.0, groundY - targetStepPx);
            double stepEndX = Math.Min(w - 8.0, stepStartX + Math.Max(targetStepPx, 35.0));

            var stairGeom = new StreamGeometry();
            using (var ctx = stairGeom.Open())
            {
                ctx.BeginFigure(new Point(stepStartX, groundY), true, true);
                ctx.LineTo(new Point(stepStartX, stepTopY), true, false);
                ctx.LineTo(new Point(stepEndX, stepTopY), true, false);
                ctx.LineTo(new Point(stepEndX, groundY), true, false);
            }
            stairGeom.Freeze();

            var stairPath = new System.Windows.Shapes.Path
            {
                Data = stairGeom,
                Fill = stepFill,
                Stroke = accentBrush,
                StrokeThickness = 1.3
            };
            CnvHumanScaleReference.Children.Add(stairPath);

            // Pixel Step Dimension Pill
            string stepDimText = ratio > 5.0 ? $"1 PX = {stepM:0.#}m (CLIFF)" : $"1 PX = {stepM:0.##}m";
            var txtStep = new TextBlock
            {
                Text = stepDimText,
                FontSize = 8.0,
                FontWeight = FontWeights.Bold,
                Foreground = accentBrush
            };
            Canvas.SetLeft(txtStep, Math.Min(w - 75.0, stepStartX + 5.0));
            Canvas.SetTop(txtStep, Math.Max(2.0, stepTopY - 12.0));
            CnvHumanScaleReference.Children.Add(txtStep);

            // Hatching lines for huge cliff steps
            if (ratio > 5.0)
            {
                for (double hy = stepTopY + 8.0; hy < groundY - 4.0; hy += 7.0)
                {
                    var hatch = new Line
                    {
                        X1 = stepStartX + 3.0,
                        Y1 = hy,
                        X2 = stepStartX + 12.0,
                        Y2 = hy - 4.0,
                        Stroke = accentBrush,
                        StrokeThickness = 0.8,
                        Opacity = 0.4
                    };
                    CnvHumanScaleReference.Children.Add(hatch);
                }
            }
        }

        // Draw Stepping Human Silhouette
        DrawSteppingHumanFigure(baseX, groundY, targetHumanPx, stepStartX, groundY - targetStepPx);

        // Height bracket for the human
        double bracketX = Math.Max(2.0, baseX - 10.0);
        double humanTopY = groundY - targetHumanPx;
        var bracketLine = new Line
        {
            X1 = bracketX,
            Y1 = humanTopY,
            X2 = bracketX,
            Y2 = groundY,
            Stroke = primaryBrush,
            StrokeThickness = 0.8,
            Opacity = 0.7
        };
        var tickTop = new Line { X1 = bracketX - 2.0, Y1 = humanTopY, X2 = bracketX + 2.0, Y2 = humanTopY, Stroke = primaryBrush, StrokeThickness = 0.8 };
        var tickBottom = new Line { X1 = bracketX - 2.0, Y1 = groundY, X2 = bracketX + 2.0, Y2 = groundY, Stroke = primaryBrush, StrokeThickness = 0.8 };
        CnvHumanScaleReference.Children.Add(bracketLine);
        CnvHumanScaleReference.Children.Add(tickTop);
        CnvHumanScaleReference.Children.Add(tickBottom);

        var txtHuman = new TextBlock
        {
            Text = "1.75m",
            FontSize = 7.0,
            FontWeight = FontWeights.SemiBold,
            Foreground = primaryBrush,
            Opacity = 0.9
        };
        Canvas.SetLeft(txtHuman, Math.Max(1.0, bracketX - 20.0));
        Canvas.SetTop(txtHuman, groundY - (targetHumanPx * 0.5) - 5.0);
        CnvHumanScaleReference.Children.Add(txtHuman);
    }

    private void DrawSteppingHumanFigure(double baseX, double baseY, double h, double stepX, double stepY)
    {
        var figureBrush = (Brush)Resources["ThemeTextPrimary"] ?? Brushes.White;
        double strokeW = Math.Max(1.3, h * 0.075);

        // Head
        double headR = Math.Max(1.5, h * 0.08);
        double headCx = baseX + 0.16 * h;
        double headCy = baseY - 0.90 * h;

        var head = new Ellipse
        {
            Width = headR * 2.0,
            Height = headR * 2.0,
            Fill = figureBrush
        };
        Canvas.SetLeft(head, headCx - headR);
        Canvas.SetTop(head, headCy - headR);
        CnvHumanScaleReference.Children.Add(head);

        // Torso (leaning slightly into the step)
        var shoulders = new Point(baseX + 0.14 * h, baseY - 0.77 * h);
        var hips = new Point(baseX + 0.08 * h, baseY - 0.48 * h);
        AddFigureLine(shoulders, hips, strokeW, figureBrush);

        // Back leg (supporting on ground)
        var backKnee = new Point(baseX + 0.03 * h, baseY - 0.25 * h);
        var backAnkle = new Point(baseX + 0.00 * h, baseY - 0.03 * h);
        AddFigureLine(hips, backKnee, strokeW, figureBrush);
        AddFigureLine(backKnee, backAnkle, strokeW, figureBrush);
        AddFigureLine(new Point(baseX - 0.04 * h, baseY), new Point(baseX + 0.04 * h, baseY), strokeW * 0.9, figureBrush);

        // Front leg (stepping up onto the step tread)
        double targetStepRise = baseY - stepY;
        double stepContactY = (targetStepRise > 0.0 && targetStepRise < h * 0.6) ? stepY : (baseY - Math.Min(h * 0.45, Math.Max(4.0, targetStepRise)));
        double frontFootX = Math.Max(baseX + 0.22 * h, stepX + 3.0);
        var frontKnee = new Point(baseX + 0.20 * h, Math.Min(baseY - 0.40 * h, stepContactY - 0.06 * h));
        var frontAnkle = new Point(frontFootX, stepContactY - 0.02 * h);

        AddFigureLine(hips, frontKnee, strokeW, figureBrush);
        AddFigureLine(frontKnee, frontAnkle, strokeW, figureBrush);
        AddFigureLine(new Point(frontFootX - 0.02 * h, stepContactY), new Point(frontFootX + 0.06 * h, stepContactY), strokeW * 0.9, figureBrush);

        // Arms (Dynamic climbing posture)
        var leadElbow = new Point(baseX + 0.26 * h, baseY - 0.62 * h);
        var leadHand = new Point(baseX + 0.33 * h, baseY - 0.54 * h);
        AddFigureLine(shoulders, leadElbow, strokeW * 0.85, figureBrush);
        AddFigureLine(leadElbow, leadHand, strokeW * 0.85, figureBrush);

        var trailElbow = new Point(baseX - 0.01 * h, baseY - 0.64 * h);
        var trailHand = new Point(baseX - 0.06 * h, baseY - 0.52 * h);
        AddFigureLine(shoulders, trailElbow, strokeW * 0.85, figureBrush);
        AddFigureLine(trailElbow, trailHand, strokeW * 0.85, figureBrush);
    }

    private void AddFigureLine(Point p1, Point p2, double thickness, Brush brush)
    {
        var line = new Line
        {
            X1 = p1.X,
            Y1 = p1.Y,
            X2 = p2.X,
            Y2 = p2.Y,
            Stroke = brush,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        CnvHumanScaleReference.Children.Add(line);
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

        int fileIndex = 1;
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
                    Index: fileIndex++,
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

        HighlightSelectedResolutionButton();
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
        HighlightSelectedResolutionButton();
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
        HighlightSelectedResolutionButton();
        UpdateNewExportResolutionSummary();
        RenderExportAreaOverlay();
    }

    private void TxtNewExportHeight_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isSyncingExportResolutionBoxes) return;
        _matchNativeExportResolution = false;
        HighlightSelectedResolutionButton();
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
                ("1009", "1009", "Unreal Engine Landscape recommended heightmap resolution: 1009 \u00d7 1009 (16\u00d716 components, 63\u00d763 quads)"),
                ("2017", "2017", "Unreal Engine Landscape recommended heightmap resolution: 2017 \u00d7 2017 (16\u00d716 components, 126\u00d7126 quads)"),
                ("4033", "4033", "Unreal Engine Landscape recommended heightmap resolution: 4033 \u00d7 4033 (32\u00d732 components, 126\u00d7126 quads)"),
                ("8129", "8129", "Unreal Engine Landscape recommended heightmap resolution: 8129 \u00d7 8129 (32\u00d732 components, 254\u00d7254 quads)"),
                ("16129", "16129", "Unreal Engine Landscape 16K recommended heightmap resolution: 16129 \u00d7 16129 (64\u00d764 components, 254\u00d7254 quads)")
            },
            2 => new[]
            {
                ("1025", "1025", "Unity Terrain standard heightmap resolution (2^n + 1): 1025 \u00d7 1025"),
                ("2049", "2049", "Unity Terrain standard heightmap resolution (2^n + 1): 2049 \u00d7 2049"),
                ("4097", "4097", "Unity Terrain maximum standard heightmap resolution (2^n + 1): 4097 \u00d7 4097"),
                ("8193", "8193", "Unity Terrain standard heightmap resolution (2^n + 1): 8193 \u00d7 8193"),
                ("16385", "16385", "Unity Terrain 16K heightmap resolution (2^n + 1): 16385 \u00d7 16385")
            },
            _ => new[]
            {
                ("1024", "1024", "Power-of-Two resolution: 1024 \u00d7 1024 px"),
                ("2048", "2048", "Power-of-Two resolution: 2048 \u00d7 2048 px"),
                ("4096", "4096", "Power-of-Two resolution: 4096 \u00d7 4096 px"),
                ("8192", "8192", "Power-of-Two resolution: 8192 \u00d7 8192 px"),
                ("16384", "16384", "Power-of-Two 16K resolution: 16384 \u00d7 16384 px")
            }
        };

        BtnResPreset0.Content = "Source";
        BtnResPreset0.Tag = "Native";
        BtnResPreset0.ToolTip = "Set file resolution to exact 1:1 source pixels of the export area divided by the output grid";

        Button[] numericButtons = { BtnResPreset1, BtnResPreset2, BtnResPreset3, BtnResPreset4, BtnResPreset5 };
        for (int i = 0; i < numericButtons.Length; i++)
        {
            numericButtons[i].Content = presets[i].Label;
            numericButtons[i].Tag = presets[i].Tag;
            numericButtons[i].ToolTip = presets[i].Tooltip;
        }

        HighlightSelectedResolutionButton();
    }

    private void HighlightSelectedResolutionButton()
    {
        if (BtnResPreset0 == null || BtnResPreset1 == null || BtnResPreset2 == null ||
            BtnResPreset3 == null || BtnResPreset4 == null || BtnResPreset5 == null) return;

        var primaryStyle = (Style)FindResource("PrimaryButton");
        var secondaryStyle = (Style)FindResource("SecondaryButton");

        if (_matchNativeExportResolution)
        {
            BtnResPreset0.Style = primaryStyle;
            BtnResPreset1.Style = secondaryStyle;
            BtnResPreset2.Style = secondaryStyle;
            BtnResPreset3.Style = secondaryStyle;
            BtnResPreset4.Style = secondaryStyle;
            BtnResPreset5.Style = secondaryStyle;
            return;
        }

        BtnResPreset0.Style = secondaryStyle;
        var (w, _) = GetNewExportFileResolutionFromUi();
        string wStr = w.ToString(CultureInfo.InvariantCulture);

        Button[] numericButtons = { BtnResPreset1, BtnResPreset2, BtnResPreset3, BtnResPreset4, BtnResPreset5 };
        foreach (var btn in numericButtons)
        {
            btn.Style = (btn.Tag is string tag && tag == wStr) ? primaryStyle : secondaryStyle;
        }
    }

    private void BtnCenterExportArea_Click(object sender, RoutedEventArgs e)
    {
        if (_tiles.Count == 0) return;
        var mosaic = DemProcessor.BuildMosaicDescriptor(_tiles, _globalMin);
        if (mosaic.TotalWidth <= 0 || mosaic.TotalHeight <= 0) return;

        var (fileW, fileH) = GetNewExportFileResolutionFromUi();
        var (outRows, outCols) = GetNewExportGridDimensionsFromUi();

        int targetW = fileW * outCols;
        int targetH = fileH * outRows;

        targetW = Math.Clamp(targetW, 1, mosaic.TotalWidth);
        targetH = Math.Clamp(targetH, 1, mosaic.TotalHeight);

        double normW = Math.Min(1.0, (double)targetW / mosaic.TotalWidth);
        double normH = Math.Min(1.0, (double)targetH / mosaic.TotalHeight);

        _exportNormX0 = Math.Max(0.0, (1.0 - normW) * 0.5);
        _exportNormY0 = Math.Max(0.0, (1.0 - normH) * 0.5);
        _exportNormX1 = Math.Min(1.0, _exportNormX0 + normW);
        _exportNormY1 = Math.Min(1.0, _exportNormY0 + normH);

        _isAuto3DZScale = true;
        UpdateGlobalStatsDisplay();
        UpdateExportAreaMetricsAndUi();
        RenderExportAreaOverlay();
        Update2DScaleBar();

        double mpp = GetMetersPerPixelFromUi();
        double wM = _exportAreaStats.PixelWidth * mpp;
        double hM = _exportAreaStats.PixelHeight * mpp;
        TxtStatusBar.Text = $"Centered export area: {_exportAreaStats.PixelWidth} \u00d7 {_exportAreaStats.PixelHeight} px ({wM:N0}m \u00d7 {hM:N0}m) \u2022 Scale: {mpp:0.##} m/px \u2022 Elev [{_exportAreaStats.MinElevation:F2}m .. {_exportAreaStats.MaxElevation:F2}m]";

        if (RbView3D?.IsChecked == true && _tiles.Count > 0)
        {
            _ = Rebuild3DVisualizerAsync();
        }
    }

    private void BtnDivideResolution_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        var (curW, curH) = GetNewExportFileResolutionFromUi();
        int newW = Math.Max(1, curW / 2);
        int newH = Math.Max(1, curH / 2);
        _matchNativeExportResolution = false;
        _isSyncingExportResolutionBoxes = true;
        try
        {
            TxtNewExportWidth.Text = newW.ToString(CultureInfo.InvariantCulture);
            TxtNewExportHeight.Text = newH.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _isSyncingExportResolutionBoxes = false;
        }

        if (ChkLockExportAspectRatio?.IsChecked == true)
        {
            AdjustExportHeightForLockedAspectRatio();
        }

        UpdateNewExportResolutionSummary();
        HighlightSelectedResolutionButton();
        RenderExportAreaOverlay();
    }

    private void BtnMultiplyResolution_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        var (curW, curH) = GetNewExportFileResolutionFromUi();
        int newW = Math.Min(65536, curW * 2);
        int newH = Math.Min(65536, curH * 2);
        _matchNativeExportResolution = false;
        _isSyncingExportResolutionBoxes = true;
        try
        {
            TxtNewExportWidth.Text = newW.ToString(CultureInfo.InvariantCulture);
            TxtNewExportHeight.Text = newH.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _isSyncingExportResolutionBoxes = false;
        }

        if (ChkLockExportAspectRatio?.IsChecked == true)
        {
            AdjustExportHeightForLockedAspectRatio();
        }

        UpdateNewExportResolutionSummary();
        HighlightSelectedResolutionButton();
        RenderExportAreaOverlay();
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

        HighlightSelectedResolutionButton();
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
        if (BtnStopMain != null) BtnStopMain.IsEnabled = busy;
        PbProgress.IsIndeterminate = busy;
        if (!busy)
        {
            PbProgress.Value = 0;
        }
    }
}

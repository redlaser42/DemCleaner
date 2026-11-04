using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace DemCleaner.Models;

public enum FillMethod
{
    Minimum,
    Maximum,
    Mean,
    Interpolated
}

public enum TileFillModeOverride
{
    UseGlobalDefault,
    Minimum,
    Maximum,
    Mean,
    Interpolated
}

public enum NoDataComparison
{
    LessThanOrEqual,
    Equal,
    GreaterThanOrEqual
}

public class DemProcessingOptions
{
    public FillMethod FillMethod { get; set; } = FillMethod.Minimum;
    public bool UseGdalNoDataTag { get; set; } = true;
    public bool UseCustomThreshold { get; set; } = true;
    public NoDataComparison CustomComparison { get; set; } = NoDataComparison.LessThanOrEqual;
    public float CustomNoDataValue { get; set; } = -9999f;
    public bool FillUsingGlobalStats { get; set; } = false; // False = per-tile min/max/mean, True = global dataset min/max/mean
    public int InterpolationIterations { get; set; } = 10;
    public int SeamSmoothingSteps { get; set; } = 0;
}

public class DemTile : INotifyPropertyChanged
{
    private BitmapSource? _rawThumbnail;
    private BitmapSource? _processedThumbnail;
    private int _gridRow;
    private int _gridCol;
    private string _colCoordinateLabel = string.Empty;
    private string _rowCoordinateLabel = string.Empty;
    private TileFillModeOverride _fillOverride = TileFillModeOverride.UseGlobalDefault;
    private int _voidPixelCount;
    private float _minElevation;
    private float _maxElevation;
    private float _meanElevation;
    private string _status = "Loaded";
    private int _index;

    public static IReadOnlyList<TileFillModeOverride> AvailableFillOverrides { get; } =
        Enum.GetValues<TileFillModeOverride>();

    public int Index
    {
        get => _index;
        set { _index = value; OnPropertyChanged(); }
    }

    public string FilePath { get; init; } = string.Empty;
    public string FileName => Path.GetFileName(FilePath);
    public int Width { get; init; }
    public int Height { get; init; }
    public int BitsPerSample { get; init; }
    public string SampleFormatDescription { get; init; } = string.Empty;

    /// <summary>
    /// Raw elevation values loaded from the TIFF (row-major: Height * Width).
    /// </summary>
    public float[] RawData { get; init; } = Array.Empty<float>();

    /// <summary>
    /// Un-smoothed filled elevation values (before inter-tile seam smoothing) so seam smoothing steps can be adjusted quickly.
    /// </summary>
    public float[]? BaseFilledData { get; set; }

    /// <summary>
    /// Filled elevation values after applying the chosen empty-area fill strategy and optional inter-tile seam smoothing.
    /// </summary>
    public float[]? FilledData { get; set; }

    /// <summary>
    /// Boolean mask where true indicates the pixel was originally empty/NoData.
    /// </summary>
    public bool[]? VoidMask { get; set; }

    /// <summary>
    /// Optional GDAL_NODATA metadata tag (TIFF tag 42113) parsed from the file.
    /// </summary>
    public float? GdalNoDataValue { get; init; }

    /// <summary>
    /// Optional GeoTIFF ModelTiepointTag (X, Y geographic or projected coordinate) for automatic grid arrangement.
    /// </summary>
    public double? GeoOriginX { get; init; }
    public double? GeoOriginY { get; init; }
    public double? PixelScaleX { get; init; }
    public double? PixelScaleY { get; init; }

    /// <summary>
    /// Optional UTM zone (1..60) parsed from GeoTIFF GeoKeyDirectoryTag or USGS filename (e.g. USGS_1M_10_x56y475...).
    /// </summary>
    public int? UtmZone { get; set; }
    public bool IsNorthernHemisphere { get; set; } = true;

    /// <summary>
    /// Optional numeric X/Y index parsed from the filename (e.g. x56, y475).
    /// </summary>
    public double? ParsedFileX { get; set; }
    public double? ParsedFileY { get; set; }

    public int GridRow
    {
        get => _gridRow;
        set { _gridRow = value; OnPropertyChanged(); }
    }

    public int GridCol
    {
        get => _gridCol;
        set { _gridCol = value; OnPropertyChanged(); }
    }

    public string ColCoordinateLabel
    {
        get => _colCoordinateLabel;
        set { _colCoordinateLabel = value; OnPropertyChanged(); }
    }

    public string RowCoordinateLabel
    {
        get => _rowCoordinateLabel;
        set { _rowCoordinateLabel = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Per-tile fill mode override. Defaults to UseGlobalDefault so all tiles follow the global setting unless customized.
    /// </summary>
    public TileFillModeOverride FillOverride
    {
        get => _fillOverride;
        set
        {
            if (_fillOverride != value)
            {
                _fillOverride = value;
                OnPropertyChanged();
            }
        }
    }

    public FillMethod ResolveEffectiveFillMethod(FillMethod globalDefault) => FillOverride switch
    {
        TileFillModeOverride.Minimum => FillMethod.Minimum,
        TileFillModeOverride.Maximum => FillMethod.Maximum,
        TileFillModeOverride.Mean => FillMethod.Mean,
        TileFillModeOverride.Interpolated => FillMethod.Interpolated,
        _ => globalDefault
    };

    public int VoidPixelCount
    {
        get => _voidPixelCount;
        set
        {
            _voidPixelCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VoidPercentage));
        }
    }

    public double VoidPercentage => Width * Height > 0 ? (_voidPixelCount * 100.0) / (Width * Height) : 0.0;

    public float MinElevation
    {
        get => _minElevation;
        set
        {
            _minElevation = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ElevationRange));
        }
    }

    public float MaxElevation
    {
        get => _maxElevation;
        set
        {
            _maxElevation = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ElevationRange));
        }
    }

    public float ElevationRange => Math.Max(0f, _maxElevation - _minElevation);

    public float MeanElevation
    {
        get => _meanElevation;
        set { _meanElevation = value; OnPropertyChanged(); }
    }

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public BitmapSource? RawThumbnail
    {
        get => _rawThumbnail;
        set { _rawThumbnail = value; OnPropertyChanged(); }
    }

    public BitmapSource? ProcessedThumbnail
    {
        get => _processedThumbnail;
        set { _processedThumbnail = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record ExpectedOutputFileItem(
    int Index,
    string OutputFileName,
    string GridSlotLabel,
    string FormatDescription,
    string OutputResolutionLabel,
    string SourceAreaPixelsLabel,
    string ResolutionRatioLabel,
    string PhysicalSizeLabel,
    float SubMinElevation,
    float SubMaxElevation,
    float SubElevationRange,
    string EstimatedFileSizeLabel,
    string FullOutputPath);

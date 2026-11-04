using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BitMiracle.LibTiff.Classic;
using DemCleaner.Models;

namespace DemCleaner.Services;

public static class TiffDemReader
{
    private const TiffTag TIFFTAG_GDAL_NODATA = (TiffTag)42113;
    private const TiffTag TIFFTAG_MODELPIXELSCALETAG = (TiffTag)33550;
    private const TiffTag TIFFTAG_MODELTIEPOINTTAG = (TiffTag)33922;
    private const TiffTag TIFFTAG_GEOKEYDIRECTORYTAG = (TiffTag)34735;
    private const TiffTag TIFFTAG_GEOASCIIPARAMSTAG = (TiffTag)34737;

    // Explicit USGS 1m pattern: e.g. "USGS_1M_10_x56y475_OR_SouthEast_D22"
    private static readonly Regex UsgsPattern = new(
        @"USGS_(?:1M|1m|NED)_(\d{1,2})_[xX](\d+)[yY](\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // General X/Y coordinate pattern in filename: e.g. "x56y475" or "x56_y475"
    private static readonly Regex ExplicitXyPattern = new(
        @"[xX]_?(-?\d+(?:\.\d+)?)[_\-\s]*[yY]_?(-?\d+(?:\.\d+)?)",
        RegexOptions.Compiled);

    // General Row/Col pattern in filename: e.g. "r0_c1" or "y475_x56"
    private static readonly Regex ExplicitRowColPattern = new(
        @"(?:[rR]_?(\d+)[_\-\s]*[cC]_?(\d+))|(?:[yY]_?(-?\d+)[_\-\s]*[xX]_?(-?\d+))",
        RegexOptions.Compiled);

    static TiffDemReader()
    {
        Tiff.SetTagExtender(TagExtender);
    }

    public static void EnsureTagsRegistered()
    {
        // Triggers the static constructor if not already run
    }

    private static void TagExtender(Tiff tif)
    {
        TiffFieldInfo[] tiffFieldInfo =
        {
            new TiffFieldInfo(TIFFTAG_GDAL_NODATA, -1, -1, TiffType.ASCII, FieldBit.Custom, true, false, "GDAL_NODATA"),
            new TiffFieldInfo(TIFFTAG_MODELPIXELSCALETAG, -1, -1, TiffType.DOUBLE, FieldBit.Custom, false, true, "ModelPixelScaleTag"),
            new TiffFieldInfo(TIFFTAG_MODELTIEPOINTTAG, -1, -1, TiffType.DOUBLE, FieldBit.Custom, false, true, "ModelTiepointTag"),
            new TiffFieldInfo(TIFFTAG_GEOKEYDIRECTORYTAG, -1, -1, TiffType.SHORT, FieldBit.Custom, false, true, "GeoKeyDirectoryTag"),
            new TiffFieldInfo(TIFFTAG_GEOASCIIPARAMSTAG, -1, -1, TiffType.ASCII, FieldBit.Custom, true, false, "GeoAsciiParamsTag")
        };
        tif.MergeFieldInfo(tiffFieldInfo, tiffFieldInfo.Length);
    }

    public static DemTile LoadTile(string filePath)
    {
        using Tiff? tif = Tiff.Open(filePath, "r");
        if (tif == null)
        {
            throw new InvalidDataException($"Could not open TIFF file: {filePath}");
        }

        int width = tif.GetField(TiffTag.IMAGEWIDTH)?[0].ToInt() ?? 0;
        int height = tif.GetField(TiffTag.IMAGELENGTH)?[0].ToInt() ?? 0;
        int bitsPerSample = tif.GetField(TiffTag.BITSPERSAMPLE)?[0].ToInt() ?? 8;
        int samplesPerPixel = tif.GetField(TiffTag.SAMPLESPERPIXEL)?[0].ToInt() ?? 1;
        SampleFormat sampleFormat = SampleFormat.UINT;
        FieldValue[]? fmtField = tif.GetField(TiffTag.SAMPLEFORMAT);
        if (fmtField != null && fmtField.Length > 0)
        {
            sampleFormat = (SampleFormat)fmtField[0].ToInt();
        }

        PlanarConfig planarConfig = PlanarConfig.CONTIG;
        FieldValue[]? planarField = tif.GetField(TiffTag.PLANARCONFIG);
        if (planarField != null && planarField.Length > 0)
        {
            planarConfig = (PlanarConfig)planarField[0].ToInt();
        }

        float? gdalNoData = TryReadGdalNoData(tif);
        (double? originX, double? originY) = TryReadGeoTiepoint(tif);
        (double? scaleX, double? scaleY) = TryReadPixelScale(tif);
        (int? utmZone, bool isNorth) = TryReadUtmZoneFromGeoTiff(tif);

        float[] rawData = new float[width * height];

        if (tif.IsTiled())
        {
            ReadTiledTiff(tif, width, height, bitsPerSample, samplesPerPixel, sampleFormat, planarConfig, rawData);
        }
        else
        {
            ReadScanlineTiff(tif, width, height, bitsPerSample, samplesPerPixel, sampleFormat, planarConfig, rawData);
        }

        string formatDesc = $"{bitsPerSample}-bit {sampleFormat}";

        var tile = new DemTile
        {
            FilePath = filePath,
            Width = width,
            Height = height,
            BitsPerSample = bitsPerSample,
            SampleFormatDescription = formatDesc,
            RawData = rawData,
            GdalNoDataValue = gdalNoData,
            GeoOriginX = originX,
            GeoOriginY = originY,
            PixelScaleX = scaleX,
            PixelScaleY = scaleY,
            UtmZone = utmZone,
            IsNorthernHemisphere = isNorth
        };

        ParseFilenameMetadata(tile);
        return tile;
    }

    private static void ParseFilenameMetadata(DemTile tile)
    {
        string nameNoExt = Path.GetFileNameWithoutExtension(tile.FilePath);

        // 1. Check USGS 1M pattern first: e.g. "USGS_1M_10_x56y475_OR_SouthEast_D22"
        Match usgsMatch = UsgsPattern.Match(nameNoExt);
        if (usgsMatch.Success)
        {
            if (int.TryParse(usgsMatch.Groups[1].Value, out int zone) && zone is >= 1 and <= 60)
            {
                tile.UtmZone ??= zone;
            }
            if (double.TryParse(usgsMatch.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double xVal) &&
                double.TryParse(usgsMatch.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double yVal))
            {
                tile.ParsedFileX = xVal;
                tile.ParsedFileY = yVal;
                return;
            }
        }

        // 2. Check explicit x...y... pattern (e.g. x56y475, x56_y475)
        Match xyMatch = ExplicitXyPattern.Match(nameNoExt);
        if (xyMatch.Success &&
            double.TryParse(xyMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double px) &&
            double.TryParse(xyMatch.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double py))
        {
            tile.ParsedFileX = px;
            tile.ParsedFileY = py;
            return;
        }

        // 3. Check r...c... or y...x... pattern
        Match rcMatch = ExplicitRowColPattern.Match(nameNoExt);
        if (rcMatch.Success)
        {
            if (rcMatch.Groups[1].Success && rcMatch.Groups[2].Success)
            {
                // r0_c1 -> X is Col, Y is Row
                tile.ParsedFileX = int.Parse(rcMatch.Groups[2].Value);
                tile.ParsedFileY = int.Parse(rcMatch.Groups[1].Value);
            }
            else if (rcMatch.Groups[3].Success && rcMatch.Groups[4].Success)
            {
                tile.ParsedFileY = int.Parse(rcMatch.Groups[3].Value);
                tile.ParsedFileX = int.Parse(rcMatch.Groups[4].Value);
            }
        }
    }

    private static float? TryReadGdalNoData(Tiff tif)
    {
        try
        {
            FieldValue[]? field = tif.GetField(TIFFTAG_GDAL_NODATA);
            if (field != null && field.Length > 0)
            {
                string? rawStr = null;
                if (field.Length >= 2 && field[1].Value is byte[] bytes)
                {
                    rawStr = Encoding.ASCII.GetString(bytes).Trim('\0', ' ', '\t', '\r', '\n');
                }
                else
                {
                    rawStr = field[0].ToString()?.Trim('\0', ' ', '\t', '\r', '\n');
                }

                if (!string.IsNullOrEmpty(rawStr) &&
                    float.TryParse(rawStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                {
                    return parsed;
                }
            }
        }
        catch
        {
            // Ignore malformed custom tag
        }
        return null;
    }

    private static (double? X, double? Y) TryReadGeoTiepoint(Tiff tif)
    {
        try
        {
            FieldValue[]? field = tif.GetField(TIFFTAG_MODELTIEPOINTTAG);
            if (field != null && field.Length >= 2)
            {
                double[]? doubles = field[1].ToDoubleArray();
                if (doubles != null && doubles.Length >= 6)
                {
                    // ModelTiepointTag stores (I, J, K, X, Y, Z)
                    return (doubles[3], doubles[4]);
                }
            }
        }
        catch
        {
            // Ignore
        }
        return (null, null);
    }

    private static (double? ScaleX, double? ScaleY) TryReadPixelScale(Tiff tif)
    {
        try
        {
            FieldValue[]? field = tif.GetField(TIFFTAG_MODELPIXELSCALETAG);
            if (field != null && field.Length >= 2)
            {
                double[]? doubles = field[1].ToDoubleArray();
                if (doubles != null && doubles.Length >= 2)
                {
                    return (doubles[0], doubles[1]);
                }
            }
        }
        catch
        {
            // Ignore
        }
        return (null, null);
    }

    private static (int? Zone, bool IsNorth) TryReadUtmZoneFromGeoTiff(Tiff tif)
    {
        try
        {
            FieldValue[]? field = tif.GetField(TIFFTAG_GEOKEYDIRECTORYTAG);
            if (field != null && field.Length >= 2)
            {
                short[]? shorts = field[1].ToShortArray();
                if (shorts != null && shorts.Length >= 4)
                {
                    int numKeys = shorts[3];
                    for (int k = 0; k < numKeys; k++)
                    {
                        int offset = 4 + k * 4;
                        if (offset + 3 >= shorts.Length) break;

                        ushort keyId = (ushort)shorts[offset];
                        ushort tiffTagLocation = (ushort)shorts[offset + 1];
                        ushort valueOffset = (ushort)shorts[offset + 3];

                        // ProjectedCSTypeGeoKey = 3072
                        if (keyId == 3072 && tiffTagLocation == 0)
                        {
                            var parsed = UtmCoordinateConverter.TryParseEpsgUtmZone(valueOffset);
                            if (parsed.Zone.HasValue)
                                return parsed;
                        }
                    }
                }
            }

            // Fallback: check GeoAsciiParamsTag for "UTM zone 10N" etc.
            FieldValue[]? asciiField = tif.GetField(TIFFTAG_GEOASCIIPARAMSTAG);
            if (asciiField != null && asciiField.Length > 0)
            {
                string? ascii = asciiField.Length >= 2 && asciiField[1].Value is byte[] b
                    ? Encoding.ASCII.GetString(b)
                    : asciiField[0].ToString();

                if (!string.IsNullOrEmpty(ascii))
                {
                    Match m = Regex.Match(ascii, @"UTM\s*zone\s*(\d{1,2})([NSns])?", RegexOptions.IgnoreCase);
                    if (m.Success && int.TryParse(m.Groups[1].Value, out int z))
                    {
                        bool north = !m.Groups[2].Success || !m.Groups[2].Value.Equals("S", StringComparison.OrdinalIgnoreCase);
                        return (z, north);
                    }
                }
            }
        }
        catch
        {
            // Ignore
        }
        return (null, true);
    }

    private static void ReadScanlineTiff(
        Tiff tif, int width, int height, int bitsPerSample, int samplesPerPixel,
        SampleFormat sampleFormat, PlanarConfig planarConfig, float[] output)
    {
        int scanlineSize = tif.ScanlineSize();
        byte[] buffer = new byte[scanlineSize];
        int strideSamples = planarConfig == PlanarConfig.CONTIG ? samplesPerPixel : 1;

        for (int row = 0; row < height; row++)
        {
            if (!tif.ReadScanline(buffer, row, 0))
            {
                throw new InvalidDataException($"Failed to read TIFF scanline at row {row}.");
            }

            DecodeRowSamples(buffer, 0, width, bitsPerSample, strideSamples, sampleFormat, output, row * width);
        }
    }

    private static void ReadTiledTiff(
        Tiff tif, int width, int height, int bitsPerSample, int samplesPerPixel,
        SampleFormat sampleFormat, PlanarConfig planarConfig, float[] output)
    {
        int tileWidth = tif.GetField(TiffTag.TILEWIDTH)[0].ToInt();
        int tileHeight = tif.GetField(TiffTag.TILELENGTH)[0].ToInt();
        int tileSize = tif.TileSize();
        byte[] buffer = new byte[tileSize];
        int strideSamples = planarConfig == PlanarConfig.CONTIG ? samplesPerPixel : 1;
        int bytesPerSample = Math.Max(1, bitsPerSample / 8);
        int tileRowByteSize = tileWidth * strideSamples * bytesPerSample;

        for (int y = 0; y < height; y += tileHeight)
        {
            for (int x = 0; x < width; x += tileWidth)
            {
                int read = tif.ReadTile(buffer, 0, x, y, 0, 0);
                if (read < 0)
                {
                    throw new InvalidDataException($"Failed to read TIFF tile at ({x}, {y}).");
                }

                int copyWidth = Math.Min(tileWidth, width - x);
                int copyHeight = Math.Min(tileHeight, height - y);

                for (int ty = 0; ty < copyHeight; ty++)
                {
                    int srcByteOffset = ty * tileRowByteSize;
                    int dstIndex = (y + ty) * width + x;
                    DecodeRowSamples(buffer, srcByteOffset, copyWidth, bitsPerSample, strideSamples, sampleFormat, output, dstIndex);
                }
            }
        }
    }

    private static void DecodeRowSamples(
        byte[] buffer, int srcByteOffset, int count, int bitsPerSample, int strideSamples,
        SampleFormat sampleFormat, float[] output, int dstOffset)
    {
        int bytesPerSample = Math.Max(1, bitsPerSample / 8);
        int pixelStepBytes = bytesPerSample * strideSamples;

        for (int i = 0; i < count; i++)
        {
            int pos = srcByteOffset + i * pixelStepBytes;
            float val = sampleFormat switch
            {
                SampleFormat.IEEEFP when bitsPerSample == 32 => BitConverter.ToSingle(buffer, pos),
                SampleFormat.IEEEFP when bitsPerSample == 64 => (float)BitConverter.ToDouble(buffer, pos),
                SampleFormat.INT when bitsPerSample == 16 => BitConverter.ToInt16(buffer, pos),
                SampleFormat.INT when bitsPerSample == 32 => BitConverter.ToInt32(buffer, pos),
                SampleFormat.INT when bitsPerSample == 8 => (sbyte)buffer[pos],
                SampleFormat.UINT when bitsPerSample == 16 => BitConverter.ToUInt16(buffer, pos),
                SampleFormat.UINT when bitsPerSample == 32 => BitConverter.ToUInt32(buffer, pos),
                SampleFormat.UINT when bitsPerSample == 8 => buffer[pos],
                _ => bitsPerSample switch
                {
                    32 => BitConverter.ToSingle(buffer, pos),
                    16 => BitConverter.ToUInt16(buffer, pos),
                    8 => buffer[pos],
                    _ => 0f
                }
            };
            output[dstOffset + i] = val;
        }
    }

    /// <summary>
    /// Automatically assigns GridRow, GridCol, and Column/Row Global X/Y Coordinate Header labels
    /// using parsed filename coordinates (such as USGS x56y475), GeoTIFF tiepoints, or a square grid fallback.
    /// </summary>
    public static void ArrangeTilesInGrid(IList<DemTile> tiles)
    {
        if (tiles.Count == 0) return;

        // 1. If all tiles have parsed filename X/Y coordinates (e.g. USGS_1M_10_x56y475_OR_SouthEast_D22)
        if (tiles.All(t => t.ParsedFileX.HasValue && t.ParsedFileY.HasValue))
        {
            var distinctX = tiles.Select(t => t.ParsedFileX!.Value).Distinct().OrderBy(x => x).ToList();
            // Geographic Y (such as USGS y475, y476) increases Northward (upward), while r0/r1 row indices increase downward
            bool isRowColNaming = tiles.All(t => Regex.IsMatch(Path.GetFileNameWithoutExtension(t.FilePath), @"[rR]_?\d+.*?[cC]_?\d+"));
            var distinctY = isRowColNaming
                ? tiles.Select(t => t.ParsedFileY!.Value).Distinct().OrderBy(y => y).ToList()
                : tiles.Select(t => t.ParsedFileY!.Value).Distinct().OrderByDescending(y => y).ToList();

            if (distinctX.Count > 1 || distinctY.Count > 1 || tiles.Count == 1)
            {
                foreach (var tile in tiles)
                {
                    tile.GridCol = distinctX.IndexOf(tile.ParsedFileX!.Value);
                    tile.GridRow = distinctY.IndexOf(tile.ParsedFileY!.Value);
                    tile.ColCoordinateLabel = $"X: {tile.ParsedFileX.Value:0.##}";
                    tile.RowCoordinateLabel = $"Y: {tile.ParsedFileY.Value:0.##}";
                }
                return;
            }
        }

        // 2. Check if all tiles have distinct valid GeoTIFF tiepoints
        if (tiles.All(t => t.GeoOriginX.HasValue && t.GeoOriginY.HasValue))
        {
            var distinctX = tiles.Select(t => Math.Round(t.GeoOriginX!.Value, 4)).Distinct().OrderBy(x => x).ToList();
            // In geographic/projected coordinates, North (larger Y) is at the top (row 0)
            var distinctY = tiles.Select(t => Math.Round(t.GeoOriginY!.Value, 4)).Distinct().OrderByDescending(y => y).ToList();

            if (distinctX.Count > 1 || distinctY.Count > 1)
            {
                foreach (var tile in tiles)
                {
                    double rx = Math.Round(tile.GeoOriginX!.Value, 4);
                    double ry = Math.Round(tile.GeoOriginY!.Value, 4);
                    tile.GridCol = distinctX.IndexOf(rx);
                    tile.GridRow = distinctY.IndexOf(ry);
                    tile.ColCoordinateLabel = FormatCoordinateHeader("X", rx);
                    tile.RowCoordinateLabel = FormatCoordinateHeader("Y", ry);
                }
                return;
            }
        }

        // 3. Fallback: Sort alphabetically and arrange in a near-square grid
        int cols = (int)Math.Ceiling(Math.Sqrt(tiles.Count));
        var sorted = tiles.OrderBy(t => t.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int r = i / cols;
            int c = i % cols;
            sorted[i].GridRow = r;
            sorted[i].GridCol = c;
            sorted[i].ColCoordinateLabel = $"X: {c}";
            sorted[i].RowCoordinateLabel = $"Y: {r}";
        }
    }

    private static string FormatCoordinateHeader(string axis, double value)
    {
        // If it looks like UTM meters (e.g., 560000m or 4750000m), show km and meters clearly
        if (Math.Abs(value) >= 10000)
        {
            return $"{axis}: {value:N0}m";
        }
        return $"{axis}: {value:0.####}";
    }

    /// <summary>
    /// Computes the geographic center (Latitude, Longitude in WGS84 decimal degrees) of all loaded tiles in the grid.
    /// Supports GeoTIFF geographic lat/lon, GeoTIFF UTM meters, and USGS 1m naming conventions (e.g. USGS_1M_10_x56y475...).
    /// </summary>
    public static bool TryGetGridCenterLatLon(IList<DemTile> tiles, out double centerLat, out double centerLon, out string description)
    {
        centerLat = 0;
        centerLon = 0;
        description = string.Empty;

        if (tiles.Count == 0) return false;

        int? sharedUtmZone = tiles.Select(t => t.UtmZone).FirstOrDefault(z => z.HasValue);
        bool isNorth = tiles.FirstOrDefault(t => t.UtmZone.HasValue)?.IsNorthernHemisphere ?? true;

        var tileCentersLatLon = new List<(double Lat, double Lon)>();

        foreach (var tile in tiles)
        {
            // Case A: Tile has GeoTIFF tiepoint
            if (tile.GeoOriginX.HasValue && tile.GeoOriginY.HasValue)
            {
                double scaleX = tile.PixelScaleX ?? 1.0;
                double scaleY = tile.PixelScaleY ?? 1.0;

                // Tiepoint is top-left corner; center of tile is +Width/2 * scaleX, -Height/2 * scaleY
                double cx = tile.GeoOriginX.Value + (tile.Width * 0.5) * scaleX;
                double cy = tile.GeoOriginY.Value - (tile.Height * 0.5) * scaleY;

                // Check if already in geographic degrees (Lon in [-180..180], Lat in [-90..90])
                if (Math.Abs(cx) <= 180.0 && Math.Abs(cy) <= 90.0)
                {
                    tileCentersLatLon.Add((cy, cx));
                    continue;
                }

                // Otherwise, if UTM zone is known, convert UTM meters to Lat/Lon
                int? zone = tile.UtmZone ?? sharedUtmZone;
                if (zone.HasValue && cx > 10000 && cy > 10000)
                {
                    var (lat, lon) = UtmCoordinateConverter.UtmToLatLon(cx, cy, zone.Value, tile.IsNorthernHemisphere);
                    tileCentersLatLon.Add((lat, lon));
                    continue;
                }
            }

            // Case B: USGS 1M naming convention (e.g. USGS_1M_10_x56y475_OR_SouthEast_D22)
            // In USGS 1m tiles, x56 = 56 * 10,000m = 560,000m Easting (top-left), y475 = 475 * 10,000m = 4,750,000m Northing (top-left), 10km x 10km tile
            int? fallbackZone = tile.UtmZone ?? sharedUtmZone;
            if (fallbackZone.HasValue && tile.ParsedFileX.HasValue && tile.ParsedFileY.HasValue)
            {
                double px = tile.ParsedFileX.Value;
                double py = tile.ParsedFileY.Value;

                double eastingMeters = px < 1000 ? px * 10000.0 + 5000.0 : px;
                // In USGS 1m standard, y### is the top-left or bottom-left 10km index (center is +5,000m or -5,000m; 5km is ~0.04 deg)
                double northingMeters = py < 10000 ? py * 10000.0 - 5000.0 : py;

                if (eastingMeters is >= 100000 and <= 900000 && northingMeters is >= 100000 and <= 10000000)
                {
                    var (lat, lon) = UtmCoordinateConverter.UtmToLatLon(eastingMeters, northingMeters, fallbackZone.Value, isNorth);
                    tileCentersLatLon.Add((lat, lon));
                }
            }
        }

        if (tileCentersLatLon.Count > 0)
        {
            double minLat = tileCentersLatLon.Min(p => p.Lat);
            double maxLat = tileCentersLatLon.Max(p => p.Lat);
            double minLon = tileCentersLatLon.Min(p => p.Lon);
            double maxLon = tileCentersLatLon.Max(p => p.Lon);

            centerLat = (minLat + maxLat) * 0.5;
            centerLon = (minLon + maxLon) * 0.5;
            description = sharedUtmZone.HasValue
                ? $"UTM Zone {sharedUtmZone.Value}{(isNorth ? "N" : "S")} → {centerLat:F5}°, {centerLon:F5}°"
                : $"{centerLat:F5}°, {centerLon:F5}°";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Derives a location-specific export folder name from the source DEM file path or USGS naming pattern (e.g. "USGS_1M_10_x56y475_OR_SouthEast_D22" -> "OR_SouthEast_D22").
    /// </summary>
    public static string DeriveLocationName(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "Exported_16Bit_PNG";

        string nameNoExt = Path.GetFileNameWithoutExtension(filePath);

        // 1. Explicit USGS naming convention: USGS_1M_10_x56y475_OR_SouthEast_D22 or USGS_1M_x56y475_OR_SouthEast_D22
        var usgsMatch = Regex.Match(nameNoExt, @"USGS_(?:1M|1m|NED|2M|10M)(?:_\d{1,2})?_[xX]\d+[yY]\d+_(.+)", RegexOptions.IgnoreCase);
        if (usgsMatch.Success && !string.IsNullOrWhiteSpace(usgsMatch.Groups[1].Value))
        {
            return usgsMatch.Groups[1].Value.Trim();
        }

        // 2. Coordinate prefix convention: e.g. x56y475_LocationName or x56_y475_LocationName
        var coordMatch = Regex.Match(nameNoExt, @"[xX]\d+[_\-]?[yY]\d+_(.+)", RegexOptions.IgnoreCase);
        if (coordMatch.Success && !string.IsNullOrWhiteSpace(coordMatch.Groups[1].Value))
        {
            return coordMatch.Groups[1].Value.Trim();
        }

        // 3. Fallback: Check parent directory name if meaningful
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            string dirName = Path.GetFileName(dir);
            if (!string.IsNullOrWhiteSpace(dirName) &&
                !dirName.Equals("ExportedPngs", StringComparison.OrdinalIgnoreCase) &&
                !dirName.Equals("bin", StringComparison.OrdinalIgnoreCase))
            {
                return dirName;
            }
        }

        return "Exported_16Bit_PNG";
    }

    /// <summary>
    /// Derives a location-specific export folder name from the loaded tiles collection.
    /// </summary>
    public static string DeriveLocationName(IEnumerable<DemTile> tiles)
    {
        var first = tiles.FirstOrDefault();
        return first != null ? DeriveLocationName(first.FilePath) : "Exported_16Bit_PNG";
    }
}

namespace DemCleaner.Services;

/// <summary>
/// Converts Universal Transverse Mercator (UTM) coordinates (e.g. NAD83 / WGS84 UTM Zones 1..60)
/// to geographic Latitude and Longitude (WGS84 degrees).
/// Also extracts UTM zone and hemisphere from GeoTIFF tags or USGS 1m tile filenames.
/// </summary>
public static class UtmCoordinateConverter
{
    private const double WGS84_A = 6378137.0;
    private const double WGS84_F = 1.0 / 298.257223563;
    private const double K0 = 0.9996;

    /// <summary>
    /// Converts UTM Easting and Northing (in meters) to (Latitude, Longitude) in decimal degrees.
    /// </summary>
    public static (double Latitude, double Longitude) UtmToLatLon(
        double eastingMeters, double northingMeters, int utmZone, bool isNorthernHemisphere = true)
    {
        double eSq = 2 * WGS84_F - WGS84_F * WGS84_F;
        double ePrimeSq = eSq / (1.0 - eSq);
        double e1 = (1.0 - Math.Sqrt(1.0 - eSq)) / (1.0 + Math.Sqrt(1.0 - eSq));

        double x = eastingMeters - 500000.0;
        double y = isNorthernHemisphere ? northingMeters : northingMeters - 10000000.0;

        double m = y / K0;
        double mu = m / (WGS84_A * (1.0 - eSq / 4.0 - 3.0 * eSq * eSq / 64.0 - 5.0 * eSq * eSq * eSq / 256.0));

        double phi1Rad = mu
            + (3.0 * e1 / 2.0 - 27.0 * Math.Pow(e1, 3) / 32.0) * Math.Sin(2.0 * mu)
            + (21.0 * e1 * e1 / 16.0 - 55.0 * Math.Pow(e1, 4) / 32.0) * Math.Sin(4.0 * mu)
            + (151.0 * Math.Pow(e1, 3) / 96.0) * Math.Sin(6.0 * mu)
            + (1097.0 * Math.Pow(e1, 4) / 512.0) * Math.Sin(8.0 * mu);

        double sinPhi1 = Math.Sin(phi1Rad);
        double cosPhi1 = Math.Cos(phi1Rad);
        double tanPhi1 = Math.Tan(phi1Rad);

        double n1 = WGS84_A / Math.Sqrt(1.0 - eSq * sinPhi1 * sinPhi1);
        double t1 = tanPhi1 * tanPhi1;
        double c1 = ePrimeSq * cosPhi1 * cosPhi1;
        double r1 = WGS84_A * (1.0 - eSq) / Math.Pow(1.0 - eSq * sinPhi1 * sinPhi1, 1.5);
        double d = x / (n1 * K0);

        double latRad = phi1Rad - (n1 * tanPhi1 / r1) * (
            d * d / 2.0
            - (5.0 + 3.0 * t1 + 10.0 * c1 - 4.0 * c1 * c1 - 9.0 * ePrimeSq) * Math.Pow(d, 4) / 24.0
            + (61.0 + 90.0 * t1 + 298.0 * c1 + 45.0 * t1 * t1 - 252.0 * ePrimeSq - 3.0 * c1 * c1) * Math.Pow(d, 6) / 720.0);

        double lonOrigin = (utmZone - 1) * 6.0 - 180.0 + 3.0;
        double lonRad = (
            d
            - (1.0 + 2.0 * t1 + c1) * Math.Pow(d, 3) / 6.0
            + (5.0 - 2.0 * c1 + 28.0 * t1 - 3.0 * c1 * c1 + 8.0 * ePrimeSq + 24.0 * t1 * t1) * Math.Pow(d, 5) / 120.0) / cosPhi1;

        double latDeg = latRad * (180.0 / Math.PI);
        double lonDeg = lonOrigin + lonRad * (180.0 / Math.PI);

        return (latDeg, lonDeg);
    }

    /// <summary>
    /// Extracts UTM zone from EPSG PCS code (e.g. 26910 -> Zone 10N, 32610 -> Zone 10N, 32710 -> Zone 10S).
    /// </summary>
    public static (int? Zone, bool IsNorth) TryParseEpsgUtmZone(int epsgCode)
    {
        // NAD83 UTM zones 26901..26923
        if (epsgCode is >= 26901 and <= 26923)
            return (epsgCode - 26900, true);

        // NAD83(2011) / NSRS2007 UTM zones 6329..6352 (6339 = Zone 10N, 6340 = Zone 11N, etc.)
        if (epsgCode is >= 6330 and <= 6348)
            return (epsgCode - 6329, true);

        // WGS84 UTM North 32601..32660
        if (epsgCode is >= 32601 and <= 32660)
            return (epsgCode - 32600, true);

        // WGS84 UTM South 32701..32760
        if (epsgCode is >= 32701 and <= 32760)
            return (epsgCode - 32700, false);

        return (null, true);
    }
}


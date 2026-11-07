// AeroHelm offline survey mission generator. No MAVLink transmission.
// GPL-3.0-or-later, consistent with Mission Planner fork.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AeroHelm.GcsCloud
{
    public sealed class SurveyOptions
    {
        public double North { get; set; }
        public double South { get; set; }
        public double East { get; set; }
        public double West { get; set; }
        public double SpacingMetres { get; set; }
        public double RelativeAltitudeMetres { get; set; }
        public double CruiseMetresPerSecond { get; set; }
    }

    public sealed class SurveyPoint
    {
        public int Sequence { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double RelativeAltitudeMetres { get; set; }
    }

    public sealed class SurveyDraft
    {
        public IReadOnlyList<SurveyPoint> Points { get; private set; }
        public double HorizontalDistanceMetres { get; private set; }
        public double NominalTravelSeconds { get; private set; }
        internal SurveyDraft(IReadOnlyList<SurveyPoint> points, double distance, double seconds)
        {
            Points = points;
            HorizontalDistanceMetres = distance;
            NominalTravelSeconds = seconds;
        }
    }

    public static class SurveyPlanner
    {
        private const double EarthRadiusMetres = 6371000.0;
        private const int MaxPoints = 120;
        private const double MaxSpanMetres = 5000.0;

        private static bool Finite(double x)
        {
            return !double.IsNaN(x) && !double.IsInfinity(x);
        }

        private static double Radians(double deg) { return deg * Math.PI / 180.0; }

        public static double DistanceMetres(double lat1, double lon1, double lat2, double lon2)
        {
            double dLat = Radians(lat2 - lat1);
            double dLon = Radians(lon2 - lon1);
            double a = Math.Pow(Math.Sin(dLat / 2), 2) +
                       Math.Cos(Radians(lat1)) * Math.Cos(Radians(lat2)) *
                       Math.Pow(Math.Sin(dLon / 2), 2);
            return 2 * EarthRadiusMetres * Math.Asin(Math.Min(1, Math.Sqrt(Math.Max(0, a))));
        }

        public static SurveyDraft Generate(SurveyOptions o)
        {
            if (o == null) throw new ArgumentNullException(nameof(o));
            foreach (double value in new[] {o.North, o.South, o.East, o.West,
                                            o.SpacingMetres, o.RelativeAltitudeMetres,
                                            o.CruiseMetresPerSecond})
                if (!Finite(value)) throw new ArgumentException("Non-finite survey input.");
            if (o.South <= -85 || o.North >= 85 || o.North <= o.South ||
                o.West < -180 || o.East > 180 || o.East <= o.West)
                throw new ArgumentException("Specify a non-wrapping bounding box (South < North; West < East; latitude within +/-85 degrees).");
            if (o.SpacingMetres < 5 || o.SpacingMetres > 300)
                throw new ArgumentException("Survey row spacing must be 5-300 metres.");
            if (o.RelativeAltitudeMetres < 5 || o.RelativeAltitudeMetres > 120)
                throw new ArgumentException("Draft relative altitude must be 5-120 metres; these are planner guardrails, not flight authorization or legal limits.");
            if (o.CruiseMetresPerSecond < 0.5 || o.CruiseMetresPerSecond > 15)
                throw new ArgumentException("Nominal planning speed must be 0.5-15 m/s.");

            double height = DistanceMetres(o.North, o.West, o.South, o.West);
            double width = DistanceMetres(o.North, o.West, o.North, o.East);
            if (height < 1 || width < 1 || height > MaxSpanMetres || width > MaxSpanMetres)
                throw new ArgumentException("Survey area must be at least 1 metre each way and no more than 5 km on each axis.");

            // A two-point per stripe serpentine scan. Even stripes west->east,
            // odd stripes east->west. Count bounded before allocation.
            int stripes = Math.Max(1, (int)Math.Ceiling(height / o.SpacingMetres)) + 1;
            if (stripes * 2 > MaxPoints)
                throw new ArgumentException("This survey would exceed 120 waypoints; increase spacing or reduce area.");
            var points = new List<SurveyPoint>(stripes * 2);
            for (int row = 0; row < stripes; row++)
            {
                double fraction = (double)row / (stripes - 1);
                double lat = o.North + (o.South - o.North) * fraction;
                double firstLon = (row % 2 == 0) ? o.West : o.East;
                double secondLon = (row % 2 == 0) ? o.East : o.West;
                points.Add(new SurveyPoint
                {
                    Sequence = points.Count + 1,
                    Latitude = lat,
                    Longitude = firstLon,
                    RelativeAltitudeMetres = o.RelativeAltitudeMetres
                });
                points.Add(new SurveyPoint
                {
                    Sequence = points.Count + 1,
                    Latitude = lat,
                    Longitude = secondLon,
                    RelativeAltitudeMetres = o.RelativeAltitudeMetres
                });
            }

            double distance = 0;
            for (int i = 1; i < points.Count; i++)
                distance += DistanceMetres(points[i - 1].Latitude, points[i - 1].Longitude,
                                           points[i].Latitude, points[i].Longitude);
            return new SurveyDraft(points.AsReadOnly(), distance,
                                   distance / o.CruiseMetresPerSecond);
        }

        public static string ToQgcWpl110(SurveyDraft draft)
        {
            if (draft == null || draft.Points == null || draft.Points.Count < 2)
                throw new ArgumentException("No valid draft.");
            var b = new StringBuilder("QGC WPL 110\n");
            // Index 0 is a HOME PLACEHOLDER, not an executable takeoff command.
            // ArduPilot substitutes the vehicle's actual home on upload.
            var home = draft.Points[0];
            b.AppendLine(string.Join("\t", new[] {
                "0", "1", "0", "16", "0", "0", "0", "0",
                home.Latitude.ToString("F7", CultureInfo.InvariantCulture),
                home.Longitude.ToString("F7", CultureInfo.InvariantCulture), "0", "1"
            }));
            for (int i = 0; i < draft.Points.Count; i++)
            {
                var p = draft.Points[i];
                b.AppendLine(string.Join("\t", new[] {
                    (i+1).ToString(CultureInfo.InvariantCulture), "0", "3", "16",
                    "0", "0", "0", "0",
                    p.Latitude.ToString("F7", CultureInfo.InvariantCulture),
                    p.Longitude.ToString("F7", CultureInfo.InvariantCulture),
                    p.RelativeAltitudeMetres.ToString("F2", CultureInfo.InvariantCulture),
                    "1"
                }));
            }
            return b.ToString();
        }

        public static void SaveDraft(SurveyDraft draft, string path)
        {
            // Export only. NEVER push this data to an autopilot or cloud endpoint.
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Invalid path.");
            File.WriteAllText(path, ToQgcWpl110(draft), new UTF8Encoding(false));
        }
    }
}

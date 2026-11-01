using System;
using System.Globalization;
using AeroHelm.GcsCloud;

public static class Program
{
    private static int checks;
    private static void Assert(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static bool Throws(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return true; }
        return false;
    }

    public static void Main()
    {
        // These values describe a small SYNTHETIC rectangle near the equator.
        // They are not a flight-approved geographic location.
        var options = new SurveyOptions {
            North = 0.002, South = 0.001,
            West = 0.002, East = 0.003,
            SpacingMetres = 25, RelativeAltitudeMetres = 30,
            CruiseMetresPerSecond = 5
        };
        var plan = SurveyPlanner.Generate(options);
        Assert(plan.Points.Count >= 4 && plan.Points.Count <= 120, "waypoint bound");
        Assert(plan.Points[0].Longitude == options.West, "first stripe starts west");
        Assert(plan.Points[1].Longitude == options.East, "first stripe ends east");
        Assert(plan.Points[2].Longitude == options.East, "second stripe reverses");
        Assert(plan.Points[3].Longitude == options.West, "second stripe ends west");
        Assert(Math.Abs(plan.Points[plan.Points.Count - 1].Latitude - options.South) < 1e-10,
               "coverage reaches south boundary");
        Assert(plan.HorizontalDistanceMetres > 100, "computed path distance");
        Assert(Math.Abs(plan.NominalTravelSeconds -
                        plan.HorizontalDistanceMetres / options.CruiseMetresPerSecond) < .001,
               "travel estimate uses nominal speed");
        string wpl = SurveyPlanner.ToQgcWpl110(plan);
        string[] lines = wpl.Trim().Split('\n');
        Assert(lines[0] == "QGC WPL 110", "WPL header");
        Assert(lines.Length == plan.Points.Count + 2, "home placeholder plus all waypoints");
        Assert(lines[1].Split('\t').Length == 12, "12 home columns");
        Assert(lines[2].Split('\t').Length == 12, "12 waypoint columns");
        Assert(lines[2].Split('\t')[2] == "3", "relative-altitude coordinate frame");
        Assert(lines[2].Split('\t')[3] == "16", "waypoint command");
        Assert(lines[2].Split('\t')[8] == options.North.ToString("F7", CultureInfo.InvariantCulture),
               "lat/lon order and invariant decimal");
        Assert(Throws(() => SurveyPlanner.Generate(new SurveyOptions {
            North = 0.001, South = 0.002, East = 0.003, West = 0.002,
            SpacingMetres = 25, RelativeAltitudeMetres = 30, CruiseMetresPerSecond = 5
        })), "reversed latitude bounds rejected");
        options.SpacingMetres = 5;
        options.North = 0.04;
        Assert(Throws(() => SurveyPlanner.Generate(options)), "too many waypoints rejected");
        options.North = .002;
        options.SpacingMetres = 25;
        options.RelativeAltitudeMetres = double.NaN;
        Assert(Throws(() => SurveyPlanner.Generate(options)), "non-finite altitude rejected");
        options.RelativeAltitudeMetres = 30;
        options.West = 179.99;
        options.East = -179.99;
        Assert(Throws(() => SurveyPlanner.Generate(options)), "antimeridian crossing rejected");
        Console.WriteLine("AeroHelm Survey Planner: PASS (" + checks + " assertions)");
    }
}

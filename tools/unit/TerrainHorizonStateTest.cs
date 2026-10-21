using System;

public struct Vector3d
{
    public double x;
    public double y;
    public double z;

    public Vector3d(double x, double y, double z)
    {
        this.x = x;
        this.y = y;
        this.z = z;
    }
}

namespace UnityEngine
{
    public struct Vector3
    {
        public float x;
        public float y;
        public float z;

        public Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
    }
}

internal static class TerrainHorizonStateTest
{
    private static int _failures;

    private static void Check(bool condition, string label)
    {
        if (condition)
        {
            Console.WriteLine("PASS: " + label);
            return;
        }

        _failures++;
        Console.WriteLine("FAIL: " + label);
    }

    private static bool Shift(double globalX, double globalZ, double unityX, double unityZ, double unityY = 0) =>
        TLDRevamp.Fixes.TerrainHorizon.IsOriginShift(
            new Vector3d(globalX, 0, globalZ),
            new UnityEngine.Vector3((float)unityX, (float)unityY, (float)unityZ));

    private static void SignatureBudgetContracts()
    {
        var budget = new TLDRevamp.Fixes.TerrainHorizonGeometry.SignatureBuilder(8, 1000);
        int accepted = 0;
        for (int i = 0; i < 100; i++)
            if (budget.TryVisit()) accepted++;
        Check(accepted == 8 && budget.Visits == 8, "SIGNATURE_VISIT_LIMIT");
        Check(budget.Truncated, "SIGNATURE_VISIT_LIMIT_TRUNCATED");
        Check(!budget.AcceptsMoreRoads, "SIGNATURE_VISIT_LIMIT_STOPS_WORK");

        budget.AddRoad(true, int.MaxValue, 0);
        budget.AddRoad(true, int.MaxValue, 0);
        Check(budget.Elements == int.MaxValue && budget.Elements >= 0, "SIGNATURE_ELEMENT_COUNT_SATURATES");

        var roadLimited = new TLDRevamp.Fixes.TerrainHorizonGeometry.SignatureBuilder(8, 1);
        roadLimited.TryVisit();
        roadLimited.AddRoad(false, 0, 0);
        Check(!roadLimited.AcceptsMoreRoads, "SIGNATURE_ROAD_LIMIT_STOPS_WORK");
    }

    private static int Main()
    {
        TLDRevamp.Fixes.TerrainHorizon.ResetOriginObservation();
        Check(!Shift(0, 0, 0, 0), "initial sample is false");

        for (int i = 1; i <= 15; i++)
            Check(!Shift(i, 0, i, 0), "normal driving frame " + i + " is false");

        Check(Shift(16, 0, -1984, 0), "DRIVING_BEFORE_REBUILD origin shift is true");
        Check(!Shift(16, 0, -1984, 0), "unchanged sample has no duplicate shift");
        Check(!Shift(51, 0, -1949, 0), "35 m accumulated walk is false");
        Check(!Shift(151, 0, -1849, 0), "100 m accumulated walk is false");
        Check(!Shift(10000, 0, 10000, 0), "large global and Unity teleport is false");
        Check(Shift(10000, 0, -4849, 0), "second actual origin shift is true");

        TLDRevamp.Fixes.TerrainHorizon.ResetOriginObservation();
        Check(!Shift(5000, 5000, -5000, 5000), "large first sample after reset is false");

        TLDRevamp.Fixes.TerrainHorizon.ResetOriginObservation();
        Check(!Shift(2, 0, 1000, 0), "RESET_FORGETS_PREVIOUS: first sample near the global origin after reset is false");

        Check(!Shift(double.NaN, 0, 0, 0), "non-finite observation is false");
        Check(!Shift(20, 0, 20, 0), "finite sample after non-finite reset is false");
        Check(Shift(20, 0, -1980, 0), "real shift after non-finite reset is true");

        TLDRevamp.Fixes.TerrainHorizon.ResetOriginObservation();
        Check(!Shift(0, 0, 0, 0), "initial sample after reset is false");
        Check(Shift(0, 0, 0, 0, -2000), "VERTICAL_SHIFT");
        Check(!Shift(0, 0, 0, 0, -2000), "unchanged vertical sample has no duplicate shift");

        SignatureBudgetContracts();

        if (_failures == 0)
        {
            Console.WriteLine("PASS");
            return 0;
        }

        Console.WriteLine("FAIL");
        return 1;
    }
}

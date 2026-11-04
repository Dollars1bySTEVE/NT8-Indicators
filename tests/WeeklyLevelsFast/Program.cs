using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WeeklyLevelsFastCalculation;

internal static class Program
{
    private static int assertions;
    private static readonly DateTime Monday = new DateTime(2026, 11, 2);
    private static readonly DateTime Begin = Monday.AddDays(-1).AddHours(18);

    private static void Equal(double expected, double actual, string name)
    {
        assertions++;
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > 0.0000001)
            throw new Exception(name + ": expected " + expected + ", got " + actual);
    }

    private static void True(bool value, string name)
    {
        assertions++;
        if (!value) throw new Exception(name);
    }

    private static void Trade(WeeklyEngine engine, long id, double price, double volume, double hours)
    {
        engine.Update(id, Monday, Begin, Begin.AddHours(hours),
            price, price, price, price, volume, true);
    }

    private static void Main()
    {
        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("IndicatorSource"))
        using (StreamReader reader = new StreamReader(stream))
        {
            SyntaxTree syntax = CSharpSyntaxTree.ParseText(reader.ReadToEnd(),
                new CSharpParseOptions(LanguageVersion.CSharp5));
            foreach (Diagnostic diagnostic in syntax.GetDiagnostics())
                True(diagnostic.Severity != DiagnosticSeverity.Error, diagnostic.ToString());
            True(true, "Full adapter and calculation source parsed as C# 5");
        }
        True(WeeklyEngine.Monday(Monday.AddDays(-1)) == Monday.AddDays(-7),
            "Calendar Sunday is not automatically Monday; caller uses exchange trading day");
        True(WeeklyEngine.Monday(Monday.AddDays(4)) == Monday, "Friday maps to Monday");
        WeeklyEngine exact = new WeeklyEngine(1, TimeSpan.FromHours(30), true);
        Trade(exact, 0, 100, 10, 0);
        Trade(exact, 1, 110, 20, 29);
        Trade(exact, 2, 120, 10, 30);
        LevelSnapshot values = exact.Snapshot(70, false, false, true);
        Equal(110, values.Levels[0], "30-hour IB excludes boundary trade");
        Equal(105, values.Levels[1], "IB midpoint");
        Equal(100, values.Levels[2], "IB low");
        Equal(115, values.Levels[3], "Upper 50% extension beyond IB high");
        Equal(95, values.Levels[4], "Lower 50% extension below IB low");
        Equal(130, values.Levels[9], "Upper 200%");
        Equal(80, values.Levels[10], "Lower 200%");
        Equal(110, values.Levels[21], "Trade VWAP");
        Equal(110, values.Levels[16], "Volume POC");
        True(values.IbComplete, "IB complete after 30 hours");
        Equal(110, values.Levels[25], "Week midpoint is high-low midpoint");

        Trade(exact, 2, 120, 15, 30);
        Equal(45, exact.Current.Volume, "Revisited tick volume replaces, not duplicates");
        Equal((1000.0 + 2200 + 1800) / 45, exact.Snapshot(70, false, false, true).Levels[21],
            "Revisited tick VWAP");
        Trade(exact, 2, 120, 15, 30);
        Equal(45, exact.Current.Volume, "Repeated same tick is idempotent");

        WeeklyEngine area = new WeeklyEngine(1, TimeSpan.FromMinutes(30), true);
        Trade(area, 0, 99, 2, 0);
        Trade(area, 1, 100, 10, 0.1);
        Trade(area, 2, 101, 6, 0.2);
        Trade(area, 3, 102, 2, 0.3);
        LevelSnapshot a = area.Snapshot(70, false, false, true);
        Equal(100, a.Levels[16], "POC");
        Equal(101, a.Levels[15], "VAH expands to heavier adjacent side");
        Equal(100, a.Levels[17], "VAL");
        Equal(100.5, area.Snapshot(70, true, false, true).Levels[16], "Optional VA midpoint line");
        a = area.Snapshot(100, false, false, true);
        Equal(102, a.Levels[15], "100% VAH");
        Equal(99, a.Levels[17], "100% VAL");

        WeeklyEngine ties = new WeeklyEngine(1, TimeSpan.FromHours(30), true);
        Trade(ties, 0, 102, 10, 0);
        Trade(ties, 1, 100, 10, 1);
        Equal(100, ties.Snapshot(50, false, false, true).Levels[16], "POC ties choose lower price");
        a = ties.Snapshot(100, false, false, true);
        Equal(100, a.Levels[17], "Gap profile VAL");
        Equal(102, a.Levels[15], "Gap profile VAH");

        WeeklyEngine estimate = new WeeklyEngine(1, TimeSpan.FromMinutes(30), true);
        estimate.Update(0, Monday, Begin, Begin.AddMinutes(1), 100, 102, 100, 101, 30, false);
        Equal(10, estimate.Current.Profile[100], "Uniform bar distribution");
        estimate.Update(0, Monday, Begin, Begin.AddMinutes(1), 100, 103, 100, 103, 60, false);
        Equal(60, estimate.Current.Volume, "Developing minute bar replaces contribution");
        Equal(15, estimate.Current.Profile[100], "Old minute distribution removed");
        Equal(15, estimate.Current.Profile[103], "Expanded minute range");
        Equal((103 + 100 + 103) / 3.0, estimate.Snapshot(70, false, false, true).Levels[21],
            "Estimated VWAP updates weighted price as well as volume");
        estimate.Update(1, Monday, Begin, Begin.AddMinutes(30), 103, 105, 103, 104, 5, false);
        estimate.Update(2, Monday, Begin, Begin.AddMinutes(31), 104, 200, 104, 200, 5, false);
        Equal(105, estimate.Snapshot(70, false, false, true).Levels[0],
            "Minute bar ending at IB boundary included; next bar excluded");

        exact.Update(3, Monday.AddDays(7), Begin.AddDays(7), Begin.AddDays(7),
            130, 130, 130, 130, 5, true);
        a = exact.Snapshot(70, false, false, true);
        Equal(120, a.Levels[11], "Previous-week high after rollover");
        Equal(100, a.Levels[12], "Previous-week low after rollover");
        Equal(120, a.Levels[13], "Previous-week close");
        Equal(130, a.Levels[14], "New week open");
        Equal(100, a.Levels[22], "Previous-week open");
        True(!a.IbComplete, "New IB developing");

        string directory = Path.Combine(Path.GetTempPath(), "weekly-levels-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "week.bin");
        try
        {
            WeeklyCache.Save(path, "test-identity", exact.Previous);
            WeekData restored = WeeklyCache.Load(path, "test-identity", Monday);
            Equal(45, restored.Volume, "Cached volume");
            Equal(15, restored.Profile[120], "Cached price bucket");
            WeeklyCache.Save(path, "test-identity", exact.Previous);
            restored = WeeklyCache.Load(path, "test-identity", Monday);
            Equal(100, restored.Open, "Atomic cache replacement");
            WeeklyEngine seeded = new WeeklyEngine(1, TimeSpan.FromHours(30), true);
            seeded.SeedPrevious(restored);
            seeded.Update(0, Monday.AddDays(7), Begin.AddDays(7), Begin.AddDays(7),
                130, 130, 130, 130, 1, true);
            Equal(120, seeded.Snapshot(70, false, false, true).Levels[11], "Seeded previous week retained");
            bool rejected = false;
            try { WeeklyCache.Load(path, "wrong", Monday); }
            catch (InvalidDataException) { rejected = true; }
            True(rejected, "Cache identity mismatch rejected");
            File.WriteAllBytes(path, new byte[] { 1, 2 });
            rejected = false;
            try { WeeklyCache.Load(path, "test-identity", Monday); }
            catch (IOException) { rejected = true; }
            True(rejected, "Truncated cache rejected");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }

        exact.Update(4, Monday.AddDays(21), Begin.AddDays(21), Begin.AddDays(21),
            140, 140, 140, 140, 1, true);
        True(exact.Previous == null, "Missing calendar week not replaced by an older week");
        True(double.IsNaN(exact.Snapshot(70, false, false, false).Levels[21]),
            "Price-only preview never pretends to have exact VWAP");

        WeeklyEngine zero = new WeeklyEngine(0.25, TimeSpan.FromMinutes(30), true);
        Trade(zero, 0, -10.25, 0, 0);
        True(double.IsNaN(zero.Snapshot(70, false, false, true).Levels[21]),
            "Zero-volume VWAP unavailable");
        Trade(zero, 0, -10.25, 10, 0);
        Equal(-10.25, zero.Snapshot(70, false, false, true).Levels[16], "Negative prices supported");
        Equal(-10.25, zero.Snapshot(70, false, false, true).Levels[21], "Negative-price VWAP");
        bool invalid = false;
        try { zero.Snapshot(double.NaN, false, false, true); }
        catch (ArgumentOutOfRangeException) { invalid = true; }
        True(invalid, "NaN value-area percentage rejected");

        Random random = new Random(814);
        for (int trial = 0; trial < 100; trial++)
        {
            WeeklyEngine sample = new WeeklyEngine(1, TimeSpan.FromHours(30), true);
            for (int i = 0; i < 250; i++)
                Trade(sample, i, 100 + random.Next(30), random.Next(1, 100), i / 1000.0);
            a = sample.Snapshot(70, false, false, true);
            double covered = 0;
            double total = 0;
            foreach (var bin in sample.Current.Profile)
            {
                total += bin.Value;
                if (bin.Key >= a.Levels[17] && bin.Key <= a.Levels[15])
                    covered += bin.Value;
            }
            True(covered + 1e-8 >= total * 0.7, "Actual returned VA contains at least 70% volume");
            True(a.Levels[17] <= a.Levels[16] && a.Levels[16] <= a.Levels[15],
                "POC is inside the returned value area");
        }

        WeeklyEngine performance = new WeeklyEngine(0.25, TimeSpan.FromHours(30), true);
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < 1000000; i++)
            Trade(performance, i, 20000 + i % 2000 * 0.25, 1, i / 1000000.0);
        a = performance.Snapshot(70, false, false, true);
        watch.Stop();
        Equal(1000000, performance.Current.Volume, "One million ticks counted once");
        Equal(2000, performance.Current.Profile.Count, "Profile memory depends on price bins, not trades");
        True(!double.IsNaN(a.Levels[16]), "Million-tick profile available");
        Console.WriteLine("PASS: " + assertions + " assertions.");
        Console.WriteLine("Portable core only: 1,000,000 synthetic trades + value area in "
            + watch.ElapsedMilliseconds + " ms; " + performance.Current.Profile.Count + " price bins.");
        Console.WriteLine("Native NinjaTrader compilation, data loading and rendering remain unverified.");
    }
}

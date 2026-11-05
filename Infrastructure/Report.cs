using System.Text.Json;
using CloudQA.RobustForm.Actions;
using CloudQA.RobustForm.Locator;

namespace CloudQA.RobustForm.Infrastructure;

public sealed class PageRun
{
    public string Url { get; set; } = "";
    public bool Passed { get; set; }
    public string Scope { get; set; } = "";
    public string Context { get; set; } = "";
    public double Seconds { get; set; }
    public string? Error { get; set; }
    public string? Screenshot { get; set; }
    public List<FieldReport> Fields { get; set; } = new();
}

public sealed class FieldReport
{
    public string Field { get; set; } = "";
    public bool Passed { get; set; }
    public string? Element { get; set; }
    public double Score { get; set; }
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public string? Warning { get; set; }
    public string? ExpectationCheck { get; set; }
    public List<string> Evidence { get; set; } = new();
    public List<string> Alternatives { get; set; } = new();

    public static FieldReport From(LocatedField f, StepResult s) => new()
    {
        Field = f.Key,
        Passed = s.Passed,
        Element = f.Description,
        Score = f.Score,
        Action = s.Action,
        Detail = s.Detail,
        Warning = f.Warning,
        Evidence = f.Evidence,
        Alternatives = f.Alternatives
    };
}

public static class Report
{
    public static void PrintPage(PageRun run)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"Page   : {run.Url}");
        Console.WriteLine($"Form   : {run.Scope}  ({run.Context})");
        foreach (var f in run.Fields)
        {
            WriteStatus(f.Passed);
            Console.WriteLine($" {f.Field,-10} {f.Action}");
            Console.WriteLine($"        element : {f.Element ?? "(not found)"}  [confidence {f.Score:0.00}]");
            if (f.Evidence.Count > 0) Console.WriteLine($"        because : {string.Join("; ", f.Evidence)}");
            Console.WriteLine($"        result  : {f.Detail}");
            if (f.ExpectationCheck != null) Console.WriteLine($"        check   : {f.ExpectationCheck}");
            if (f.Warning != null) Console.WriteLine($"        warning : {f.Warning}");
        }
        if (run.Error != null) Console.WriteLine($"ERROR  : {run.Error}");
        Console.Write("Result : ");
        WriteStatus(run.Passed);
        Console.WriteLine($"  ({run.Seconds:0.0}s){(run.Screenshot != null ? "  screenshot: " + run.Screenshot : "")}");
    }

    public static void PrintSummary(IReadOnlyList<PageRun> runs)
    {
        if (runs.Count < 2) return;
        Console.WriteLine();
        Console.WriteLine(new string('=', 78));
        Console.WriteLine("SUMMARY");
        foreach (var r in runs)
        {
            WriteStatus(r.Passed);
            Console.WriteLine($" {Path.GetFileName(new Uri(r.Url).LocalPath)}");
        }
        Console.WriteLine($"{runs.Count(r => r.Passed)}/{runs.Count} pages passed");
    }

    public static string Save(IReadOnlyList<PageRun> runs, string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"report-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(runs, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static void WriteStatus(bool passed)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = passed ? ConsoleColor.Green : ConsoleColor.Red;
        Console.Write(passed ? "PASS" : "FAIL");
        Console.ForegroundColor = old;
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using CloudQA.RobustForm.Config;
using OpenQA.Selenium;

namespace CloudQA.RobustForm.Locator;

public sealed class LocatedField
{
    public required string Key { get; init; }
    public bool Found { get; init; }
    public double Score { get; init; }
    public string? Description { get; init; }
    public List<string> Evidence { get; init; } = new();
    public List<string> Alternatives { get; init; } = new();
    public string? Warning { get; init; }
    public IWebElement? Element { get; init; }

    // radio / select specifics
    public IWebElement? ClickTarget { get; init; }
    public string? OptionText { get; init; }
    public int OptionIndex { get; init; } = -1;
    public bool OptionFound { get; init; }
    public List<string> Options { get; init; } = new();
}

public sealed class LocateResult
{
    public bool Complete { get; init; }
    public string Scope { get; init; } = "";
    public string Context { get; set; } = "top page";
    public Dictionary<string, LocatedField> Fields { get; init; } = new();
    public int FoundCount => Fields.Values.Count(f => f.Found);
}

/// <summary>
/// Finds First Name / Gender / State by meaning rather than by fixed selectors.
/// The scoring engine (SmartLocator.js) runs inside the page in one round-trip;
/// this class handles waiting, iframes, and turning the result into C# objects.
/// </summary>
public sealed class SmartFormLocator
{
    public const string FirstName = "firstName";
    public const string Gender = "gender";
    public const string State = "state";

    private static readonly Lazy<string> EngineScript = new(() => LoadEngine());

    private const string FindFramesScript = """
        var out = [];
        (function walk(n) {
            if (n.nodeType === 1) {
                if ((n.tagName === 'IFRAME' || n.tagName === 'FRAME') && n.getClientRects().length) out.push(n);
                if (n.shadowRoot) Array.prototype.forEach.call(n.shadowRoot.children, walk);
            }
            Array.prototype.forEach.call(n.children || [], walk);
        })(document.documentElement);
        return out;
        """;

    private readonly IWebDriver _driver;
    private readonly AppConfig _cfg;
    private readonly string _specJson;
    private List<IWebElement> _currentPath = new();

    public SmartFormLocator(IWebDriver driver, AppConfig cfg)
    {
        _driver = driver;
        _cfg = cfg;
        _specJson = BuildSpec(cfg);
    }

    /// <summary>Keeps searching (top page, then iframes) until all fields are found or the timeout passes.</summary>
    public LocateResult LocateWithWait()
    {
        var sw = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(_cfg.TimeoutSeconds);
        LocateResult? best = null;
        List<IWebElement> bestPath = new();

        while (true)
        {
            try
            {
                EnterPath(new List<IWebElement>());
                WaitForDocumentReady(TimeSpan.FromSeconds(Math.Max(1, _cfg.TimeoutSeconds - sw.Elapsed.TotalSeconds)));
                var (result, path) = SearchContext(new List<IWebElement>(), 0);
                if (result.Complete)
                {
                    _currentPath = path;
                    result.Context = DescribePath(path);
                    return result;
                }
                if (best == null || result.FoundCount > best.FoundCount) { best = result; bestPath = path; }
            }
            catch (WebDriverException) when (sw.Elapsed < timeout)
            {
                // page still navigating / frame detached: try again
            }

            if (sw.Elapsed >= timeout) break;
            Thread.Sleep(500);
        }

        // Not everything found: return the best partial result with fresh element references
        EnterPath(bestPath);
        _currentPath = bestPath;
        var partial = Run();
        partial.Context = DescribePath(bestPath);
        return partial;
    }

    /// <summary>Switches the driver back into the frame where the fields were found.</summary>
    public void EnterFieldContext() => EnterPath(_currentPath);

    private (LocateResult, List<IWebElement>) SearchContext(List<IWebElement> path, int depth)
    {
        var here = Run();
        if (here.Complete || !_cfg.SearchIframes || depth >= 3) return (here, path);

        var bestResult = here;
        var bestPath = path;
        foreach (var frame in FindFrames())
        {
            var childPath = new List<IWebElement>(path) { frame };
            try
            {
                EnterPath(childPath);
                var (result, resultPath) = SearchContext(childPath, depth + 1);
                if (result.Complete) return (result, resultPath);
                if (result.FoundCount > bestResult.FoundCount) { bestResult = result; bestPath = resultPath; }
            }
            catch (WebDriverException)
            {
                // cross-origin or detached frame: skip it
            }
            EnterPath(path);
        }
        return (bestResult, bestPath);
    }

    private IEnumerable<IWebElement> FindFrames()
    {
        var raw = ((IJavaScriptExecutor)_driver).ExecuteScript(FindFramesScript);
        if (raw is IEnumerable<object> items)
            return items.OfType<IWebElement>().ToList();
        return Array.Empty<IWebElement>();
    }

    private void EnterPath(List<IWebElement> path)
    {
        _driver.SwitchTo().DefaultContent();
        foreach (var frame in path) _driver.SwitchTo().Frame(frame);
    }

    private static string DescribePath(List<IWebElement> path) =>
        path.Count == 0 ? "top page" : $"iframe (depth {path.Count})";

    private void WaitForDocumentReady(TimeSpan max)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < max)
        {
            var state = ((IJavaScriptExecutor)_driver).ExecuteScript("return document.readyState;") as string;
            if (state == "complete") return;
            Thread.Sleep(200);
        }
    }

    /// <summary>Runs the scoring engine once in the current browsing context.</summary>
    public LocateResult Run()
    {
        var raw = ((IJavaScriptExecutor)_driver).ExecuteScript(
            "return (" + EngineScript.Value + ")(JSON.parse(arguments[0]));", _specJson);

        if (raw is not IDictionary<string, object> dict)
            throw new InvalidOperationException("Locator script returned an unexpected result.");

        var fields = new Dictionary<string, LocatedField>();
        if (Get(dict, "fields") is IDictionary<string, object> fieldMap)
        {
            foreach (var (key, value) in fieldMap)
            {
                if (value is IDictionary<string, object> f) fields[key] = ParseField(key, f);
            }
        }

        return new LocateResult
        {
            Complete = Get(dict, "complete") is true,
            Scope = Get(dict, "scope")?.ToString() ?? "",
            Fields = fields
        };
    }

    private static LocatedField ParseField(string key, IDictionary<string, object> d) => new()
    {
        Key = key,
        Found = Get(d, "found") is true,
        Score = Num(d, "score"),
        Description = Get(d, "description")?.ToString(),
        Evidence = StrList(d, "evidence"),
        Alternatives = StrList(d, "alternatives"),
        Warning = Get(d, "warning")?.ToString(),
        Element = Get(d, "element") as IWebElement,
        ClickTarget = Get(d, "clickTarget") as IWebElement,
        OptionText = Get(d, "optionText")?.ToString(),
        OptionIndex = Get(d, "optionIndex") == null ? -1 : (int)Num(d, "optionIndex"),
        OptionFound = Get(d, "optionFound") is true,
        Options = StrList(d, "options")
    };

    private static object? Get(IDictionary<string, object> d, string key) =>
        d.TryGetValue(key, out var v) ? v : null;

    private static double Num(IDictionary<string, object> d, string key)
    {
        var v = Get(d, key);
        return v == null ? 0 : Convert.ToDouble(v, CultureInfo.InvariantCulture);
    }

    private static List<string> StrList(IDictionary<string, object> d, string key) =>
        Get(d, key) is IEnumerable<object> items && Get(d, key) is not string
            ? items.Select(x => x?.ToString() ?? "").ToList()
            : new List<string>();

    private static string BuildSpec(AppConfig cfg)
    {
        object Field(string key, string kind, string desired, FieldDefinition def) => new
        {
            key,
            kind,
            desired,
            keywords = def.Keywords,
            weakKeywords = def.WeakKeywords,
            negativeKeywords = def.NegativeKeywords,
            optionVocabulary = def.OptionVocabulary
        };

        var spec = new
        {
            minScore = cfg.MinScore,
            includeShadow = cfg.IncludeShadowDom,
            fields = new[]
            {
                Field(FirstName, "text", cfg.TestData.FirstName, cfg.Fields.FirstName),
                Field(Gender, "radio", cfg.TestData.Gender, cfg.Fields.Gender),
                Field(State, "select", cfg.TestData.State, cfg.Fields.State)
            }
        };
        return JsonSerializer.Serialize(spec);
    }

    private static string LoadEngine()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SmartLocator.js")
            ?? throw new InvalidOperationException("Embedded resource SmartLocator.js is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}

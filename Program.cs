using System.Diagnostics;
using CloudQA.RobustForm.Actions;
using CloudQA.RobustForm.Config;
using CloudQA.RobustForm.Infrastructure;
using CloudQA.RobustForm.Locator;
using OpenQA.Selenium;

namespace CloudQA.RobustForm;

public static class Program
{
    // Test pages may mark the element that SHOULD be picked with data-expect="firstName|gender|state".
    // The locator never reads this attribute; it is only used afterwards to prove the right element was used.
    private const string PageHasExpectationsScript = """
        var found = false;
        (function walk(n) {
            if (found) return;
            if (n.nodeType === 1) {
                if (n.hasAttribute('data-expect')) { found = true; return; }
                if (n.shadowRoot) Array.prototype.forEach.call(n.shadowRoot.children, walk);
            }
            Array.prototype.forEach.call(n.children || [], walk);
        })(document.documentElement);
        return found;
        """;

    public static int Main(string[] args)
    {
        if (ConfigLoader.WantsHelp(args))
        {
            Console.WriteLine(ConfigLoader.HelpText);
            return 0;
        }

        AppConfig cfg;
        try
        {
            cfg = ConfigLoader.Load(args);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or FileNotFoundException)
        {
            Console.Error.WriteLine("Configuration error: " + ex.Message);
            return 2;
        }

        List<string> urls;
        if (cfg.SuiteDirectory != null)
        {
            if (!Directory.Exists(cfg.SuiteDirectory))
            {
                Console.Error.WriteLine($"Suite folder not found: {cfg.SuiteDirectory}");
                return 2;
            }
            urls = Directory.GetFiles(cfg.SuiteDirectory, "*.html")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => new Uri(Path.GetFullPath(p)).AbsoluteUri)
                .ToList();
        }
        else
        {
            urls = new List<string> { cfg.Url };
        }

        Console.WriteLine($"Browser: {cfg.Browser}{(cfg.Headless ? " (headless)" : "")}   Data: First Name=\"{cfg.TestData.FirstName}\", " +
                          $"Gender=\"{cfg.TestData.Gender}\", State=\"{cfg.TestData.State}\"");

        IWebDriver driver;
        try
        {
            driver = DriverFactory.Create(cfg);
        }
        catch (WebDriverException ex)
        {
            Console.Error.WriteLine($"Could not start {cfg.Browser}: {ex.Message}");
            return 3;
        }

        var runs = new List<PageRun>();
        try
        {
            foreach (var url in urls)
            {
                var run = RunPage(driver, cfg, url);
                runs.Add(run);
                Report.PrintPage(run);
            }
        }
        finally
        {
            driver.Quit();
        }

        Report.PrintSummary(runs);
        var reportPath = Report.Save(runs, cfg.ReportDirectory);
        Console.WriteLine($"Report : {Path.GetFullPath(reportPath)}");
        return runs.All(r => r.Passed) ? 0 : 1;
    }

    private static PageRun RunPage(IWebDriver driver, AppConfig cfg, string url)
    {
        var run = new PageRun { Url = url };
        var sw = Stopwatch.StartNew();
        try
        {
            driver.SwitchTo().DefaultContent();
            driver.Navigate().GoToUrl(url);

            var locator = new SmartFormLocator(driver, cfg);
            var located = locator.LocateWithWait();
            run.Scope = located.Scope;
            run.Context = located.Context;

            var actions = new FieldActions(driver, cfg.HighlightElements && !cfg.Headless);
            var data = cfg.TestData;
            var checkExpectations = (bool)(((IJavaScriptExecutor)driver).ExecuteScript(PageHasExpectationsScript) ?? false);

            FieldReport Act(string key, Func<LocatedField, StepResult> action)
            {
                var field = located.Fields.GetValueOrDefault(key) ?? new LocatedField { Key = key };
                StepResult step;
                try
                {
                    step = action(field);
                }
                catch (StaleElementReferenceException)
                {
                    // The page re-rendered between locating and acting: locate again and retry once
                    located = locator.LocateWithWait();
                    field = located.Fields.GetValueOrDefault(key) ?? new LocatedField { Key = key };
                    step = action(field);
                }

                var report = FieldReport.From(field, step);
                if (checkExpectations && field.Element != null)
                {
                    var marker = ((IJavaScriptExecutor)driver).ExecuteScript("return arguments[0].getAttribute('data-expect');", field.Element) as string;
                    var correct = marker == key;
                    report.ExpectationCheck = correct
                        ? "acted on the element the page marks as the real field"
                        : "WRONG ELEMENT: the page marks a different element as the real field";
                    report.Passed &= correct;
                }
                return report;
            }

            run.Fields.Add(Act(SmartFormLocator.FirstName, f => actions.FillText(f, data.FirstName)));
            run.Fields.Add(Act(SmartFormLocator.Gender, f => actions.ChooseRadio(f, data.Gender)));
            run.Fields.Add(Act(SmartFormLocator.State, f => actions.SelectOption(f, data.State)));

            run.Screenshot = TakeScreenshot(driver, cfg.ReportDirectory, url);
            run.Passed = run.Fields.Count == 3 && run.Fields.All(f => f.Passed);
        }
        catch (Exception ex) when (ex is WebDriverException or InvalidOperationException)
        {
            run.Error = ex.GetType().Name + ": " + ex.Message;
            run.Passed = false;
            try { run.Screenshot = TakeScreenshot(driver, cfg.ReportDirectory, url); } catch (WebDriverException) { /* ignore */ }
        }

        run.Seconds = sw.Elapsed.TotalSeconds;
        return run;
    }

    private static string TakeScreenshot(IWebDriver driver, string directory, string url)
    {
        Directory.CreateDirectory(directory);
        var name = Path.GetFileNameWithoutExtension(new Uri(url).LocalPath);
        if (string.IsNullOrWhiteSpace(name)) name = "page";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        var file = Path.Combine(directory, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        ((ITakesScreenshot)driver).GetScreenshot().SaveAsFile(file);
        return Path.GetFullPath(file);
    }
}

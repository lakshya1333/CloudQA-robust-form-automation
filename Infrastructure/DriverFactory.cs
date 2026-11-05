using CloudQA.RobustForm.Config;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Edge;
using OpenQA.Selenium.Firefox;

namespace CloudQA.RobustForm.Infrastructure;

public static class DriverFactory
{
    /// <summary>Selenium Manager (built into Selenium 4) resolves the matching driver binary automatically.</summary>
    public static IWebDriver Create(AppConfig cfg)
    {
        IWebDriver driver;
        switch (cfg.Browser.Trim().ToLowerInvariant())
        {
            case "edge":
            case "msedge":
                var edge = new EdgeOptions();
                if (cfg.Headless) edge.AddArgument("--headless=new");
                edge.AddArgument("--window-size=1400,1000");
                driver = new EdgeDriver(edge);
                break;

            case "firefox":
                var ff = new FirefoxOptions();
                if (cfg.Headless) ff.AddArgument("-headless");
                driver = new FirefoxDriver(ff);
                driver.Manage().Window.Size = new System.Drawing.Size(1400, 1000);
                break;

            case "chrome":
            default:
                var chrome = new ChromeOptions();
                if (cfg.Headless) chrome.AddArgument("--headless=new");
                chrome.AddArgument("--window-size=1400,1000");
                chrome.AddArgument("--disable-search-engine-choice-screen");
                driver = new ChromeDriver(chrome);
                break;
        }

        driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(Math.Max(30, cfg.TimeoutSeconds * 2));
        driver.Manage().Timeouts().AsynchronousJavaScript = TimeSpan.FromSeconds(30);
        return driver;
    }
}

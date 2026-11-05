using System.Text.RegularExpressions;
using CloudQA.RobustForm.Locator;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;

namespace CloudQA.RobustForm.Actions;

public sealed class StepResult
{
    public required string Field { get; init; }
    public bool Passed { get; set; }
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
}

/// <summary>
/// Performs the user actions like a person would (click/type/select), falls back to
/// alternative interaction paths when a widget is styled or covered, and then reads
/// the state back from the page to verify the action really took effect.
/// </summary>
public sealed class FieldActions
{
    private readonly IWebDriver _driver;
    private readonly bool _highlight;
    private IJavaScriptExecutor Js => (IJavaScriptExecutor)_driver;

    public FieldActions(IWebDriver driver, bool highlight)
    {
        _driver = driver;
        _highlight = highlight;
    }

    public StepResult FillText(LocatedField field, string value)
    {
        var step = new StepResult { Field = field.Key, Action = $"type \"{value}\"" };
        if (!EnsureFound(field, step)) return step;
        var el = field.Element!;
        Prepare(el);

        try
        {
            el.Clear();
            el.SendKeys(value);
        }
        catch (WebDriverException ex) when (ex is not StaleElementReferenceException)
        {
            step.Detail = $"native typing failed ({ex.GetType().Name}), set value via script. ";
            SetValueByScript(el, value);
        }

        var actual = el.GetDomProperty("value") ?? "";
        if (actual != value)
        {
            // e.g. a masked/controlled input swallowed keystrokes
            SetValueByScript(el, value);
            actual = el.GetDomProperty("value") ?? "";
            step.Detail += "value re-applied via script. ";
        }

        step.Passed = actual == value;
        step.Detail += step.Passed ? $"read back \"{actual}\"" : $"expected \"{value}\" but field contains \"{actual}\"";
        return step;
    }

    public StepResult ChooseRadio(LocatedField field, string desired)
    {
        var step = new StepResult { Field = field.Key, Action = $"choose \"{desired}\"" };
        if (!EnsureFound(field, step)) return step;
        if (!field.OptionFound)
        {
            step.Detail = $"group found but it has no option matching \"{desired}\" (options: {string.Join(", ", field.Options)})";
            return step;
        }

        var radio = field.Element!;
        Prepare(field.ClickTarget ?? radio);

        try
        {
            radio.Click();
        }
        catch (WebDriverException ex) when (ex is not StaleElementReferenceException)
        {
            // Styled radios are often hidden behind their label: click the label instead
            if (field.ClickTarget != null)
            {
                try { field.ClickTarget.Click(); step.Detail = "clicked option label. "; }
                catch (WebDriverException) { Js.ExecuteScript("arguments[0].click();", radio); step.Detail = "clicked via script. "; }
            }
            else
            {
                Js.ExecuteScript("arguments[0].click();", radio);
                step.Detail = "clicked via script. ";
            }
        }

        if (!radio.Selected)
        {
            Js.ExecuteScript("arguments[0].click();", radio);
            step.Detail += "re-clicked via script. ";
        }

        step.Passed = radio.Selected;
        step.Detail += step.Passed ? $"option \"{field.OptionText}\" is selected" : $"option \"{field.OptionText}\" did not become selected";
        return step;
    }

    public StepResult SelectOption(LocatedField field, string desired)
    {
        var step = new StepResult { Field = field.Key, Action = $"select \"{desired}\"" };
        if (!EnsureFound(field, step)) return step;
        if (!field.OptionFound || field.OptionIndex < 0)
        {
            step.Detail = $"dropdown found but it has no option matching \"{desired}\"";
            return step;
        }

        var el = field.Element!;
        Prepare(el);
        var select = new SelectElement(el);

        try
        {
            select.SelectByIndex(field.OptionIndex);
        }
        catch (WebDriverException ex) when (ex is not StaleElementReferenceException)
        {
            Js.ExecuteScript(
                "arguments[0].selectedIndex = arguments[1];" +
                "arguments[0].dispatchEvent(new Event('input', {bubbles: true}));" +
                "arguments[0].dispatchEvent(new Event('change', {bubbles: true}));",
                el, field.OptionIndex);
            step.Detail = "selected via script. ";
        }

        var selectedText = select.SelectedOption.Text.Trim();
        step.Passed = Normalize(selectedText) == Normalize(field.OptionText) ||
                      Normalize(select.SelectedOption.GetDomProperty("value")) == Normalize(desired);
        step.Detail += step.Passed ? $"selected option is \"{selectedText}\"" : $"selected option is \"{selectedText}\", expected \"{field.OptionText}\"";
        return step;
    }

    private static bool EnsureFound(LocatedField field, StepResult step)
    {
        if (field.Found && field.Element != null) return true;
        step.Passed = false;
        step.Detail = $"field not found (best candidate score {field.Score:0.00}). " +
                      (field.Alternatives.Count > 0 ? "Closest: " + string.Join(" | ", field.Alternatives) : "No candidates.");
        return false;
    }

    private void Prepare(IWebElement el)
    {
        Js.ExecuteScript("arguments[0].scrollIntoView({block: 'center', inline: 'nearest'});", el);
        if (_highlight)
            Js.ExecuteScript("arguments[0].style.outline = '3px solid #e8590c'; arguments[0].style.outlineOffset = '2px';", el);
    }

    private void SetValueByScript(IWebElement el, string value)
    {
        // Uses the native setter so frameworks such as React/Angular notice the change
        Js.ExecuteScript(
            "var el = arguments[0], v = arguments[1];" +
            "var setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;" +
            "setter.call(el, v);" +
            "el.dispatchEvent(new Event('input', {bubbles: true}));" +
            "el.dispatchEvent(new Event('change', {bubbles: true}));",
            el, value);
    }

    private static string Normalize(string? s) =>
        Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]+", "");
}

namespace CloudQA.RobustForm.Config;

/// <summary>What a field means: words that identify it, hint at it, or contradict it.</summary>
public sealed class FieldDefinition
{
    public List<string> Keywords { get; set; } = new();
    public List<string> WeakKeywords { get; set; } = new();
    public List<string> NegativeKeywords { get; set; } = new();
    public List<string> OptionVocabulary { get; set; } = new();
}

public sealed class FieldsConfig
{
    public FieldDefinition FirstName { get; set; } = new()
    {
        Keywords = new() { "first name", "firstname", "fname", "f name", "given name", "forename", "first" },
        WeakKeywords = new() { "name" },
        NegativeKeywords = new()
        {
            "last", "lname", "surname", "family", "middle", "father", "mother", "spouse", "guardian", "parent",
            "nick", "nickname", "user", "username", "company", "maiden", "emergency", "search"
        }
    };

    public FieldDefinition Gender { get; set; } = new()
    {
        Keywords = new() { "gender", "sex" },
        OptionVocabulary = new() { "male", "female", "man", "woman", "transgender", "non binary", "nonbinary", "prefer not to say" }
    };

    public FieldDefinition State { get; set; } = new()
    {
        Keywords = new() { "state", "province", "region", "state province" },
        NegativeKeywords = new() { "country", "city", "shipping", "billing", "status" }
    };
}

public sealed class TestDataConfig
{
    public string FirstName { get; set; } = "John";
    public string Gender { get; set; } = "Female";
    public string State { get; set; } = "India";
}

public sealed class AppConfig
{
    public string Url { get; set; } = "https://app.cloudqa.io/home/AutomationPracticeForm";
    public string Browser { get; set; } = "chrome";
    public bool Headless { get; set; }
    public int TimeoutSeconds { get; set; } = 15;
    public bool SearchIframes { get; set; } = true;
    public bool IncludeShadowDom { get; set; } = true;
    public double MinScore { get; set; } = 0.45;
    public string ReportDirectory { get; set; } = "Reports";
    public bool HighlightElements { get; set; } = true;
    public TestDataConfig TestData { get; set; } = new();
    public FieldsConfig Fields { get; set; } = new();

    /// <summary>Folder of .html pages to run one after another (set with --suite).</summary>
    public string? SuiteDirectory { get; set; }
}

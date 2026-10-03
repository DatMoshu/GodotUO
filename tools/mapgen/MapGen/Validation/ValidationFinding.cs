namespace CentrED.MapGen.Validation;

public enum ValidationSeverity : byte { Info = 0, Warn = 1, Error = 2 }

public sealed record ValidationFinding(
    string RuleName,
    ValidationSeverity Severity,
    string Message,
    int X = -1,
    int Y = -1,
    bool AutoFixed = false);

public sealed class ValidationResult
{
    public string RuleName { get; }
    public int Fixed { get; set; }
    public int Warned { get; set; }
    public int Errors { get; set; }
    public List<ValidationFinding> Findings { get; } = new();

    public ValidationResult(string ruleName) { RuleName = ruleName; }

    public void Add(ValidationFinding f)
    {
        Findings.Add(f);
        switch (f.Severity)
        {
            case ValidationSeverity.Error: Errors++; break;
            case ValidationSeverity.Warn: Warned++; break;
        }
        if (f.AutoFixed) Fixed++;
    }
}

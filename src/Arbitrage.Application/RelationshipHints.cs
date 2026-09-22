using System.Globalization;
using System.Text.RegularExpressions;
using Arbitrage.Domain;

namespace Arbitrage.Application;

// Review hints only. Title provenance cannot pass RelationshipValidator's authority gate.
public static partial class RelationshipHints
{
    public static MarketSemantics FromTitle(string? title)
    {
        var text = RelationshipPolicy.Normalize(title);
        SemanticFact<string> Hint(string value) => new(value, FactSource.Title, title ?? "");
        var years = Year().Matches(text).Select(m => m.Value).Distinct().ToArray();
        var predicate = text.Contains("NOMINEE", StringComparison.Ordinal) || text.Contains("NOMINATION", StringComparison.Ordinal) ? "nomination" :
            text.Contains("ELECTION", StringComparison.Ordinal) && text.Contains("WIN", StringComparison.Ordinal) ? "win election" : null;
        var geography = text.Contains("CALIFORNIA", StringComparison.Ordinal) ? "California" : Regex.IsMatch(text, @"\bUS\b|\bUNITED STATES\b", RegexOptions.CultureInvariant) ? "US" : null;
        SemanticFact<SemanticThreshold>? threshold = null;
        var match = Threshold().Match(text);
        if (match.Success && decimal.TryParse(match.Groups[2].Value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            threshold = new(new("Unspecified metric", match.Groups[1].Value switch { ">" => ThresholdOperator.GreaterThan, ">=" => ThresholdOperator.GreaterThanOrEqual, "<" => ThresholdOperator.LessThan, "<=" => ThresholdOperator.LessThanOrEqual, _ => ThresholdOperator.Equal }, value, match.Groups[3].Value), FactSource.Title, match.Value);
        return new() { Edition = years.Length == 1 ? Hint(years[0]) : null, Predicate = predicate is null ? null : Hint(predicate), Geography = geography is null ? null : Hint(geography),
            Threshold = threshold, ThresholdApplicable = threshold is null ? null : new(true, FactSource.Title, threshold.Reference) };
    }
    [GeneratedRegex(@"\b(?:19|20|21)\d{2}\b")] private static partial Regex Year();
    [GeneratedRegex(@"(>=|<=|>|<|=)\s*([+-]?\d+(?:\.\d+)?)\s*(USD\b|EUR\b|%|BASIS POINTS\b)")] private static partial Regex Threshold();
}

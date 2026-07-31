using System.Text;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

public sealed class ElementResolutionAmbiguityException : InvalidOperationException
{
    public ElementResolutionAmbiguityException(ResolveElementAmbiguity ambiguity)
        : base(BuildMessage(ambiguity))
    {
        Ambiguity = ambiguity;
    }

    internal ElementResolutionAmbiguityException(ResolveElementAmbiguity ambiguity, Exception diagnosticCause)
        : base(BuildMessage(ambiguity), diagnosticCause ?? throw new ArgumentNullException(nameof(diagnosticCause)))
    {
        Ambiguity = ambiguity;
    }

    public ResolveElementAmbiguity Ambiguity { get; }

    private static string BuildMessage(ResolveElementAmbiguity ambiguity)
    {
        ArgumentNullException.ThrowIfNull(ambiguity);

        var recovery = ambiguity.Candidates.Count > 0
            ? "Retry with locator.index using a candidate index, or reuse a candidate elementId."
            : "Retry with a stricter locator or locator.index.";
        var builder = new StringBuilder(
            $"{ambiguity.Code}: Locator is ambiguous (found {ambiguity.DiscoveredCandidates}). {recovery}");

        foreach (var candidate in ambiguity.Candidates.Take(5))
        {
            var element = candidate.Element;
            builder.AppendLine();
            builder.Append($"[{candidate.Index}] {Bound(element.Type)}");
            AppendIdentity(builder, "automationId", element.AutomationId);
            AppendIdentity(builder, "name", element.Name);
            AppendIdentity(builder, "elementId", element.ElementId);
            AppendIdentity(builder, "xpath", element.XPath);
        }

        return builder.ToString();
    }

    private static void AppendIdentity(StringBuilder builder, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.Append($", {name}='{Bound(value)}'");
        }
    }

    private static string Bound(string value) =>
        value.Length <= 160 ? value : value[..160] + "...";
}

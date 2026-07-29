using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

internal static class DiagnosticSnapshotRequestValidator
{
    public static CaptureDiagnosticSnapshotRequest Validate(CaptureDiagnosticSnapshotRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);

        if (request.Sections is null || request.Sections.Count is < 1 or > DiagnosticSnapshotLimits.MaxSections)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.Sections),
                $"sections must contain between 1 and {DiagnosticSnapshotLimits.MaxSections} entries.");
        }

        var sections = request.Sections.ToArray();
        if (sections.Any(section => !Enum.IsDefined(section)))
        {
            throw new ArgumentOutOfRangeException(nameof(request.Sections), "sections contains an unsupported value.");
        }

        if (sections.Distinct().Count() != sections.Length)
        {
            throw new ArgumentException("sections must not contain duplicates.", nameof(request.Sections));
        }

        if (request.Locator is not null && !string.IsNullOrWhiteSpace(request.ElementId))
        {
            throw new ArgumentException("Provide at most one of locator or elementId.");
        }

        if (request.ElementId is not null && string.IsNullOrWhiteSpace(request.ElementId))
        {
            throw new ArgumentException("elementId must not be empty or whitespace.", nameof(request.ElementId));
        }

        var budget = request.Budget ?? new DiagnosticSnapshotBudget();
        ValidateRange(nameof(budget.MaxDepth), budget.MaxDepth, DiagnosticSnapshotLimits.MinDepth, DiagnosticSnapshotLimits.MaxDepth);
        ValidateRange(nameof(budget.MaxItems), budget.MaxItems, DiagnosticSnapshotLimits.MinItems, DiagnosticSnapshotLimits.MaxItems);
        ValidateRange(nameof(budget.MaxNodes), budget.MaxNodes, DiagnosticSnapshotLimits.MinNodes, DiagnosticSnapshotLimits.MaxNodes);
        ValidateRange(nameof(budget.MaxValueLength), budget.MaxValueLength, DiagnosticSnapshotLimits.MinValueLength, DiagnosticSnapshotLimits.MaxValueLength);
        ValidateRange(nameof(budget.MaxPayloadChars), budget.MaxPayloadChars, DiagnosticSnapshotLimits.MinPayloadChars, DiagnosticSnapshotLimits.MaxPayloadChars);
        ValidateRange(nameof(request.TimeoutMs), request.TimeoutMs, DiagnosticSnapshotLimits.MinTimeoutMs, DiagnosticSnapshotLimits.MaxTimeoutMs);

        var propertyNames = NormalizeNames(request.PropertyNames, nameof(request.PropertyNames));
        if (sections.Contains(DiagnosticSection.WpfProperties) && propertyNames is null)
        {
            throw new ArgumentException("propertyNames is required when sections includes WpfProperties.", nameof(request.PropertyNames));
        }

        if (!sections.Contains(DiagnosticSection.WpfProperties) && propertyNames is not null)
        {
            throw new ArgumentException("propertyNames is only valid when sections includes WpfProperties.", nameof(request.PropertyNames));
        }

        var dataContextProperties = NormalizeNames(request.DataContextProperties, nameof(request.DataContextProperties));
        if (!sections.Contains(DiagnosticSection.DataContext) && dataContextProperties is not null)
        {
            throw new ArgumentException(
                "dataContextProperties is only valid when sections includes DataContext.",
                nameof(request.DataContextProperties));
        }

        return request with
        {
            SessionId = request.SessionId.Trim(),
            Sections = sections,
            ElementId = request.ElementId?.Trim(),
            Budget = budget,
            PropertyNames = propertyNames,
            DataContextProperties = dataContextProperties
        };
    }

    private static IReadOnlyList<string>? NormalizeNames(IReadOnlyList<string>? values, string parameterName)
    {
        if (values is null)
        {
            return null;
        }

        if (values.Count is < 1 or > DiagnosticSnapshotLimits.MaxPropertyNames)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"{parameterName} must contain between 1 and {DiagnosticSnapshotLimits.MaxPropertyNames} entries.");
        }

        var normalized = new string[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"{parameterName} must not contain empty names.", parameterName);
            }

            value = value.Trim();
            if (value.Length > DiagnosticSnapshotLimits.MaxPropertyNameLength)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    $"{parameterName} entries must not exceed {DiagnosticSnapshotLimits.MaxPropertyNameLength} characters.");
            }

            normalized[index] = value;
        }

        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw new ArgumentException($"{parameterName} must not contain duplicates.", parameterName);
        }

        return normalized;
    }

    private static void ValidateRange(string parameterName, int value, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"{parameterName} must be between {minimum} and {maximum}.");
        }
    }
}

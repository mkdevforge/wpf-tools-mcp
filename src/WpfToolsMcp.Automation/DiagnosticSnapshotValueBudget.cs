using System.Text.Json.Nodes;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

internal static class DiagnosticSnapshotValueBudget
{
    public static IReadOnlyList<DiagnosticSectionResult> Apply(
        IReadOnlyList<DiagnosticSectionResult> sections,
        int maxValueLength)
    {
        ArgumentNullException.ThrowIfNull(sections);
        if (maxValueLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxValueLength));
        }

        return sections.Select(section => Apply(section, maxValueLength)).ToArray();
    }

    private static DiagnosticSectionResult Apply(
        DiagnosticSectionResult section,
        int maxValueLength)
    {
        var message = Truncate(section.Message, maxValueLength, out _);
        if (section.Data is null || section.Section == DiagnosticSection.Screenshot)
        {
            return section with { Message = message };
        }

        var data = section.Data.DeepClone();
        var truncated = false;
        BoundStrings(data, maxValueLength, ref truncated);
        if (!truncated)
        {
            return section with { Message = message };
        }

        if (data is JsonObject evidence && evidence.ContainsKey("truncated"))
        {
            evidence["truncated"] = true;
            if (evidence["truncatedReason"] is null)
            {
                evidence["truncatedReason"] = "maxValueLength";
            }
        }

        return section with
        {
            Status = section.Status == DiagnosticSectionStatus.Success
                ? DiagnosticSectionStatus.Truncated
                : section.Status,
            Data = data,
            Code = section.Code ?? "maxValueLength",
            Message = message ?? "Evidence strings reached maxValueLength."
        };
    }

    private static void BoundStrings(JsonNode? node, int maxValueLength, ref bool truncated)
    {
        switch (node)
        {
            case JsonObject value:
                foreach (var property in value.ToArray())
                {
                    if (property.Value is JsonValue scalar &&
                        scalar.TryGetValue<string>(out var text))
                    {
                        var bounded = Truncate(text, maxValueLength, out var valueTruncated);
                        if (valueTruncated)
                        {
                            value[property.Key] = bounded;
                            truncated = true;
                        }

                        continue;
                    }

                    BoundStrings(property.Value, maxValueLength, ref truncated);
                }

                break;

            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    if (array[index] is JsonValue scalar &&
                        scalar.TryGetValue<string>(out var text))
                    {
                        var bounded = Truncate(text, maxValueLength, out var valueTruncated);
                        if (valueTruncated)
                        {
                            array[index] = bounded;
                            truncated = true;
                        }

                        continue;
                    }

                    BoundStrings(array[index], maxValueLength, ref truncated);
                }

                break;
        }
    }

    private static string? Truncate(string? value, int maxValueLength, out bool truncated)
    {
        truncated = value is not null && value.Length > maxValueLength;
        if (!truncated)
        {
            return value;
        }

        var length = maxValueLength;
        if (length > 0 &&
            length < value!.Length &&
            char.IsHighSurrogate(value[length - 1]) &&
            char.IsLowSurrogate(value[length]))
        {
            length--;
        }

        return value![..length];
    }
}

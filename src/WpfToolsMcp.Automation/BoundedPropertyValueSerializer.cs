using System.Collections;
using System.Drawing;
using System.Globalization;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;

namespace WpfToolsMcp.Automation;

[Flags]
internal enum PropertyValueTruncation
{
    None = 0,
    StringLength = 1,
    CollectionItems = 2,
    ValueDepth = 4,
    ValueCharacters = 8,
    XPathLength = 16
}

internal sealed class PropertyValueBudget
{
    internal const int MaxStringLength = 2_000;
    internal const int MaxCollectionItems = 50;
    internal const int MaxValueDepth = 2;
    internal const int MaxSerializedValueCharacters = 20_000;
    internal const int MaxXPathLength = 2_000;

    private readonly int _maxStringLength;
    private readonly int _maxXPathLength;
    private int _remainingCharacters;

    internal PropertyValueBudget(
        int maxStringLength = MaxStringLength,
        int maxCollectionItems = MaxCollectionItems,
        int maxValueDepth = MaxValueDepth,
        int maxSerializedValueCharacters = MaxSerializedValueCharacters,
        int maxXPathLength = MaxXPathLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStringLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCollectionItems, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxValueDepth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSerializedValueCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxXPathLength, 1);

        _maxStringLength = maxStringLength;
        MaxCollectionItemsForCapture = maxCollectionItems;
        MaxValueDepthForCapture = maxValueDepth;
        _remainingCharacters = maxSerializedValueCharacters;
        _maxXPathLength = maxXPathLength;
    }

    internal int MaxCollectionItemsForCapture { get; }

    internal int MaxValueDepthForCapture { get; }

    internal PropertyValueTruncation Truncation { get; private set; }

    internal bool IsCharacterLimitReached =>
        (Truncation & PropertyValueTruncation.ValueCharacters) != 0;

    internal string? ApplyStringLimit(string? value)
    {
        if (value is null || value.Length <= _maxStringLength)
        {
            return value;
        }

        Truncation |= PropertyValueTruncation.StringLength;
        var length = _maxStringLength;
        if (char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length]))
        {
            length--;
        }

        return value[..length];
    }

    internal void Mark(PropertyValueTruncation truncation) =>
        Truncation |= truncation;

    internal bool TryConsume(int characters)
    {
        if (IsCharacterLimitReached)
        {
            return false;
        }

        if (characters <= _remainingCharacters)
        {
            _remainingCharacters -= characters;
            return true;
        }

        _remainingCharacters = 0;
        Truncation |= PropertyValueTruncation.ValueCharacters;
        return false;
    }

    internal bool IsXPathTooLong(string value) => value.Length > _maxXPathLength;
}

internal static class BoundedPropertyValueSerializer
{
    internal static JsonNode? Serialize(object? value, PropertyValueBudget budget) =>
        Serialize(value, depth: 0, budget);

    internal static bool TrySerialize(
        object? value,
        PropertyValueBudget budget,
        out JsonNode? serialized)
    {
        serialized = Serialize(value, budget);
        return serialized is not null || !budget.IsCharacterLimitReached;
    }

    internal static string? SerializeString(string? value, PropertyValueBudget budget)
    {
        var bounded = budget.ApplyStringLimit(value);
        var json = bounded is null ? "null" : JsonValue.Create(bounded)!.ToJsonString();
        return budget.TryConsume(json.Length) ? bounded : null;
    }

    internal static string? SerializeXPath(
        string value,
        PropertyValueBudget budget,
        out bool omitted)
    {
        if (budget.IsXPathTooLong(value))
        {
            budget.Mark(PropertyValueTruncation.XPathLength);
            omitted = true;
            return null;
        }

        var json = JsonValue.Create(value)!.ToJsonString();
        omitted = !budget.TryConsume(json.Length);
        return omitted ? null : value;
    }

    internal static string? GetTruncatedReason(
        bool propertiesTruncated,
        PropertyValueBudget budget,
        bool mappingCandidatesTruncated = false)
        => GetTruncatedReasons(propertiesTruncated, budget, mappingCandidatesTruncated).FirstOrDefault();

    internal static IReadOnlyList<string> GetTruncatedReasons(
        bool propertiesTruncated,
        PropertyValueBudget budget,
        bool mappingCandidatesTruncated = false)
    {
        var reasons = new List<string>(capacity: 7);
        if (propertiesTruncated)
        {
            reasons.Add("maxProperties");
        }

        if (mappingCandidatesTruncated)
        {
            reasons.Add("maxMappingCandidates");
        }

        var valueTruncation = budget.Truncation;
        if ((valueTruncation & PropertyValueTruncation.ValueCharacters) != 0)
        {
            reasons.Add("maxValueCharacters");
        }

        if ((valueTruncation & PropertyValueTruncation.XPathLength) != 0)
        {
            reasons.Add("maxXPathLength");
        }

        if ((valueTruncation & PropertyValueTruncation.StringLength) != 0)
        {
            reasons.Add("maxStringLength");
        }

        if ((valueTruncation & PropertyValueTruncation.CollectionItems) != 0)
        {
            reasons.Add("maxCollectionItems");
        }

        if ((valueTruncation & PropertyValueTruncation.ValueDepth) != 0)
        {
            reasons.Add("maxValueDepth");
        }

        return reasons;
    }

    private static JsonNode? Serialize(object? value, int depth, PropertyValueBudget budget)
    {
        if (budget.IsCharacterLimitReached)
        {
            return null;
        }

        if (value is null)
        {
            return Consume(JsonNode.Parse("null"), budget);
        }

        if (value is string text)
        {
            return SerializeStringNode(text, budget);
        }

        if (value is char character)
        {
            return SerializeStringNode(character.ToString(), budget);
        }

        if (value is bool boolean)
        {
            return Consume(JsonValue.Create(boolean), budget);
        }

        if (value is byte byteValue)
        {
            return Consume(JsonValue.Create(byteValue), budget);
        }

        if (value is sbyte signedByte)
        {
            return Consume(JsonValue.Create(signedByte), budget);
        }

        if (value is short shortValue)
        {
            return Consume(JsonValue.Create(shortValue), budget);
        }

        if (value is ushort unsignedShort)
        {
            return Consume(JsonValue.Create(unsignedShort), budget);
        }

        if (value is int integer)
        {
            return Consume(JsonValue.Create(integer), budget);
        }

        if (value is uint unsignedInteger)
        {
            return Consume(JsonValue.Create(unsignedInteger), budget);
        }

        if (value is long longValue)
        {
            return Consume(JsonValue.Create(longValue), budget);
        }

        if (value is ulong unsignedLong)
        {
            return Consume(JsonValue.Create(unsignedLong), budget);
        }

        if (value is double doubleValue)
        {
            return Consume(CreateDouble(doubleValue), budget);
        }

        if (value is float floatValue)
        {
            return Consume(CreateFloat(floatValue), budget);
        }

        if (value is decimal decimalValue)
        {
            return Consume(JsonValue.Create(decimalValue), budget);
        }

        if (value is Enum enumValue)
        {
            return SerializeStringNode(enumValue.ToString(), budget);
        }

        if (value is IntPtr pointer)
        {
            return Consume(JsonValue.Create(pointer.ToInt64()), budget);
        }

        if (value is UIntPtr unsignedPointer)
        {
            return Consume(JsonValue.Create(unsignedPointer.ToUInt64()), budget);
        }

        if (value is Guid guid)
        {
            return SerializeStringNode(guid.ToString(), budget);
        }

        if (value is DateTime dateTime)
        {
            return Consume(JsonValue.Create(dateTime), budget);
        }

        if (value is DateTimeOffset dateTimeOffset)
        {
            return Consume(JsonValue.Create(dateTimeOffset), budget);
        }

        if (value is TimeSpan timeSpan)
        {
            return SerializeStringNode(timeSpan.ToString("c", CultureInfo.InvariantCulture), budget);
        }

        if (value is Rectangle rectangle)
        {
            return Consume(new JsonObject
            {
                ["X"] = rectangle.X,
                ["Y"] = rectangle.Y,
                ["Width"] = rectangle.Width,
                ["Height"] = rectangle.Height
            }, budget);
        }

        if (value is Point point)
        {
            return Consume(new JsonObject
            {
                ["X"] = point.X,
                ["Y"] = point.Y
            }, budget);
        }

        if (value is Size size)
        {
            return Consume(new JsonObject
            {
                ["Width"] = size.Width,
                ["Height"] = size.Height
            }, budget);
        }

        if (value is Color color)
        {
            return Consume(new JsonObject
            {
                ["A"] = color.A,
                ["R"] = color.R,
                ["G"] = color.G,
                ["B"] = color.B,
                ["Name"] = budget.ApplyStringLimit(color.Name)
            }, budget);
        }

        if (value is AutomationElement element)
        {
            return SerializeAutomationElement(element, budget);
        }

        if (value is IEnumerable enumerable)
        {
            return SerializeEnumerable(enumerable, depth, budget);
        }

        string fallback;
        try
        {
            fallback = value.ToString() ?? value.GetType().FullName ?? value.GetType().Name;
        }
        catch
        {
            fallback = value.GetType().FullName ?? value.GetType().Name;
        }

        return SerializeStringNode(fallback, budget);
    }

    private static JsonNode? SerializeEnumerable(
        IEnumerable enumerable,
        int depth,
        PropertyValueBudget budget)
    {
        if (depth >= budget.MaxValueDepthForCapture)
        {
            budget.Mark(PropertyValueTruncation.ValueDepth);
            return SerializeStringNode("<truncated:maxValueDepth>", budget);
        }

        if (!budget.TryConsume(2))
        {
            return null;
        }

        var array = new JsonArray();
        IEnumerator? enumerator = null;
        try
        {
            enumerator = enumerable.GetEnumerator();
            while (enumerator.MoveNext())
            {
                if (array.Count >= budget.MaxCollectionItemsForCapture)
                {
                    budget.Mark(PropertyValueTruncation.CollectionItems);
                    break;
                }

                if (array.Count > 0 && !budget.TryConsume(1))
                {
                    break;
                }

                var item = Serialize(enumerator.Current, depth + 1, budget);
                if (item is null && budget.IsCharacterLimitReached)
                {
                    break;
                }

                array.Add(item);
                if (budget.IsCharacterLimitReached)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            if (array.Count < budget.MaxCollectionItemsForCapture && !budget.IsCharacterLimitReached)
            {
                if (array.Count == 0 || budget.TryConsume(1))
                {
                    var error = Serialize($"<error: {ex.Message}>", depth + 1, budget);
                    if (error is not null || !budget.IsCharacterLimitReached)
                    {
                        array.Add(error);
                    }
                }
            }
            else if (array.Count >= budget.MaxCollectionItemsForCapture)
            {
                budget.Mark(PropertyValueTruncation.CollectionItems);
            }
        }
        finally
        {
            try
            {
                (enumerator as IDisposable)?.Dispose();
            }
            catch
            {
            }
        }

        return array;
    }

    private static JsonNode? SerializeAutomationElement(AutomationElement element, PropertyValueBudget budget)
    {
        var json = new JsonObject
        {
            ["elementType"] = ReadAutomationString(() => element.ControlType.ToString(), budget),
            ["automationId"] = ReadAutomationString(() => element.Properties.AutomationId.Value, budget),
            ["name"] = ReadAutomationString(() => element.Properties.Name.Value, budget),
            ["className"] = ReadAutomationString(() => element.Properties.ClassName.Value, budget)
        };

        return Consume(json, budget);
    }

    private static string? ReadAutomationString(Func<string?> read, PropertyValueBudget budget)
    {
        try
        {
            var value = read();
            return budget.ApplyStringLimit(string.IsNullOrWhiteSpace(value) ? null : value);
        }
        catch
        {
            return null;
        }
    }

    private static JsonNode? SerializeStringNode(string value, PropertyValueBudget budget)
    {
        var bounded = budget.ApplyStringLimit(value);
        return Consume(JsonValue.Create(bounded), budget);
    }

    private static JsonNode? Consume(JsonNode? node, PropertyValueBudget budget)
    {
        var characters = node?.ToJsonString().Length ?? 4;
        return budget.TryConsume(characters) ? node : null;
    }

    private static JsonNode CreateDouble(double value)
    {
        if (double.IsNaN(value))
        {
            return JsonValue.Create("{NaN}")!;
        }

        if (double.IsPositiveInfinity(value))
        {
            return JsonValue.Create("{Infinity}")!;
        }

        if (double.IsNegativeInfinity(value))
        {
            return JsonValue.Create("{-Infinity}")!;
        }

        return JsonValue.Create(value)!;
    }

    private static JsonNode CreateFloat(float value)
    {
        if (float.IsNaN(value))
        {
            return JsonValue.Create("{NaN}")!;
        }

        if (float.IsPositiveInfinity(value))
        {
            return JsonValue.Create("{Infinity}")!;
        }

        if (float.IsNegativeInfinity(value))
        {
            return JsonValue.Create("{-Infinity}")!;
        }

        return JsonValue.Create(value)!;
    }
}

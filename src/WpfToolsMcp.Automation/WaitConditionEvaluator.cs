using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

internal readonly record struct WaitEvaluationResult(bool Satisfied, string? FailureReason);

internal static class WaitConditionEvaluator
{
    private const string ValueUnset = "value_unset";
    private const string ValueUnavailable = "value_unavailable";
    private const string ValueError = "value_error";
    private const string ValueTruncated = "value_truncated";
    private const string ValueTypeMismatch = "value_type_mismatch";
    private const string ValueMismatch = "value_mismatch";

    public static bool TryValidateScalar(WaitScalar? scalar, out string? error)
    {
        if (scalar is null)
        {
            error = "Expected value is required.";
            return false;
        }

        var populatedFields = (scalar.StringValue is not null ? 1 : 0) +
                              (scalar.NumberValue.HasValue ? 1 : 0) +
                              (scalar.BooleanValue.HasValue ? 1 : 0);

        var valid = scalar.Kind switch
        {
            WaitScalarKind.String => scalar.StringValue is not null && populatedFields == 1,
            WaitScalarKind.Number => scalar.NumberValue.HasValue && populatedFields == 1,
            WaitScalarKind.Boolean => scalar.BooleanValue.HasValue && populatedFields == 1,
            WaitScalarKind.Null => populatedFields == 0,
            _ => false
        };

        if (!valid)
        {
            error = scalar.Kind switch
            {
                WaitScalarKind.String => "String expected values must populate only StringValue.",
                WaitScalarKind.Number => "Number expected values must populate only NumberValue.",
                WaitScalarKind.Boolean => "Boolean expected values must populate only BooleanValue.",
                WaitScalarKind.Null => "Null expected values must not populate a value field.",
                _ => $"Unsupported expected value kind '{scalar.Kind}'."
            };
            return false;
        }

        if (scalar.Kind == WaitScalarKind.Number && !double.IsFinite(scalar.NumberValue!.Value))
        {
            error = "Number expected values must be finite.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateComparison(
        WaitComparison comparison,
        WaitScalar? expected,
        out string? error)
    {
        if (!TryValidateScalar(expected, out error))
        {
            return false;
        }

        var supported = comparison switch
        {
            WaitComparison.Equals or WaitComparison.NotEquals => true,
            WaitComparison.Contains => expected!.Kind == WaitScalarKind.String,
            WaitComparison.GreaterThan or
                WaitComparison.GreaterThanOrEqual or
                WaitComparison.LessThan or
                WaitComparison.LessThanOrEqual => expected!.Kind == WaitScalarKind.Number,
            _ => false
        };

        if (supported)
        {
            error = null;
            return true;
        }

        error = comparison switch
        {
            WaitComparison.Contains => "Contains comparisons require a String expected value.",
            WaitComparison.GreaterThan or
                WaitComparison.GreaterThanOrEqual or
                WaitComparison.LessThan or
                WaitComparison.LessThanOrEqual => "Ordered comparisons require a Number expected value.",
            _ => $"Unsupported wait comparison '{comparison}'."
        };
        return false;
    }

    public static WaitEvaluationResult Evaluate(
        WaitObservedValue observed,
        WaitComparison comparison,
        WaitScalar expected)
    {
        ArgumentNullException.ThrowIfNull(observed);

        if (!TryValidateComparison(comparison, expected, out var validationError))
        {
            throw new ArgumentException(validationError, nameof(expected));
        }

        var stateFailure = observed.State switch
        {
            WaitObservedValueState.Unset => ValueUnset,
            WaitObservedValueState.Unavailable => ValueUnavailable,
            WaitObservedValueState.Error => ValueError,
            WaitObservedValueState.Value or WaitObservedValueState.Null => null,
            _ => ValueUnavailable
        };
        if (stateFailure is not null)
        {
            return Failed(stateFailure);
        }

        if (observed.Truncated == true)
        {
            return Failed(ValueTruncated);
        }

        if (!TryReadObservedScalar(observed, out var actual))
        {
            return Failed(ValueTypeMismatch);
        }

        var satisfied = comparison switch
        {
            WaitComparison.Equals => ScalarEquals(actual, expected),
            WaitComparison.NotEquals => !ScalarEquals(actual, expected),
            WaitComparison.Contains => actual.Kind == WaitScalarKind.String &&
                                       actual.StringValue!.Contains(
                                           expected.StringValue!,
                                           StringComparison.Ordinal),
            WaitComparison.GreaterThan => TryCompareNumbers(actual, expected, result => result > 0),
            WaitComparison.GreaterThanOrEqual => TryCompareNumbers(actual, expected, result => result >= 0),
            WaitComparison.LessThan => TryCompareNumbers(actual, expected, result => result < 0),
            WaitComparison.LessThanOrEqual => TryCompareNumbers(actual, expected, result => result <= 0),
            _ => false
        };

        if (satisfied)
        {
            return new WaitEvaluationResult(true, null);
        }

        var requiresMatchingType = comparison is WaitComparison.Contains or
            WaitComparison.GreaterThan or
            WaitComparison.GreaterThanOrEqual or
            WaitComparison.LessThan or
            WaitComparison.LessThanOrEqual;
        return Failed(requiresMatchingType && actual.Kind != expected.Kind
            ? ValueTypeMismatch
            : ValueMismatch);
    }

    public static WaitObservedValue FromObserveStateValue(ObserveStateValue value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return new WaitObservedValue(
            State: value.State switch
            {
                ObserveStateValueState.Value => WaitObservedValueState.Value,
                ObserveStateValueState.Null => WaitObservedValueState.Null,
                ObserveStateValueState.Unset => WaitObservedValueState.Unset,
                ObserveStateValueState.Unavailable => WaitObservedValueState.Unavailable,
                ObserveStateValueState.Error => WaitObservedValueState.Error,
                _ => WaitObservedValueState.Unavailable
            },
            Value: value.Value?.DeepClone(),
            ValueType: value.ValueType,
            Truncated: value.Truncated,
            Detail: value.Error);
    }

    private static WaitEvaluationResult Failed(string reason) => new(false, reason);

    private static bool TryReadObservedScalar(WaitObservedValue observed, out WaitScalar scalar)
    {
        if (observed.State == WaitObservedValueState.Null)
        {
            if (observed.Value is not null)
            {
                scalar = default!;
                return false;
            }

            scalar = new WaitScalar(WaitScalarKind.Null);
            return true;
        }

        if (observed.State != WaitObservedValueState.Value || observed.Value is null)
        {
            scalar = default!;
            return false;
        }

        try
        {
            switch (observed.Value.GetValueKind())
            {
                case JsonValueKind.String:
                    scalar = new WaitScalar(
                        WaitScalarKind.String,
                        StringValue: observed.Value.GetValue<string>());
                    return true;
                case JsonValueKind.Number:
                    if (double.TryParse(
                            observed.Value.ToJsonString(),
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out var number) &&
                        double.IsFinite(number))
                    {
                        scalar = new WaitScalar(WaitScalarKind.Number, NumberValue: number);
                        return true;
                    }

                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    scalar = new WaitScalar(
                        WaitScalarKind.Boolean,
                        BooleanValue: observed.Value.GetValue<bool>());
                    return true;
            }
        }
        catch (InvalidOperationException)
        {
            // A JsonNode with a non-scalar or incompatible backing value is not a wait scalar.
        }
        catch (FormatException)
        {
            // Invalid numeric payloads are reported as a type mismatch below.
        }

        scalar = default!;
        return false;
    }

    private static bool ScalarEquals(WaitScalar actual, WaitScalar expected)
    {
        if (actual.Kind != expected.Kind)
        {
            return false;
        }

        return actual.Kind switch
        {
            WaitScalarKind.String => string.Equals(
                actual.StringValue,
                expected.StringValue,
                StringComparison.Ordinal),
            WaitScalarKind.Number => actual.NumberValue == expected.NumberValue,
            WaitScalarKind.Boolean => actual.BooleanValue == expected.BooleanValue,
            WaitScalarKind.Null => true,
            _ => false
        };
    }

    private static bool TryCompareNumbers(
        WaitScalar actual,
        WaitScalar expected,
        Func<int, bool> predicate)
    {
        if (actual.Kind != WaitScalarKind.Number)
        {
            return false;
        }

        return predicate(actual.NumberValue!.Value.CompareTo(expected.NumberValue!.Value));
    }
}

internal sealed class ContinuousHoldTracker
{
    private readonly int _holdForMs;
    private long? _matchingSinceMs;

    public ContinuousHoldTracker(int holdForMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(holdForMs);
        _holdForMs = holdForMs;
    }

    public bool Observe(bool conditionSatisfied, long elapsedMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedMs);

        if (!conditionSatisfied)
        {
            Reset();
            return false;
        }

        if (_matchingSinceMs is null || elapsedMs < _matchingSinceMs.Value)
        {
            _matchingSinceMs = elapsedMs;
        }

        return elapsedMs - _matchingSinceMs.Value >= _holdForMs;
    }

    public void Reset() => _matchingSinceMs = null;
}

internal sealed class BoundsStabilityTracker
{
    private readonly ContinuousHoldTracker _holdTracker;
    private Rect? _lastBounds;

    public BoundsStabilityTracker(int holdForMs)
    {
        _holdTracker = new ContinuousHoldTracker(holdForMs);
    }

    public bool Observe(Rect? bounds, long elapsedMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedMs);

        if (bounds is null)
        {
            Reset();
            return false;
        }

        var unchanged = _lastBounds is not null &&
                        _lastBounds.X == bounds.X &&
                        _lastBounds.Y == bounds.Y &&
                        _lastBounds.Width == bounds.Width &&
                        _lastBounds.Height == bounds.Height;
        _lastBounds = bounds;

        if (!unchanged)
        {
            _holdTracker.Reset();
        }

        return _holdTracker.Observe(true, elapsedMs);
    }

    public void Reset()
    {
        _lastBounds = null;
        _holdTracker.Reset();
    }
}

using System.Text.Json.Nodes;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class WaitConditionEvaluatorTests
{
    [Test]
    public void Scalar_validation_accepts_exactly_the_field_for_each_kind()
    {
        var validScalars = new[]
        {
            new WaitScalar(WaitScalarKind.String, StringValue: string.Empty),
            new WaitScalar(WaitScalarKind.Number, NumberValue: -1.25),
            new WaitScalar(WaitScalarKind.Boolean, BooleanValue: false),
            new WaitScalar(WaitScalarKind.Null)
        };

        Assert.Multiple(() =>
        {
            foreach (var scalar in validScalars)
            {
                Assert.That(
                    WaitConditionEvaluator.TryValidateScalar(scalar, out var error),
                    Is.True,
                    error);
                Assert.That(error, Is.Null);
            }
        });
    }

    [Test]
    public void Scalar_validation_rejects_missing_extra_and_nonfinite_fields()
    {
        var invalidScalars = new[]
        {
            new WaitScalar(WaitScalarKind.String),
            new WaitScalar(WaitScalarKind.String, StringValue: "ready", BooleanValue: false),
            new WaitScalar(WaitScalarKind.Number),
            new WaitScalar(WaitScalarKind.Number, StringValue: "1", NumberValue: 1),
            new WaitScalar(WaitScalarKind.Number, NumberValue: double.NaN),
            new WaitScalar(WaitScalarKind.Number, NumberValue: double.PositiveInfinity),
            new WaitScalar(WaitScalarKind.Boolean),
            new WaitScalar(WaitScalarKind.Boolean, NumberValue: 0, BooleanValue: true),
            new WaitScalar(WaitScalarKind.Null, StringValue: string.Empty),
            new WaitScalar((WaitScalarKind)999)
        };

        Assert.Multiple(() =>
        {
            Assert.That(
                WaitConditionEvaluator.TryValidateScalar(null, out var nullError),
                Is.False);
            Assert.That(nullError, Is.EqualTo("Expected value is required."));

            foreach (var scalar in invalidScalars)
            {
                Assert.That(
                    WaitConditionEvaluator.TryValidateScalar(scalar, out var error),
                    Is.False,
                    scalar.ToString());
                Assert.That(error, Is.Not.Null.And.Not.Empty, scalar.ToString());
            }
        });
    }

    [Test]
    public void Comparison_validation_accepts_only_semantically_supported_pairs()
    {
        var text = new WaitScalar(WaitScalarKind.String, StringValue: "ready");
        var number = new WaitScalar(WaitScalarKind.Number, NumberValue: 3);
        var boolean = new WaitScalar(WaitScalarKind.Boolean, BooleanValue: true);
        var @null = new WaitScalar(WaitScalarKind.Null);

        Assert.Multiple(() =>
        {
            foreach (var scalar in new[] { text, number, boolean, @null })
            {
                Assert.That(
                    WaitConditionEvaluator.TryValidateComparison(
                        WaitComparison.Equals,
                        scalar,
                        out var equalsError),
                    Is.True,
                    equalsError);
                Assert.That(
                    WaitConditionEvaluator.TryValidateComparison(
                        WaitComparison.NotEquals,
                        scalar,
                        out var notEqualsError),
                    Is.True,
                    notEqualsError);
            }

            Assert.That(
                WaitConditionEvaluator.TryValidateComparison(
                    WaitComparison.Contains,
                    text,
                    out _),
                Is.True);

            foreach (var comparison in new[]
                     {
                         WaitComparison.GreaterThan,
                         WaitComparison.GreaterThanOrEqual,
                         WaitComparison.LessThan,
                         WaitComparison.LessThanOrEqual
                     })
            {
                Assert.That(
                    WaitConditionEvaluator.TryValidateComparison(comparison, number, out _),
                    Is.True,
                    comparison.ToString());
            }
        });
    }

    [Test]
    public void Comparison_validation_rejects_incompatible_kinds_and_invalid_scalars()
    {
        var text = new WaitScalar(WaitScalarKind.String, StringValue: "3");
        var number = new WaitScalar(WaitScalarKind.Number, NumberValue: 3);
        var boolean = new WaitScalar(WaitScalarKind.Boolean, BooleanValue: true);
        var @null = new WaitScalar(WaitScalarKind.Null);

        Assert.Multiple(() =>
        {
            foreach (var scalar in new[] { number, boolean, @null })
            {
                Assert.That(
                    WaitConditionEvaluator.TryValidateComparison(
                        WaitComparison.Contains,
                        scalar,
                        out var error),
                    Is.False);
                Assert.That(error, Does.Contain("String"));
            }

            foreach (var scalar in new[] { text, boolean, @null })
            {
                Assert.That(
                    WaitConditionEvaluator.TryValidateComparison(
                        WaitComparison.GreaterThan,
                        scalar,
                        out var error),
                    Is.False);
                Assert.That(error, Does.Contain("Number"));
            }

            Assert.That(
                WaitConditionEvaluator.TryValidateComparison(
                    WaitComparison.Equals,
                    new WaitScalar(WaitScalarKind.String),
                    out var scalarError),
                Is.False);
            Assert.That(scalarError, Does.Contain("StringValue"));
            Assert.That(
                WaitConditionEvaluator.TryValidateComparison(
                    (WaitComparison)999,
                    text,
                    out var comparisonError),
                Is.False);
            Assert.That(comparisonError, Does.Contain("Unsupported"));
        });
    }

    [TestCase(WaitObservedValueState.Unset, "value_unset")]
    [TestCase(WaitObservedValueState.Unavailable, "value_unavailable")]
    [TestCase(WaitObservedValueState.Error, "value_error")]
    public void Nonvalue_states_never_satisfy_not_equals(
        WaitObservedValueState state,
        string expectedReason)
    {
        var result = WaitConditionEvaluator.Evaluate(
            new WaitObservedValue(state, Detail: "evidence"),
            WaitComparison.NotEquals,
            String("expected"));

        Assert.That(result, Is.EqualTo(new WaitEvaluationResult(false, expectedReason)));
    }

    [Test]
    public void Truncated_values_never_satisfy_not_equals()
    {
        var result = WaitConditionEvaluator.Evaluate(
            Observed("actual") with { Truncated = true },
            WaitComparison.NotEquals,
            String("expected"));

        Assert.That(result, Is.EqualTo(new WaitEvaluationResult(false, "value_truncated")));
    }

    [Test]
    public void Equality_supports_every_scalar_kind()
    {
        var cases = new[]
        {
            (Observed: Observed("ready"), Expected: String("ready")),
            (Observed: Observed(2.5), Expected: Number(2.5)),
            (Observed: Observed(false), Expected: Boolean(false)),
            (Observed: new WaitObservedValue(WaitObservedValueState.Null), Expected: Null())
        };

        Assert.Multiple(() =>
        {
            foreach (var testCase in cases)
            {
                Assert.That(
                    WaitConditionEvaluator.Evaluate(
                        testCase.Observed,
                        WaitComparison.Equals,
                        testCase.Expected),
                    Is.EqualTo(new WaitEvaluationResult(true, null)));
                Assert.That(
                    WaitConditionEvaluator.Evaluate(
                        testCase.Observed,
                        WaitComparison.NotEquals,
                        testCase.Expected),
                    Is.EqualTo(new WaitEvaluationResult(false, "value_mismatch")));
            }
        });
    }

    [Test]
    public void Evaluation_accepts_scalar_nodes_deserialized_from_json()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                WaitConditionEvaluator.Evaluate(
                    new WaitObservedValue(WaitObservedValueState.Value, JsonNode.Parse("\"ready\"")),
                    WaitComparison.Equals,
                    String("ready")),
                Is.EqualTo(new WaitEvaluationResult(true, null)));
            Assert.That(
                WaitConditionEvaluator.Evaluate(
                    new WaitObservedValue(WaitObservedValueState.Value, JsonNode.Parse("42.5")),
                    WaitComparison.Equals,
                    Number(42.5)),
                Is.EqualTo(new WaitEvaluationResult(true, null)));
            Assert.That(
                WaitConditionEvaluator.Evaluate(
                    new WaitObservedValue(WaitObservedValueState.Value, JsonNode.Parse("false")),
                    WaitComparison.Equals,
                    Boolean(false)),
                Is.EqualTo(new WaitEvaluationResult(true, null)));
        });
    }

    [Test]
    public void Equality_and_contains_use_ordinal_string_semantics()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                WaitConditionEvaluator.Evaluate(
                    Observed("Ready"),
                    WaitComparison.Equals,
                    String("ready")),
                Is.EqualTo(new WaitEvaluationResult(false, "value_mismatch")));
            Assert.That(
                WaitConditionEvaluator.Evaluate(
                    Observed("NotReadyYet"),
                    WaitComparison.Contains,
                    String("Ready")),
                Is.EqualTo(new WaitEvaluationResult(true, null)));
            Assert.That(
                WaitConditionEvaluator.Evaluate(
                    Observed("NotReadyYet"),
                    WaitComparison.Contains,
                    String("ready")),
                Is.EqualTo(new WaitEvaluationResult(false, "value_mismatch")));
        });
    }

    [Test]
    public void Not_equals_compares_scalar_kinds_without_string_coercion()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                WaitConditionEvaluator.Evaluate(
                    Observed(1),
                    WaitComparison.NotEquals,
                    String("1")),
                Is.EqualTo(new WaitEvaluationResult(true, null)));
            Assert.That(
                WaitConditionEvaluator.Evaluate(
                    new WaitObservedValue(WaitObservedValueState.Null),
                    WaitComparison.NotEquals,
                    String(string.Empty)),
                Is.EqualTo(new WaitEvaluationResult(true, null)));
        });
    }

    [TestCase(2.0, WaitComparison.GreaterThan, 1.0, true)]
    [TestCase(2.0, WaitComparison.GreaterThan, 2.0, false)]
    [TestCase(2.0, WaitComparison.GreaterThanOrEqual, 2.0, true)]
    [TestCase(1.0, WaitComparison.GreaterThanOrEqual, 2.0, false)]
    [TestCase(1.0, WaitComparison.LessThan, 2.0, true)]
    [TestCase(2.0, WaitComparison.LessThan, 2.0, false)]
    [TestCase(2.0, WaitComparison.LessThanOrEqual, 2.0, true)]
    [TestCase(3.0, WaitComparison.LessThanOrEqual, 2.0, false)]
    public void Ordered_number_comparisons_are_exact(
        double actual,
        WaitComparison comparison,
        double expected,
        bool satisfied)
    {
        var result = WaitConditionEvaluator.Evaluate(
            Observed(actual),
            comparison,
            Number(expected));

        Assert.That(
            result,
            Is.EqualTo(new WaitEvaluationResult(
                satisfied,
                satisfied ? null : "value_mismatch")));
    }

    [Test]
    public void Comparison_reports_type_mismatch_for_incompatible_or_nonscalar_observations()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                WaitConditionEvaluator.Evaluate(
                    Observed(3),
                    WaitComparison.Contains,
                    String("3")),
                Is.EqualTo(new WaitEvaluationResult(false, "value_type_mismatch")));
            Assert.That(
                WaitConditionEvaluator.Evaluate(
                    Observed("3"),
                    WaitComparison.GreaterThan,
                    Number(2)),
                Is.EqualTo(new WaitEvaluationResult(false, "value_type_mismatch")));
            Assert.That(
                WaitConditionEvaluator.Evaluate(
                    new WaitObservedValue(
                        WaitObservedValueState.Value,
                        JsonNode.Parse("{\"value\":3}")),
                    WaitComparison.Equals,
                    Number(3)),
                Is.EqualTo(new WaitEvaluationResult(false, "value_type_mismatch")));
            Assert.That(
                WaitConditionEvaluator.Evaluate(
                    new WaitObservedValue(WaitObservedValueState.Value),
                    WaitComparison.Equals,
                    Null()),
                Is.EqualTo(new WaitEvaluationResult(false, "value_type_mismatch")));
            Assert.That(
                WaitConditionEvaluator.Evaluate(
                    new WaitObservedValue(
                        WaitObservedValueState.Null,
                        JsonValue.Create("contradiction")),
                    WaitComparison.Equals,
                    Null()),
                Is.EqualTo(new WaitEvaluationResult(false, "value_type_mismatch")));
        });
    }

    [Test]
    public void Evaluation_rejects_an_invalid_expected_scalar_before_comparing()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            WaitConditionEvaluator.Evaluate(
                Observed("ready"),
                WaitComparison.Equals,
                new WaitScalar(WaitScalarKind.String)));

        Assert.That(exception!.Message, Does.Contain("StringValue"));
    }

    [Test]
    public void Observe_state_conversion_preserves_typed_json_and_evidence_without_aliasing()
    {
        var sourceNode = JsonNode.Parse("{\"nested\":[1,true,\"value\"]}")!;
        var source = new ObserveStateValue(
            ObserveStateValueState.Value,
            Value: sourceNode,
            ValueType: "System.Object",
            Truncated: true,
            Error: "detail");

        var converted = WaitConditionEvaluator.FromObserveStateValue(source);

        Assert.Multiple(() =>
        {
            Assert.That(converted.State, Is.EqualTo(WaitObservedValueState.Value));
            Assert.That(converted.Value?.ToJsonString(), Is.EqualTo(sourceNode.ToJsonString()));
            Assert.That(converted.Value, Is.Not.SameAs(sourceNode));
            Assert.That(converted.ValueType, Is.EqualTo("System.Object"));
            Assert.That(converted.Truncated, Is.True);
            Assert.That(converted.Detail, Is.EqualTo("detail"));
        });
    }

    [TestCase(ObserveStateValueState.Value, WaitObservedValueState.Value)]
    [TestCase(ObserveStateValueState.Null, WaitObservedValueState.Null)]
    [TestCase(ObserveStateValueState.Unset, WaitObservedValueState.Unset)]
    [TestCase(ObserveStateValueState.Unavailable, WaitObservedValueState.Unavailable)]
    [TestCase(ObserveStateValueState.Error, WaitObservedValueState.Error)]
    public void Observe_state_conversion_maps_every_state_directly(
        ObserveStateValueState source,
        WaitObservedValueState expected)
    {
        var converted = WaitConditionEvaluator.FromObserveStateValue(
            new ObserveStateValue(source));

        Assert.That(converted.State, Is.EqualTo(expected));
    }

    [Test]
    public void Continuous_hold_requires_an_uninterrupted_interval()
    {
        var tracker = new ContinuousHoldTracker(100);

        Assert.Multiple(() =>
        {
            Assert.That(tracker.Observe(true, 10), Is.False);
            Assert.That(tracker.Observe(true, 109), Is.False);
            Assert.That(tracker.Observe(true, 110), Is.True);
            Assert.That(tracker.Observe(false, 111), Is.False, "mismatch resets the hold");
            Assert.That(tracker.Observe(true, 150), Is.False);
            Assert.That(tracker.Observe(true, 249), Is.False);
            Assert.That(tracker.Observe(true, 250), Is.True);
        });
    }

    [Test]
    public void Continuous_hold_zero_duration_succeeds_on_first_match_only()
    {
        var tracker = new ContinuousHoldTracker(0);

        Assert.Multiple(() =>
        {
            Assert.That(tracker.Observe(false, 0), Is.False);
            Assert.That(tracker.Observe(true, 0), Is.True);
            tracker.Reset();
            Assert.That(tracker.Observe(true, 50), Is.True);
        });
    }

    [Test]
    public void Continuous_hold_restarts_when_elapsed_time_moves_backwards()
    {
        var tracker = new ContinuousHoldTracker(100);

        Assert.Multiple(() =>
        {
            Assert.That(tracker.Observe(true, 200), Is.False);
            Assert.That(tracker.Observe(true, 250), Is.False);
            Assert.That(tracker.Observe(true, 10), Is.False);
            Assert.That(tracker.Observe(true, 109), Is.False);
            Assert.That(tracker.Observe(true, 110), Is.True);
        });
    }

    [Test]
    public void Bounds_stability_compares_all_rect_components_exactly()
    {
        var baseline = new Rect(10, 20, 300, 200);
        var changedBounds = new[]
        {
            baseline with { X = 11 },
            baseline with { Y = 21 },
            baseline with { Width = 301 },
            baseline with { Height = 201 }
        };

        Assert.Multiple(() =>
        {
            foreach (var changed in changedBounds)
            {
                var tracker = new BoundsStabilityTracker(100);
                Assert.That(tracker.Observe(baseline, 0), Is.False);
                Assert.That(tracker.Observe(baseline, 50), Is.False);
                Assert.That(tracker.Observe(changed, 100), Is.False, changed.ToString());
                Assert.That(tracker.Observe(changed, 199), Is.False, changed.ToString());
                Assert.That(tracker.Observe(changed, 200), Is.True, changed.ToString());
            }
        });
    }

    [Test]
    public void Bounds_stability_resets_on_missing_bounds_and_explicit_reset()
    {
        var bounds = new Rect(10, 20, 300, 200);
        var tracker = new BoundsStabilityTracker(100);

        Assert.Multiple(() =>
        {
            Assert.That(tracker.Observe(bounds, 0), Is.False);
            Assert.That(tracker.Observe(bounds, 75), Is.False);
            Assert.That(tracker.Observe(null, 100), Is.False, "missing bounds reset the hold");
            Assert.That(tracker.Observe(bounds, 125), Is.False);
            Assert.That(tracker.Observe(bounds, 225), Is.True);
            tracker.Reset();
            Assert.That(tracker.Observe(bounds, 300), Is.False);
            Assert.That(tracker.Observe(bounds, 400), Is.True);
        });
    }

    [Test]
    public void Bounds_stability_zero_duration_accepts_the_first_present_bounds()
    {
        var tracker = new BoundsStabilityTracker(0);

        Assert.Multiple(() =>
        {
            Assert.That(tracker.Observe(null, 0), Is.False);
            Assert.That(tracker.Observe(new Rect(0, 0, 1, 1), 0), Is.True);
        });
    }

    [Test]
    public void Hold_trackers_reject_negative_duration_and_elapsed_inputs()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ContinuousHoldTracker(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BoundsStabilityTracker(-1));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new ContinuousHoldTracker(0).Observe(true, -1));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new BoundsStabilityTracker(0).Observe(new Rect(0, 0, 1, 1), -1));
        });
    }

    private static WaitScalar String(string value) =>
        new(WaitScalarKind.String, StringValue: value);

    private static WaitScalar Number(double value) =>
        new(WaitScalarKind.Number, NumberValue: value);

    private static WaitScalar Boolean(bool value) =>
        new(WaitScalarKind.Boolean, BooleanValue: value);

    private static WaitScalar Null() => new(WaitScalarKind.Null);

    private static WaitObservedValue Observed(string value) =>
        new(WaitObservedValueState.Value, JsonValue.Create(value));

    private static WaitObservedValue Observed(double value) =>
        new(WaitObservedValueState.Value, JsonValue.Create(value));

    private static WaitObservedValue Observed(bool value) =>
        new(WaitObservedValueState.Value, JsonValue.Create(value));
}

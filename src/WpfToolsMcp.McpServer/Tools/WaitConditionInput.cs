using System.Text.Json.Serialization;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.McpServer.Tools;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AttachedWaitConditionInput), nameof(WaitConditionKind.Attached))]
[JsonDerivedType(typeof(VisibleWaitConditionInput), nameof(WaitConditionKind.Visible))]
[JsonDerivedType(typeof(EnabledWaitConditionInput), nameof(WaitConditionKind.Enabled))]
[JsonDerivedType(typeof(ActionableWaitConditionInput), nameof(WaitConditionKind.Actionable))]
[JsonDerivedType(typeof(BoundsStableWaitConditionInput), nameof(WaitConditionKind.BoundsStable))]
[JsonDerivedType(typeof(NumericValueEqualsWaitConditionInput), nameof(WaitConditionKind.NumericValueEquals))]
[JsonDerivedType(typeof(NameContainsWaitConditionInput), nameof(WaitConditionKind.NameContains))]
[JsonDerivedType(typeof(DependencyPropertyValueWaitConditionInput), nameof(WaitConditionKind.DependencyPropertyValue))]
[JsonDerivedType(typeof(DataContextValueWaitConditionInput), nameof(WaitConditionKind.DataContextValue))]
[JsonDerivedType(typeof(WindowOpenWaitConditionInput), nameof(WaitConditionKind.WindowOpen))]
[JsonDerivedType(typeof(WindowClosedWaitConditionInput), nameof(WaitConditionKind.WindowClosed))]
public abstract record WaitConditionInput
{
    internal abstract WaitCondition ToContract();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AttachedWaitConditionInput : WaitConditionInput
{
    internal override WaitCondition ToContract() => new(WaitConditionKind.Attached);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VisibleWaitConditionInput : WaitConditionInput
{
    internal override WaitCondition ToContract() => new(WaitConditionKind.Visible);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnabledWaitConditionInput : WaitConditionInput
{
    internal override WaitCondition ToContract() => new(WaitConditionKind.Enabled);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ActionableWaitConditionInput : WaitConditionInput
{
    internal override WaitCondition ToContract() => new(WaitConditionKind.Actionable);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BoundsStableWaitConditionInput(
    int? HoldForMs = null) : WaitConditionInput
{
    internal override WaitCondition ToContract() =>
        new(WaitConditionKind.BoundsStable, HoldForMs: HoldForMs);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NumericValueEqualsWaitConditionInput(
    WaitScalar Expected,
    WaitComparison? Comparison = null) : WaitConditionInput
{
    internal override WaitCondition ToContract() =>
        new(WaitConditionKind.NumericValueEquals, Comparison: Comparison, Expected: Expected);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NameContainsWaitConditionInput(
    WaitScalar Expected,
    WaitComparison? Comparison = null) : WaitConditionInput
{
    internal override WaitCondition ToContract() =>
        new(WaitConditionKind.NameContains, Comparison: Comparison, Expected: Expected);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DependencyPropertyValueWaitConditionInput(
    string PropertyName,
    WaitScalar Expected,
    WaitComparison? Comparison = null,
    int? HoldForMs = null) : WaitConditionInput
{
    internal override WaitCondition ToContract() =>
        new(
            WaitConditionKind.DependencyPropertyValue,
            PropertyName: PropertyName,
            Comparison: Comparison,
            Expected: Expected,
            HoldForMs: HoldForMs);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DataContextValueWaitConditionInput(
    string DataContextPath,
    WaitScalar Expected,
    WaitComparison? Comparison = null,
    int? HoldForMs = null) : WaitConditionInput
{
    internal override WaitCondition ToContract() =>
        new(
            WaitConditionKind.DataContextValue,
            DataContextPath: DataContextPath,
            Comparison: Comparison,
            Expected: Expected,
            HoldForMs: HoldForMs);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WindowOpenWaitConditionInput(
    WaitWindowSelector Window) : WaitConditionInput
{
    internal override WaitCondition ToContract() =>
        new(WaitConditionKind.WindowOpen, Window: Window);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WindowClosedWaitConditionInput(
    WaitWindowSelector Window) : WaitConditionInput
{
    internal override WaitCondition ToContract() =>
        new(WaitConditionKind.WindowClosed, Window: Window);
}

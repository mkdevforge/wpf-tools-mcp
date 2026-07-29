using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

internal readonly record struct EffectiveInteractionPolicy(
    bool AllowForegroundActivation,
    bool AllowPhysicalInput)
{
    internal InteractionPolicy ToContract() =>
        new(AllowForegroundActivation, AllowPhysicalInput);
}

internal static class InteractionPolicyResolver
{
    internal static EffectiveInteractionPolicy Resolve(
        InteractionPolicy? policy,
        EffectiveInteractionPolicy? fallback = null)
    {
        var baseline = fallback ?? new EffectiveInteractionPolicy(
            AllowForegroundActivation: true,
            AllowPhysicalInput: true);

        return new EffectiveInteractionPolicy(
            AllowForegroundActivation: policy?.AllowForegroundActivation ?? baseline.AllowForegroundActivation,
            AllowPhysicalInput: policy?.AllowPhysicalInput ?? baseline.AllowPhysicalInput);
    }

    internal static InvalidOperationException Blocked(
        string operation,
        string requiredEffect,
        string policySetting,
        string alternative) =>
        new(
            $"interaction_policy_blocked: operation={operation} requires {requiredEffect}, " +
            $"but {policySetting}=false. {alternative}");
}

internal sealed class InteractionEffectTracker
{
    internal bool Semantic { get; private set; }
    internal bool ForegroundActivated { get; private set; }
    internal bool WindowRestored { get; private set; }
    internal bool MouseInput { get; private set; }
    internal bool KeyboardInput { get; private set; }
    internal bool CursorMoved { get; private set; }
    internal bool KeyboardFocusChanged { get; private set; }

    internal void MarkSemantic() => Semantic = true;

    internal void MarkForegroundActivated() => ForegroundActivated = true;

    internal void MarkWindowRestored() => WindowRestored = true;

    internal void MarkMouseInput(bool cursorMoved = true)
    {
        MouseInput = true;
        CursorMoved |= cursorMoved;
    }

    internal void MarkKeyboardInput() => KeyboardInput = true;

    internal void MarkKeyboardFocusChanged() => KeyboardFocusChanged = true;

    internal InteractionEffects ToContract() =>
        new(
            Semantic,
            ForegroundActivated,
            WindowRestored,
            MouseInput,
            KeyboardInput,
            CursorMoved,
            KeyboardFocusChanged);
}

using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class KeyboardNavigationTraceContractTests
{
    [Test]
    public void Current_agent_advertises_keyboard_navigation_steps()
    {
        Assert.That(
            AgentProtocolCapabilities.Current,
            Does.Contain(AgentProtocolCapabilities.KeyboardNavigationStep));
    }

    [Test]
    public void Stop_reason_names_are_stable_and_cover_each_terminal_observation()
    {
        Assert.That(
            Enum.GetNames<KeyboardNavigationStopReason>(),
            Is.EqualTo(new[]
            {
                "MaximumSteps",
                "NoFocusChange",
                "CycleDetected",
                "FocusLeftWindow",
                "WindowClosed",
                "FocusUnavailable",
                "SemanticInteropBoundary"
            }));
    }

    [Test]
    public void Either_comparable_identity_layer_can_reveal_a_focus_change()
    {
        var stableUiaBefore = new KeyboardNavigationFocusIdentity(Uia: "uia-parent", Wpf: "wpf-child-a");
        var changedWpf = new KeyboardNavigationFocusIdentity(Uia: "uia-parent", Wpf: "wpf-child-b");
        var stableWpfBefore = new KeyboardNavigationFocusIdentity(Uia: "uia-child-a", Wpf: "wpf-host");
        var changedUia = new KeyboardNavigationFocusIdentity(Uia: "uia-child-b", Wpf: "wpf-host");

        Assert.Multiple(() =>
        {
            Assert.That(stableUiaBefore.Matches(changedWpf), Is.False);
            Assert.That(stableWpfBefore.Matches(changedUia), Is.False);
        });
    }

    [Test]
    public void Uia_identity_is_used_when_wpf_identity_is_not_comparable()
    {
        var before = new KeyboardNavigationFocusIdentity(Uia: "uia-button", Wpf: "wpf-button");
        var after = new KeyboardNavigationFocusIdentity(Uia: "uia-button", Wpf: null);

        Assert.That(before.Matches(after), Is.True);
    }

    [Test]
    public void Immediate_repeat_is_no_change_but_a_return_to_an_earlier_focus_is_a_cycle()
    {
        var start = new KeyboardNavigationFocusIdentity("uia-a", "wpf-a");
        var state = new KeyboardNavigationTraceState(start);

        var firstMove = state.Add(new KeyboardNavigationFocusIdentity("uia-b", "wpf-b"));
        var noChange = state.Add(new KeyboardNavigationFocusIdentity("uia-b", "wpf-b"));

        var cycleState = new KeyboardNavigationTraceState(start);
        _ = cycleState.Add(new KeyboardNavigationFocusIdentity("uia-b", "wpf-b"));
        var cycle = cycleState.Add(new KeyboardNavigationFocusIdentity("uia-a", "wpf-a"));

        Assert.Multiple(() =>
        {
            Assert.That(firstMove.StopReason, Is.Null);
            Assert.That(firstMove.FocusChanged, Is.True);
            Assert.That(noChange.StopReason, Is.EqualTo(KeyboardNavigationStopReason.NoFocusChange));
            Assert.That(noChange.FocusChanged, Is.False);
            Assert.That(cycle.StopReason, Is.EqualTo(KeyboardNavigationStopReason.CycleDetected));
            Assert.That(cycle.FocusChanged, Is.True);
        });
    }

    [Test]
    public async Task Physical_mode_uses_only_a_passive_agent_connection_for_optional_wpf_evidence()
    {
        var passiveCalls = 0;
        var injectionCalls = 0;
        var expected = new object();

        var result = await AutomationController.SelectKeyboardNavigationAgentAsync(
            KeyboardNavigationTraceMode.Physical,
            () =>
            {
                passiveCalls++;
                return Task.FromResult<object?>(expected);
            },
            () =>
            {
                injectionCalls++;
                return Task.FromResult(new object());
            });

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.SameAs(expected));
            Assert.That(passiveCalls, Is.EqualTo(1));
            Assert.That(injectionCalls, Is.Zero);
        });
    }

    [Test]
    public async Task Wpf_semantic_mode_uses_the_required_agent_path_without_fallback()
    {
        var passiveCalls = 0;
        var injectionCalls = 0;
        var expected = new object();

        var result = await AutomationController.SelectKeyboardNavigationAgentAsync(
            KeyboardNavigationTraceMode.WpfSemantic,
            () =>
            {
                passiveCalls++;
                return Task.FromResult<object?>(new object());
            },
            () =>
            {
                injectionCalls++;
                return Task.FromResult(expected);
            });

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.SameAs(expected));
            Assert.That(passiveCalls, Is.Zero);
            Assert.That(injectionCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public void Physical_mode_honors_interaction_policy_before_requiring_an_attachment()
    {
        using var controller = new AutomationController();
        var request = new TraceKeyboardNavigationRequest(
            Mode: KeyboardNavigationTraceMode.Physical,
            InteractionPolicy: new InteractionPolicy(AllowPhysicalInput: false));

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.TraceKeyboardNavigationAsync(request));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("interaction_policy_blocked"));
            Assert.That(exception.Message, Does.Contain("allowPhysicalInput=false"));
            Assert.That(exception.Message, Does.Not.Contain("not_attached").IgnoreCase);
        });
    }

    [Test]
    public void Trace_defaults_to_twenty_steps_and_clamps_the_execution_bound_to_one_hundred()
    {
        var request = new TraceKeyboardNavigationRequest();

        Assert.Multiple(() =>
        {
            Assert.That(request.MaxSteps, Is.EqualTo(20));
            Assert.That(AutomationController.NormalizeKeyboardNavigationMaxSteps(0), Is.EqualTo(1));
            Assert.That(AutomationController.NormalizeKeyboardNavigationMaxSteps(20), Is.EqualTo(20));
            Assert.That(AutomationController.NormalizeKeyboardNavigationMaxSteps(100), Is.EqualTo(100));
            Assert.That(AutomationController.NormalizeKeyboardNavigationMaxSteps(101), Is.EqualTo(100));
        });
    }

    [Test]
    public void Restoration_reports_success_failure_and_an_unavailable_original_focus()
    {
        var success = AutomationController.BuildKeyboardNavigationRestoration(
            requested: true,
            attempted: true,
            restored: true,
            methodUsed: "uia_focus",
            failures: ["ignored after success"]);
        var failure = AutomationController.BuildKeyboardNavigationRestoration(
            requested: true,
            attempted: true,
            restored: false,
            methodUsed: null,
            failures: ["wpf failed", "wpf failed", "uia failed"]);
        var unavailable = AutomationController.BuildKeyboardNavigationRestoration(
            requested: true,
            attempted: false,
            restored: false,
            methodUsed: null,
            failures: null);

        Assert.Multiple(() =>
        {
            Assert.That(success.Restored, Is.True);
            Assert.That(success.MethodUsed, Is.EqualTo("uia_focus"));
            Assert.That(success.Failure, Is.Null);
            Assert.That(failure.Attempted, Is.True);
            Assert.That(failure.Restored, Is.False);
            Assert.That(failure.Failure, Is.EqualTo("wpf failed uia failed"));
            Assert.That(unavailable.Requested, Is.True);
            Assert.That(unavailable.Attempted, Is.False);
            Assert.That(unavailable.Failure, Is.EqualTo("original_focus_unavailable"));
        });
    }

    [Test]
    public async Task Navigation_trace_participates_in_existing_action_tracing_on_failure()
    {
        using var controller = new AutomationController();
        var trace = await controller.TraceStartAsync("keyboard-navigation-test", resetIfRunning: false);
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"wpf-tools-keyboard-navigation-{Guid.NewGuid():N}.json");

        try
        {
            _ = Assert.ThrowsAsync<InvalidOperationException>(() =>
                controller.TraceKeyboardNavigationAsync(
                    new TraceKeyboardNavigationRequest(
                        Mode: KeyboardNavigationTraceMode.Physical,
                        InteractionPolicy: new InteractionPolicy(AllowPhysicalInput: false))));

            var stopped = await controller.TraceStopAsync(
                trace.TraceId,
                outputPath,
                includeEvents: true,
                maxEvents: 10);
            var action = stopped.Events!.Single(item => item.Tool == "trace_keyboard_navigation");

            Assert.Multiple(() =>
            {
                Assert.That(action.Error, Does.Contain("interaction_policy_blocked"));
                Assert.That(action.Summary, Is.Null);
            });
        }
        finally
        {
            File.Delete(outputPath);
        }
    }
}

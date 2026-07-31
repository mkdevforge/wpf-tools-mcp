using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[Apartment(ApartmentState.STA)]
[NonParallelizable]
public sealed class KeyboardNavigationTraceIntegrationTests
{
    [Test]
    public async Task Physical_trace_observes_skip_redirect_cycle_and_restores_the_pre_target_focus_without_an_agent()
    {
        using var controller = new AutomationController();
        var executable = TestAppPaths.FindFocusProbeTestAppExecutable();
        _ = await controller.LaunchAsync(new LaunchAppRequest(
            ExePath: executable,
            WorkingDirectory: Path.GetDirectoryName(executable)!));

        try
        {
            var original = await controller.ResolveElementAsync(
                InspectionBackend.Uia,
                new ElementLocator(AutomationId: "FocusProbe_TextBox"),
                timeoutMs: 5_000);
            _ = await controller.SendKeysAsync(new SendKeysRequest(
                Sequence: [new KeyStroke(KeyboardKey.Escape)],
                ElementId: original.Element.ElementId,
                InteractionPolicy: new InteractionPolicy(
                    AllowForegroundActivation: true,
                    AllowPhysicalInput: true)));

            var response = await controller.TraceKeyboardNavigationAsync(
                new TraceKeyboardNavigationRequest(
                    Locator: new ElementLocator(AutomationId: "FocusProbe_NavigationStart"),
                    Direction: KeyboardNavigationDirection.Next,
                    Mode: KeyboardNavigationTraceMode.Physical,
                    MaxSteps: 4,
                    RestoreFocus: true,
                    InteractionPolicy: new InteractionPolicy(
                        AllowForegroundActivation: true,
                        AllowPhysicalInput: true)));

            Assert.Multiple(() =>
            {
                Assert.That(response.Start?.Uia?.AutomationId, Is.EqualTo("FocusProbe_NavigationStart"));
                Assert.That(response.Start?.Wpf, Is.Null);
                Assert.That(response.Steps, Has.Count.EqualTo(2));
                Assert.That(response.Steps[0].Focus?.Uia?.AutomationId, Is.EqualTo("FocusProbe_NavigationDestination"));
                Assert.That(response.Steps[0].MethodUsed, Is.EqualTo("physical_tab"));
                Assert.That(response.Steps[1].Focus?.Uia?.AutomationId, Is.EqualTo("FocusProbe_NavigationStart"));
                Assert.That(response.StopReason, Is.EqualTo(KeyboardNavigationStopReason.CycleDetected));
                Assert.That(response.Restoration.Requested, Is.True);
                Assert.That(response.Restoration.Attempted, Is.True);
                Assert.That(response.Restoration.Restored, Is.True);
                Assert.That(response.Restoration.MethodUsed, Is.EqualTo("uia_focus"));
                Assert.That(response.Effects?.KeyboardInput, Is.True);
                Assert.That(response.Effects?.KeyboardFocusChanged, Is.True);
                Assert.That(controller.IsAgentConnected, Is.False);
            });
        }
        finally
        {
            try
            {
                _ = await controller.CloseAsync(new CloseAppRequest(Force: true, TimeoutMs: 2_000));
            }
            catch
            {
            }
        }
    }
}

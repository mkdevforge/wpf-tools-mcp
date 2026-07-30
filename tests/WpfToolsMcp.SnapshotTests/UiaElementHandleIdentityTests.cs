using System.Threading;
using NUnit.Framework;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class UiaElementHandleIdentityTests
{
    private static readonly InteractionPolicy SemanticOnlyPolicy = new(
        AllowForegroundActivation: false,
        AllowPhysicalInput: false);

    [Test]
    public async Task Read_only_resolution_does_not_authorize_replacement_for_interaction()
    {
        using var controller = new AutomationController();
        var executable = TestAppPaths.FindDynamicContentTestAppExecutable();
        var screenshotPath = Path.Combine(
            Path.GetTempPath(),
            $"wpf-tools-mcp-stale-{Guid.NewGuid():N}.png");
        _ = await controller.LaunchAsync(new LaunchAppRequest(
            ExePath: executable,
            WorkingDirectory: Path.GetDirectoryName(executable)!));

        try
        {
            var add = await ResolveUiaElementAsync(controller, "Dynamic_AddButton");
            var remove = await ResolveUiaElementAsync(controller, "Dynamic_RemoveButton");

            _ = await InvokeUiaElementAsync(controller, add.Element.ElementId!);
            var original = await ResolveUiaElementAsync(controller, "Dynamic_NewButton");

            _ = await InvokeUiaElementAsync(controller, remove.Element.ElementId!);
            await WaitForUiaElementGoneAsync(controller, "Dynamic_NewButton");

            _ = await InvokeUiaElementAsync(controller, add.Element.ElementId!);
            var replacement = await ResolveUiaElementAsync(controller, "Dynamic_NewButton");
            Assert.That(replacement.Element.XPath, Is.EqualTo(original.Element.XPath));

            var observedReplacement = await controller.GetElementPropertiesAsync(
                elementId: original.Element.ElementId);
            Assert.That(observedReplacement.Element.AutomationId, Is.EqualTo("Dynamic_NewButton"));

            var staleElementId = original.Element.ElementId!;
            await AssertRejectsStaleIdentityAsync(
                "click",
                async () => _ = await controller.ClickElementAsync(new ClickElementRequest(
                    ElementId: staleElementId,
                    ClickMode: ClickMode.InvokePreferred,
                    InteractionPolicy: SemanticOnlyPolicy)));
            await AssertRejectsStaleIdentityAsync(
                "invoke",
                async () => _ = await controller.InvokeAsync(new InvokeRequest(
                    ElementId: staleElementId,
                    InteractionPolicy: SemanticOnlyPolicy)));
            await AssertRejectsStaleIdentityAsync(
                "type_text",
                async () => _ = await controller.TypeTextAsync(new TypeTextRequest(
                    Text: "blocked",
                    ElementId: staleElementId,
                    InteractionPolicy: SemanticOnlyPolicy)));
            await AssertRejectsStaleIdentityAsync(
                "send_keys",
                async () => _ = await controller.SendKeysAsync(new SendKeysRequest(
                    Sequence: [new KeyStroke(KeyboardKey.Space)],
                    ElementId: staleElementId,
                    InteractionPolicy: SemanticOnlyPolicy)));
            await AssertRejectsStaleIdentityAsync(
                "set_value",
                async () => _ = await controller.SetValueAsync(new SetValueRequest(
                    Text: "blocked",
                    ElementId: staleElementId,
                    InteractionPolicy: SemanticOnlyPolicy)));
            await AssertRejectsStaleIdentityAsync(
                "select_item",
                async () => _ = await controller.SelectItemAsync(new SelectItemRequest(
                    ElementId: add.Element.ElementId,
                    ItemElementId: staleElementId,
                    InteractionPolicy: SemanticOnlyPolicy)));
            await AssertRejectsStaleIdentityAsync(
                "scroll_to_element",
                async () => _ = await controller.ScrollToElementAsync(new ScrollToElementRequest(
                    ElementId: staleElementId,
                    InteractionPolicy: SemanticOnlyPolicy)));
            await AssertRejectsStaleIdentityAsync(
                "drag",
                async () => _ = await controller.DragAsync(new DragRequest(
                    ElementId: staleElementId,
                    ToX: 1,
                    ToY: 1,
                    InteractionPolicy: SemanticOnlyPolicy)));
            await AssertRejectsStaleIdentityAsync(
                "highlight_element",
                async () => _ = await controller.HighlightElementAsync(new HighlightElementRequest(
                    ElementId: staleElementId,
                    PreferInProcHighlight: false,
                    DurationMs: 1)));
            await AssertRejectsStaleIdentityAsync(
                "take_screenshot autoScroll",
                async () => _ = await controller.TakeScreenshotAsync(new TakeScreenshotRequest(
                    ElementId: staleElementId,
                    OutputPath: screenshotPath,
                    AutoScroll: true)));

            var status = await controller.GetElementPropertiesAsync(
                locator: new ElementLocator(AutomationId: "Dynamic_Status"));
            Assert.That(status.Element.Name, Is.EqualTo("Clicks: 0"));

            _ = await InvokeUiaElementAsync(controller, replacement.Element.ElementId!);
            status = await controller.GetElementPropertiesAsync(
                locator: new ElementLocator(AutomationId: "Dynamic_Status"));
            Assert.That(status.Element.Name, Is.EqualTo("Clicks: 1"));
        }
        finally
        {
            try
            {
                _ = await controller.CloseAsync(new CloseAppRequest(Force: true, TimeoutMs: 2000));
            }
            catch
            {
            }

            if (File.Exists(screenshotPath))
            {
                File.Delete(screenshotPath);
            }
        }
    }

    private static Task AssertRejectsStaleIdentityAsync(string operation, Func<Task> action)
    {
        var error = Assert.ThrowsAsync<InvalidOperationException>(async () => await action());
        Assert.That(
            error!.Message,
            Does.Contain("stale_element: identity_changed"),
            $"{operation} must reject the replacement before acting on it.");
        return Task.CompletedTask;
    }

    private static async Task<ResolveElementResponse> ResolveUiaElementAsync(
        AutomationController controller,
        string automationId,
        int attempts = 25,
        int delayMs = 75)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                return await controller.ResolveElementAsync(
                    backend: InspectionBackend.Uia,
                    locator: new ElementLocator(AutomationId: automationId),
                    timeoutMs: 0,
                    cancellationToken: default,
                    autoInject: false);
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("element_not_found", StringComparison.Ordinal))
            {
                await Task.Delay(delayMs);
            }
        }

        Assert.Fail($"UIA element '{automationId}' did not appear within timeout.");
        throw new AssertionException("Unreachable.");
    }

    private static async Task WaitForUiaElementGoneAsync(
        AutomationController controller,
        string automationId,
        int attempts = 25,
        int delayMs = 75)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                _ = await controller.ResolveElementAsync(
                    backend: InspectionBackend.Uia,
                    locator: new ElementLocator(AutomationId: automationId),
                    timeoutMs: 0,
                    cancellationToken: default,
                    autoInject: false);
                await Task.Delay(delayMs);
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("element_not_found", StringComparison.Ordinal))
            {
                return;
            }
        }

        Assert.Fail($"UIA element '{automationId}' did not disappear within timeout.");
    }

    private static async Task<ClickElementResponse> InvokeUiaElementAsync(
        AutomationController controller,
        string elementId)
    {
        var response = await controller.ClickElementAsync(new ClickElementRequest(
            ElementId: elementId,
            ClickMode: ClickMode.InvokePreferred,
            InteractionPolicy: SemanticOnlyPolicy));

        Assert.Multiple(() =>
        {
            Assert.That(response.MethodUsed, Is.EqualTo("invoke"));
            Assert.That(response.Effects?.Semantic, Is.True);
            Assert.That(response.Effects?.ForegroundActivated, Is.False);
            Assert.That(response.Effects?.MouseInput, Is.False);
        });
        return response;
    }
}

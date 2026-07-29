using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class NonIntrusiveInteractionTests
{
    private const int ShowWindowRestore = 9;
    private const int UserObjectName = 2;

    private static readonly Regex ProbeActivityPattern = new(
        @"\[A:(?<activation>\d+) D:(?<deactivation>\d+)\]$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private McpTestContext _mcp = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _mcp = await McpTestContext.StartAsync(McpServerPaths.FindMcpServerExecutable());
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_mcp is not null)
        {
            await _mcp.DisposeAsync();
        }
    }

    [Test]
    public async Task Attach_preserves_foreground_cursor_and_target_activation_state()
    {
        var probes = await StartProbePairAsync(attachTarget: false);
        try
        {
            await FocusSentinelAsync(probes);
            var before = CaptureDesktopState(probes.Target.WindowHandle);
            AssertSentinelIsForeground(before, probes);

            var attached = await AttachAsync(probes.Target.Process);
            probes.TargetSessionId = attached.SessionId;

            var after = CaptureDesktopState(probes.Target.WindowHandle);

            Assert.Multiple(() =>
            {
                Assert.That(attached.Pid, Is.EqualTo(probes.Target.Process.Id));
                AssertDesktopStatePreserved(before, after);
            });
        }
        finally
        {
            await CloseProbePairAsync(probes);
        }
    }

    [Test]
    public async Task Read_only_inspection_preserves_desktop_state_and_exposes_probe_surface()
    {
        var probes = await StartProbePairAsync(attachTarget: true);
        try
        {
            await FocusSentinelAsync(probes);
            var before = CaptureDesktopState(probes.Target.WindowHandle);
            AssertSentinelIsForeground(before, probes);

            var tree = await _mcp.CallToolAsync<GetVisualTreeResponse>("get_visual_tree", new Dictionary<string, object?>
            {
                ["sessionId"] = probes.TargetSessionId,
                ["backend"] = "uia",
                ["depth"] = 6,
                ["maxNodes"] = 200,
                ["visibleOnly"] = true
            });
            var activationStatus = await ReadElementNameAsync(probes.TargetSessionId!, "FocusProbe_ActivationCount");
            var deactivationStatus = await ReadElementNameAsync(probes.TargetSessionId!, "FocusProbe_DeactivationCount");

            var after = CaptureDesktopState(probes.Target.WindowHandle);
            var automationIds = CollectAutomationIds(tree.Root);

            Assert.Multiple(() =>
            {
                Assert.That(tree.ReturnedNodes, Is.GreaterThan(0));
                Assert.That(automationIds, Does.Contain("FocusProbe_Button"));
                Assert.That(automationIds, Does.Contain("FocusProbe_TextBox"));
                Assert.That(automationIds, Does.Contain("FocusProbe_ListBox"));
                Assert.That(automationIds, Does.Contain("FocusProbe_ButtonStatus"));
                Assert.That(automationIds, Does.Contain("FocusProbe_KeyboardFallbackStatus"));
                Assert.That(automationIds, Does.Contain("FocusProbe_PhysicalFallbackStatus"));
                Assert.That(automationIds, Does.Contain("FocusProbe_SelectionStatus"));
                Assert.That(activationStatus, Is.EqualTo($"Activated: {after.TargetActivity.Activations}"));
                Assert.That(deactivationStatus, Is.EqualTo($"Deactivated: {after.TargetActivity.Deactivations}"));
                AssertDesktopStatePreserved(before, after);
            });
        }
        finally
        {
            await CloseProbePairAsync(probes);
        }
    }

    [Test]
    public async Task Auto_screenshot_preserves_foreground_cursor_and_target_activation_state()
    {
        var probes = await StartProbePairAsync(attachTarget: true);
        string? screenshotPath = null;
        try
        {
            await FocusSentinelAsync(probes);
            var before = CaptureDesktopState(probes.Target.WindowHandle);
            AssertSentinelIsForeground(before, probes);

            var screenshot = await _mcp.CallToolAsync<TakeScreenshotResponse>("take_screenshot", new Dictionary<string, object?>
            {
                ["sessionId"] = probes.TargetSessionId,
                ["captureMode"] = "auto"
            });
            screenshotPath = screenshot.Path;

            var after = CaptureDesktopState(probes.Target.WindowHandle);

            Assert.Multiple(() =>
            {
                Assert.That(screenshot.Width, Is.GreaterThan(0));
                Assert.That(screenshot.Height, Is.GreaterThan(0));
                Assert.That(File.Exists(screenshot.Path), Is.True, $"Screenshot file was not created: {screenshot.Path}");
                AssertDesktopStatePreserved(before, after);
            });
        }
        finally
        {
            TryDeleteFile(screenshotPath);
            await CloseProbePairAsync(probes);
        }
    }

    [Test]
    public async Task Semantic_invoke_under_strict_session_policy_preserves_desktop_state()
    {
        var probes = await StartProbePairAsync(attachTarget: true, strictTargetPolicy: true);
        try
        {
            AssertStrictPolicy(probes.TargetInteractionPolicy);
            await FocusSentinelAsync(probes);
            var before = CaptureDesktopState(probes.Target.WindowHandle);
            AssertSentinelIsForeground(before, probes);

            var invoked = await _mcp.CallToolAsync<InvokeResponse>("invoke", new Dictionary<string, object?>
            {
                ["sessionId"] = probes.TargetSessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "FocusProbe_Button"
                }
            });

            await WaitForProbeActivityToStabilizeAsync(probes.Target.WindowHandle);
            var afterInvoke = CaptureDesktopState(probes.Target.WindowHandle);
            var status = await WaitForElementNameAsync(
                probes.TargetSessionId!,
                "FocusProbe_ButtonStatus",
                "Semantic invokes: 1");
            var afterInspection = CaptureDesktopState(probes.Target.WindowHandle);

            Assert.Multiple(() =>
            {
                Assert.That(invoked.Invoked, Is.True);
                Assert.That(invoked.MethodUsed, Is.Not.Null.And.Not.Empty);
                AssertOnlySemanticEffects(invoked.Effects);
                Assert.That(status, Is.EqualTo("Semantic invokes: 1"));
                AssertDesktopStatePreserved(before, afterInvoke);
                AssertDesktopStatePreserved(before, afterInspection);
            });
        }
        finally
        {
            await CloseProbePairAsync(probes);
        }
    }

    [Test]
    public async Task Fallback_click_forbidden_by_session_policy_fails_before_desktop_state_changes()
    {
        var probes = await StartProbePairAsync(attachTarget: true, strictTargetPolicy: true);
        try
        {
            AssertStrictPolicy(probes.TargetInteractionPolicy);
            await FocusSentinelAsync(probes);
            PlaceCursorAtKnownPoint();
            var before = CaptureDesktopState(probes.Target.WindowHandle);
            AssertSentinelIsForeground(before, probes);

            InvalidOperationException? exception = null;
            try
            {
                _ = await ClickPhysicalFallbackTargetAsync(probes.TargetSessionId!);
                Assert.Fail("Expected the strict session policy to block the physical click fallback.");
            }
            catch (InvalidOperationException caught)
            {
                exception = caught;
            }

            var after = CaptureDesktopState(probes.Target.WindowHandle);
            var status = await ReadElementNameAsync(probes.TargetSessionId!, "FocusProbe_PhysicalFallbackStatus");

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(exception!.Message, Does.Contain("interaction_policy_blocked"));
                Assert.That(exception.Message, Does.Contain("allowPhysicalInput").IgnoreCase);
                Assert.That(exception.Message, Does.Contain("interactionPolicy.allowPhysicalInput=true"));
                Assert.That(status, Is.EqualTo("Physical clicks: 0"));
                AssertDesktopStatePreserved(before, after);
            });
        }
        finally
        {
            await CloseProbePairAsync(probes);
        }
    }

    [Test]
    public async Task Operation_policy_can_forbid_physical_fallback_on_a_permissive_session()
    {
        var probes = await StartProbePairAsync(attachTarget: true);
        try
        {
            AssertPermissivePolicy(probes.TargetInteractionPolicy);
            await FocusSentinelAsync(probes);
            PlaceCursorAtKnownPoint();
            var before = CaptureDesktopState(probes.Target.WindowHandle);
            AssertSentinelIsForeground(before, probes);

            InvalidOperationException? exception = null;
            try
            {
                _ = await ClickPhysicalFallbackTargetAsync(
                    probes.TargetSessionId!,
                    allowPhysicalInput: false);
                Assert.Fail("Expected the operation policy to block the physical click fallback.");
            }
            catch (InvalidOperationException caught)
            {
                exception = caught;
            }

            var after = CaptureDesktopState(probes.Target.WindowHandle);
            var status = await ReadElementNameAsync(probes.TargetSessionId!, "FocusProbe_PhysicalFallbackStatus");

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(exception!.Message, Does.Contain("interaction_policy_blocked"));
                Assert.That(exception.Message, Does.Contain("allowPhysicalInput").IgnoreCase);
                Assert.That(exception.Message, Does.Contain("interactionPolicy.allowPhysicalInput=true"));
                Assert.That(status, Is.EqualTo("Physical clicks: 0"));
                AssertDesktopStatePreserved(before, after);
            });
        }
        finally
        {
            await CloseProbePairAsync(probes);
        }
    }

    [Test]
    public async Task Operation_policy_inherits_physical_allowance_but_forbids_foreground_activation()
    {
        var probes = await StartProbePairAsync(attachTarget: true);
        try
        {
            AssertPermissivePolicy(probes.TargetInteractionPolicy);
            await FocusSentinelAsync(probes);
            PlaceCursorAtKnownPoint();
            var before = CaptureDesktopState(probes.Target.WindowHandle);
            AssertSentinelIsForeground(before, probes);

            InvalidOperationException? exception = null;
            try
            {
                _ = await ClickPhysicalFallbackTargetAsync(
                    probes.TargetSessionId!,
                    allowForegroundActivation: false);
                Assert.Fail("Expected the operation policy to block foreground activation.");
            }
            catch (InvalidOperationException caught)
            {
                exception = caught;
            }

            var after = CaptureDesktopState(probes.Target.WindowHandle);
            var status = await ReadElementNameAsync(probes.TargetSessionId!, "FocusProbe_PhysicalFallbackStatus");

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(exception!.Message, Does.Contain("interaction_policy_blocked"));
                Assert.That(exception.Message, Does.Contain("allowForegroundActivation").IgnoreCase);
                Assert.That(exception.Message, Does.Contain("interactionPolicy.allowForegroundActivation=true"));
                Assert.That(status, Is.EqualTo("Physical clicks: 0"));
                AssertDesktopStatePreserved(before, after);
            });
        }
        finally
        {
            await CloseProbePairAsync(probes);
        }
    }

    [Test]
    public async Task Raw_mouse_click_cannot_bypass_foreground_policy_for_a_background_target()
    {
        var probes = await StartProbePairAsync(attachTarget: true);
        try
        {
            AssertPermissivePolicy(probes.TargetInteractionPolicy);
            var targetPoint = await GetClientPointForElementAsync(
                probes.TargetSessionId!,
                probes.Target.WindowHandle,
                "FocusProbe_PhysicalFallbackTarget");

            await FocusSentinelAsync(probes);
            PlaceCursorAtKnownPoint();
            var before = CaptureDesktopState(probes.Target.WindowHandle);
            AssertSentinelIsForeground(before, probes);

            InvalidOperationException? exception = null;
            try
            {
                _ = await RawMouseClickAsync(
                    probes.TargetSessionId!,
                    probes.Target.WindowHandle,
                    targetPoint,
                    allowForegroundActivation: false,
                    allowPhysicalInput: true);
                Assert.Fail("Expected raw mouse input to be blocked before foreground activation.");
            }
            catch (InvalidOperationException caught)
            {
                exception = caught;
            }

            var after = CaptureDesktopState(probes.Target.WindowHandle);
            var status = await ReadElementNameAsync(probes.TargetSessionId!, "FocusProbe_PhysicalFallbackStatus");

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(exception!.Message, Does.Contain("interaction_policy_blocked"));
                Assert.That(exception.Message, Does.Not.Contain("mouse_target_occluded"));
                Assert.That(exception.Message, Does.Contain("allowForegroundActivation").IgnoreCase);
                Assert.That(exception.Message, Does.Contain("interactionPolicy.allowForegroundActivation=true"));
                Assert.That(status, Is.EqualTo("Physical clicks: 0"));
                AssertDesktopStatePreserved(before, after);
            });
        }
        finally
        {
            await CloseProbePairAsync(probes);
        }
    }

    [Test]
    public async Task Raw_mouse_click_rejects_an_occluded_background_target_before_input()
    {
        var probes = await StartProbePairAsync(attachTarget: true);
        try
        {
            AssertPermissivePolicy(probes.TargetInteractionPolicy);
            var targetPoint = await GetClientPointForElementAsync(
                probes.TargetSessionId!,
                probes.Target.WindowHandle,
                "FocusProbe_PhysicalFallbackTarget");

            await FocusSentinelAsync(probes);
            AssertSentinelOccludesClientPoint(probes, targetPoint);
            PlaceCursorAtKnownPoint();
            var before = CaptureDesktopState(probes.Target.WindowHandle);
            AssertSentinelIsForeground(before, probes);

            InvalidOperationException? exception = null;
            try
            {
                _ = await RawMouseClickAsync(
                    probes.TargetSessionId!,
                    probes.Target.WindowHandle,
                    targetPoint,
                    allowForegroundActivation: true,
                    allowPhysicalInput: true);
                Assert.Fail("Expected raw mouse input to reject the occluded target before sending input.");
            }
            catch (InvalidOperationException caught)
            {
                exception = caught;
            }

            var after = CaptureDesktopState(probes.Target.WindowHandle);
            var status = await ReadElementNameAsync(probes.TargetSessionId!, "FocusProbe_PhysicalFallbackStatus");

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(exception!.Message, Does.Contain("mouse_target_occluded"));
                Assert.That(exception.Message, Does.Not.Contain("interaction_policy_blocked"));
                Assert.That(exception.Message, Does.Contain("Use click_element"));
                Assert.That(exception.Message, Does.Contain("uncover the target").IgnoreCase);
                Assert.That(status, Is.EqualTo("Physical clicks: 0"));
                AssertDesktopStatePreserved(before, after);
            });
        }
        finally
        {
            await CloseProbePairAsync(probes);
        }
    }

    [Test]
    public async Task Strict_background_policy_blocks_maximizing_a_normal_wpf_window()
    {
        var probes = await StartProbePairAsync(attachTarget: true, strictTargetPolicy: true);
        try
        {
            AssertStrictPolicy(probes.TargetInteractionPolicy);
            Assert.That(IsIconic(probes.Target.WindowHandle), Is.False, "The target must start restored.");
            Assert.That(IsZoomed(probes.Target.WindowHandle), Is.False, "The target must start in the Normal state.");

            await FocusSentinelAsync(probes);
            PlaceCursorAtKnownPoint();
            var before = CaptureDesktopState(probes.Target.WindowHandle);
            AssertSentinelIsForeground(before, probes);

            InvalidOperationException? exception = null;
            SetWindowStateResponse? unexpectedResponse = null;
            try
            {
                unexpectedResponse = await SetWindowStateAsync(
                    probes.TargetSessionId!,
                    probes.Target.WindowHandle,
                    WindowState.Maximized,
                    ensureForeground: false);
            }
            catch (InvalidOperationException caught)
            {
                exception = caught;
            }

            var after = CaptureDesktopState(probes.Target.WindowHandle);

            Assert.Multiple(() =>
            {
                Assert.That(
                    exception,
                    Is.Not.Null,
                    $"Expected strict policy to block maximize, but received: {unexpectedResponse}");
                Assert.That(exception!.Message, Does.Contain("interaction_policy_blocked"));
                Assert.That(exception.Message, Does.Contain("allowForegroundActivation").IgnoreCase);
                Assert.That(exception.Message, Does.Contain("interactionPolicy.allowForegroundActivation=true"));
                Assert.That(IsIconic(probes.Target.WindowHandle), Is.False);
                Assert.That(IsZoomed(probes.Target.WindowHandle), Is.False);
                AssertDesktopStatePreserved(before, after);
            });
        }
        finally
        {
            await CloseProbePairAsync(probes);
        }
    }

    [Test]
    public async Task Strict_background_policy_can_minimize_restore_and_repeat_normal_without_activation()
    {
        var probes = await StartProbePairAsync(attachTarget: true, strictTargetPolicy: true);
        try
        {
            AssertStrictPolicy(probes.TargetInteractionPolicy);
            Assert.That(IsIconic(probes.Target.WindowHandle), Is.False, "The target must start restored.");
            Assert.That(IsZoomed(probes.Target.WindowHandle), Is.False, "The target must start in the Normal state.");

            await FocusSentinelAsync(probes);
            PlaceCursorAtKnownPoint();
            var before = CaptureDesktopState(probes.Target.WindowHandle);
            AssertSentinelIsForeground(before, probes);

            var minimized = await SetWindowStateAsync(
                probes.TargetSessionId!,
                probes.Target.WindowHandle,
                WindowState.Minimized,
                ensureForeground: false);
            await WaitForWindowStateAsync(probes.Target.WindowHandle, WindowState.Minimized);
            var afterMinimize = CaptureDesktopState(probes.Target.WindowHandle);

            Assert.Multiple(() =>
            {
                Assert.That(minimized.Updated, Is.True);
                Assert.That(minimized.State, Is.EqualTo(WindowState.Minimized));
                AssertWindowStateEffects(minimized.Effects, windowRestored: false);
                Assert.That(IsIconic(probes.Target.WindowHandle), Is.True);
                AssertDesktopStatePreserved(before, afterMinimize);
            });

            var restored = await SetWindowStateAsync(
                probes.TargetSessionId!,
                probes.Target.WindowHandle,
                WindowState.Normal,
                ensureForeground: false);
            await WaitForWindowStateAsync(probes.Target.WindowHandle, WindowState.Normal);
            var afterRestore = CaptureDesktopState(probes.Target.WindowHandle);

            Assert.Multiple(() =>
            {
                Assert.That(restored.Updated, Is.True);
                Assert.That(restored.State, Is.EqualTo(WindowState.Normal));
                AssertWindowStateEffects(restored.Effects, windowRestored: true);
                Assert.That(IsIconic(probes.Target.WindowHandle), Is.False);
                Assert.That(IsZoomed(probes.Target.WindowHandle), Is.False);
                AssertDesktopStatePreserved(before, afterRestore);
            });

            var alreadyNormal = await SetWindowStateAsync(
                probes.TargetSessionId!,
                probes.Target.WindowHandle,
                WindowState.Normal,
                ensureForeground: false);
            await WaitForWindowStateAsync(probes.Target.WindowHandle, WindowState.Normal);
            var afterAlreadyNormal = CaptureDesktopState(probes.Target.WindowHandle);

            Assert.Multiple(() =>
            {
                Assert.That(alreadyNormal.Updated, Is.False);
                Assert.That(alreadyNormal.State, Is.EqualTo(WindowState.Normal));
                AssertWindowStateEffects(alreadyNormal.Effects, windowRestored: false);
                AssertDesktopStatePreserved(before, afterAlreadyNormal);
            });
        }
        finally
        {
            await CloseProbePairAsync(probes);
        }
    }

    [Test]
    public async Task Strict_background_policy_can_resize_the_client_viewport_without_activation()
    {
        var probes = await StartProbePairAsync(attachTarget: true, strictTargetPolicy: true);
        try
        {
            AssertStrictPolicy(probes.TargetInteractionPolicy);
            await FocusSentinelAsync(probes);
            PlaceCursorAtKnownPoint();
            var before = CaptureDesktopState(probes.Target.WindowHandle);
            AssertSentinelIsForeground(before, probes);

            var resized = await SetWindowViewportAsync(
                probes.TargetSessionId!,
                probes.Target.WindowHandle,
                clientWidth: 600,
                clientHeight: 500);
            var after = CaptureDesktopState(probes.Target.WindowHandle);

            Assert.Multiple(() =>
            {
                Assert.That(resized.Actual.ClientSizePhysicalPixels, Is.EqualTo(new ViewportSize(600, 500)));
                Assert.That(resized.Adjustment.ExactMatch, Is.True);
                Assert.That(resized.Effects?.ForegroundActivated, Is.False);
                Assert.That(resized.Effects?.MouseInput, Is.False);
                Assert.That(resized.Effects?.KeyboardInput, Is.False);
                Assert.That(resized.Effects?.CursorMoved, Is.False);
                AssertDesktopStatePreserved(before, after);
            });
        }
        finally
        {
            await CloseProbePairAsync(probes);
        }
    }

    [Test]
    public async Task Permissive_operation_override_allows_fallback_and_reports_physical_effects()
    {
        var probes = await StartProbePairAsync(attachTarget: true, strictTargetPolicy: true);
        try
        {
            AssertStrictPolicy(probes.TargetInteractionPolicy);
            await FocusSentinelAsync(probes);
            PlaceCursorAtKnownPoint();
            var before = CaptureDesktopState(probes.Target.WindowHandle);
            AssertSentinelIsForeground(before, probes);

            var clicked = await ClickPhysicalFallbackTargetAsync(
                probes.TargetSessionId!,
                allowForegroundActivation: true,
                allowPhysicalInput: true);

            var status = await WaitForElementNameAsync(
                probes.TargetSessionId!,
                "FocusProbe_PhysicalFallbackStatus",
                "Physical clicks: 1");
            await WaitForActivationIncreaseAsync(
                probes.Target.WindowHandle,
                before.TargetActivity.Activations);
            var after = CaptureDesktopState(probes.Target.WindowHandle);

            Assert.Multiple(() =>
            {
                Assert.That(clicked.Clicked, Is.True);
                Assert.That(clicked.MethodUsed, Does.Contain("mouse").IgnoreCase);
                AssertPhysicalFallbackEffects(clicked.Effects);
                Assert.That(status, Is.EqualTo("Physical clicks: 1"));
                Assert.That(after.ForegroundWindowHandle, Is.EqualTo(probes.Target.WindowHandle));
                Assert.That(after.CursorPosition, Is.Not.EqualTo(before.CursorPosition));
                Assert.That(after.TargetActivity.Activations, Is.GreaterThan(before.TargetActivity.Activations));
            });
        }
        finally
        {
            await CloseProbePairAsync(probes);
        }
    }

    [Test]
    public async Task Permissive_operation_override_reports_keyboard_fallback_effects()
    {
        const string expectedText = "keyboard fallback";
        var probes = await StartProbePairAsync(attachTarget: true, strictTargetPolicy: true);
        try
        {
            AssertStrictPolicy(probes.TargetInteractionPolicy);
            await FocusSentinelAsync(probes);
            PlaceCursorAtKnownPoint();
            var before = CaptureDesktopState(probes.Target.WindowHandle);
            AssertSentinelIsForeground(before, probes);

            var typed = await TypeTextIntoKeyboardFallbackTargetAsync(
                probes.TargetSessionId!,
                expectedText,
                allowForegroundActivation: true,
                allowPhysicalInput: true);

            var status = await WaitForElementNameAsync(
                probes.TargetSessionId!,
                "FocusProbe_KeyboardFallbackStatus",
                $"Keyboard text: {expectedText}");
            await WaitForActivationIncreaseAsync(
                probes.Target.WindowHandle,
                before.TargetActivity.Activations);
            var after = CaptureDesktopState(probes.Target.WindowHandle);

            Assert.Multiple(() =>
            {
                Assert.That(typed.Typed, Is.True);
                Assert.That(typed.MethodUsed, Is.EqualTo("keyboard"));
                Assert.That(typed.ModeUsed, Is.EqualTo(TextEntryMode.Replace));
                Assert.That(typed.ForegroundFocusRequired, Is.True);
                Assert.That(typed.PhysicalInputRequired, Is.True);
                AssertKeyboardOnlyEffects(typed.Effects);
                Assert.That(status, Is.EqualTo($"Keyboard text: {expectedText}"));
                Assert.That(after.ForegroundWindowHandle, Is.EqualTo(probes.Target.WindowHandle));
                Assert.That(after.CursorPosition, Is.EqualTo(before.CursorPosition));
                Assert.That(after.TargetActivity.Activations, Is.GreaterThan(before.TargetActivity.Activations));
            });
        }
        finally
        {
            await CloseProbePairAsync(probes);
        }
    }

    [Test]
    public async Task Explicit_text_entry_modes_replace_then_append_without_physical_input()
    {
        var probes = await StartProbePairAsync(attachTarget: true, strictTargetPolicy: true);
        try
        {
            AssertStrictPolicy(probes.TargetInteractionPolicy);
            await FocusSentinelAsync(probes);
            var before = CaptureDesktopState(probes.Target.WindowHandle);
            AssertSentinelIsForeground(before, probes);

            var replaced = await TypeTextIntoTextBoxAsync(
                probes.TargetSessionId!,
                "Replaced",
                TextEntryMode.Replace);
            var appended = await TypeTextIntoTextBoxAsync(
                probes.TargetSessionId!,
                " + appended",
                TextEntryMode.Append);
            var value = await ReadElementValueAsync(probes.TargetSessionId!, "FocusProbe_TextBox");
            var after = CaptureDesktopState(probes.Target.WindowHandle);

            Assert.Multiple(() =>
            {
                Assert.That(replaced.Typed, Is.True);
                Assert.That(replaced.ModeUsed, Is.EqualTo(TextEntryMode.Replace));
                Assert.That(replaced.ForegroundFocusRequired, Is.False);
                Assert.That(replaced.PhysicalInputRequired, Is.False);
                AssertOnlySemanticEffects(replaced.Effects);
                Assert.That(appended.Typed, Is.True);
                Assert.That(appended.ModeUsed, Is.EqualTo(TextEntryMode.Append));
                Assert.That(appended.ForegroundFocusRequired, Is.False);
                Assert.That(appended.PhysicalInputRequired, Is.False);
                AssertOnlySemanticEffects(appended.Effects);
                Assert.That(value, Is.EqualTo("Replaced + appended"));
                AssertDesktopStatePreserved(before, after);
            });
        }
        finally
        {
            await CloseProbePairAsync(probes);
        }
    }

    [Test]
    public async Task Send_keys_delivers_ordered_navigation_and_modifier_chords_without_moving_cursor()
    {
        var probes = await StartProbePairAsync(attachTarget: true, strictTargetPolicy: true);
        try
        {
            AssertStrictPolicy(probes.TargetInteractionPolicy);
            await FocusSentinelAsync(probes);
            PlaceCursorAtKnownPoint();
            var before = CaptureDesktopState(probes.Target.WindowHandle);
            AssertSentinelIsForeground(before, probes);

            var sent = await _mcp.CallToolAsync<SendKeysResponse>("send_keys", new Dictionary<string, object?>
            {
                ["sessionId"] = probes.TargetSessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "FocusProbe_KeyboardFallbackTarget"
                },
                ["sequence"] = new object[]
                {
                    new Dictionary<string, object?> { ["key"] = "Enter" },
                    new Dictionary<string, object?> { ["key"] = "Escape" },
                    new Dictionary<string, object?> { ["key"] = "ArrowLeft" },
                    new Dictionary<string, object?> { ["key"] = "ArrowUp" },
                    new Dictionary<string, object?> { ["key"] = "ArrowRight" },
                    new Dictionary<string, object?> { ["key"] = "ArrowDown" },
                    new Dictionary<string, object?>
                    {
                        ["key"] = "A",
                        ["modifiers"] = new[] { "Control" }
                    },
                    new Dictionary<string, object?> { ["key"] = "Tab" }
                },
                ["interactionPolicy"] = CreateInteractionPolicy(
                    allowForegroundActivation: true,
                    allowPhysicalInput: true)
            });

            var status = await WaitForElementNameAsync(
                probes.TargetSessionId!,
                "FocusProbe_KeyboardEventStatus",
                "Keys: Enter,Escape,ArrowLeft,ArrowUp,ArrowRight,ArrowDown,Control+A,Tab");
            await WaitForActivationIncreaseAsync(
                probes.Target.WindowHandle,
                before.TargetActivity.Activations);
            var after = CaptureDesktopState(probes.Target.WindowHandle);

            Assert.Multiple(() =>
            {
                Assert.That(sent.Sent, Is.True);
                Assert.That(sent.MethodUsed, Does.Contain("keyboard").IgnoreCase);
                Assert.That(sent.ForegroundFocusRequired, Is.True);
                Assert.That(sent.PhysicalInputRequired, Is.True);
                AssertKeyboardOnlyEffects(sent.Effects);
                Assert.That(status, Is.EqualTo("Keys: Enter,Escape,ArrowLeft,ArrowUp,ArrowRight,ArrowDown,Control+A,Tab"));
                Assert.That(after.ForegroundWindowHandle, Is.EqualTo(probes.Target.WindowHandle));
                Assert.That(after.CursorPosition, Is.EqualTo(before.CursorPosition));
                Assert.That(after.TargetActivity.Activations, Is.GreaterThan(before.TargetActivity.Activations));
            });
        }
        finally
        {
            await CloseProbePairAsync(probes);
        }
    }

    [Test]
    public async Task Send_keys_strict_policy_fails_before_foreground_focus_or_input()
    {
        var probes = await StartProbePairAsync(attachTarget: true, strictTargetPolicy: true);
        try
        {
            AssertStrictPolicy(probes.TargetInteractionPolicy);
            await FocusSentinelAsync(probes);
            var before = CaptureDesktopState(probes.Target.WindowHandle);
            AssertSentinelIsForeground(before, probes);

            InvalidOperationException? exception = null;
            try
            {
                _ = await _mcp.CallToolAsync<SendKeysResponse>("send_keys", new Dictionary<string, object?>
                {
                    ["sessionId"] = probes.TargetSessionId,
                    ["locator"] = new Dictionary<string, object?>
                    {
                        ["automationId"] = "FocusProbe_KeyboardFallbackTarget"
                    },
                    ["sequence"] = new object[]
                    {
                        new Dictionary<string, object?> { ["key"] = "Enter" }
                    }
                });
                Assert.Fail("Expected strict policy to block physical keyboard input.");
            }
            catch (InvalidOperationException caught)
            {
                exception = caught;
            }

            var status = await ReadElementNameAsync(
                probes.TargetSessionId!,
                "FocusProbe_KeyboardEventStatus");
            var after = CaptureDesktopState(probes.Target.WindowHandle);

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(exception!.Message, Does.Contain("interaction_policy_blocked"));
                Assert.That(exception.Message, Does.Contain("allowPhysicalInput=false"));
                Assert.That(status, Is.EqualTo("Keys: (none)"));
                AssertDesktopStatePreserved(before, after);
            });
        }
        finally
        {
            await CloseProbePairAsync(probes);
        }
    }

    private async Task<ProbePair> StartProbePairAsync(bool attachTarget, bool strictTargetPolicy = false)
    {
        FocusProbeProcess? target = null;
        FocusProbeProcess? sentinel = null;
        ProbePair? pair = null;
        try
        {
            target = await StartProbeProcessAsync();
            sentinel = await StartProbeProcessAsync();
            pair = new ProbePair(target, sentinel);

            if (attachTarget)
            {
                var attached = await AttachAsync(target.Process, strictTargetPolicy);
                pair.TargetSessionId = attached.SessionId;
                pair.TargetInteractionPolicy = attached.InteractionPolicy;
            }

            return pair;
        }
        catch
        {
            if (pair is not null)
            {
                await CloseProbePairAsync(pair);
            }
            else
            {
                StopProcess(sentinel?.Process);
                StopProcess(target?.Process);
            }

            throw;
        }
    }

    private async Task<AttachToAppResponse> AttachAsync(Process process, bool strictPolicy = false)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["pid"] = process.Id
        };

        if (strictPolicy)
        {
            arguments["interactionPolicy"] = CreateInteractionPolicy(
                allowForegroundActivation: false,
                allowPhysicalInput: false);
        }

        return await _mcp.CallToolAsync<AttachToAppResponse>("attach_to_app", arguments);
    }

    private async Task FocusSentinelAsync(ProbePair probes)
    {
        // Prime WPF's activation state before installing the sentinel. This makes
        // the counters meaningful even when process startup never granted focus.
        await EnsureForegroundPreconditionAsync(probes.Target.WindowHandle, "target");
        await WaitUntilAsync(
            () => GetForegroundWindow() == probes.Target.WindowHandle,
            "The target window did not become foreground while priming the test state.");
        await WaitForProbeActivityToStabilizeAsync(probes.Target.WindowHandle);

        await EnsureForegroundPreconditionAsync(probes.Sentinel.WindowHandle, "sentinel");
        await WaitUntilAsync(
            () => GetForegroundWindow() == probes.Sentinel.WindowHandle,
            "The sentinel window did not become the foreground window.");
        await WaitForTargetDeactivationAsync(probes.Target.WindowHandle);
    }

    private static async Task EnsureForegroundPreconditionAsync(IntPtr windowHandle, string role)
    {
        if (await TrySetForegroundWindowAsync(windowHandle))
        {
            return;
        }

        if (!IsInteractiveDesktop(out var reason))
        {
            Assert.Ignore($"Non-intrusive desktop tests require an interactive Windows desktop: {reason}");
        }

        Assert.Fail(
            $"The {role} window could not become foreground on the interactive desktop. " +
            $"Current foreground handle: 0x{GetForegroundWindow().ToInt64():X}.");
    }

    private async Task<string?> ReadElementNameAsync(string sessionId, string automationId)
    {
        var result = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["locator"] = new Dictionary<string, object?>
            {
                ["automationId"] = automationId
            }
        });

        return result.Element.Name;
    }

    private async Task<string?> WaitForElementNameAsync(string sessionId, string automationId, string expected)
    {
        string? actual = null;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            actual = await ReadElementNameAsync(sessionId, automationId);
            if (string.Equals(actual, expected, StringComparison.Ordinal))
            {
                return actual;
            }

            await Task.Delay(25);
        }

        return actual;
    }

    private async Task<ClickElementResponse> ClickPhysicalFallbackTargetAsync(
        string sessionId,
        bool? allowForegroundActivation = null,
        bool? allowPhysicalInput = null)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["locator"] = new Dictionary<string, object?>
            {
                ["automationId"] = "FocusProbe_PhysicalFallbackTarget"
            }
        };

        if (allowForegroundActivation is not null || allowPhysicalInput is not null)
        {
            arguments["interactionPolicy"] = CreateInteractionPolicy(
                allowForegroundActivation,
                allowPhysicalInput);
        }

        return await _mcp.CallToolAsync<ClickElementResponse>("click_element", arguments);
    }

    private async Task<CursorPosition> GetClientPointForElementAsync(
        string sessionId,
        IntPtr windowHandle,
        string automationId)
    {
        var result = await _mcp.CallToolAsync<ResolveElementResponse>("resolve_element", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["backend"] = "wpf",
            ["windowHandle"] = windowHandle.ToInt64(),
            ["locator"] = new Dictionary<string, object?>
            {
                ["automationId"] = automationId
            }
        });

        try
        {
            var bounds = result.Element.Bounds
                ?? throw new InvalidOperationException("The raw mouse target did not report bounds.");
            Assert.That(bounds.Width, Is.GreaterThan(0), "The raw mouse target must have non-empty bounds.");
            Assert.That(bounds.Height, Is.GreaterThan(0), "The raw mouse target must have non-empty bounds.");

            var point = new NativePoint
            {
                X = bounds.X + (bounds.Width / 2),
                Y = bounds.Y + (bounds.Height / 2)
            };
            if (!ScreenToClient(windowHandle, ref point))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not convert the raw mouse target to client coordinates.");
            }

            return new CursorPosition(point.X, point.Y);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(result.Element.ElementId))
            {
                _ = await _mcp.CallToolAsync<ReleaseElementResponse>("release_element", new Dictionary<string, object?>
                {
                    ["sessionId"] = sessionId,
                    ["elementId"] = result.Element.ElementId
                });
            }
        }
    }

    private async Task<MouseClickResponse> RawMouseClickAsync(
        string sessionId,
        IntPtr windowHandle,
        CursorPosition clientPoint,
        bool allowForegroundActivation,
        bool allowPhysicalInput) =>
        await _mcp.CallToolAsync<MouseClickResponse>("mouse_click", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["x"] = clientPoint.X,
            ["y"] = clientPoint.Y,
            ["coordSpace"] = "client",
            ["windowHandle"] = windowHandle.ToInt64(),
            ["ensureForeground"] = false,
            ["interactionPolicy"] = CreateInteractionPolicy(
                allowForegroundActivation,
                allowPhysicalInput)
        });

    private async Task<SetWindowStateResponse> SetWindowStateAsync(
        string sessionId,
        IntPtr windowHandle,
        WindowState state,
        bool ensureForeground) =>
        await _mcp.CallToolAsync<SetWindowStateResponse>("set_window_state", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["windowHandle"] = windowHandle.ToInt64(),
            ["state"] = state.ToString().ToLowerInvariant(),
            ["ensureForeground"] = ensureForeground
        });

    private async Task<SetWindowViewportResponse> SetWindowViewportAsync(
        string sessionId,
        IntPtr windowHandle,
        double clientWidth,
        double clientHeight) =>
        await _mcp.CallToolAsync<SetWindowViewportResponse>("set_window_viewport", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["windowHandle"] = windowHandle.ToInt64(),
            ["clientWidth"] = clientWidth,
            ["clientHeight"] = clientHeight,
            ["unit"] = "physicalPixels",
            ["ensureForeground"] = false
        });

    private async Task<TypeTextResponse> TypeTextIntoKeyboardFallbackTargetAsync(
        string sessionId,
        string text,
        bool? allowForegroundActivation = null,
        bool? allowPhysicalInput = null)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["text"] = text,
            ["locator"] = new Dictionary<string, object?>
            {
                ["automationId"] = "FocusProbe_KeyboardFallbackTarget"
            }
        };

        if (allowForegroundActivation is not null || allowPhysicalInput is not null)
        {
            arguments["interactionPolicy"] = CreateInteractionPolicy(
                allowForegroundActivation,
                allowPhysicalInput);
        }

        return await _mcp.CallToolAsync<TypeTextResponse>("type_text", arguments);
    }

    private async Task<TypeTextResponse> TypeTextIntoTextBoxAsync(
        string sessionId,
        string text,
        TextEntryMode mode) =>
        await _mcp.CallToolAsync<TypeTextResponse>("type_text", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["text"] = text,
            ["mode"] = mode.ToString(),
            ["locator"] = new Dictionary<string, object?>
            {
                ["automationId"] = "FocusProbe_TextBox"
            }
        });

    private async Task<string?> ReadElementValueAsync(string sessionId, string automationId)
    {
        var response = await _mcp.CallToolAsync<GetElementPropertiesResponse>(
            "get_element_properties",
            new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["locator"] = new Dictionary<string, object?> { ["automationId"] = automationId }
            });

        return response.Patterns.TryGetValue("Value", out var valuePattern)
            ? valuePattern?["values"]?["Value"]?.GetValue<string>()
            : null;
    }

    private static Dictionary<string, object?> CreateInteractionPolicy(
        bool? allowForegroundActivation,
        bool? allowPhysicalInput)
    {
        var policy = new Dictionary<string, object?>();
        if (allowForegroundActivation is not null)
        {
            policy["allowForegroundActivation"] = allowForegroundActivation.Value;
        }

        if (allowPhysicalInput is not null)
        {
            policy["allowPhysicalInput"] = allowPhysicalInput.Value;
        }

        return policy;
    }

    private static void AssertStrictPolicy(InteractionPolicy? policy)
    {
        Assert.That(policy, Is.Not.Null, "attach_to_app must report the effective session interaction policy.");
        Assert.That(policy!.AllowForegroundActivation, Is.False);
        Assert.That(policy.AllowPhysicalInput, Is.False);
    }

    private static void AssertPermissivePolicy(InteractionPolicy? policy)
    {
        Assert.That(policy, Is.Not.Null, "attach_to_app must report the effective session interaction policy.");
        Assert.That(policy!.AllowForegroundActivation, Is.True);
        Assert.That(policy.AllowPhysicalInput, Is.True);
    }

    private static void AssertOnlySemanticEffects(InteractionEffects? effects)
    {
        Assert.That(effects, Is.Not.Null, "The interaction response must report effects.");
        Assert.That(effects!.Semantic, Is.True);
        Assert.That(effects.ForegroundActivated, Is.False);
        Assert.That(effects.WindowRestored, Is.False);
        Assert.That(effects.MouseInput, Is.False);
        Assert.That(effects.KeyboardInput, Is.False);
        Assert.That(effects.CursorMoved, Is.False);
        Assert.That(effects.KeyboardFocusChanged, Is.Null);
    }

    private static void AssertPhysicalFallbackEffects(InteractionEffects? effects)
    {
        Assert.That(effects, Is.Not.Null, "The interaction response must report effects.");
        Assert.That(effects!.Semantic, Is.False);
        Assert.That(effects.ForegroundActivated, Is.True);
        Assert.That(effects.WindowRestored, Is.False);
        Assert.That(effects.MouseInput, Is.True);
        Assert.That(effects.KeyboardInput, Is.False);
        Assert.That(effects.CursorMoved, Is.True);
        Assert.That(effects.KeyboardFocusChanged, Is.Null);
    }

    private static void AssertKeyboardOnlyEffects(InteractionEffects? effects)
    {
        Assert.That(effects, Is.Not.Null, "The interaction response must report effects.");
        Assert.That(effects!.Semantic, Is.False);
        Assert.That(effects.ForegroundActivated, Is.True);
        Assert.That(effects.WindowRestored, Is.False);
        Assert.That(effects.MouseInput, Is.False);
        Assert.That(effects.KeyboardInput, Is.True);
        Assert.That(effects.CursorMoved, Is.False);
        Assert.That(effects.KeyboardFocusChanged, Is.True);
    }

    private static void AssertWindowStateEffects(InteractionEffects? effects, bool windowRestored)
    {
        Assert.That(effects, Is.Not.Null, "The window-state response must report effects.");
        Assert.That(effects!.ForegroundActivated, Is.False);
        Assert.That(effects.WindowRestored, Is.EqualTo(windowRestored));
        Assert.That(effects.MouseInput, Is.False);
        Assert.That(effects.KeyboardInput, Is.False);
        Assert.That(effects.CursorMoved, Is.False);
    }

    private async Task CloseProbePairAsync(ProbePair probes)
    {
        await TryCloseSessionAsync(probes.TargetSessionId);
        StopProcess(probes.Target.Process);
        StopProcess(probes.Sentinel.Process);
    }

    private async Task TryCloseSessionAsync(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        try
        {
            _ = await _mcp.CallToolAsync<CloseAppResponse>("close_session", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["force"] = true,
                ["timeoutMs"] = 2000
            });
        }
        catch (Exception ex)
        {
            ReportCleanupFailure($"close_session for '{sessionId}' failed", ex);
        }
    }

    private static async Task<FocusProbeProcess> StartProbeProcessAsync()
    {
        var exePath = TestAppPaths.FindFocusProbeTestAppExecutable();
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start the FocusProbe test app.");

        try
        {
            var windowHandle = await WaitForMainWindowAsync(process);
            return new FocusProbeProcess(process, windowHandle);
        }
        catch
        {
            StopProcess(process);
            throw;
        }
    }

    private static async Task<IntPtr> WaitForMainWindowAsync(Process process)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"FocusProbe process {process.Id} exited before creating a window.");
            }

            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                _ = ReadProbeActivity(process.MainWindowHandle);
                return process.MainWindowHandle;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"FocusProbe process {process.Id} did not create a main window within 10 seconds.");
    }

    private static async Task WaitForProbeActivityToStabilizeAsync(IntPtr windowHandle)
    {
        var previous = ReadProbeActivity(windowHandle);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(25);
            var current = ReadProbeActivity(windowHandle);
            if (current == previous)
            {
                return;
            }

            previous = current;
        }

        throw new TimeoutException("The FocusProbe activation counters did not stabilize.");
    }

    private static async Task WaitForTargetDeactivationAsync(IntPtr windowHandle)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            var activity = ReadProbeActivity(windowHandle);
            if (activity.Deactivations >= activity.Activations)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The FocusProbe target did not report deactivation after the sentinel took foreground.");
    }

    private static async Task WaitForActivationIncreaseAsync(IntPtr windowHandle, int previousActivationCount)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (ReadProbeActivity(windowHandle).Activations > previousActivationCount)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The FocusProbe target did not report the expected activation.");
    }

    private static async Task WaitForWindowStateAsync(IntPtr windowHandle, WindowState expectedState)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            var stateMatches = expectedState switch
            {
                WindowState.Normal => !IsIconic(windowHandle) && !IsZoomed(windowHandle),
                WindowState.Minimized => IsIconic(windowHandle),
                WindowState.Maximized => IsZoomed(windowHandle),
                _ => false
            };
            if (stateMatches)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"The FocusProbe target did not reach window state '{expectedState}'.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string timeoutMessage)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(timeoutMessage);
    }

    private static async Task<bool> TrySetForegroundWindowAsync(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        _ = ShowWindowAsync(windowHandle, ShowWindowRestore);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (GetForegroundWindow() == windowHandle)
            {
                return true;
            }

            var foregroundHandle = GetForegroundWindow();
            var currentThreadId = GetCurrentThreadId();
            var targetThreadId = GetWindowThreadProcessId(windowHandle, out _);
            var foregroundThreadId = foregroundHandle == IntPtr.Zero
                ? 0
                : GetWindowThreadProcessId(foregroundHandle, out _);

            var attachedToForeground = AttachInputQueue(currentThreadId, foregroundThreadId);
            var attachedToTarget = AttachInputQueue(currentThreadId, targetThreadId);
            try
            {
                _ = BringWindowToTop(windowHandle);
                _ = SetForegroundWindow(windowHandle);
                _ = SetFocus(windowHandle);

                if (GetForegroundWindow() != windowHandle)
                {
                    SwitchToThisWindow(windowHandle, altTab: true);
                }
            }
            finally
            {
                DetachInputQueue(currentThreadId, targetThreadId, attachedToTarget);
                DetachInputQueue(currentThreadId, foregroundThreadId, attachedToForeground);
            }

            await Task.Delay(50);
        }

        return GetForegroundWindow() == windowHandle;
    }

    private static bool AttachInputQueue(uint currentThreadId, uint otherThreadId)
    {
        if (otherThreadId == 0 || otherThreadId == currentThreadId)
        {
            return false;
        }

        return AttachThreadInput(currentThreadId, otherThreadId, attach: true);
    }

    private static void DetachInputQueue(uint currentThreadId, uint otherThreadId, bool attached)
    {
        if (attached)
        {
            _ = AttachThreadInput(currentThreadId, otherThreadId, attach: false);
        }
    }

    private static bool IsInteractiveDesktop(out string reason)
    {
        if (!Environment.UserInteractive)
        {
            reason = "Environment.UserInteractive is false";
            return false;
        }

        var windowStation = ReadUserObjectName(GetProcessWindowStation());
        var desktop = ReadUserObjectName(GetThreadDesktop(GetCurrentThreadId()));
        if (!string.Equals(windowStation, "WinSta0", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"window station is '{windowStation ?? "(unknown)"}', not WinSta0";
            return false;
        }

        if (string.IsNullOrWhiteSpace(desktop))
        {
            reason = "the current thread desktop could not be identified";
            return false;
        }

        if (GetForegroundWindow() == IntPtr.Zero)
        {
            reason = $"desktop '{desktop}' has no foreground window";
            return false;
        }

        reason = $"interactive desktop WinSta0\\{desktop}";
        return true;
    }

    private static string? ReadUserObjectName(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        const int maxCharacters = 256;
        var buffer = new StringBuilder(maxCharacters);
        return GetUserObjectInformation(
            handle,
            UserObjectName,
            buffer,
            checked((uint)(maxCharacters * sizeof(char))),
            out _)
            ? buffer.ToString()
            : null;
    }

    private static void PlaceCursorAtKnownPoint()
    {
        if (!SetCursorPos(0, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not position the cursor for the test precondition.");
        }
    }

    private static DesktopState CaptureDesktopState(IntPtr targetWindowHandle)
    {
        if (!GetCursorPos(out var point))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the current cursor position.");
        }

        return new DesktopState(
            GetForegroundWindow(),
            new CursorPosition(point.X, point.Y),
            ReadProbeActivity(targetWindowHandle));
    }

    private static ProbeActivity ReadProbeActivity(IntPtr windowHandle)
    {
        var title = ReadWindowTitle(windowHandle);
        var match = ProbeActivityPattern.Match(title);
        if (!match.Success)
        {
            throw new InvalidOperationException($"FocusProbe window title did not contain activation counters: '{title}'.");
        }

        return new ProbeActivity(
            int.Parse(match.Groups["activation"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["deactivation"].Value, CultureInfo.InvariantCulture));
    }

    private static string ReadWindowTitle(IntPtr windowHandle)
    {
        var buffer = new StringBuilder(capacity: 512);
        var length = GetWindowText(windowHandle, buffer, buffer.Capacity);
        if (length == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the FocusProbe window title.");
        }

        return buffer.ToString(0, length);
    }

    private static IReadOnlyCollection<string> CollectAutomationIds(TreeNode root)
    {
        var automationIds = new HashSet<string>(StringComparer.Ordinal);
        CollectAutomationIds(root, automationIds);
        return automationIds;
    }

    private static void CollectAutomationIds(TreeNode node, ISet<string> automationIds)
    {
        if (!string.IsNullOrWhiteSpace(node.AutomationId))
        {
            automationIds.Add(node.AutomationId);
        }

        foreach (var child in node.Children)
        {
            CollectAutomationIds(child, automationIds);
        }
    }

    private static void AssertSentinelIsForeground(DesktopState state, ProbePair probes) =>
        Assert.That(
            state.ForegroundWindowHandle,
            Is.EqualTo(probes.Sentinel.WindowHandle),
            "The sentinel window must own the foreground before exercising a non-intrusive operation.");

    private static void AssertSentinelOccludesClientPoint(ProbePair probes, CursorPosition targetClientPoint)
    {
        var screenPoint = new NativePoint
        {
            X = targetClientPoint.X,
            Y = targetClientPoint.Y
        };
        if (!ClientToScreen(probes.Target.WindowHandle, ref screenPoint))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not locate the raw mouse target on screen.");
        }

        Assert.That(
            WindowFromPoint(screenPoint),
            Is.EqualTo(probes.Sentinel.WindowHandle),
            "The foreground sentinel must cover the raw mouse target point for the occlusion precondition.");
    }

    private static void AssertDesktopStatePreserved(DesktopState before, DesktopState after)
    {
        Assert.That(after.ForegroundWindowHandle, Is.EqualTo(before.ForegroundWindowHandle), "Foreground ownership changed.");
        Assert.That(after.CursorPosition, Is.EqualTo(before.CursorPosition), "The cursor position changed.");
        Assert.That(
            after.TargetActivity,
            Is.EqualTo(before.TargetActivity),
            "The target received a transient activation or deactivation event.");
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            ReportCleanupFailure($"screenshot file '{path}' could not be deleted", ex);
        }
    }

    private static void StopProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5000))
                {
                    ReportCleanupFailure(
                        $"process {process.Id} ({process.ProcessName}) did not exit within five seconds");
                }
            }
        }
        catch (Exception ex)
        {
            ReportCleanupFailure($"process {TryGetProcessId(process)} could not be stopped", ex);
        }
        finally
        {
            try
            {
                process.Dispose();
            }
            catch (Exception ex)
            {
                ReportCleanupFailure($"process {TryGetProcessId(process)} handle could not be disposed", ex);
            }
        }
    }

    private static int? TryGetProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return null;
        }
    }

    private static void ReportCleanupFailure(string message, Exception? exception = null)
    {
        var detail = exception is null
            ? message
            : $"{message}: {exception.GetType().Name}: {exception.Message}";
        TestContext.Error.WriteLine($"Cleanup warning: {detail}");
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr windowHandle, ref NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr windowHandle, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr windowHandle, [MarshalAs(UnmanagedType.Bool)] bool altTab);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(
        IntPtr handle,
        int index,
        StringBuilder information,
        uint length,
        out uint needed);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maxCount);

    private sealed class ProbePair(FocusProbeProcess target, FocusProbeProcess sentinel)
    {
        public FocusProbeProcess Target { get; } = target;

        public FocusProbeProcess Sentinel { get; } = sentinel;

        public string? TargetSessionId { get; set; }

        public InteractionPolicy? TargetInteractionPolicy { get; set; }
    }

    private sealed record FocusProbeProcess(Process Process, IntPtr WindowHandle);

    private sealed record DesktopState(
        IntPtr ForegroundWindowHandle,
        CursorPosition CursorPosition,
        ProbeActivity TargetActivity);

    private readonly record struct CursorPosition(int X, int Y);

    private readonly record struct ProbeActivity(int Activations, int Deactivations);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}

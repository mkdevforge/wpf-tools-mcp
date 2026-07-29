using System.Threading;
using NUnit.Framework;
using VerifyNUnit;
using VerifyTests;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class DialogsSnapshots
{
    private const string MainWindowTitle = "WPF Tools MCP Dialogs TestApp";
    private const string DialogTitle = "WPF Tools MCP Confirm Dialog";
    private const string NativeDialogTitle = "WPF Tools MCP Native Open Dialog";

    // Common Item Dialog control IDs are stable across display languages.
    private const string NativeFileNameAutomationId = "1148";
    private const string NativeAcceptAutomationId = "1";
    private const string NativeCancelAutomationId = "2";

    private McpTestContext _mcp = null!;
    private string _sessionId = "";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        _mcp = await McpTestContext.StartAsync(serverExe);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_mcp is null)
        {
            return;
        }

        await _mcp.DisposeAsync();
    }

    private async Task LaunchDialogsAppAsync(string? nativeDialogFilePath = null, bool strictPolicy = false)
    {
        _sessionId = await LaunchDialogsAppSessionAsync(nativeDialogFilePath, strictPolicy);
    }

    private async Task<string> LaunchDialogsAppSessionAsync(
        string? nativeDialogFilePath = null,
        bool strictPolicy = false)
    {
        var exePath = TestAppPaths.FindDialogsTestAppExecutable();
        var workingDirectory = Path.GetDirectoryName(exePath)!;

        var arguments = new Dictionary<string, object?>
        {
            ["exePath"] = exePath,
            ["workingDirectory"] = workingDirectory,
            ["reuseExistingInstance"] = false
        };

        if (nativeDialogFilePath is not null)
        {
            arguments["args"] = new[] { "--native-dialog-file", nativeDialogFilePath };
        }

        if (strictPolicy)
        {
            arguments["interactionPolicy"] = CreateStrictInteractionPolicy();
        }

        var launch = await _mcp.CallToolAsync<LaunchAppResponse>("launch_app", arguments);

        return launch.SessionId;
    }

    private async Task CloseAppAsync()
    {
        var sessionId = _sessionId;
        _sessionId = "";
        await CloseAppAsync(sessionId);
    }

    private async Task CloseAppAsync(string? sessionId)
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
        catch
        {
        }
    }

    private async Task<ListWindowsResponse> ListWindowsAsync(string? sessionId = null) =>
        await _mcp.CallToolAsync<ListWindowsResponse>("list_windows", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId ?? _sessionId
        });

    private static object ToStableWindows(ListWindowsResponse response) => new
    {
        response.ProcessName,
        Windows = response.Windows
            .Select(w => new
            {
                w.Title,
                Handle = 0,
                Bounds = w.Bounds with { X = 0, Y = 0 },
                w.IsVisible,
                w.IsEnabled
            })
            .OrderBy(w => w.Title, StringComparer.Ordinal)
            .ToArray()
    };

    private async Task<WindowInfo> WaitForWindowAsync(
        string sessionId,
        string title,
        int attempts = 200,
        int delayMs = 75)
    {
        for (var i = 0; i < attempts; i++)
        {
            var windows = await ListWindowsAsync(sessionId);
            var match = windows.Windows.FirstOrDefault(w => string.Equals(w.Title, title, StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(delayMs);
        }

        Assert.Fail($"Window '{title}' did not appear within timeout.");
        throw new AssertionException("Unreachable.");
    }

    private async Task WaitForWindowClosedAsync(
        string sessionId,
        string title,
        int attempts = 200,
        int delayMs = 75)
    {
        for (var i = 0; i < attempts; i++)
        {
            var windows = await ListWindowsAsync(sessionId);
            var any = windows.Windows.Any(w => string.Equals(w.Title, title, StringComparison.Ordinal));
            if (!any)
            {
                return;
            }

            await Task.Delay(delayMs);
        }

        Assert.Fail($"Window '{title}' did not close within timeout.");
    }

    [Test]
    public async Task Modal_dialog_can_be_focused_and_clicked_by_window_handle_snapshot()
    {
        await LaunchDialogsAppAsync();
        try
        {
            var openDialog = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Dialogs_OpenDialog"
                },
                ["clickMode"] = "mouseAlways"
            });

            var dialogHandle = (await WaitForWindowAsync(_sessionId, DialogTitle)).Handle;
            var windowsWhileDialogOpen = await ListWindowsAsync();

            var focus = await _mcp.CallToolAsync<FocusWindowResponse>("set_active_window", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["title"] = DialogTitle
            });

            var clickOk = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["windowHandle"] = dialogHandle,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Dialog_OK"
                }
            });

            await WaitForWindowClosedAsync(_sessionId, DialogTitle);

            var status = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Dialogs_Status"
                }
            });

            var windowsAfterClose = await ListWindowsAsync();

            await Verifier.Verify(new
            {
                OpenDialog = openDialog,
                WindowsWhileDialogOpen = ToStableWindows(windowsWhileDialogOpen),
                Focus = focus with { Handle = 0 },
                ClickOk = clickOk,
                Status = status.Element.Name,
                WindowsAfterClose = ToStableWindows(windowsAfterClose)
            });
        }
        finally
        {
            await CloseAppAsync();
        }
    }

    [Test]
    public async Task Native_open_dialog_supports_auto_uia_semantics_and_window_lifecycle()
    {
        var fixtureDirectory = CreateFixtureDirectory(out var initialFilePath, out var targetFilePath);
        string? foreignSessionId = null;

        try
        {
            await LaunchDialogsAppAsync(initialFilePath, strictPolicy: true);
            var ownerWindow = await WaitForWindowAsync(_sessionId, MainWindowTitle);
            var openDialog = await InvokeAsync(
                _sessionId,
                ownerWindow.Handle,
                "Dialogs_OpenNativeFileDialog");

            var nativeWindow = await WaitForWindowAsync(_sessionId, NativeDialogTitle);
            var sameNativeWindow = await WaitForWindowAsync(_sessionId, NativeDialogTitle);
            var activeDialog = await _mcp.CallToolAsync<GetActiveWindowResponse>(
                "get_active_window",
                new Dictionary<string, object?> { ["sessionId"] = _sessionId });

            var tree = await _mcp.CallToolAsync<GetVisualTreeResponse>("get_visual_tree", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["backend"] = "auto",
                ["windowHandle"] = nativeWindow.Handle,
                ["depth"] = 2,
                ["maxNodes"] = 100,
                ["visibleOnly"] = true
            });

            var fileNameControl = await _mcp.CallToolAsync<ResolveElementResponse>(
                "resolve_element",
                new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["backend"] = "auto",
                    ["windowHandle"] = nativeWindow.Handle,
                    ["locator"] = new Dictionary<string, object?>
                    {
                        ["automationId"] = NativeFileNameAutomationId
                    }
                });

            var openButtons = await _mcp.CallToolAsync<FindElementsResponse>(
                "find_elements",
                new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["backend"] = "auto",
                    ["windowHandle"] = nativeWindow.Handle,
                    ["query"] = new Dictionary<string, object?>
                    {
                        ["automationIdEquals"] = NativeAcceptAutomationId,
                        ["typeEquals"] = "Button"
                    },
                    ["maxResults"] = 5
                });

            var blockedKeysError = await CaptureToolErrorAsync(async () =>
            {
                _ = await _mcp.CallToolAsync<SendKeysResponse>("send_keys", new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["windowHandle"] = nativeWindow.Handle,
                    ["locator"] = new Dictionary<string, object?>
                    {
                        ["automationId"] = NativeFileNameAutomationId
                    },
                    ["sequence"] = new object[]
                    {
                        new Dictionary<string, object?> { ["key"] = "Enter" }
                    }
                });
            });

            var typed = await _mcp.CallToolAsync<TypeTextResponse>("type_text", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["windowHandle"] = nativeWindow.Handle,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = NativeFileNameAutomationId
                },
                ["text"] = targetFilePath,
                ["mode"] = "Replace"
            });

            foreignSessionId = await LaunchDialogsAppSessionAsync(initialFilePath, strictPolicy: true);
            var foreignWindowError = await CaptureToolErrorAsync(async () =>
            {
                _ = await _mcp.CallToolAsync<ResolveElementResponse>(
                    "resolve_element",
                    new Dictionary<string, object?>
                    {
                        ["sessionId"] = foreignSessionId,
                        ["backend"] = "auto",
                        ["windowHandle"] = nativeWindow.Handle,
                        ["locator"] = new Dictionary<string, object?>
                        {
                            ["automationId"] = NativeAcceptAutomationId,
                            ["controlTypeEquals"] = "Button"
                        }
                    });
            });
            await CloseAppAsync(foreignSessionId);

            if (!string.IsNullOrWhiteSpace(fileNameControl.Element.ElementId))
            {
                _ = await _mcp.CallToolAsync<ReleaseElementResponse>(
                    "release_element",
                    new Dictionary<string, object?>
                    {
                        ["sessionId"] = _sessionId,
                        ["elementId"] = fileNameControl.Element.ElementId
                    });
            }

            var accept = await InvokeAsync(
                _sessionId,
                nativeWindow.Handle,
                NativeAcceptAutomationId,
                controlType: "Button");
            await WaitForWindowClosedAsync(_sessionId, NativeDialogTitle);

            var activeOwner = await _mcp.CallToolAsync<GetActiveWindowResponse>(
                "get_active_window",
                new Dictionary<string, object?> { ["sessionId"] = _sessionId });
            var status = await WaitForElementNameAsync(
                _sessionId,
                ownerWindow.Handle,
                "Dialogs_NativeStatus",
                "Native dialog: Opened native-dialog-target.txt");

            var staleWindowError = await CaptureToolErrorAsync(async () =>
            {
                _ = await _mcp.CallToolAsync<ResolveElementResponse>(
                    "resolve_element",
                    new Dictionary<string, object?>
                    {
                        ["sessionId"] = _sessionId,
                        ["backend"] = "auto",
                        ["windowHandle"] = nativeWindow.Handle,
                        ["locator"] = new Dictionary<string, object?>
                        {
                            ["automationId"] = NativeAcceptAutomationId,
                            ["controlTypeEquals"] = "Button"
                        }
                    });
            });

            Assert.Multiple(() =>
            {
                Assert.That(openDialog.Invoked, Is.True);
                Assert.That(openDialog.Effects?.Semantic, Is.True);
                Assert.That(openDialog.Effects?.MouseInput, Is.False);
                Assert.That(nativeWindow.Handle, Is.EqualTo(sameNativeWindow.Handle));
                Assert.That(nativeWindow.OwnerHandle, Is.EqualTo(ownerWindow.Handle));
                Assert.That(nativeWindow.IsModal, Is.True);
                Assert.That(nativeWindow.FrameworkId, Is.EqualTo("Win32").IgnoreCase);
                Assert.That(activeDialog.Handle, Is.EqualTo(nativeWindow.Handle));
                Assert.That(activeDialog.Title, Is.EqualTo(NativeDialogTitle));
                Assert.That(tree.BackendUsed, Is.EqualTo(InspectionBackend.Uia));
                Assert.That(fileNameControl.BackendUsed, Is.EqualTo(InspectionBackend.Uia));
                Assert.That(fileNameControl.Element.AutomationId, Is.EqualTo(NativeFileNameAutomationId));
                Assert.That(openButtons.BackendUsed, Is.EqualTo(InspectionBackend.Uia));
                Assert.That(openButtons.Matches, Has.Exactly(1).Matches<ElementRef>(element =>
                    string.Equals(element.AutomationId, NativeAcceptAutomationId, StringComparison.Ordinal) &&
                    string.Equals(element.Type, "Button", StringComparison.OrdinalIgnoreCase)));
                Assert.That(blockedKeysError, Does.Contain("interaction_policy_blocked"));
                Assert.That(blockedKeysError, Does.Contain("allowPhysicalInput=false"));
                Assert.That(typed.Typed, Is.True);
                Assert.That(typed.MethodUsed, Is.EqualTo("valuePattern"));
                Assert.That(typed.ModeUsed, Is.EqualTo(TextEntryMode.Replace));
                Assert.That(typed.Effects?.Semantic, Is.True);
                Assert.That(typed.Effects?.KeyboardInput, Is.False);
                Assert.That(typed.ForegroundFocusRequired, Is.False);
                Assert.That(typed.PhysicalInputRequired, Is.False);
                Assert.That(foreignWindowError, Does.Contain("window_outside_session"));
                Assert.That(accept.Invoked, Is.True);
                Assert.That(accept.MethodUsed, Is.EqualTo("invoke"));
                Assert.That(accept.Effects?.Semantic, Is.True);
                Assert.That(activeOwner.Handle, Is.EqualTo(ownerWindow.Handle));
                Assert.That(activeOwner.Title, Is.EqualTo(MainWindowTitle));
                Assert.That(status, Is.EqualTo("Native dialog: Opened native-dialog-target.txt"));
                Assert.That(staleWindowError, Does.Contain("window_closed"));
            });
        }
        finally
        {
            await CloseAppAsync(foreignSessionId);
            await CloseAppAsync();
            DeleteFixtureDirectory(fixtureDirectory);
        }
    }

    [Test]
    public async Task Native_open_dialog_can_be_cancelled_semantically()
    {
        var fixtureDirectory = CreateFixtureDirectory(out var initialFilePath, out _);

        try
        {
            await LaunchDialogsAppAsync(initialFilePath, strictPolicy: true);
            var ownerWindow = await WaitForWindowAsync(_sessionId, MainWindowTitle);
            var openDialog = await InvokeAsync(
                _sessionId,
                ownerWindow.Handle,
                "Dialogs_OpenNativeFileDialog");
            var nativeWindow = await WaitForWindowAsync(_sessionId, NativeDialogTitle);

            var cancel = await InvokeAsync(
                _sessionId,
                nativeWindow.Handle,
                NativeCancelAutomationId,
                controlType: "Button");
            await WaitForWindowClosedAsync(_sessionId, NativeDialogTitle);

            var activeOwner = await _mcp.CallToolAsync<GetActiveWindowResponse>(
                "get_active_window",
                new Dictionary<string, object?> { ["sessionId"] = _sessionId });
            var status = await WaitForElementNameAsync(
                _sessionId,
                ownerWindow.Handle,
                "Dialogs_NativeStatus",
                "Native dialog: Cancel");

            Assert.Multiple(() =>
            {
                Assert.That(openDialog.Invoked, Is.True);
                Assert.That(openDialog.Effects?.Semantic, Is.True);
                Assert.That(cancel.Invoked, Is.True);
                Assert.That(cancel.MethodUsed, Is.EqualTo("invoke"));
                Assert.That(cancel.Effects?.Semantic, Is.True);
                Assert.That(cancel.Effects?.MouseInput, Is.False);
                Assert.That(activeOwner.Handle, Is.EqualTo(ownerWindow.Handle));
                Assert.That(activeOwner.Title, Is.EqualTo(MainWindowTitle));
                Assert.That(status, Is.EqualTo("Native dialog: Cancel"));
            });
        }
        finally
        {
            await CloseAppAsync();
            DeleteFixtureDirectory(fixtureDirectory);
        }
    }

    private async Task<InvokeResponse> InvokeAsync(
        string sessionId,
        long windowHandle,
        string automationId,
        string? controlType = null)
    {
        var locator = new Dictionary<string, object?>
        {
            ["automationId"] = automationId
        };
        if (controlType is not null)
        {
            locator["controlTypeEquals"] = controlType;
        }

        return await _mcp.CallToolAsync<InvokeResponse>("invoke", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["windowHandle"] = windowHandle,
            ["locator"] = locator
        });
    }

    private async Task<string?> WaitForElementNameAsync(
        string sessionId,
        long windowHandle,
        string automationId,
        string expected,
        int attempts = 100,
        int delayMs = 50)
    {
        string? actual = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var response = await _mcp.CallToolAsync<GetElementPropertiesResponse>(
                "get_element_properties",
                new Dictionary<string, object?>
                {
                    ["sessionId"] = sessionId,
                    ["windowHandle"] = windowHandle,
                    ["locator"] = new Dictionary<string, object?>
                    {
                        ["automationId"] = automationId
                    }
                });
            actual = response.Element.Name;
            if (string.Equals(actual, expected, StringComparison.Ordinal))
            {
                return actual;
            }

            await Task.Delay(delayMs);
        }

        return actual;
    }

    private static async Task<string> CaptureToolErrorAsync(Func<Task> action)
    {
        try
        {
            await action();
            Assert.Fail("Expected the tool call to fail.");
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message.Split("--- server stderr", StringSplitOptions.None)[0].TrimEnd();
        }

        throw new AssertionException("Unreachable.");
    }

    private static Dictionary<string, object?> CreateStrictInteractionPolicy() => new()
    {
        ["allowForegroundActivation"] = false,
        ["allowPhysicalInput"] = false
    };

    private static string CreateFixtureDirectory(out string initialFilePath, out string targetFilePath)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"WpfToolsMcp.DialogsSnapshots.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        initialFilePath = Path.Combine(directory, "native-dialog-initial.txt");
        targetFilePath = Path.Combine(directory, "native-dialog-target.txt");
        File.WriteAllText(initialFilePath, "initial");
        File.WriteAllText(targetFilePath, "target");
        return directory;
    }

    private static void DeleteFixtureDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
        }
    }
}

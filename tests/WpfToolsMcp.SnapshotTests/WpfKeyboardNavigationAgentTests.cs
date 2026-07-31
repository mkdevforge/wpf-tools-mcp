using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using WpfToolsMcp.Agent;
using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[Category("Wpf")]
[Apartment(ApartmentState.STA)]
[NonParallelizable]
public sealed class WpfKeyboardNavigationAgentTests
{
    [Test]
    public void Semantic_steps_move_forward_and_backward_skip_non_tab_stops_and_report_group_metadata()
    {
        var first = CreateButton("Trace_First", "First");
        var skipped = CreateButton("Trace_Skipped", "Skipped");
        skipped.IsTabStop = false;
        var last = CreateButton("Trace_Last", "Last");
        KeyboardNavigation.SetTabIndex(last, 7);

        var group = new StackPanel();
        KeyboardNavigation.SetTabNavigation(group, KeyboardNavigationMode.Cycle);
        FocusManager.SetIsFocusScope(group, true);
        group.Children.Add(first);
        group.Children.Add(skipped);
        group.Children.Add(last);

        var window = CreateWindow(group);
        var ownerId = $"keyboard-navigation-{Guid.NewGuid():N}";
        try
        {
            ShowAndFocus(window, first);

            var forward = Step(ownerId, window, KeyboardNavigationDirection.Next);
            var backward = Step(ownerId, window, KeyboardNavigationDirection.Previous);

            Assert.Multiple(() =>
            {
                Assert.That(forward.InteropBoundary, Is.False);
                Assert.That(forward.MoveAttempted, Is.True);
                Assert.That(forward.MoveAccepted, Is.True);
                Assert.That(forward.Focus?.AutomationId, Is.EqualTo("Trace_Last"));
                Assert.That(forward.Focus?.ElementIdWpf, Does.StartWith("wpfobj_"));
                Assert.That(forward.Metadata?.TabIndex, Is.EqualTo(7));
                Assert.That(forward.Metadata?.IsTabStop, Is.True);
                Assert.That(forward.Metadata?.Focusable, Is.True);
                Assert.That(forward.Metadata?.FocusScopeXPath, Does.Contain("StackPanel"));
                Assert.That(forward.Metadata?.NavigationGroupXPath, Does.Contain("StackPanel"));
                Assert.That(forward.Metadata?.TabNavigation, Is.EqualTo("Cycle"));
                Assert.That(backward.Focus?.AutomationId, Is.EqualTo("Trace_First"));
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Nested_cycle_returns_to_an_earlier_focus_identity()
    {
        var first = CreateButton("Trace_CycleFirst", "First");
        var last = CreateButton("Trace_CycleLast", "Last");
        var group = new StackPanel();
        KeyboardNavigation.SetTabNavigation(group, KeyboardNavigationMode.Cycle);
        group.Children.Add(first);
        group.Children.Add(last);
        var outer = new StackPanel();
        outer.Children.Add(group);
        outer.Children.Add(CreateButton("Trace_Outside", "Outside"));

        var window = CreateWindow(outer);
        var ownerId = $"keyboard-cycle-{Guid.NewGuid():N}";
        try
        {
            ShowAndFocus(window, last);

            var response = Step(ownerId, window, KeyboardNavigationDirection.Next);

            Assert.Multiple(() =>
            {
                Assert.That(response.MoveAccepted, Is.True);
                Assert.That(response.Focus?.AutomationId, Is.EqualTo("Trace_CycleFirst"));
                Assert.That(response.Metadata?.TabNavigation, Is.EqualTo("Cycle"));
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Navigation_metadata_uses_the_nearest_tab_group_not_an_inner_directional_group()
    {
        var focused = CreateButton("Trace_NestedMetadata", "Focused");
        var inner = new StackPanel();
        KeyboardNavigation.SetDirectionalNavigation(inner, KeyboardNavigationMode.Cycle);
        inner.Children.Add(focused);

        var outer = new StackPanel();
        KeyboardNavigation.SetTabNavigation(outer, KeyboardNavigationMode.Cycle);
        outer.Children.Add(inner);

        var window = CreateWindow(outer);
        var ownerId = $"keyboard-metadata-{Guid.NewGuid():N}";
        try
        {
            ShowAndFocus(window, focused);

            var response = WpfVisualTreeInspector.TraceKeyboardNavigationStep(
                ownerId,
                new WpfKeyboardNavigationStepRequest(
                    GetWindowHandle(window),
                    KeyboardNavigationDirection.Next,
                    Move: false),
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(response.Metadata?.TabNavigation, Is.EqualTo("Cycle"));
                Assert.That(response.Metadata?.NavigationGroupXPath, Does.Not.Contain("StackPanel[1]/StackPanel[1]"));
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Focus_handler_redirection_reports_the_element_that_actually_received_focus()
    {
        var first = CreateButton("Trace_RedirectStart", "Start");
        var redirected = CreateButton("Trace_Redirected", "Redirected");
        var destination = CreateButton("Trace_RedirectDestination", "Destination");
        redirected.GotKeyboardFocus += (_, _) => _ = destination.Focus();
        var group = new StackPanel();
        group.Children.Add(first);
        group.Children.Add(redirected);
        group.Children.Add(destination);

        var window = CreateWindow(group);
        var ownerId = $"keyboard-redirect-{Guid.NewGuid():N}";
        try
        {
            ShowAndFocus(window, first);

            var response = Step(ownerId, window, KeyboardNavigationDirection.Next);

            Assert.That(response.Focus?.AutomationId, Is.EqualTo("Trace_RedirectDestination"));
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Single_item_cycle_observes_no_focus_change()
    {
        var only = CreateButton("Trace_Only", "Only");
        var group = new StackPanel();
        KeyboardNavigation.SetTabNavigation(group, KeyboardNavigationMode.Cycle);
        group.Children.Add(only);

        var window = CreateWindow(group);
        var ownerId = $"keyboard-no-change-{Guid.NewGuid():N}";
        try
        {
            ShowAndFocus(window, only);
            var before = WpfVisualTreeInspector.TraceKeyboardNavigationStep(
                ownerId,
                new WpfKeyboardNavigationStepRequest(
                    GetWindowHandle(window),
                    KeyboardNavigationDirection.Next,
                    Move: false),
                CancellationToken.None);

            var after = Step(ownerId, window, KeyboardNavigationDirection.Next);

            Assert.Multiple(() =>
            {
                Assert.That(after.Focus?.ElementIdWpf, Is.EqualTo(before.Focus?.ElementIdWpf));
                Assert.That(after.Focus?.AutomationId, Is.EqualTo("Trace_Only"));
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Focus_in_another_window_is_reported_as_an_interop_boundary_for_the_pinned_window()
    {
        var firstWindowButton = CreateButton("Trace_WindowOne", "One");
        var secondWindowButton = CreateButton("Trace_WindowTwo", "Two");
        var firstWindow = CreateWindow(firstWindowButton);
        var secondWindow = CreateWindow(secondWindowButton);
        var ownerId = $"keyboard-boundary-{Guid.NewGuid():N}";
        try
        {
            ShowAndFocus(firstWindow, firstWindowButton);
            ShowAndFocus(secondWindow, secondWindowButton);

            var response = WpfVisualTreeInspector.TraceKeyboardNavigationStep(
                ownerId,
                new WpfKeyboardNavigationStepRequest(
                    GetWindowHandle(firstWindow),
                    KeyboardNavigationDirection.Next,
                    Move: true),
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(response.InteropBoundary, Is.True);
                Assert.That(response.Focus, Is.Null);
                Assert.That(response.Metadata, Is.Null);
                Assert.That(response.MoveAccepted, Is.False);
            });
        }
        finally
        {
            CloseAndRelease(secondWindow, ownerId);
            CloseAndRelease(firstWindow, ownerId);
        }
    }

    [Test]
    public void Focused_content_element_uses_bounded_logical_ancestry_without_losing_identity()
    {
        var hyperlink = new Hyperlink(new Run("Focusable link")) { Focusable = true };
        AutomationProperties.SetAutomationId(hyperlink, "Trace_Hyperlink");
        var textBlock = new TextBlock();
        textBlock.Inlines.Add(hyperlink);
        var window = CreateWindow(textBlock);
        var ownerId = $"keyboard-content-{Guid.NewGuid():N}";
        try
        {
            ShowAndFocus(window, hyperlink);

            var response = WpfVisualTreeInspector.TraceKeyboardNavigationStep(
                ownerId,
                new WpfKeyboardNavigationStepRequest(
                    GetWindowHandle(window),
                    KeyboardNavigationDirection.Next,
                    Move: false),
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(response.InteropBoundary, Is.False);
                Assert.That(response.Focus?.AutomationId, Is.EqualTo("Trace_Hyperlink"));
                Assert.That(response.Focus?.ElementIdWpf, Does.StartWith("wpfobj_"));
                Assert.That(response.Focus?.XPath, Does.Contain("Hyperlink"));
                Assert.That(response.Metadata?.Focusable, Is.True);
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public async Task Agent_server_routes_navigation_and_focus_to_the_pinned_windows_dispatcher()
    {
        var ready = new TaskCompletionSource<SecondaryWindowContext>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new SecondaryWindowHost();
        var thread = new Thread(() => RunSecondaryWindow(ready, host))
        {
            IsBackground = true,
            Name = "WPF keyboard navigation secondary dispatcher test"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var ownerId = $"keyboard-dispatcher-{Guid.NewGuid():N}";
        try
        {
            var context = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var observed = await SendAgentRequestAsync<WpfKeyboardNavigationStepResponse>(
                ownerId,
                AgentProtocolCapabilities.KeyboardNavigationStep,
                new WpfKeyboardNavigationStepRequest(
                    context.WindowHandle,
                    KeyboardNavigationDirection.Next,
                    Move: false));
            var moved = await SendAgentRequestAsync<WpfKeyboardNavigationStepResponse>(
                ownerId,
                AgentProtocolCapabilities.KeyboardNavigationStep,
                new WpfKeyboardNavigationStepRequest(
                    context.WindowHandle,
                    KeyboardNavigationDirection.Next,
                    Move: true));
            var restored = await SendAgentRequestAsync<FocusWpfElementResponse>(
                ownerId,
                AgentProtocolCapabilities.FocusElement,
                new FocusWpfElementRequest(
                    WindowHandle: context.WindowHandle,
                    ElementId: observed.Focus?.ElementIdWpf,
                    MaxNodes: 1));

            Assert.Multiple(() =>
            {
                Assert.That(observed.Focus?.AutomationId, Is.EqualTo("Trace_SecondaryFirst"));
                Assert.That(moved.Focus?.AutomationId, Is.EqualTo("Trace_SecondaryLast"));
                Assert.That(restored.Focused, Is.True);
                Assert.That(restored.KeyboardFocusChanged, Is.True);
            });
        }
        finally
        {
            host.RequestShutdown(ownerId);
            Assert.That(thread.Join(TimeSpan.FromSeconds(5)), Is.True, "Secondary WPF dispatcher did not stop.");
        }
    }

    private static WpfKeyboardNavigationStepResponse Step(
        string ownerId,
        Window window,
        KeyboardNavigationDirection direction) =>
        WpfVisualTreeInspector.TraceKeyboardNavigationStep(
            ownerId,
            new WpfKeyboardNavigationStepRequest(GetWindowHandle(window), direction, Move: true),
            CancellationToken.None);

    private static async Task<T> SendAgentRequestAsync<T>(string ownerId, string method, object parameters)
        where T : class
    {
        var response = await AgentServer.HandleAsync(
            ownerId,
            new AgentRequest(
                Guid.NewGuid().ToString("N"),
                method,
                JsonSerializer.SerializeToNode(parameters)),
            CancellationToken.None);

        Assert.That(response.Ok, Is.True, response.Error?.Message);
        return response.Result?.Deserialize<T>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new AssertionException($"Agent method '{method}' returned no result.");
    }

    private static void RunSecondaryWindow(
        TaskCompletionSource<SecondaryWindowContext> ready,
        SecondaryWindowHost host)
    {
        try
        {
            var first = CreateButton("Trace_SecondaryFirst", "First");
            var last = CreateButton("Trace_SecondaryLast", "Last");
            var group = new StackPanel();
            group.Children.Add(first);
            group.Children.Add(last);

            var window = CreateWindow(group);
            ShowAndFocus(window, first);
            var windowHandle = GetWindowHandle(window);
            if (!host.Publish(window))
            {
                window.Close();
                return;
            }

            ready.TrySetResult(new SecondaryWindowContext(windowHandle));
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
        }
    }

    private static Button CreateButton(string automationId, string content)
    {
        var button = new Button
        {
            Content = content,
            Width = 140,
            Height = 36,
            Margin = new Thickness(2)
        };
        AutomationProperties.SetAutomationId(button, automationId);
        AutomationProperties.SetName(button, content);
        return button;
    }

    private static Window CreateWindow(UIElement content) =>
        new()
        {
            Title = "WPF keyboard navigation unit test",
            Width = 320,
            Height = 240,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
            Left = -10_000,
            Top = -10_000,
            Content = content
        };

    private static void ShowAndFocus(Window window, DependencyObject target)
    {
        window.Show();
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        _ = window.Activate();
        _ = Keyboard.Focus((IInputElement)target);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Input);
        Assert.That(Keyboard.FocusedElement, Is.SameAs(target));
    }

    private static long GetWindowHandle(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle.ToInt64();
        Assert.That(handle, Is.Not.Zero);
        return handle;
    }

    private static void CloseAndRelease(Window window, string ownerId)
    {
        WpfVisualTreeInspector.ReleaseOwnerResources(ownerId);
        if (window.IsVisible)
        {
            window.Close();
        }
    }

    private sealed record SecondaryWindowContext(long WindowHandle);

    private sealed class SecondaryWindowHost
    {
        private readonly object _gate = new();
        private Window? _window;
        private bool _shutdownRequested;

        internal bool Publish(Window window)
        {
            lock (_gate)
            {
                _window = window;
                return !_shutdownRequested;
            }
        }

        internal void RequestShutdown(string ownerId)
        {
            Window? window;
            lock (_gate)
            {
                _shutdownRequested = true;
                window = _window;
            }

            if (window is null ||
                window.Dispatcher.HasShutdownStarted ||
                window.Dispatcher.HasShutdownFinished)
            {
                return;
            }

            _ = window.Dispatcher.InvokeAsync(() =>
            {
                WpfVisualTreeInspector.ReleaseOwnerResources(ownerId);
                if (window.IsVisible)
                {
                    window.Close();
                }

                window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }, DispatcherPriority.Send);
        }
    }
}

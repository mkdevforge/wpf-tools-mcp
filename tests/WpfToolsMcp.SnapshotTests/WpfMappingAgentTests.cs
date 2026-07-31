using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using WpfToolsMcp.Agent;
using WpfToolsMcp.Contracts;
using ContractRect = WpfToolsMcp.Contracts.Rect;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[Category("Wpf")]
[Apartment(ApartmentState.STA)]
[NonParallelizable]
public sealed class WpfMappingAgentTests
{
    [Test]
    public void Unique_peer_identity_maps_to_a_reusable_wpf_element()
    {
        var button = CreateButton("SaveButton", "Save");
        var window = CreateWindow(button);
        var ownerId = $"mapping-exact-{Guid.NewGuid():N}";

        try
        {
            ShowAndLayout(window);

            var response = WpfVisualTreeInspector.MapUiaToWpf(
                ownerId,
                new MapUiaToWpfAgentRequest(
                    GetWindowHandle(window),
                    SourceFor(button, "Button", "SaveButton", "Save", "Button"),
                    MaxNodes: 100),
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(response.Mapping.Available, Is.True);
                Assert.That(response.Mapping.Status, Is.EqualTo(ElementMappingStatus.Exact));
                Assert.That(response.Mapping.ScanComplete, Is.True);
                Assert.That(response.Mapping.Method, Is.EqualTo("automationPeerScoredWindowScan"));
                Assert.That(response.SelectedElement, Is.Not.Null);
                Assert.That(response.SelectedElement!.ElementIdWpf, Does.StartWith("wpfobj_"));
                Assert.That(response.SelectedElement.AutomationId, Is.EqualTo("SaveButton"));
                Assert.That(response.Mapping.Candidates.All(candidate => candidate.Element.ElementIdWpf is null), Is.True);
                Assert.That(response.Mapping.Evidence, Does.Contain("unique_exact_automation_id_and_control_type"));
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Tied_peer_identity_is_reported_as_ambiguous_without_registering_a_handle()
    {
        var first = CreateButton("DuplicateButton", "Same");
        var second = CreateButton("DuplicateButton", "Same");
        var grid = new Grid();
        grid.Children.Add(first);
        grid.Children.Add(second);
        var window = CreateWindow(grid);
        var ownerId = $"mapping-tied-{Guid.NewGuid():N}";

        try
        {
            ShowAndLayout(window);

            var response = WpfVisualTreeInspector.MapUiaToWpf(
                ownerId,
                new MapUiaToWpfAgentRequest(
                    GetWindowHandle(window),
                    SourceFor(first, "Button", "DuplicateButton", "Same", "Button"),
                    MaxNodes: 100),
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(response.Mapping.Status, Is.EqualTo(ElementMappingStatus.Ambiguous));
                Assert.That(response.Mapping.ScoreLead, Is.Zero);
                Assert.That(
                    response.Mapping.Candidates.Count(candidate => candidate.Element.AutomationId == "DuplicateButton"),
                    Is.EqualTo(2));
                Assert.That(response.SelectedElement, Is.Null);
                Assert.That(response.Mapping.Candidates.All(candidate => candidate.Element.ElementIdWpf is null), Is.True);
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Incomplete_scan_never_selects_a_candidate()
    {
        var button = CreateButton("TargetButton", "Target");
        var window = CreateWindow(button);
        var ownerId = $"mapping-budget-{Guid.NewGuid():N}";

        try
        {
            ShowAndLayout(window);

            var response = WpfVisualTreeInspector.MapUiaToWpf(
                ownerId,
                new MapUiaToWpfAgentRequest(
                    GetWindowHandle(window),
                    SourceFor(button, "Button", "TargetButton", "Target", "Button"),
                    MaxNodes: 1),
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(response.Mapping.Status, Is.EqualTo(ElementMappingStatus.Ambiguous));
                Assert.That(response.Mapping.ScanComplete, Is.False);
                Assert.That(response.Mapping.Truncated, Is.True);
                Assert.That(response.Mapping.TruncatedReason, Is.EqualTo("maxNodes"));
                Assert.That(response.Mapping.ScannedNodes, Is.EqualTo(1));
                Assert.That(response.SelectedElement, Is.Null);
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Complete_scan_without_a_relevant_peer_is_unmapped()
    {
        var button = CreateButton("ActualButton", "Actual");
        var window = CreateWindow(button);
        var ownerId = $"mapping-unmapped-{Guid.NewGuid():N}";

        try
        {
            ShowAndLayout(window);

            var response = WpfVisualTreeInspector.MapUiaToWpf(
                ownerId,
                new MapUiaToWpfAgentRequest(
                    GetWindowHandle(window),
                    new UiaMappingSource(
                        "Unknown",
                        "MissingButton",
                        "Missing",
                        "MissingClass",
                        new ContractRect(1_000_000, 1_000_000, 1, 1)),
                    MaxNodes: 100),
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(response.Mapping.Status, Is.EqualTo(ElementMappingStatus.Unmapped));
                Assert.That(response.Mapping.ScanComplete, Is.True);
                Assert.That(response.Mapping.TotalCandidates, Is.Zero);
                Assert.That(response.SelectedElement, Is.Null);
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Throwing_peer_creation_marks_the_scan_incomplete_and_prevents_selection()
    {
        var button = CreateButton("TargetButton", "Target");
        var panel = new StackPanel();
        panel.Children.Add(new ThrowingAutomationPeerButton());
        panel.Children.Add(button);
        var window = CreateWindow(panel);
        var ownerId = $"mapping-peer-failure-{Guid.NewGuid():N}";

        try
        {
            ShowAndLayout(window);

            var response = WpfVisualTreeInspector.MapUiaToWpf(
                ownerId,
                new MapUiaToWpfAgentRequest(
                    GetWindowHandle(window),
                    SourceFor(button, "Button", "TargetButton", "Target", "Button"),
                    MaxNodes: 100),
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(response.Mapping.Status, Is.EqualTo(ElementMappingStatus.Ambiguous));
                Assert.That(response.Mapping.ScanComplete, Is.False);
                Assert.That(response.Mapping.Truncated, Is.True);
                Assert.That(response.Mapping.TruncatedReason, Is.EqualTo("automationPeerCreationFailed"));
                Assert.That(response.Mapping.Evidence, Does.Contain("scan_incomplete"));
                Assert.That(response.SelectedElement, Is.Null);
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    private static Button CreateButton(string automationId, string content)
    {
        var button = new Button
        {
            Content = content,
            Width = 140,
            Height = 40
        };
        AutomationProperties.SetAutomationId(button, automationId);
        AutomationProperties.SetName(button, content);
        return button;
    }

    private static UiaMappingSource SourceFor(
        FrameworkElement element,
        string controlType,
        string automationId,
        string name,
        string className)
    {
        var origin = element.PointToScreen(new Point(0, 0));
        return new UiaMappingSource(
            controlType,
            automationId,
            name,
            className,
            new ContractRect(
                (int)Math.Round(origin.X),
                (int)Math.Round(origin.Y),
                (int)Math.Round(element.ActualWidth),
                (int)Math.Round(element.ActualHeight)));
    }

    private static Window CreateWindow(UIElement content) =>
        new()
        {
            Title = "WPF mapping unit test",
            Width = 300,
            Height = 180,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
            Left = -10_000,
            Top = -10_000,
            Content = content
        };

    private static void ShowAndLayout(Window window)
    {
        window.Show();
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
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

    private sealed class ThrowingAutomationPeerButton : Button
    {
        protected override AutomationPeer OnCreateAutomationPeer() =>
            throw new InvalidOperationException("Test peer creation failure.");
    }
}

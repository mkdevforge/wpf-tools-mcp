using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;
using WpfToolsMcp.Agent;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[Category("Wpf")]
[Apartment(ApartmentState.STA)]
[NonParallelizable]
public sealed class InspectionResponseMetadataWpfTests
{
    [Test]
    public void Binding_info_evaluates_eligibility_before_applying_the_result_cap()
    {
        var ownerId = $"binding-info-metadata-{Guid.NewGuid():N}";
        var target = new TextBox { Name = "BindingInfoTarget", Width = 120, Height = 24 };
        AutomationProperties.SetAutomationId(target, "BindingInfoTarget");
        target.SetBinding(TextBox.TextProperty, new Binding(nameof(BindingModel.Text)) { Source = new BindingModel() });
        target.SetBinding(FrameworkElement.TagProperty, new Binding(nameof(BindingModel.Tag)) { Source = new BindingModel() });
        var window = CreateWindow(target);

        try
        {
            ShowAndLayout(window);
            var exact = InspectBindingInfo(window, ownerId, maxProperties: 2);
            var bounded = InspectBindingInfo(window, ownerId, maxProperties: 1);

            Assert.Multiple(() =>
            {
                Assert.That(exact.WindowHandleUsed, Is.EqualTo(GetWindowHandle(window)));
                Assert.That(exact.ReturnedBindings, Is.EqualTo(2));
                Assert.That(exact.DiscoveredBindings, Is.EqualTo(2));
                Assert.That(exact.ScannedProperties, Is.GreaterThan(2));
                Assert.That(exact.ScanComplete, Is.True);
                Assert.That(exact.Truncated, Is.False);
                Assert.That(exact.TruncatedReasons, Is.Null);
                Assert.That(bounded.ReturnedBindings, Is.EqualTo(1));
                Assert.That(bounded.DiscoveredBindings, Is.EqualTo(2));
                Assert.That(bounded.ScanComplete, Is.True);
                Assert.That(bounded.TruncatedReason, Is.EqualTo("maxProperties"));
                Assert.That(bounded.TruncatedReasons, Is.EqualTo(new[] { "maxProperties" }));
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Binding_errors_distinguish_exact_fit_from_omitted_evidence()
    {
        var ownerId = $"binding-errors-metadata-{Guid.NewGuid():N}";
        var first = CreateInvalidBoundTextBox("BindingErrorFirst");
        var second = CreateInvalidBoundTextBox("BindingErrorSecond");
        var panel = new StackPanel();
        panel.Children.Add(first);
        panel.Children.Add(second);
        var window = CreateWindow(panel);

        try
        {
            ShowAndLayout(window);
            MarkBindingInvalid(first);
            MarkBindingInvalid(second);

            var exact = InspectBindingErrors(window, ownerId, maxErrors: 2);
            var bounded = InspectBindingErrors(window, ownerId, maxErrors: 1);
            var nodeBounded = WpfVisualTreeInspector.GetBindingErrors(
                ownerId,
                new GetBindingErrorsRequest(
                    WindowHandle: GetWindowHandle(window),
                    Depth: 10,
                    MaxErrors: 1,
                    MaxNodes: 1),
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(exact.WindowHandleUsed, Is.EqualTo(GetWindowHandle(window)));
                Assert.That(exact.ReturnedErrors, Is.EqualTo(2));
                Assert.That(exact.DiscoveredErrors, Is.EqualTo(2));
                Assert.That(exact.ScanComplete, Is.True);
                Assert.That(exact.Truncated, Is.False);
                Assert.That(bounded.ReturnedErrors, Is.EqualTo(1));
                Assert.That(bounded.DiscoveredErrors, Is.EqualTo(2));
                Assert.That(bounded.ScanComplete, Is.True);
                Assert.That(bounded.TruncatedReasons, Is.EqualTo(new[] { "maxErrors" }));
                Assert.That(nodeBounded.ScannedNodes, Is.EqualTo(1));
                Assert.That(nodeBounded.ScanComplete, Is.False);
                Assert.That(nodeBounded.TruncatedReasons, Does.Contain("maxNodes"));
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Computed_properties_count_resolved_properties_not_requested_names()
    {
        var ownerId = $"computed-metadata-{Guid.NewGuid():N}";
        var target = new Button { Name = "ComputedTarget", Width = 120, Height = 30 };
        AutomationProperties.SetAutomationId(target, "ComputedTarget");
        var window = CreateWindow(target);

        try
        {
            ShowAndLayout(window);
            var exact = InspectComputed(window, ownerId, ["MissingProperty", "Width"], maxProperties: 1);
            var bounded = InspectComputed(window, ownerId, ["MissingProperty", "Width", "Height"], maxProperties: 1);
            var independentlyBoundedNames = Enumerable.Repeat("Width", 101).ToArray();
            independentlyBoundedNames[0] = new string('x', 600);
            var independentlyBounded = InspectComputed(
                window,
                ownerId,
                independentlyBoundedNames,
                maxProperties: 1,
                includeProvenance: true);

            Assert.Multiple(() =>
            {
                Assert.That(exact.WindowHandleUsed, Is.EqualTo(GetWindowHandle(window)));
                Assert.That(exact.ReturnedProperties, Is.EqualTo(1));
                Assert.That(exact.DiscoveredProperties, Is.EqualTo(1));
                Assert.That(exact.ScannedProperties, Is.EqualTo(2));
                Assert.That(exact.ScanComplete, Is.True);
                Assert.That(exact.Truncated, Is.False);
                Assert.That(exact.MissingPropertyNames, Is.EqualTo(new[] { "MissingProperty" }));
                Assert.That(bounded.ReturnedProperties, Is.EqualTo(1));
                Assert.That(bounded.DiscoveredProperties, Is.EqualTo(2));
                Assert.That(bounded.ScannedProperties, Is.EqualTo(3));
                Assert.That(bounded.TruncatedReasons, Is.EqualTo(new[] { "maxProperties" }));
                Assert.That(
                    independentlyBounded.TruncatedReasons!.Take(2),
                    Is.EqualTo(new[]
                    {
                        "maxProvenancePropertyNames",
                        "maxProvenancePropertyNameLength"
                    }));
                Assert.That(independentlyBounded.ScanComplete, Is.False);
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Coverage_reports_returned_and_discovered_evidence_independently()
    {
        var ownerId = $"coverage-metadata-{Guid.NewGuid():N}";
        var panel = new StackPanel();
        panel.Children.Add(new Button { Width = 80, Height = 24 });
        panel.Children.Add(new Button { Width = 80, Height = 24 });
        var window = CreateWindow(panel);

        try
        {
            ShowAndLayout(window);
            var full = InspectCoverage(window, ownerId, maxFindings: 100, maxNodes: 100);
            Assert.That(full.Summary.DiscoveredFindings, Is.GreaterThanOrEqualTo(2));

            var exact = InspectCoverage(
                window,
                ownerId,
                maxFindings: full.Summary.DiscoveredFindings,
                maxNodes: 100);
            var bounded = InspectCoverage(
                window,
                ownerId,
                maxFindings: full.Summary.DiscoveredFindings - 1,
                maxNodes: 100);
            var nodeBounded = InspectCoverage(window, ownerId, maxFindings: 1, maxNodes: 1);

            Assert.Multiple(() =>
            {
                Assert.That(exact.WindowHandleUsed, Is.EqualTo(GetWindowHandle(window)));
                Assert.That(exact.Summary.FindingsCount, Is.EqualTo(exact.Findings.Count));
                Assert.That(exact.Summary.ReturnedFindings, Is.EqualTo(exact.Findings.Count));
                Assert.That(exact.Summary.DiscoveredFindings, Is.EqualTo(exact.Findings.Count));
                Assert.That(exact.Summary.ScanComplete, Is.True);
                Assert.That(exact.Summary.Truncated, Is.False);
                Assert.That(exact.Summary.IssueCounts, Is.EqualTo(exact.Summary.DiscoveredIssueCounts));
                Assert.That(bounded.Summary.ReturnedFindings, Is.EqualTo(bounded.Findings.Count));
                Assert.That(bounded.Summary.DiscoveredFindings, Is.EqualTo(full.Summary.DiscoveredFindings));
                Assert.That(bounded.Summary.TruncatedReasons, Does.Contain("maxFindings"));
                Assert.That(nodeBounded.Summary.ScannedNodes, Is.EqualTo(1));
                Assert.That(nodeBounded.Summary.ScanComplete, Is.False);
                Assert.That(nodeBounded.Summary.TruncatedReasons, Does.Contain("maxNodes"));
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Data_context_reports_each_applied_budget_and_keeps_strings_within_the_limit()
    {
        var ownerId = $"data-context-metadata-{Guid.NewGuid():N}";
        var target = new Border { Name = "DataContextTarget", Width = 100, Height = 30 };
        AutomationProperties.SetAutomationId(target, "DataContextTarget");
        target.DataContext = new DataContextModel { AFirst = "short" };
        var boundedModel = new DataContextModel
        {
            AFirst = "abc\U0001F600xyz",
            BSecond = "second",
            CChild = new DataContextChild { Value = "child" }
        };
        var window = CreateWindow(target);

        try
        {
            ShowAndLayout(window);
            var exact = InspectDataContext(
                window,
                ownerId,
                maxDepth: 2,
                maxPropertiesPerObject: 1,
                maxStringLength: 100);
            target.DataContext = boundedModel;
            var bounded = InspectDataContext(
                window,
                ownerId,
                maxDepth: 1,
                maxPropertiesPerObject: 1,
                maxStringLength: 7);
            var zeroLength = InspectDataContext(
                window,
                ownerId,
                maxDepth: 2,
                maxPropertiesPerObject: 10,
                maxStringLength: 0);
            var depthBounded = InspectDataContext(
                window,
                ownerId,
                maxDepth: 0,
                maxPropertiesPerObject: 10,
                maxStringLength: 100);

            var boundedFirst = bounded.Data!.AsObject()[nameof(DataContextModel.AFirst)]!.GetValue<string>();
            var zeroFirst = zeroLength.Data!.AsObject()[nameof(DataContextModel.AFirst)]!.GetValue<string>();

            Assert.Multiple(() =>
            {
                Assert.That(exact.Data!.AsObject(), Has.Count.EqualTo(1));
                Assert.That(exact.Truncated, Is.False);
                Assert.That(exact.TruncatedReasons, Is.Null);
                Assert.That(bounded.WindowHandleUsed, Is.EqualTo(GetWindowHandle(window)));
                Assert.That(bounded.Element.AutomationId, Is.EqualTo("DataContextTarget"));
                Assert.That(bounded.TruncatedReasons, Is.EqualTo(new[]
                {
                    "maxPropertiesPerObject",
                    "maxStringLength"
                }));
                Assert.That(boundedFirst.Length, Is.LessThanOrEqualTo(7));
                Assert.That(IsWellFormedUtf16(boundedFirst), Is.True);
                Assert.That(zeroLength.Summary, Is.Empty);
                Assert.That(zeroFirst, Is.Empty);
                Assert.That(zeroLength.TruncatedReasons, Does.Contain("maxStringLength"));
                Assert.That(depthBounded.TruncatedReasons, Is.EqualTo(new[] { "maxDepth" }));
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [TestCase(DataContextMode.Full)]
    [TestCase(DataContextMode.Summary)]
    public void Data_context_formatting_failures_return_bounded_type_fallbacks_and_warnings(
        DataContextMode mode)
    {
        const int maxStringLength = 32;
        var ownerId = $"data-context-formatting-{mode}-{Guid.NewGuid():N}";
        var dataContext = new ThrowingDataContextDictionary
        {
            [new ThrowingDataContextKey()] = "payload"
        };
        var target = new Border
        {
            Name = "DataContextTarget",
            Width = 100,
            Height = 30,
            DataContext = dataContext
        };
        AutomationProperties.SetAutomationId(target, "DataContextTarget");
        var window = CreateWindow(target);

        try
        {
            ShowAndLayout(window);
            var response = InspectDataContext(
                window,
                ownerId,
                maxDepth: 2,
                maxPropertiesPerObject: 10,
                maxStringLength: maxStringLength,
                mode: mode);
            var dictionaryEntry = response.Data!.AsObject().Single();
            var dataContextType = typeof(ThrowingDataContextDictionary).FullName!;
            var keyType = typeof(ThrowingDataContextKey).FullName!;

            Assert.Multiple(() =>
            {
                Assert.That(response.Summary, Has.Length.EqualTo(maxStringLength));
                Assert.That(response.Summary, Does.EndWith("..."));
                Assert.That(dataContextType, Does.StartWith(response.Summary![..^3]));
                Assert.That(dictionaryEntry.Key, Has.Length.EqualTo(maxStringLength));
                Assert.That(dictionaryEntry.Key, Does.EndWith("..."));
                Assert.That(keyType, Does.StartWith(dictionaryEntry.Key[..^3]));
                Assert.That(dictionaryEntry.Value!.GetValue<string>(), Is.EqualTo("payload"));
                Assert.That(response.Truncated, Is.True);
                Assert.That(response.TruncatedReasons, Is.EqualTo(new[] { "maxStringLength" }));
                Assert.That(
                    response.Warnings,
                    Is.EqualTo(new[]
                    {
                        "DataContext.ToString: InvalidOperationException",
                        "DataContext dictionary key ToString: InvalidOperationException"
                    }));
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Data_context_property_paths_cache_shared_getters_and_report_unresolved_paths()
    {
        var ownerId = $"data-context-paths-{Guid.NewGuid():N}";
        var model = new CountingDataContextModel();
        var target = new Border
        {
            Name = "DataContextTarget",
            Width = 100,
            Height = 30,
            DataContext = model
        };
        AutomationProperties.SetAutomationId(target, "DataContextTarget");
        var window = CreateWindow(target);

        try
        {
            ShowAndLayout(window);
            var response = WpfVisualTreeInspector.GetDataContext(
                ownerId,
                new GetDataContextRequest(
                    WindowHandle: GetWindowHandle(window),
                    Locator: new ElementLocator(AutomationId: "DataContextTarget"),
                    Mode: DataContextMode.Summary,
                    MaxDepth: 2,
                    PropertyPaths: ["Current.Kind", "Current.Key", "Absent.Kind", "Unknown.Value"]),
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(model.CurrentReads, Is.EqualTo(1));
                Assert.That(response.Data?["Current"]?["Kind"]?.GetValue<string>(), Is.EqualTo("Primary"));
                Assert.That(response.Data?["Current"]?["Key"]?.GetValue<string>(), Is.EqualTo("item-7"));
                Assert.That(response.Warnings, Has.Some.StartsWith("property_path_null:"));
                Assert.That(response.Warnings, Has.Some.StartsWith("property_path_not_found:"));
            });

            var tooManyPaths = Enumerable.Range(0, DataContextPropertyPathLimits.MaxPaths + 1)
                .Select(index => $"Property{index}")
                .ToArray();
            var exception = Assert.Throws<ArgumentException>(() => WpfVisualTreeInspector.GetDataContext(
                ownerId,
                new GetDataContextRequest(
                    WindowHandle: GetWindowHandle(window),
                    Locator: new ElementLocator(AutomationId: "DataContextTarget"),
                    PropertyPaths: tooManyPaths),
                CancellationToken.None));
            Assert.That(exception!.Message, Does.Contain("supports at most 32 paths"));
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Style_and_template_nested_collections_report_one_item_lookahead()
    {
        const string templateXaml = """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             TargetType="{x:Type Button}">
                <StackPanel>
                    <Border x:Name="FirstPart" />
                    <Border x:Name="SecondPart" />
                </StackPanel>
            </ControlTemplate>
            """;
        var ownerId = $"nested-metadata-{Guid.NewGuid():N}";
        var baseStyle = new Style(typeof(Button));
        var middleStyle = new Style(typeof(Button), baseStyle);
        var target = new Button
        {
            Name = "NestedMetadataTarget",
            Width = 100,
            Height = 30,
            Style = new Style(typeof(Button), middleStyle),
            Template = (ControlTemplate)XamlReader.Parse(templateXaml)
        };
        AutomationProperties.SetAutomationId(target, "NestedMetadataTarget");
        var window = CreateWindow(target);

        try
        {
            ShowAndLayout(window);
            var styleExact = InspectStyle(window, ownerId, maxBasedOnDepth: 2).Styles.Single();
            var styleBounded = InspectStyle(window, ownerId, maxBasedOnDepth: 1).Styles.Single();
            var templateExact = InspectTemplate(window, ownerId, maxNamedElements: 2).Template;
            var templateBounded = InspectTemplate(window, ownerId, maxNamedElements: 1).Template;

            Assert.Multiple(() =>
            {
                Assert.That(styleExact.ReturnedBasedOnStyles, Is.EqualTo(2));
                Assert.That(styleExact.ReturnedBasedOnStyles, Is.EqualTo(styleExact.BasedOnChainTargetTypes.Count));
                Assert.That(styleExact.DiscoveredBasedOnStyles, Is.EqualTo(2));
                Assert.That(styleExact.BasedOnScanComplete, Is.True);
                Assert.That(styleExact.BasedOnTruncated, Is.False);
                Assert.That(styleExact.MaxBasedOnDepth, Is.EqualTo(2));
                Assert.That(styleBounded.ReturnedBasedOnStyles, Is.EqualTo(1));
                Assert.That(styleBounded.DiscoveredBasedOnStyles, Is.EqualTo(2));
                Assert.That(styleBounded.BasedOnScanComplete, Is.False);
                Assert.That(styleBounded.BasedOnTruncated, Is.True);
                Assert.That(templateExact.ReturnedNamedElements, Is.EqualTo(2));
                Assert.That(templateExact.ReturnedNamedElements, Is.EqualTo(templateExact.NamedElements!.Count));
                Assert.That(templateExact.DiscoveredNamedElements, Is.EqualTo(2));
                Assert.That(templateExact.NamedElementsScanComplete, Is.True);
                Assert.That(templateExact.NamedElementsTruncated, Is.False);
                Assert.That(templateBounded.ReturnedNamedElements, Is.EqualTo(1));
                Assert.That(templateBounded.DiscoveredNamedElements, Is.EqualTo(2));
                Assert.That(templateBounded.NamedElementsScanComplete, Is.False);
                Assert.That(templateBounded.NamedElementsTruncated, Is.True);
                Assert.That(templateBounded.MaxNamedElements, Is.EqualTo(1));
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Template_metadata_reports_incomplete_when_template_inspection_fails()
    {
        var ownerId = $"template-failure-metadata-{Guid.NewGuid():N}";
        var target = new ThrowingTemplateFrameworkElement { Width = 100, Height = 30 };
        AutomationProperties.SetAutomationId(target, "TemplateFailureTarget");
        var window = CreateWindow(target);

        try
        {
            ShowAndLayout(window);
            var response = WpfVisualTreeInspector.GetTemplateInfo(
                ownerId,
                new GetTemplateInfoRequest(
                    WindowHandle: GetWindowHandle(window),
                    Locator: new ElementLocator(AutomationId: "TemplateFailureTarget"),
                    IncludeNamedElements: true,
                    MaxNamedElements: 1),
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(response.Template.ReturnedNamedElements, Is.Zero);
                Assert.That(response.Template.DiscoveredNamedElements, Is.Zero);
                Assert.That(response.Template.NamedElementsScanComplete, Is.False);
                Assert.That(response.Template.NamedElementsTruncated, Is.False);
                Assert.That(response.Warnings, Has.Some.StartsWith("template_error:"));
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    private static GetBindingInfoResponse InspectBindingInfo(Window window, string ownerId, int maxProperties) =>
        WpfVisualTreeInspector.GetBindingInfo(
            ownerId,
            new GetBindingInfoRequest(
                WindowHandle: GetWindowHandle(window),
                Locator: new ElementLocator(AutomationId: "BindingInfoTarget"),
                MaxProperties: maxProperties),
            CancellationToken.None);

    private static GetBindingErrorsResponse InspectBindingErrors(Window window, string ownerId, int maxErrors) =>
        WpfVisualTreeInspector.GetBindingErrors(
            ownerId,
            new GetBindingErrorsRequest(
                WindowHandle: GetWindowHandle(window),
                Depth: 10,
                MaxErrors: maxErrors,
                MaxNodes: 100),
            CancellationToken.None);

    private static GetComputedPropertiesResponse InspectComputed(
        Window window,
        string ownerId,
        IReadOnlyList<string> propertyNames,
        int maxProperties,
        bool includeProvenance = false) =>
        WpfVisualTreeInspector.GetComputedProperties(
            ownerId,
            new GetComputedPropertiesRequest(
                WindowHandle: GetWindowHandle(window),
                Locator: new ElementLocator(AutomationId: "ComputedTarget"),
                PropertyNames: propertyNames,
                MaxProperties: maxProperties,
                IncludeProvenance: includeProvenance),
            CancellationToken.None);

    private static GetUiaCoverageReportResponse InspectCoverage(
        Window window,
        string ownerId,
        int maxFindings,
        int maxNodes) =>
        WpfVisualTreeInspector.GetUiaCoverageReport(
            ownerId,
            new GetUiaCoverageReportRequest(
                WindowHandle: GetWindowHandle(window),
                VisibleOnly: false,
                InteractiveOnly: false,
                MaxNodes: maxNodes,
                MaxFindings: maxFindings),
            CancellationToken.None);

    private static GetDataContextResponse InspectDataContext(
        Window window,
        string ownerId,
        int maxDepth,
        int maxPropertiesPerObject,
        int maxStringLength,
        DataContextMode mode = DataContextMode.Full) =>
        WpfVisualTreeInspector.GetDataContext(
            ownerId,
            new GetDataContextRequest(
                WindowHandle: GetWindowHandle(window),
                Locator: new ElementLocator(AutomationId: "DataContextTarget"),
                Mode: mode,
                MaxDepth: maxDepth,
                MaxPropertiesPerObject: maxPropertiesPerObject,
                MaxStringLength: maxStringLength),
            CancellationToken.None);

    private static GetStyleChainResponse InspectStyle(Window window, string ownerId, int maxBasedOnDepth) =>
        WpfVisualTreeInspector.GetStyleChain(
            ownerId,
            new GetStyleChainRequest(
                WindowHandle: GetWindowHandle(window),
                Locator: new ElementLocator(AutomationId: "NestedMetadataTarget"),
                IncludeThemeStyle: false,
                MaxBasedOnDepth: maxBasedOnDepth),
            CancellationToken.None);

    private static GetTemplateInfoResponse InspectTemplate(Window window, string ownerId, int maxNamedElements) =>
        WpfVisualTreeInspector.GetTemplateInfo(
            ownerId,
            new GetTemplateInfoRequest(
                WindowHandle: GetWindowHandle(window),
                Locator: new ElementLocator(AutomationId: "NestedMetadataTarget"),
                IncludeNamedElements: true,
                MaxNamedElements: maxNamedElements),
            CancellationToken.None);

    private static TextBox CreateInvalidBoundTextBox(string automationId)
    {
        var target = new TextBox { Width = 120, Height = 24 };
        AutomationProperties.SetAutomationId(target, automationId);
        target.SetBinding(TextBox.TextProperty, new Binding(nameof(BindingModel.Text)) { Source = new BindingModel() });
        return target;
    }

    private static void MarkBindingInvalid(TextBox target)
    {
        var expression = target.GetBindingExpression(TextBox.TextProperty)
            ?? throw new AssertionException("Expected an active Text binding.");
        Validation.MarkInvalid(
            expression,
            new ValidationError(new FixtureValidationRule(), expression, "invalid", null));
    }

    private static Window CreateWindow(UIElement content) =>
        new()
        {
            Title = "Inspection response metadata WPF test",
            Width = 320,
            Height = 200,
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
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }

    private static long GetWindowHandle(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle.ToInt64();
        Assert.That(handle, Is.Not.Zero);
        return handle;
    }

    private static bool IsWellFormedUtf16(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[++index]))
                {
                    return false;
                }
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static void CloseAndRelease(Window window, string ownerId)
    {
        WpfVisualTreeInspector.ReleaseOwnerResources(ownerId);
        if (window.IsVisible)
        {
            window.Close();
        }
    }

    private sealed class BindingModel
    {
        public string Text { get; set; } = "text";

        public string Tag { get; set; } = "tag";
    }

    private sealed class DataContextModel
    {
        public string AFirst { get; set; } = string.Empty;

        public string? BSecond { get; set; }

        public DataContextChild? CChild { get; set; }

        public override string ToString() => AFirst;
    }

    private sealed class DataContextChild
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class CountingDataContextModel
    {
        private readonly CountingDataContextChild _current = new();

        public int CurrentReads { get; private set; }

        public CountingDataContextChild Current
        {
            get
            {
                CurrentReads++;
                return _current;
            }
        }

        public CountingDataContextChild? Absent => null;
    }

    private sealed class CountingDataContextChild
    {
        public string Kind => "Primary";

        public string Key => "item-7";
    }

    private sealed class ThrowingDataContextDictionary : Dictionary<object, string>
    {
        public override string ToString() =>
            throw new InvalidOperationException("Synthetic DataContext formatting failure.");
    }

    private sealed class ThrowingDataContextKey
    {
        public override string ToString() =>
            throw new InvalidOperationException("Synthetic dictionary key formatting failure.");
    }

    private sealed class ThrowingTemplateFrameworkElement : FrameworkElement
    {
        private FrameworkTemplate? TemplateInternal =>
            throw new InvalidOperationException("Synthetic template inspection failure.");
    }

    private sealed class FixtureValidationRule : ValidationRule
    {
        public override ValidationResult Validate(object value, CultureInfo cultureInfo) =>
            ValidationResult.ValidResult;
    }
}

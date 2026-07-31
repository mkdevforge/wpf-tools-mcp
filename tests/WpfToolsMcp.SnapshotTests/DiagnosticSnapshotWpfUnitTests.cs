using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Threading;
using WpfToolsMcp.Agent;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[Category("Wpf")]
[Apartment(ApartmentState.STA)]
public sealed class DiagnosticSnapshotWpfUnitTests
{
    private static readonly JsonSerializerOptions EvidenceJsonOptions =
        new(JsonSerializerDefaults.Web);

    [Test]
    public void Wpf_sections_capture_one_complete_dispatcher_generation()
    {
        const string targetName = "DiagnosticSnapshotTarget";
        var ownerId = $"diagnostic-snapshot-unit-{Guid.NewGuid():N}";
        var target = new Border
        {
            Name = targetName,
            Width = 120,
            Height = 40
        };
        ApplyGeneration(target, generation: 1);

        var window = new Window
        {
            Title = "Diagnostic snapshot WPF unit test",
            Width = 240,
            Height = 140,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
            Left = -10_000,
            Top = -10_000,
            Content = target
        };

        try
        {
            window.Show();
            window.UpdateLayout();
            var windowHandle = new WindowInteropHelper(window).Handle.ToInt64();
            Assert.That(windowHandle, Is.Not.Zero);

            var request = new CaptureWpfDiagnosticSnapshotRequest(
                WindowHandle: windowHandle,
                Locator: new ElementLocator(Name: targetName),
                ElementId: null,
                RootXPath: "/Window",
                Sections:
                [
                    DiagnosticSection.DataContext,
                    DiagnosticSection.WpfProperties
                ],
                Budget: new DiagnosticSnapshotBudget(),
                PropertyNames: ["Tag"],
                DataContextProperties: ["Generation", "Label"]);

            var generationOne = WpfVisualTreeInspector.CaptureDiagnosticSnapshot(
                ownerId,
                request,
                CancellationToken.None);

            ApplyQueuedGeneration(window.Dispatcher, target, generation: 2);

            var generationTwo = WpfVisualTreeInspector.CaptureDiagnosticSnapshot(
                ownerId,
                request,
                CancellationToken.None);

            AssertCompleteGeneration(generationOne, expectedGeneration: 1);
            AssertCompleteGeneration(generationTwo, expectedGeneration: 2);

            Assert.Multiple(() =>
            {
                Assert.That(generationOne.Target.Type, Is.EqualTo(generationTwo.Target.Type));
                Assert.That(generationOne.Target.Name, Is.EqualTo(targetName));
                Assert.That(generationTwo.Target.Name, Is.EqualTo(targetName));
                Assert.That(generationOne.Target.XPath, Is.EqualTo(generationTwo.Target.XPath));
                Assert.That(
                    generationOne.Sections.Select(section => section.CaptureGroup)
                        .Concat(generationTwo.Sections.Select(section => section.CaptureGroup))
                        .Distinct(StringComparer.Ordinal),
                    Has.Exactly(1).Items);
            });
        }
        finally
        {
            WpfVisualTreeInspector.ReleaseOwnerResources(ownerId);
            if (window.IsVisible)
            {
                window.Close();
            }
        }
    }

    [Test]
    public void Wpf_section_list_is_validated_before_resolving_the_target()
    {
        Assert.That(
            () => CaptureWithSections(null!),
            NUnit.Framework.Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(
            () => CaptureWithSections([]),
            NUnit.Framework.Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(
            () => CaptureWithSections([DiagnosticSection.VisualTree, DiagnosticSection.VisualTree]),
            NUnit.Framework.Throws.TypeOf<ArgumentException>());
        Assert.That(
            () => CaptureWithSections([DiagnosticSection.UiaProperties]),
            NUnit.Framework.Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(
            () => CaptureWithSections([DiagnosticSection.Screenshot]),
            NUnit.Framework.Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(
            () => CaptureWithSections([(DiagnosticSection)int.MaxValue]),
            NUnit.Framework.Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void Oversized_section_does_not_starve_a_later_section()
    {
        const string targetName = "DiagnosticPayloadBudgetTarget";
        var ownerId = $"diagnostic-payload-unit-{Guid.NewGuid():N}";
        var target = new Border
        {
            Name = targetName,
            Tag = new string('x', 1_800),
            DataContext = new GenerationTuple(7, "small")
        };
        var window = CreateTestWindow(target, "Diagnostic payload budget unit test");

        try
        {
            ShowAndLayout(window);
            var response = WpfVisualTreeInspector.CaptureDiagnosticSnapshot(
                ownerId,
                CreateRequest(
                    window,
                    targetName,
                    sections: [DiagnosticSection.WpfProperties, DiagnosticSection.DataContext],
                    budget: new DiagnosticSnapshotBudget(
                        MaxValueLength: DiagnosticSnapshotLimits.MaxValueLength,
                        MaxPayloadChars: DiagnosticSnapshotLimits.MinPayloadChars),
                    propertyNames: ["Tag"],
                    dataContextProperties: ["Generation"]),
                CancellationToken.None);

            var properties = response.Sections[0];
            var dataContext = response.Sections[1];
            Assert.Multiple(() =>
            {
                Assert.That(
                    response.Sections.Select(section => section.Section),
                    Is.EqualTo(new[] { DiagnosticSection.WpfProperties, DiagnosticSection.DataContext }));
                Assert.That(properties.Status, Is.EqualTo(DiagnosticSectionStatus.Truncated));
                Assert.That(properties.Code, Is.EqualTo("maxPayloadChars"));
                Assert.That(properties.Data, Is.Null);
                Assert.That(dataContext.Status, Is.EqualTo(DiagnosticSectionStatus.Success));
                Assert.That(dataContext.Data, Is.Not.Null);
                Assert.That(dataContext.PayloadChars, Is.GreaterThan(0));
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Agent_keeps_individually_bounded_sections_for_server_side_global_ordering()
    {
        const string targetName = "DiagnosticCrossPhaseBudgetTarget";
        var ownerId = $"diagnostic-cross-phase-budget-{Guid.NewGuid():N}";
        var target = new Border
        {
            Name = targetName,
            Tag = new string('t', 500),
            DataContext = new GenerationTuple(9, new string('d', 500))
        };
        var window = CreateTestWindow(target, "Diagnostic cross-phase budget unit test");

        try
        {
            ShowAndLayout(window);
            var baseline = WpfVisualTreeInspector.CaptureDiagnosticSnapshot(
                ownerId,
                CreateRequest(
                    window,
                    targetName,
                    sections: [DiagnosticSection.WpfProperties, DiagnosticSection.DataContext],
                    budget: new DiagnosticSnapshotBudget(MaxPayloadChars: DiagnosticSnapshotLimits.MaxPayloadChars),
                    propertyNames: ["Tag"],
                    dataContextProperties: ["Label"]),
                CancellationToken.None);
            var perSectionCap = baseline.Sections.Max(section => section.PayloadChars) + 10;
            Assert.That(
                baseline.Sections.Sum(section => section.PayloadChars),
                Is.GreaterThan(perSectionCap));

            var bounded = WpfVisualTreeInspector.CaptureDiagnosticSnapshot(
                ownerId,
                CreateRequest(
                    window,
                    targetName,
                    sections: [DiagnosticSection.WpfProperties, DiagnosticSection.DataContext],
                    budget: new DiagnosticSnapshotBudget(MaxPayloadChars: perSectionCap),
                    propertyNames: ["Tag"],
                    dataContextProperties: ["Label"]),
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(bounded.Sections.All(section => section.Data is not null), Is.True);
                Assert.That(bounded.Sections.All(section => section.PayloadChars <= perSectionCap), Is.True);
                Assert.That(bounded.Sections.Sum(section => section.PayloadChars), Is.GreaterThan(perSectionCap));
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Property_and_binding_values_honor_max_value_length()
    {
        const string targetName = "DiagnosticValueBudgetTarget";
        const int maxValueLength = DiagnosticSnapshotLimits.MinValueLength;
        var ownerId = $"diagnostic-value-unit-{Guid.NewGuid():N}";
        var longValue = new string('v', 300);
        var target = new TextBlock
        {
            Name = targetName,
            Tag = longValue
        };
        target.SetBinding(TextBlock.TextProperty, new Binding(".") { Source = longValue });
        var window = CreateTestWindow(target, "Diagnostic value budget unit test");

        try
        {
            ShowAndLayout(window);
            PumpDataBinding(window.Dispatcher);
            var response = WpfVisualTreeInspector.CaptureDiagnosticSnapshot(
                ownerId,
                CreateRequest(
                    window,
                    targetName,
                    sections: [DiagnosticSection.WpfProperties, DiagnosticSection.Bindings],
                    budget: new DiagnosticSnapshotBudget(MaxValueLength: maxValueLength),
                    propertyNames: ["Tag"]),
                CancellationToken.None);

            var propertySection = response.Sections[0];
            var bindingSection = response.Sections[1];
            var properties = propertySection.Data!.Deserialize<GetComputedPropertiesResponse>(EvidenceJsonOptions)!;
            var bindings = bindingSection.Data!.Deserialize<GetBindingInfoResponse>(EvidenceJsonOptions)!;
            var tag = properties.Properties.Single(property => property.Name == "Tag");
            var textBinding = bindings.Bindings.Single(binding => binding.TargetProperty == "Text");

            Assert.Multiple(() =>
            {
                Assert.That(propertySection.Status, Is.EqualTo(DiagnosticSectionStatus.Truncated));
                Assert.That(propertySection.Code, Is.EqualTo("maxValueLength"));
                Assert.That(tag.Value, Has.Length.LessThanOrEqualTo(maxValueLength));
                Assert.That(tag.Value, Does.EndWith("..."));
                Assert.That(bindingSection.Status, Is.EqualTo(DiagnosticSectionStatus.Truncated));
                Assert.That(bindingSection.Code, Is.EqualTo("maxValueLength"));
                Assert.That(textBinding.CurrentValue, Has.Length.LessThanOrEqualTo(maxValueLength));
                Assert.That(textBinding.CurrentValue, Does.EndWith("..."));
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Tree_and_binding_error_strings_honor_max_value_length()
    {
        const string targetName = "DiagnosticTreeValueBudgetTarget";
        const int maxValueLength = DiagnosticSnapshotLimits.MinValueLength;
        var ownerId = $"diagnostic-tree-value-unit-{Guid.NewGuid():N}";
        var longAutomationId = new string('a', 180);
        var longMissingPath = "Missing" + new string('P', 180);
        var target = new TextBlock { Name = targetName };
        AutomationProperties.SetAutomationId(target, longAutomationId);
        target.SetBinding(TextBlock.TextProperty, new Binding(longMissingPath) { Source = new object() });
        var window = CreateTestWindow(target, "Diagnostic tree value budget unit test");

        try
        {
            ShowAndLayout(window);
            PumpDataBinding(window.Dispatcher);
            var response = WpfVisualTreeInspector.CaptureDiagnosticSnapshot(
                ownerId,
                CreateRequest(
                    window,
                    targetName,
                    sections: [DiagnosticSection.VisualTree, DiagnosticSection.BindingErrors],
                    budget: new DiagnosticSnapshotBudget(MaxValueLength: maxValueLength)),
                CancellationToken.None);

            var treeSection = response.Sections[0];
            var errorSection = response.Sections[1];
            var tree = treeSection.Data!.Deserialize<GetVisualTreeResponse>(EvidenceJsonOptions)!;
            var bindingErrors = errorSection.Data!.Deserialize<GetBindingErrorsResponse>(EvidenceJsonOptions)!;
            var bindingError = bindingErrors.Errors.Single(error => error.TargetProperty == "Text");

            Assert.Multiple(() =>
            {
                Assert.That(treeSection.Status, Is.EqualTo(DiagnosticSectionStatus.Truncated));
                Assert.That(treeSection.Code, Is.EqualTo("maxValueLength"));
                Assert.That(tree.Truncated, Is.True);
                Assert.That(tree.TruncatedReason, Is.EqualTo("maxValueLength"));
                Assert.That(tree.Root.AutomationId, Has.Length.LessThanOrEqualTo(maxValueLength));
                Assert.That(tree.Root.AutomationId, Does.EndWith("..."));
                Assert.That(errorSection.Status, Is.EqualTo(DiagnosticSectionStatus.Truncated));
                Assert.That(errorSection.Code, Is.EqualTo("maxValueLength"));
                Assert.That(bindingErrors.Truncated, Is.True);
                Assert.That(bindingErrors.TruncatedReason, Is.EqualTo("maxValueLength"));
                Assert.That(bindingError.Path, Has.Length.LessThanOrEqualTo(maxValueLength));
                Assert.That(bindingError.Path, Does.EndWith("..."));
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Hidden_pinned_target_supports_element_scoped_sections()
    {
        const string targetName = "DiagnosticHiddenTarget";
        var ownerId = $"diagnostic-hidden-unit-{Guid.NewGuid():N}";
        var target = new Border
        {
            Name = targetName,
            Visibility = Visibility.Hidden,
            DataContext = new GenerationTuple(11, "hidden-value")
        };
        target.SetBinding(FrameworkElement.TagProperty, new Binding(nameof(GenerationTuple.Label)));
        var window = CreateTestWindow(target, "Diagnostic hidden target unit test");

        try
        {
            ShowAndLayout(window);
            PumpDataBinding(window.Dispatcher);
            var response = WpfVisualTreeInspector.CaptureDiagnosticSnapshot(
                ownerId,
                CreateRequest(
                    window,
                    targetName,
                    sections:
                    [
                        DiagnosticSection.WpfProperties,
                        DiagnosticSection.Bindings,
                        DiagnosticSection.DataContext
                    ],
                    propertyNames: ["Tag"],
                    dataContextProperties: ["Generation"]),
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(response.Target.IsVisible, Is.False);
                Assert.That(response.Sections, Has.Count.EqualTo(3));
                Assert.That(
                    response.Sections.All(section => section.Status == DiagnosticSectionStatus.Success),
                    Is.True);
                Assert.That(response.Sections.All(section => section.Data is not null), Is.True);
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Diagnostic_failure_messages_replace_only_the_known_internal_agent_element_id()
    {
        const string internalAgentElementId = "wpfobj_AbCdEf0123_-xYz9";
        var message = WpfVisualTreeInspector.ReplaceInternalAgentElementId(
            $"wpf_handle_stale:not_found: '{internalAgentElementId}'; applicationText=wpfobj_1234567890ABCDEF.",
            internalAgentElementId);

        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("[internal-agent-element-id]"));
            Assert.That(message.Split("[internal-agent-element-id]").Length - 1, Is.EqualTo(1));
            Assert.That(message, Does.Contain("applicationText=wpfobj_1234567890ABCDEF"));
        });
    }

    [Test]
    public void Diagnostic_failure_message_capture_survives_a_throwing_message_getter()
    {
        var message = WpfVisualTreeInspector.GetBoundedDiagnosticFailureMessage(
            new ThrowingDiagnosticMessageException(),
            512,
            "wpfobj_unused");

        Assert.That(
            message,
            Is.EqualTo(
                $"{typeof(ThrowingDiagnosticMessageException).FullName}: " +
                "messageGetterFailed:System.InvalidOperationException"));
    }

    private static void AssertCompleteGeneration(
        CaptureWpfDiagnosticSnapshotResponse snapshot,
        int expectedGeneration)
    {
        var expectedLabel = $"generation-{expectedGeneration}";
        var propertiesSection = snapshot.Sections.Single(
            section => section.Section == DiagnosticSection.WpfProperties);
        var dataContextSection = snapshot.Sections.Single(
            section => section.Section == DiagnosticSection.DataContext);
        var properties = propertiesSection.Data!.Deserialize<GetComputedPropertiesResponse>(
            EvidenceJsonOptions)!;
        var dataContext = dataContextSection.Data!.Deserialize<GetDataContextResponse>(
            EvidenceJsonOptions)!;
        var tag = properties.Properties.Single(property => property.Name == "Tag");
        var data = dataContext.Data!.AsObject();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Sections, Has.Count.EqualTo(2));
            Assert.That(
                snapshot.Sections.Select(section => section.Section),
                Is.EqualTo(new[] { DiagnosticSection.DataContext, DiagnosticSection.WpfProperties }));
            Assert.That(snapshot.Sections.All(section => section.Status == DiagnosticSectionStatus.Success), Is.True);
            Assert.That(snapshot.Sections.All(section => section.Source == DiagnosticCaptureSource.WpfDispatcher), Is.True);
            Assert.That(snapshot.Sections.Select(section => section.CaptureGroup).Distinct(), Has.Exactly(1).Items);
            Assert.That(properties.Element.XPath, Is.EqualTo(snapshot.Target.XPath));
            Assert.That(tag.Value, Is.EqualTo(expectedLabel));
            Assert.That(data["Generation"]!.GetValue<int>(), Is.EqualTo(expectedGeneration));
            Assert.That(data["Label"]!.GetValue<string>(), Is.EqualTo(expectedLabel));
        });
    }

    private static void ApplyQueuedGeneration(
        Dispatcher dispatcher,
        FrameworkElement target,
        int generation)
    {
        var frame = new DispatcherFrame();
        Exception? failure = null;
        _ = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                ApplyGeneration(target, generation);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                frame.Continue = false;
            }
        }));

        Dispatcher.PushFrame(frame);
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void ApplyGeneration(FrameworkElement target, int generation)
    {
        target.Tag = $"generation-{generation}";
        target.DataContext = new GenerationTuple(generation, $"generation-{generation}");
    }

    private static CaptureWpfDiagnosticSnapshotResponse CaptureWithSections(
        IReadOnlyList<DiagnosticSection> sections) =>
        WpfVisualTreeInspector.CaptureDiagnosticSnapshot(
            "diagnostic-section-validation",
            new CaptureWpfDiagnosticSnapshotRequest(
                WindowHandle: null,
                Locator: null,
                ElementId: null,
                RootXPath: "/Window",
                Sections: sections,
                Budget: new DiagnosticSnapshotBudget()),
            CancellationToken.None);

    private static CaptureWpfDiagnosticSnapshotRequest CreateRequest(
        Window window,
        string targetName,
        IReadOnlyList<DiagnosticSection> sections,
        DiagnosticSnapshotBudget? budget = null,
        IReadOnlyList<string>? propertyNames = null,
        IReadOnlyList<string>? dataContextProperties = null) =>
        new(
            WindowHandle: new WindowInteropHelper(window).Handle.ToInt64(),
            Locator: new ElementLocator(Name: targetName),
            ElementId: null,
            RootXPath: "/Window",
            Sections: sections,
            Budget: budget ?? new DiagnosticSnapshotBudget(),
            PropertyNames: propertyNames,
            DataContextProperties: dataContextProperties);

    private static Window CreateTestWindow(FrameworkElement target, string title) =>
        new()
        {
            Title = title,
            Width = 240,
            Height = 140,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
            Left = -10_000,
            Top = -10_000,
            Content = target
        };

    private static void ShowAndLayout(Window window)
    {
        window.Show();
        window.UpdateLayout();
    }

    private static void PumpDataBinding(Dispatcher dispatcher) =>
        dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));

    private static void CloseAndRelease(Window window, string ownerId)
    {
        WpfVisualTreeInspector.ReleaseOwnerResources(ownerId);
        if (window.IsVisible)
        {
            window.Close();
        }
    }

    private sealed record GenerationTuple(int Generation, string Label);

    private sealed class ThrowingDiagnosticMessageException : Exception
    {
        public override string Message => throw new InvalidOperationException("Message getter failed.");
    }
}

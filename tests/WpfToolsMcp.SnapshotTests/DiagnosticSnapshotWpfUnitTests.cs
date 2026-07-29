using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
                    DiagnosticSection.WpfProperties,
                    DiagnosticSection.DataContext
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

    private sealed record GenerationTuple(int Generation, string Label);
}

using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using WpfToolsMcp.Agent;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[Category("Wpf")]
[Apartment(ApartmentState.STA)]
public sealed class PropertyProvenanceWpfUnitTests
{
    [Test]
    public void Existing_resource_probe_does_not_install_absent_dictionaries()
    {
        var element = new Border();
        var contentElement = new Run();
        var style = new Style();
        var template = new ControlTemplate();
        var application = RuntimeHelpers.GetUninitializedObject(typeof(Application));

        Assert.Multiple(() =>
        {
            AssertResourcesStayAbsent(element);
            AssertResourcesStayAbsent(contentElement);
            AssertResourcesStayAbsent(style);
            AssertResourcesStayAbsent(template);
            AssertResourcesStayAbsent(application);
        });
    }

    private static void AssertResourcesStayAbsent(object owner)
    {
        Assert.That(
            WpfVisualTreeInspector.TryGetExistingResourcesForProvenance(owner, out _),
            Is.False,
            $"{owner.GetType().Name} unexpectedly started with resource storage.");
        Assert.That(
            WpfVisualTreeInspector.TryGetExistingResourcesForProvenance(owner, out _),
            Is.False,
            $"The provenance presence check installed resources on {owner.GetType().Name}.");
    }
}

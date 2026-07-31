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

    [Test]
    public void Resource_candidate_value_comparison_uses_same_type_custom_equality()
    {
        var candidate = new ResourceValue(42);
        var effectiveValue = new ResourceValue(42);
        string? scanIncompleteReason = null;

        var matched = WpfVisualTreeInspector.AreResourceCandidateValuesEqualBestEffort(
            candidate,
            effectiveValue,
            ref scanIncompleteReason);

        Assert.Multiple(() =>
        {
            Assert.That(matched, Is.True);
            Assert.That(candidate.EqualsCalls, Is.EqualTo(1));
            Assert.That(scanIncompleteReason, Is.Null);
        });
    }

    [Test]
    public void Resource_candidate_value_comparison_marks_the_scan_incomplete_when_custom_equality_throws()
    {
        string? scanIncompleteReason = null;

        var matched = WpfVisualTreeInspector.AreResourceCandidateValuesEqualBestEffort(
            new ThrowingResourceValue(),
            new ThrowingResourceValue(),
            ref scanIncompleteReason);

        Assert.Multiple(() =>
        {
            Assert.That(matched, Is.False);
            Assert.That(
                scanIncompleteReason,
                Is.EqualTo("resource_value_comparison_failed:System.InvalidOperationException"));
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

    private sealed class ResourceValue
    {
        private readonly int _value;

        public ResourceValue(int value)
        {
            _value = value;
        }

        public int EqualsCalls { get; private set; }

        public override bool Equals(object? obj)
        {
            EqualsCalls++;
            return obj is ResourceValue other && _value == other._value;
        }

        public override int GetHashCode() => _value;
    }

    private sealed class ThrowingResourceValue
    {
        public override bool Equals(object? obj) =>
            throw new InvalidOperationException("Synthetic resource value comparison failure.");

        public override int GetHashCode() => 1;
    }
}

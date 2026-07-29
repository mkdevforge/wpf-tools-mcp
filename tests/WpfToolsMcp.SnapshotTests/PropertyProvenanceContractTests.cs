using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using NUnit.Framework;
using WpfToolsMcp.Agent;
using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class PropertyProvenanceContractTests
{
    [Test]
    public void Current_agent_capabilities_advertise_property_provenance()
    {
        Assert.That(
            AgentProtocolCapabilities.Current,
            Does.Contain(AgentProtocolCapabilities.GetComputedPropertyProvenance));
    }

    [Test]
    public void Computed_property_provenance_is_opt_in_and_omitted_by_default()
    {
        var request = new GetComputedPropertiesRequest();
        var property = new ComputedPropertyInfo(
            Name: "Width",
            OwnerType: "System.Windows.FrameworkElement",
            Value: "100");

        var json = JsonSerializer.Serialize(property);

        Assert.Multiple(() =>
        {
            Assert.That(request.IncludeProvenance, Is.False);
            Assert.That(request.MaxProvenanceCandidates, Is.EqualTo(20));
            Assert.That(property.Provenance, Is.Null);
            Assert.That(json, Does.Not.Contain("Provenance"));
        });
    }

    [Test]
    public void Structured_provenance_round_trips_stable_source_and_evidence_enums()
    {
        var provenance = new DependencyPropertyProvenance(
            ValueSource: new DependencyPropertyValueSourceProvenance(
                DependencyPropertyBaseValueSource.Local,
                IsExpression: true,
                IsAnimated: false,
                IsCoerced: false,
                IsCurrent: false,
                new ProvenanceEvidence(ProvenanceEvidenceKind.Exact)),
            Binding: null,
            Style: null,
            Resource: null,
            Template: null,
            Inheritance: null,
            Animation: null,
            Coercion: null,
            DefaultMetadata: new DefaultMetadataPropertyProvenance(
                DefaultValue: "0",
                DefaultValueType: "System.Double",
                DefaultValueEvidence: new ProvenanceEvidence(ProvenanceEvidenceKind.Exact),
                MetadataType: "System.Windows.FrameworkPropertyMetadata",
                IsEffectiveValueSource: false,
                EffectiveValueSourceEvidence: new ProvenanceEvidence(ProvenanceEvidenceKind.Exact),
                Inherits: false,
                BindsTwoWayByDefault: false,
                DefaultUpdateSourceTrigger: "PropertyChanged",
                IsAnimationProhibited: false,
                new ProvenanceEvidence(ProvenanceEvidenceKind.Exact)));

        var json = JsonSerializer.Serialize(provenance);
        var roundTrip = JsonSerializer.Deserialize<DependencyPropertyProvenance>(json);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"BaseValueSource\":\"Local\""));
            Assert.That(json, Does.Contain("\"Kind\":\"Exact\""));
            Assert.That(roundTrip!.ValueSource.BaseValueSource, Is.EqualTo(DependencyPropertyBaseValueSource.Local));
            Assert.That(roundTrip.ValueSource.IsExpression, Is.True);
        });
    }

    [Test]
    public void Provenance_text_truncation_preserves_valid_utf16_at_the_boundary()
    {
        var result = WpfVisualTreeInspector.TruncateProvenanceText("A\U0001F600BCD", maxLength: 5);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo("A..."));
            Assert.That(result.Length, Is.LessThanOrEqualTo(5));
            Assert.That(ContainsUnpairedSurrogate(result), Is.False);
        });
    }

    [Test]
    public void Base_value_source_mapping_is_exhaustive_and_preserves_overlay_flags()
    {
        var expected = new Dictionary<BaseValueSource, DependencyPropertyBaseValueSource>
        {
            [BaseValueSource.Unknown] = DependencyPropertyBaseValueSource.Unknown,
            [BaseValueSource.Default] = DependencyPropertyBaseValueSource.Default,
            [BaseValueSource.Inherited] = DependencyPropertyBaseValueSource.Inherited,
            [BaseValueSource.DefaultStyle] = DependencyPropertyBaseValueSource.DefaultStyle,
            [BaseValueSource.DefaultStyleTrigger] = DependencyPropertyBaseValueSource.DefaultStyleTrigger,
            [BaseValueSource.Style] = DependencyPropertyBaseValueSource.Style,
            [BaseValueSource.TemplateTrigger] = DependencyPropertyBaseValueSource.TemplateTrigger,
            [BaseValueSource.StyleTrigger] = DependencyPropertyBaseValueSource.StyleTrigger,
            [BaseValueSource.ImplicitStyleReference] = DependencyPropertyBaseValueSource.ImplicitStyleReference,
            [BaseValueSource.ParentTemplate] = DependencyPropertyBaseValueSource.ParentTemplate,
            [BaseValueSource.ParentTemplateTrigger] = DependencyPropertyBaseValueSource.ParentTemplateTrigger,
            [BaseValueSource.Local] = DependencyPropertyBaseValueSource.Local
        };

        Assert.That(Enum.GetValues<BaseValueSource>(), Is.EquivalentTo(expected.Keys));
        foreach (var (source, mapped) in expected)
        {
            Assert.That(WpfVisualTreeInspector.MapBaseValueSource(source), Is.EqualTo(mapped), source.ToString());
        }

        var provenance = WpfVisualTreeInspector.MapValueSource(
            BaseValueSource.Local,
            isExpression: true,
            isAnimated: true,
            isCoerced: true,
            isCurrent: true);

        Assert.Multiple(() =>
        {
            Assert.That(
                WpfVisualTreeInspector.MapBaseValueSource((BaseValueSource)int.MaxValue),
                Is.EqualTo(DependencyPropertyBaseValueSource.Unknown));
            Assert.That(provenance.BaseValueSource, Is.EqualTo(DependencyPropertyBaseValueSource.Local));
            Assert.That(provenance.IsExpression, Is.True);
            Assert.That(provenance.IsAnimated, Is.True);
            Assert.That(provenance.IsCoerced, Is.True);
            Assert.That(provenance.IsCurrent, Is.True);
            Assert.That(provenance.Evidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.Exact));
        });
    }

    [Test]
    public void Safe_value_formatter_preserves_common_wpf_values_without_calling_application_to_string()
    {
        var thickness = WpfVisualTreeInspector.FormatSafeProvenanceValueDetails(
            new Thickness(1, 2, 3, 4),
            "string",
            2000);
        var cornerRadius = WpfVisualTreeInspector.FormatSafeProvenanceValueDetails(
            new CornerRadius(1, 2, 3, 4),
            "string",
            2000);
        var gridLength = WpfVisualTreeInspector.FormatSafeProvenanceValueDetails(
            new GridLength(2, GridUnitType.Star),
            "string",
            2000);
        var color = WpfVisualTreeInspector.FormatSafeProvenanceValueDetails(
            Colors.CornflowerBlue,
            "string",
            2000);
        var fontWeight = WpfVisualTreeInspector.FormatSafeProvenanceValueDetails(
            FontWeights.Bold,
            "string",
            2000);
        var fontFamily = WpfVisualTreeInspector.FormatSafeProvenanceValueDetails(
            new FontFamily("Segoe UI"),
            "string",
            2000);
        var applicationValue = WpfVisualTreeInspector.FormatSafeProvenanceValueDetails(
            new ThrowingDisplayValue(),
            "string",
            2000);

        Assert.Multiple(() =>
        {
            Assert.That(thickness.Text, Is.EqualTo("1,2,3,4"));
            Assert.That(cornerRadius.Text, Is.EqualTo("1,2,3,4"));
            Assert.That(gridLength.Text, Is.EqualTo("2*"));
            Assert.That(color.Text, Is.EqualTo("#FF6495ED"));
            Assert.That(fontWeight.Text, Is.EqualTo("Bold"));
            Assert.That(fontFamily.Text, Is.EqualTo("Segoe UI"));
            Assert.That(applicationValue.Text, Does.EndWith("ThrowingDisplayValue"));
            Assert.That(applicationValue.RepresentsValue, Is.False);
        });
    }

    [Test]
    public void Safe_type_formatting_does_not_call_virtual_type_name_members()
    {
        var applicationType = new ThrowingTypeValue();
        WpfVisualTreeInspector.SafeProvenanceValueFormatting formatted = default;
        string? typeName = null;

        Assert.DoesNotThrow(() =>
        {
            formatted = WpfVisualTreeInspector.FormatSafeProvenanceValueDetails(
                applicationType,
                "string",
                2000);
            typeName = WpfVisualTreeInspector.GetTypeName(applicationType);
        });

        var runtimeType = WpfVisualTreeInspector.FormatSafeProvenanceValueDetails(
            typeof(string),
            "string",
            2000);
        Assert.Multiple(() =>
        {
            Assert.That(formatted.Text, Does.EndWith("ThrowingTypeValue"));
            Assert.That(formatted.RepresentsValue, Is.False);
            Assert.That(typeName, Does.EndWith("ThrowingTypeValue"));
            Assert.That(runtimeType.Text, Is.EqualTo("System.String"));
            Assert.That(runtimeType.RepresentsValue, Is.True);
        });
    }

    [Test]
    [Apartment(ApartmentState.STA)]
    public void Binding_evidence_downgrades_when_parent_or_child_text_is_truncated()
    {
        const int maxStringLength = 2000;
        var longPath = new string('P', maxStringLength + 100);
        var metadata = TextBlock.TextProperty.GetMetadata(typeof(TextBlock));

        var leafTarget = new TextBlock();
        BindingOperations.SetBinding(
            leafTarget,
            TextBlock.TextProperty,
            new Binding(longPath) { Source = new object() });
        var leafExpression = BindingOperations.GetBindingExpressionBase(
            leafTarget,
            TextBlock.TextProperty)!;
        var leaf = WpfVisualTreeInspector.BuildBindingProvenance(
            leafExpression,
            metadata,
            maxCandidates: 20,
            maxStringLength);

        var priorityBinding = new PriorityBinding();
        priorityBinding.Bindings.Add(new Binding(longPath) { Source = new object() });
        priorityBinding.Bindings.Add(new Binding("Length") { Source = "fallback" });
        var priorityTarget = new TextBlock();
        BindingOperations.SetBinding(priorityTarget, TextBlock.TextProperty, priorityBinding);
        var priorityExpression = BindingOperations.GetBindingExpressionBase(
            priorityTarget,
            TextBlock.TextProperty)!;
        var priority = WpfVisualTreeInspector.BuildBindingProvenance(
            priorityExpression,
            metadata,
            maxCandidates: 20,
            maxStringLength);

        Assert.Multiple(() =>
        {
            Assert.That(leaf.Path, Has.Length.EqualTo(maxStringLength));
            Assert.That(leaf.Path, Does.EndWith("..."));
            Assert.That(leaf.Evidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.BestEffort));
            Assert.That(leaf.Evidence.Reason, Is.EqualTo("maxStringLength"));

            Assert.That(priority.Children, Has.Count.EqualTo(2));
            Assert.That(priority.Children[0].Path, Has.Length.EqualTo(maxStringLength));
            Assert.That(priority.Evidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.BestEffort));
            Assert.That(priority.Evidence.Reason, Is.EqualTo("maxStringLength"));
        });
    }

    [Test]
    public void Truncated_safe_values_are_not_labeled_exact()
    {
        var formatted = WpfVisualTreeInspector.FormatProvenanceValueWithEvidence(
            "abcdefgh",
            "string",
            maxLength: 5);

        Assert.Multiple(() =>
        {
            Assert.That(formatted.Value, Is.EqualTo("ab..."));
            Assert.That(formatted.Evidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.BestEffort));
            Assert.That(formatted.Evidence.Reason, Is.EqualTo("maxStringLength"));
        });
    }

    [Test]
    public void Provenance_property_name_preparation_bounds_indexing_strings_and_output()
    {
        var names = new NonEnumerablePropertyNames(count: 10_000);
        var prepared = WpfVisualTreeInspector.PrepareProvenancePropertyNames(names);
        var transportPrepared = AutomationController.PrepareProvenancePropertyNamesForAgent(names);
        var longName = WpfVisualTreeInspector.PrepareProvenancePropertyNames(
            [new string('x', 600)]);
        var longTransportName = AutomationController.PrepareProvenancePropertyNamesForAgent(
            [new string('x', 600)]);

        Assert.Multiple(() =>
        {
            Assert.That(names.IndexReads, Is.EqualTo(200));
            Assert.That(prepared.Names, Has.Length.EqualTo(100));
            Assert.That(prepared.TruncatedReason, Is.EqualTo("maxProvenancePropertyNames"));
            Assert.That(transportPrepared.Names, Has.Count.EqualTo(100));
            Assert.That(transportPrepared.TruncatedReason, Is.EqualTo("maxProvenancePropertyNames"));
            Assert.That(longName.Names.Single().Length, Is.LessThanOrEqualTo(512));
            Assert.That(longName.TruncatedReason, Is.EqualTo("maxProvenancePropertyNameLength"));
            Assert.That(longTransportName.Names!.Single().Length, Is.LessThanOrEqualTo(512));
            Assert.That(longTransportName.TruncatedReason, Is.EqualTo("maxProvenancePropertyNameLength"));
        });
    }

    [Test]
    public void Missing_property_provenance_capability_requires_target_and_session_restart()
    {
        var exception = AutomationController.CreateComputedPropertyProvenanceCapabilityException();

        Assert.That(
            exception.Message,
            Is.EqualTo(
                "agent_capability_unavailable: get_computed_properties with includeProvenance=true requires the current WPF agent. " +
                "Restart the target application, start a new MCP session, and attach again so the current agent can be injected."));
        Assert.That(exception.Message, Does.Not.Contain("retry").IgnoreCase);
        Assert.That(exception.Message, Does.Not.Contain("reinject").IgnoreCase);
    }

    [Test]
    public async Task Legacy_computed_properties_call_does_not_require_provenance_capability()
    {
        var callInvoked = false;

        var result = await AutomationController.CallGetComputedPropertiesWhenSupportedAsync(
            includeProvenance: false,
            capabilities: null,
            call: () =>
            {
                callInvoked = true;
                return Task.FromResult("legacy-response");
            });

        Assert.Multiple(() =>
        {
            Assert.That(callInvoked, Is.True);
            Assert.That(result, Is.EqualTo("legacy-response"));
        });
    }

    [Test]
    public async Task Provenance_call_runs_when_the_agent_advertises_the_capability()
    {
        var callInvoked = false;

        var result = await AutomationController.CallGetComputedPropertiesWhenSupportedAsync(
            includeProvenance: true,
            capabilities: new AgentCapabilitiesResponse(
                ProtocolVersion: AgentProtocolCapabilities.CurrentProtocolVersion,
                Capabilities: [AgentProtocolCapabilities.GetComputedPropertyProvenance]),
            call: () =>
            {
                callInvoked = true;
                return Task.FromResult("provenance-response");
            });

        Assert.Multiple(() =>
        {
            Assert.That(callInvoked, Is.True);
            Assert.That(result, Is.EqualTo("provenance-response"));
        });
    }

    private static bool ContainsUnpairedSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class ThrowingDisplayValue
    {
        public override string ToString() =>
            throw new InvalidOperationException("The safe formatter must not call application ToString().");
    }

    private sealed class ThrowingTypeValue : TypeDelegator
    {
        public ThrowingTypeValue()
            : base(typeof(string))
        {
        }

        public override string? FullName =>
            throw new InvalidOperationException("The safe formatter must not call application Type.FullName.");

        public override string Name =>
            throw new InvalidOperationException("The safe formatter must not call application Type.Name.");
    }

    private sealed class NonEnumerablePropertyNames(int count) : IReadOnlyList<string>
    {
        public int Count { get; } = count;

        public int IndexReads { get; private set; }

        public string this[int index]
        {
            get
            {
                IndexReads++;
                return $"Missing{index}";
            }
        }

        public IEnumerator<string> GetEnumerator() =>
            throw new InvalidOperationException("Bounded preparation must not enumerate the complete input.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

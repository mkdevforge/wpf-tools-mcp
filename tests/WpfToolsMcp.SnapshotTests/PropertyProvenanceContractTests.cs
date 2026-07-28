using System.Text.Json;
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
}

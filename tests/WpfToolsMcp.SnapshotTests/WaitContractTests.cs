using System.Text.Json;
using System.Text.Json.Nodes;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class WaitContractTests
{
    [Test]
    public void Existing_positional_constructor_shapes_are_preserved()
    {
        var requestConstructor = typeof(WaitForRequest).GetConstructors().Single(candidate => candidate.IsPublic);
        var responseConstructor = typeof(WaitForResponse).GetConstructors().Single(candidate => candidate.IsPublic);
        var observationConstructor = typeof(WaitForObservation).GetConstructors().Single(candidate => candidate.IsPublic);

        Assert.Multiple(() =>
        {
            Assert.That(
                requestConstructor.GetParameters().Select(parameter => parameter.Name),
                Is.EqualTo(new[]
                {
                    "Locator",
                    "ElementId",
                    "WindowHandle",
                    "Backend",
                    "State",
                    "TimeoutMs",
                    "PollIntervalMs",
                    "StableMs",
                    "ExpectedValue",
                    "ExpectedText",
                    "ThrowOnTimeout"
                }));
            Assert.That(
                responseConstructor.GetParameters().Select(parameter => parameter.Name),
                Is.EqualTo(new[]
                {
                    "Succeeded",
                    "State",
                    "ElapsedMs",
                    "Attempts",
                    "LastObservation",
                    "FailureReason"
                }));
            Assert.That(
                observationConstructor.GetParameters().Select(parameter => parameter.Name),
                Is.EqualTo(new[]
                {
                    "Type",
                    "AutomationId",
                    "Name",
                    "XPath",
                    "Bounds",
                    "IsEnabled",
                    "IsOffscreen"
                }));
        });
    }

    [Test]
    public void Legacy_wait_contracts_round_trip_without_structured_fields()
    {
        var request = new WaitForRequest(
            Locator: new ElementLocator(AutomationId: "Submit"),
            WindowHandle: 123,
            Backend: InspectionBackend.Wpf,
            State: "name_contains",
            TimeoutMs: 2500,
            PollIntervalMs: 50,
            StableMs: 200,
            ExpectedValue: 4,
            ExpectedText: "Ready",
            ThrowOnTimeout: false);
        var response = new WaitForResponse(
            Succeeded: false,
            State: "name_contains",
            ElapsedMs: 2500,
            Attempts: 51,
            LastObservation: new WaitForObservation("Button", AutomationId: "Submit", Name: "Pending"),
            FailureReason: "timeout");

        var requestJson = JsonSerializer.Serialize(request);
        var responseJson = JsonSerializer.Serialize(response);

        Assert.Multiple(() =>
        {
            Assert.That(JsonSerializer.Deserialize<WaitForRequest>(requestJson), Is.EqualTo(request));
            Assert.That(JsonSerializer.Deserialize<WaitForResponse>(responseJson), Is.EqualTo(response));
            Assert.That(requestJson, Does.Not.Contain("Condition"));
            Assert.That(responseJson, Does.Not.Contain("BackendUsed"));
            Assert.That(responseJson, Does.Not.Contain("ReasonCode"));
            Assert.That(responseJson, Does.Not.Contain("LastObservedValue"));
        });
    }

    [Test]
    public void Window_observation_identity_is_additive_and_round_trips()
    {
        var observation = new WaitForObservation("Window", Name: "Orders")
        {
            WindowHandle = 456,
            OwnerHandle = 123,
            FrameworkId = "Win32"
        };

        var json = JsonSerializer.Serialize(observation);
        var roundTrip = JsonSerializer.Deserialize<WaitForObservation>(json);

        Assert.Multiple(() =>
        {
            Assert.That(roundTrip, Is.EqualTo(observation));
            Assert.That(json, Does.Contain("\"WindowHandle\":456"));
            Assert.That(json, Does.Contain("\"OwnerHandle\":123"));
            Assert.That(json, Does.Contain("\"FrameworkId\":\"Win32\""));
        });
    }

    [Test]
    public void Structured_condition_round_trips_with_string_enums()
    {
        var request = new WaitForRequest(State: "visible", ThrowOnTimeout: false)
        {
            Condition = new WaitCondition(
                Kind: WaitConditionKind.DependencyPropertyValue,
                PropertyName: "Items.Count",
                Comparison: WaitComparison.GreaterThanOrEqual,
                Expected: new WaitScalar(WaitScalarKind.Number, NumberValue: 3),
                Window: new WaitWindowSelector(
                    Handle: 456,
                    TitleContains: "Orders",
                    OwnerHandle: 123,
                    FrameworkId: "WPF"),
                HoldForMs: 300)
        };

        var json = JsonSerializer.Serialize(request);
        var roundTrip = JsonSerializer.Deserialize<WaitForRequest>(json);

        Assert.Multiple(() =>
        {
            Assert.That(roundTrip, Is.EqualTo(request));
            Assert.That(json, Does.Contain("\"Kind\":\"DependencyPropertyValue\""));
            Assert.That(json, Does.Contain("\"Comparison\":\"GreaterThanOrEqual\""));
            Assert.That(json, Does.Contain("\"Kind\":\"Number\""));
            Assert.That(json, Does.Not.Contain("StringValue"));
            Assert.That(json, Does.Not.Contain("BooleanValue"));
        });
    }

    [Test]
    public void Structured_timeout_evidence_round_trips_with_string_enums()
    {
        var response = new WaitForResponse(
            Succeeded: false,
            State: "dependency_property_value",
            ElapsedMs: 5000,
            Attempts: 42,
            FailureReason: "timeout")
        {
            BackendUsed = WaitBackend.Wpf,
            ReasonCode = "condition_not_met",
            LastObservedValue = new WaitObservedValue(
                State: WaitObservedValueState.Value,
                Value: JsonValue.Create(2),
                ValueType: "System.Int32",
                Truncated: false)
        };

        var json = JsonSerializer.Serialize(response);
        var roundTrip = JsonSerializer.Deserialize<WaitForResponse>(json);

        Assert.Multiple(() =>
        {
            Assert.That(roundTrip?.BackendUsed, Is.EqualTo(WaitBackend.Wpf));
            Assert.That(roundTrip?.ReasonCode, Is.EqualTo("condition_not_met"));
            Assert.That(roundTrip?.LastObservedValue?.State, Is.EqualTo(WaitObservedValueState.Value));
            Assert.That(roundTrip?.LastObservedValue?.Value?.GetValue<int>(), Is.EqualTo(2));
            Assert.That(json, Does.Contain("\"BackendUsed\":\"Wpf\""));
            Assert.That(json, Does.Contain("\"State\":\"Value\""));
        });
    }
}

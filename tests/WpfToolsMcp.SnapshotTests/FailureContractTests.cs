using System.Text.Json;
using System.Text.Json.Nodes;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

public sealed class FailureContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public void Backend_capability_without_failure_preserves_legacy_shape()
    {
        var json = JsonSerializer.Serialize(
            new BackendCapabilityState("wpf", "not_initialized"),
            JsonOptions);

        Assert.That(json, Is.EqualTo("{\"backend\":\"wpf\",\"state\":\"not_initialized\"}"));
    }

    [Test]
    public void Failure_and_fallback_metadata_are_structured_and_omit_unknown_values()
    {
        var failure = new FailureInfo(
            "injection_failed",
            "injection",
            "The WPF backend could not be initialized in the target process.")
        {
            Retryable = false,
            RecoveryActions = ["use_uia"],
            Cause = new DiagnosticCauseInfo(typeof(InvalidOperationException).FullName!)
            {
                Message = "local diagnostic message",
                Details = "bounded adapter details"
            }
        };
        var fallback = new BackendFallbackInfo(
            FromBackend: "wpf",
            ToBackend: "uia",
            Attempted: true,
            Available: true,
            Used: true)
        {
            Failure = failure
        };
        var state = new BackendCapabilityState("wpf", "unavailable")
        {
            Failure = failure
        };

        var stateJson = JsonNode.Parse(JsonSerializer.Serialize(state, JsonOptions))!.AsObject();
        var fallbackJson = JsonNode.Parse(JsonSerializer.Serialize(fallback, JsonOptions))!.AsObject();

        Assert.Multiple(() =>
        {
            Assert.That(stateJson["failure"]?["code"]?.GetValue<string>(), Is.EqualTo("injection_failed"));
            Assert.That(stateJson["failure"]?["retryable"]?.GetValue<bool>(), Is.False);
            Assert.That(stateJson["failure"]?.AsObject().ContainsKey("retryAfterMs"), Is.False);
            Assert.That(
                stateJson["failure"]?["cause"]?["message"]?.GetValue<string>(),
                Is.EqualTo("local diagnostic message"));
            Assert.That(fallbackJson["fromBackend"]?.GetValue<string>(), Is.EqualTo("wpf"));
            Assert.That(fallbackJson["toBackend"]?.GetValue<string>(), Is.EqualTo("uia"));
            Assert.That(fallbackJson["attempted"]?.GetValue<bool>(), Is.True);
            Assert.That(fallbackJson["available"]?.GetValue<bool>(), Is.True);
            Assert.That(fallbackJson["used"]?.GetValue<bool>(), Is.True);
            Assert.That(fallbackJson["failure"]?["stage"]?.GetValue<string>(), Is.EqualTo("injection"));
            Assert.That(
                fallbackJson["failure"]?["cause"]?["details"]?.GetValue<string>(),
                Is.EqualTo("bounded adapter details"));
        });
    }

    [Test]
    public void Backend_responses_omit_fallback_until_it_is_observed()
    {
        var element = new ElementRef(
            Type: "Button",
            AutomationId: "save",
            Name: "Save",
            XPath: "/Window/Button");
        var tree = new GetVisualTreeResponse(
            InspectionBackend.Uia,
            new TreeNode("Window", null, null, "/Window", 0, []),
            ReturnedNodes: 1,
            ScannedNodes: 1,
            Truncated: false);
        var resolved = new ResolveElementResponse(InspectionBackend.Uia, element, WindowHandleUsed: 42);
        var found = new FindElementsResponse(
            InspectionBackend.Uia,
            Matches: [element],
            ReturnedMatches: 1,
            ScannedNodes: 1,
            Truncated: false);

        Assert.Multiple(() =>
        {
            Assert.That(SerializeObject(tree).ContainsKey("fallback"), Is.False);
            Assert.That(SerializeObject(resolved).ContainsKey("fallback"), Is.False);
            Assert.That(SerializeObject(found).ContainsKey("fallback"), Is.False);
        });
    }

    [Test]
    public void Backend_responses_expose_the_same_optional_fallback_contract()
    {
        var fallback = new BackendFallbackInfo("wpf", "uia", Attempted: true, Available: true, Used: true);
        var element = new ElementRef("Button", null, null, "/Window/Button");
        var tree = new GetVisualTreeResponse(
            InspectionBackend.Uia,
            new TreeNode("Window", null, null, "/Window", 0, []),
            ReturnedNodes: 1,
            ScannedNodes: 1,
            Truncated: false)
        {
            Fallback = fallback
        };
        var resolved = new ResolveElementResponse(InspectionBackend.Uia, element, WindowHandleUsed: 42)
        {
            Fallback = fallback
        };
        var found = new FindElementsResponse(
            InspectionBackend.Uia,
            Matches: [element],
            ReturnedMatches: 1,
            ScannedNodes: 1,
            Truncated: false)
        {
            Fallback = fallback
        };

        Assert.Multiple(() =>
        {
            Assert.That(SerializeObject(tree)["fallback"]?["used"]?.GetValue<bool>(), Is.True);
            Assert.That(SerializeObject(resolved)["fallback"]?["used"]?.GetValue<bool>(), Is.True);
            Assert.That(SerializeObject(found)["fallback"]?["used"]?.GetValue<bool>(), Is.True);
        });
    }

    private static JsonObject SerializeObject<T>(T value) =>
        JsonNode.Parse(JsonSerializer.Serialize(value, JsonOptions))!.AsObject();
}

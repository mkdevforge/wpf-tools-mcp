using System.Text.Json;
using Json.Schema;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
public sealed class RealizeItemToolSchemaTests
{
    [TestCase(null)]
    [TestCase("diagnostics")]
    public async Task Schema_enforces_exclusive_container_and_item_selectors(string? toolProfile)
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe, toolProfile: toolProfile);
        var tool = (await mcp.ListToolsAsync()).Single(candidate => candidate.Name == "realize_item");
        var schema = JsonSchema.FromText(tool.JsonSchema.GetRawText());
        var inputProperties = tool.JsonSchema.GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var expectedInputProperties = string.Equals(toolProfile, "diagnostics", StringComparison.Ordinal)
            ? new[]
            {
                "advisoryElapsedLimitMs",
                "containerElementId",
                "containerLocator",
                "index",
                "maxProviderCalls",
                "name",
                "pollIntervalMs",
                "sessionId",
                "windowHandle"
            }
            : new[]
            {
                "containerElementId",
                "containerLocator",
                "index",
                "name",
                "sessionId",
                "windowHandle"
            };

        Assert.Multiple(() =>
        {
            Assert.That(inputProperties, Is.EqualTo(expectedInputProperties));
            Assert.That(IsValid(schema, Request(containerLocator: new { automationId = "Items" }, index: 0)), Is.True);
            Assert.That(IsValid(schema, Request(containerElementId: "uia_container", name: "  Exact Name  ")), Is.True);
            Assert.That(IsValid(schema, Request(containerElementId: "uia_container", name: "   ")), Is.True);

            Assert.That(IsValid(schema, Request(index: 0)), Is.False, "A container selector is required.");
            Assert.That(IsValid(schema, Request(containerElementId: "   ", index: 0)), Is.False);
            Assert.That(
                IsValid(schema, Request(
                    containerLocator: new { automationId = "Items" },
                    containerElementId: "uia_container",
                    index: 0)),
                Is.False,
                "Container locator and elementId are mutually exclusive.");
            Assert.That(IsValid(schema, Request(containerElementId: "uia_container")), Is.False, "An item selector is required.");
            Assert.That(
                IsValid(schema, Request(containerElementId: "uia_container", index: 0, name: "Item")),
                Is.False,
                "Index and Name are mutually exclusive.");
            Assert.That(IsValid(schema, Request(containerElementId: "uia_container", index: -1)), Is.False);
            Assert.That(IsValid(schema, Request(containerElementId: "uia_container", name: "")), Is.False);
        });

        var root = tool.JsonSchema;
        var nameSchema = FindExclusiveSelectorSchema(root, "name");
        Assert.Multiple(() =>
        {
            Assert.That(nameSchema.GetProperty("type").GetString(), Is.EqualTo("string"));
            Assert.That(nameSchema.GetProperty("minLength").GetInt32(), Is.EqualTo(1));
            Assert.That(nameSchema.TryGetProperty("pattern", out _), Is.False, "UIA Name must not be normalized by a whitespace pattern.");
            Assert.That(nameSchema.TryGetProperty("maxLength", out _), Is.False, "UIA Name must reach the provider unchanged.");
        });
    }

    [Test]
    public async Task Diagnostics_schema_advertises_and_enforces_provider_and_poll_bounds()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe, toolProfile: "diagnostics");
        var tool = (await mcp.ListToolsAsync()).Single(candidate => candidate.Name == "realize_item");
        var schema = JsonSchema.FromText(tool.JsonSchema.GetRawText());

        Assert.Multiple(() =>
        {
            Assert.That(IsValid(schema, Request(
                containerElementId: "uia_container",
                index: 0,
                maxProviderCalls: RealizeItemLimits.MinimumProviderCalls,
                advisoryElapsedLimitMs: RealizeItemLimits.MinimumAdvisoryElapsedLimitMs,
                pollIntervalMs: RealizeItemLimits.MinimumPollIntervalMs)), Is.True);
            Assert.That(IsValid(schema, Request(
                containerElementId: "uia_container",
                index: 0,
                maxProviderCalls: RealizeItemLimits.MaximumProviderCalls,
                advisoryElapsedLimitMs: RealizeItemLimits.MaximumAdvisoryElapsedLimitMs,
                pollIntervalMs: RealizeItemLimits.MaximumPollIntervalMs)), Is.True);

            Assert.That(IsValid(schema, Request(
                containerElementId: "uia_container",
                index: 0,
                maxProviderCalls: RealizeItemLimits.MinimumProviderCalls - 1)), Is.False);
            Assert.That(IsValid(schema, Request(
                containerElementId: "uia_container",
                index: 0,
                maxProviderCalls: RealizeItemLimits.MaximumProviderCalls + 1)), Is.False);
            Assert.That(IsValid(schema, Request(
                containerElementId: "uia_container",
                index: 0,
                advisoryElapsedLimitMs: RealizeItemLimits.MinimumAdvisoryElapsedLimitMs - 1)), Is.False);
            Assert.That(IsValid(schema, Request(
                containerElementId: "uia_container",
                index: 0,
                advisoryElapsedLimitMs: RealizeItemLimits.MaximumAdvisoryElapsedLimitMs + 1)), Is.False);
            Assert.That(IsValid(schema, Request(
                containerElementId: "uia_container",
                index: 0,
                pollIntervalMs: RealizeItemLimits.MinimumPollIntervalMs - 1)), Is.False);
            Assert.That(IsValid(schema, Request(
                containerElementId: "uia_container",
                index: 0,
                pollIntervalMs: RealizeItemLimits.MaximumPollIntervalMs + 1)), Is.False);
        });
    }

    [TestCase(null)]
    [TestCase("diagnostics")]
    public async Task Output_schema_exposes_mutation_and_reusability_evidence(string? toolProfile)
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe, toolProfile: toolProfile);
        var tool = (await mcp.ListToolsAsync()).Single(candidate => candidate.Name == "realize_item");
        var outputSchema = tool.ProtocolTool.OutputSchema
            ?? throw new AssertionException("realize_item did not advertise an output schema.");
        var success = outputSchema.GetProperty("oneOf").EnumerateArray().Single(IsSuccessBranch);
        var propertyNames = success.GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();

        Assert.That(
            propertyNames,
            Is.SupersetOf(new[]
            {
                "requestedIdentity",
                "methodUsed",
                "realizeInvoked",
                "postconditionVerified",
                "findItemByPropertyCalls",
                "postconditionPolls",
                "elapsedMs",
                "stopReason",
                "viewportMayHaveChanged",
                "dataOrContainerLoadingMayHaveOccurred",
                "reusable",
                "windowHandleUsed",
                "recoveryReason",
                "element",
                "failure"
            }));
    }

    private static Dictionary<string, object?> Request(
        object? containerLocator = null,
        string? containerElementId = null,
        int? index = null,
        string? name = null,
        int? maxProviderCalls = null,
        int? advisoryElapsedLimitMs = null,
        int? pollIntervalMs = null)
    {
        var request = new Dictionary<string, object?> { ["sessionId"] = "session" };
        AddIfNotNull(request, "containerLocator", containerLocator);
        AddIfNotNull(request, "containerElementId", containerElementId);
        AddIfNotNull(request, "index", index);
        AddIfNotNull(request, "name", name);
        AddIfNotNull(request, "maxProviderCalls", maxProviderCalls);
        AddIfNotNull(request, "advisoryElapsedLimitMs", advisoryElapsedLimitMs);
        AddIfNotNull(request, "pollIntervalMs", pollIntervalMs);
        return request;
    }

    private static void AddIfNotNull(Dictionary<string, object?> values, string name, object? value)
    {
        if (value is not null)
        {
            values[name] = value;
        }
    }

    private static bool IsValid(JsonSchema schema, object value) =>
        schema.Evaluate(
            JsonSerializer.SerializeToElement(value),
            new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;

    private static JsonElement FindExclusiveSelectorSchema(JsonElement root, string propertyName)
    {
        foreach (var constraint in root.GetProperty("allOf").EnumerateArray())
        {
            if (!constraint.TryGetProperty("oneOf", out var branches))
            {
                continue;
            }

            foreach (var branch in branches.EnumerateArray())
            {
                if (branch.TryGetProperty("required", out var required) &&
                    required.EnumerateArray().Any(item => item.GetString() == propertyName))
                {
                    return branch.GetProperty("properties").GetProperty(propertyName);
                }
            }
        }

        throw new AssertionException($"No exclusive schema branch requires '{propertyName}'.");
    }

    private static bool IsSuccessBranch(JsonElement branch) =>
        !branch.TryGetProperty("required", out var required) ||
        !required.EnumerateArray().Any(item => item.GetString() == "error");
}

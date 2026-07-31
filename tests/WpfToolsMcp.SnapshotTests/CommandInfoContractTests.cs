using System.Text.Json;
using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class CommandInfoContractTests
{
    [Test]
    public void Current_agent_capabilities_advertise_command_info()
    {
        Assert.That(
            AgentProtocolCapabilities.Current,
            Does.Contain(AgentProtocolCapabilities.GetCommandInfo));
        Assert.That(
            AgentProtocolCapabilities.GetCommandInfo,
            Is.EqualTo("wpf/get_command_info:v1"));
    }

    [Test]
    public void Structured_command_states_round_trip_without_private_element_ids()
    {
        var response = new GetCommandInfoResponse(
            Element: new ElementRef(
                "Button",
                "SaveButton",
                "Save",
                "/Window/Button",
                ElementId: "wpf_public"),
            Source: new CommandSourceInfo(
                CommandInspectionStatus.Available,
                "System.Windows.Controls.Button",
                "System.Windows.Controls.Primitives.ButtonBase.Command",
                new CommandIdentityInfo(
                    "System.Windows.Input.RoutedUICommand",
                    Name: "Save",
                    OwnerType: "Example.Commands"),
                new CommandMemberValue(CommandInspectionStatus.Null),
                new CommandTargetInfo(CommandInspectionStatus.Null)),
            ControlIsEnabled: new CommandEnabledInfo(CommandInspectionStatus.Available, true),
            CanExecute: new CommandCanExecuteInfo(
                CommandInspectionStatus.Available,
                CanExecute: false,
                Mode: CommandCanExecuteMode.RoutedCommand,
                EffectiveTarget: new CommandTargetInfo(
                    CommandInspectionStatus.Available,
                    Type: "System.Windows.Controls.Button"),
                UsedCommandSourceFallback: true),
            ContextChain: [],
            Counts: new CommandInspectionCounts(0, 0, 0, 0, 0),
            ParentChainStatus: CommandInspectionStatus.Available,
            Truncated: false)
        {
            WindowHandleUsed = 42
        };

        var json = JsonSerializer.Serialize(response);
        var roundTrip = JsonSerializer.Deserialize<GetCommandInfoResponse>(json);

        Assert.Multiple(() =>
        {
            Assert.That(roundTrip, Is.Not.Null);
            Assert.That(roundTrip!.Element.ElementId, Is.EqualTo("wpf_public"));
            Assert.That(roundTrip.Element.ElementIdWpf, Is.Null);
            Assert.That(roundTrip.CanExecute.CanExecute, Is.False);
            Assert.That(roundTrip.CanExecute.UsedCommandSourceFallback, Is.True);
            Assert.That(roundTrip.WindowHandleUsed, Is.EqualTo(42));
            Assert.That(json, Does.Contain("\"RoutedCommand\""));
            Assert.That(json, Does.Not.Contain("elementIdWpf"));
        });
    }

    [Test]
    public void Missing_command_info_capability_requires_a_current_agent()
    {
        var callInvoked = false;
        var exception = AutomationController.CreateGetCommandInfoCapabilityException();
        var gatedException = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            _ = await AutomationController.CallGetCommandInfoWhenSupportedAsync(
                new AgentCapabilitiesResponse(
                    AgentProtocolCapabilities.CurrentProtocolVersion,
                    Capabilities: []),
                () =>
                {
                    callInvoked = true;
                    return Task.FromResult("unexpected");
                }));

        Assert.Multiple(() =>
        {
            Assert.That(callInvoked, Is.False);
            Assert.That(gatedException!.Message, Is.EqualTo(exception.Message));
            Assert.That(
                exception.Message,
                Is.EqualTo(
                    "agent_capability_unavailable: get_command_info requires the current WPF agent. " +
                    "Restart the target application, start a new MCP session, and attach again so the current agent can be injected."));
        });
    }

    [Test]
    public async Task Advertised_command_info_capability_allows_the_agent_call()
    {
        var capabilities = new AgentCapabilitiesResponse(
            AgentProtocolCapabilities.CurrentProtocolVersion,
            [AgentProtocolCapabilities.GetCommandInfo]);

        var result = await AutomationController.CallGetCommandInfoWhenSupportedAsync(
            capabilities,
            () => Task.FromResult("sent"));

        Assert.That(result, Is.EqualTo("sent"));
    }

    [Test]
    public void Command_results_promote_agent_identity_to_a_public_stable_handle()
    {
        using var controller = new AutomationController();
        var agentElement = new ElementRef(
            "Button",
            "SaveButton",
            "Save",
            "/Window/Button",
            ElementIdWpf: "agent-private");

        var locatorResult = controller.RegisterCommandElement(agentElement, windowHandle: 42);
        var handleResult = AutomationController.WithPublicCommandElementId(agentElement, "wpf_existing");

        Assert.Multiple(() =>
        {
            Assert.That(locatorResult.ElementId, Does.StartWith("wpf_"));
            Assert.That(locatorResult.ElementIdWpf, Is.Null);
            Assert.That(handleResult.ElementId, Is.EqualTo("wpf_existing"));
            Assert.That(handleResult.ElementIdWpf, Is.Null);
        });
    }
}

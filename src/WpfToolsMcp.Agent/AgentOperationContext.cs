using System.Windows;
using System.Windows.Threading;

namespace WpfToolsMcp.Agent;

internal sealed class AgentOperationContext
{
    public AgentOperationContext(UiThreadLatencyRecorder uiThreadLatency)
    {
        UiThreadLatency = uiThreadLatency;
    }

    public UiThreadLatencyRecorder UiThreadLatency { get; }

    public Dispatcher Dispatcher =>
        Application.Current?.Dispatcher ?? throw AgentOperationException.DispatcherUnavailable();
}

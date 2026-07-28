using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

public sealed class WpfStateObservation
{
    private int _lifecycleState;

    internal WpfStateObservation(
        AgentClient client,
        int processId,
        ObserveStateStartResponse started)
    {
        Client = client;
        ProcessId = processId;
        Started = started;
    }

    public ObserveStateStartResponse Started { get; }

    internal AgentClient Client { get; }

    internal int ProcessId { get; }

    internal bool IsReleased => Volatile.Read(ref _lifecycleState) == 1;

    internal bool IsLost => Volatile.Read(ref _lifecycleState) == 2;

    internal void MarkReleased() => Interlocked.CompareExchange(ref _lifecycleState, 1, 0);

    internal void MarkLost() => Interlocked.CompareExchange(ref _lifecycleState, 2, 0);
}

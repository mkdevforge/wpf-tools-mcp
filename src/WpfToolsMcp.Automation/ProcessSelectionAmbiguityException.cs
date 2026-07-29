using System.Text;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

public sealed class ProcessSelectionAmbiguityException : InvalidOperationException
{
    public ProcessSelectionAmbiguityException(ProcessSelectionAmbiguity ambiguity)
        : base(BuildMessage(ambiguity))
    {
        Ambiguity = ambiguity ?? throw new ArgumentNullException(nameof(ambiguity));
    }

    public ProcessSelectionAmbiguity Ambiguity { get; }

    private static string BuildMessage(ProcessSelectionAmbiguity ambiguity)
    {
        ArgumentNullException.ThrowIfNull(ambiguity);

        var message = new StringBuilder()
            .Append(ambiguity.Code)
            .Append(": ")
            .Append(ambiguity.DiscoveredCandidates)
            .Append(" live processes match '")
            .Append(ambiguity.RequestedProcessName)
            .Append("'. ")
            .Append(ambiguity.Recovery);

        foreach (var candidate in ambiguity.Candidates)
        {
            message.Append(" [pid=")
                .Append(candidate.Pid)
                .Append(", started=")
                .Append(candidate.StartTimeUtc)
                .Append(", window=")
                .Append(candidate.MainWindowHandle)
                .Append(", title=")
                .Append(candidate.MainWindowTitle)
                .Append(']');
        }

        return message.ToString();
    }
}

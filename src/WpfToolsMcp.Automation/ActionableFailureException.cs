using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

public sealed class ActionableFailureException : InvalidOperationException
{
    public ActionableFailureException(FailureInfo failure)
        : base(CreateMessage(failure))
    {
        Failure = failure;
    }

    internal ActionableFailureException(FailureInfo failure, Exception diagnosticCause)
        : base(CreateMessage(failure), diagnosticCause ?? throw new ArgumentNullException(nameof(diagnosticCause)))
    {
        Failure = failure;
        DiagnosticCause = diagnosticCause;
    }

    public FailureInfo Failure { get; }

    internal Exception? DiagnosticCause { get; }

    private static string CreateMessage(FailureInfo failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return $"{failure.Code}: {failure.Detail}";
    }
}

namespace WpfToolsMcp.Agent;

internal static class AgentToolError
{
    private static readonly object CodeKey = new();

    public static InvalidOperationException InvalidOperation(
        string code,
        string message,
        Exception? innerException = null) =>
        Mark(
            innerException is null
                ? new InvalidOperationException(message)
                : new InvalidOperationException(message, innerException),
            code);

    public static ArgumentException InvalidArgument(
        string code,
        string message,
        string? parameterName = null) =>
        Mark(new ArgumentException(message, parameterName), code);

    public static T Mark<T>(T exception, string code)
        where T : Exception
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        exception.Data[CodeKey] = code;
        return exception;
    }

    public static string? GetCode(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Data[CodeKey] as string;
    }
}

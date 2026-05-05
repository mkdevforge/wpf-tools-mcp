namespace WpfToolsMcp.Contracts;

internal abstract record ElementTarget
{
    public abstract ElementLocator? Locator { get; }

    public abstract string? ElementId { get; }

    public abstract long? WindowHandle { get; init; }

    public bool IsLocator => Locator is not null;

    public ElementTarget WithWindowHandle(long? windowHandle) =>
        this switch
        {
            ByLocator locator => locator with { WindowHandle = windowHandle },
            ByElementId elementId => elementId with { WindowHandle = windowHandle },
            _ => throw new InvalidOperationException("Unknown element target shape.")
        };

    public static ElementTarget Parse(
        ElementLocator? locator,
        string? elementId,
        long? windowHandle,
        string? operationName = null,
        bool requireWindowHandleForElementId = false)
    {
        var hasLocator = locator is not null;
        var id = string.IsNullOrWhiteSpace(elementId) ? null : elementId.Trim();
        var hasElementId = id is not null;

        if (hasLocator == hasElementId)
        {
            throw new ArgumentException(CreateExactlyOneTargetError(operationName));
        }

        if (id is null)
        {
            return new ByLocator(locator!, windowHandle);
        }

        if (requireWindowHandleForElementId && windowHandle is not > 0)
        {
            throw new ArgumentException("invalid_request: windowHandle is required with elementId.");
        }

        return new ByElementId(id, windowHandle);
    }

    private static string CreateExactlyOneTargetError(string? operationName) =>
        string.IsNullOrWhiteSpace(operationName)
            ? "invalid_request: provide exactly one of locator OR elementId."
            : $"invalid_request: {operationName} requires exactly one of locator OR elementId.";

    public sealed record ByLocator : ElementTarget
    {
        public ByLocator(ElementLocator locator, long? windowHandle)
        {
            Value = locator;
            WindowHandle = windowHandle;
        }

        public ElementLocator Value { get; init; }

        public override ElementLocator? Locator => Value;

        public override string? ElementId => null;

        public override long? WindowHandle { get; init; }
    }

    public sealed record ByElementId : ElementTarget
    {
        public ByElementId(string elementId, long? windowHandle)
        {
            Value = elementId;
            WindowHandle = windowHandle;
        }

        public string Value { get; init; }

        public override ElementLocator? Locator => null;

        public override string? ElementId => Value;

        public override long? WindowHandle { get; init; }
    }
}

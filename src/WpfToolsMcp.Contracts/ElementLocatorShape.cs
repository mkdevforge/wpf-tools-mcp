namespace WpfToolsMcp.Contracts;

internal enum ElementLocatorShapeKind
{
    XPath,
    IndexOnly,
    Filter
}

internal readonly record struct ElementLocatorShape(ElementLocatorShapeKind Kind)
{
    private const string RequiredFields =
        "xpath, automationId, automationIdContains, name, nameContains, className, classNameContains, typeEquals, controlTypeEquals, index";

    public static ElementLocatorShape Parse(ElementLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);

        if (locator.Index is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(locator), "invalid_request: index must be >= 0.");
        }

        var hasXPath = !string.IsNullOrWhiteSpace(locator.XPath);
        var hasFilter = HasFilter(locator);
        var hasIndex = locator.Index is not null;

        if (hasXPath && hasIndex)
        {
            throw new ArgumentException("invalid_request: index cannot be used with xpath.", nameof(locator));
        }

        if (!hasXPath && !hasFilter && !hasIndex)
        {
            throw new ArgumentException(
                $"invalid_request: locator must specify at least one of: {RequiredFields}.",
                nameof(locator));
        }

        if (hasXPath)
        {
            return new ElementLocatorShape(ElementLocatorShapeKind.XPath);
        }

        return hasFilter
            ? new ElementLocatorShape(ElementLocatorShapeKind.Filter)
            : new ElementLocatorShape(ElementLocatorShapeKind.IndexOnly);
    }

    private static bool HasFilter(ElementLocator locator) =>
        !string.IsNullOrWhiteSpace(locator.AutomationId) ||
        !string.IsNullOrWhiteSpace(locator.AutomationIdContains) ||
        !string.IsNullOrWhiteSpace(locator.Name) ||
        !string.IsNullOrWhiteSpace(locator.NameContains) ||
        !string.IsNullOrWhiteSpace(locator.ClassName) ||
        !string.IsNullOrWhiteSpace(locator.ClassNameContains) ||
        !string.IsNullOrWhiteSpace(locator.TypeEquals) ||
        !string.IsNullOrWhiteSpace(locator.ControlTypeEquals);
}

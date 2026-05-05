using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class ElementLocatorShapeDesignTests
{
    [Test]
    public void Parse_rejects_empty_locator()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ElementLocatorShape.Parse(new ElementLocator()));

        Assert.That(ex?.Message, Does.Contain("invalid_request: locator must specify at least one of:"));
    }

    [Test]
    public void Parse_rejects_xpath_with_index()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ElementLocatorShape.Parse(new ElementLocator(XPath: "/Window/Button[1]", Index: 0)));

        Assert.That(ex?.Message, Does.Contain("invalid_request: index cannot be used with xpath."));
    }

    [Test]
    public void Parse_rejects_negative_index()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => ElementLocatorShape.Parse(new ElementLocator(Name: "OK", Index: -1)));

        Assert.That(ex?.Message, Does.Contain("invalid_request: index must be >= 0."));
    }

    [Test]
    public void Parse_allows_index_only_locator()
    {
        var shape = ElementLocatorShape.Parse(new ElementLocator(Index: 0));

        Assert.That(shape.Kind, Is.EqualTo(ElementLocatorShapeKind.IndexOnly));
    }
}

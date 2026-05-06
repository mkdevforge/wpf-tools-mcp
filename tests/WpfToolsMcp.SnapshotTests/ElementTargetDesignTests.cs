using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class ElementTargetDesignTests
{
    [Test]
    public void Parse_creates_locator_target_with_window_handle()
    {
        var locator = new ElementLocator(AutomationId: "Basic_Button");

        var target = ElementTarget.Parse(locator, null, 42, operationName: "click_element");

        Assert.That(target, Is.TypeOf<ElementTarget.ByLocator>());
        var locatorTarget = (ElementTarget.ByLocator)target;
        Assert.That(locatorTarget.Value, Is.SameAs(locator));
        Assert.That(locatorTarget.WindowHandle, Is.EqualTo(42));
        Assert.That(locatorTarget.ElementId, Is.Null);
    }

    [Test]
    public void Parse_creates_trimmed_element_id_target()
    {
        var target = ElementTarget.Parse(null, " uia_123 ", 42, operationName: "click_element");

        Assert.That(target, Is.TypeOf<ElementTarget.ByElementId>());
        var elementIdTarget = (ElementTarget.ByElementId)target;
        Assert.That(elementIdTarget.Value, Is.EqualTo("uia_123"));
        Assert.That(elementIdTarget.WindowHandle, Is.EqualTo(42));
        Assert.That(elementIdTarget.Locator, Is.Null);
    }

    [Test]
    public void WithInheritedWindowHandle_updates_only_locator_targets()
    {
        var locator = new ElementLocator(AutomationId: "Basic_Button");
        var locatorTarget = ElementTarget.Parse(locator, null, null, operationName: "click_element");
        var elementIdTarget = ElementTarget.Parse(null, "uia_123", 7, operationName: "click_element");

        var inheritedLocator = locatorTarget.WithInheritedWindowHandle(42);
        var inheritedElementId = elementIdTarget.WithInheritedWindowHandle(42);

        Assert.That(inheritedLocator.WindowHandle, Is.EqualTo(42));
        Assert.That(inheritedElementId.WindowHandle, Is.EqualTo(7));
    }

    [Test]
    public void Parse_requires_window_handle_for_element_id_when_policy_demands_it()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ElementTarget.Parse(
                null,
                "wpf_123",
                null,
                requireWindowHandleForElementId: true));

        Assert.That(ex?.Message, Does.Contain("invalid_request: windowHandle is required with elementId."));
    }

    [Test]
    public void ParseOptional_allows_absent_target_and_rejects_mixed_target()
    {
        Assert.That(ElementTarget.ParseOptional(null, null, null, "type_text"), Is.Null);

        var ex = Assert.Throws<ArgumentException>(
            () => ElementTarget.ParseOptional(
                new ElementLocator(AutomationId: "Basic_TextBox"),
                "uia_123",
                null,
                "type_text"));

        Assert.That(ex?.Message, Does.Contain("invalid_request: type_text requires at most one of locator OR elementId."));
    }
}

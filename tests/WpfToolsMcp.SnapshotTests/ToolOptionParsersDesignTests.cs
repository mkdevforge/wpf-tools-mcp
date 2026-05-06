using WpfToolsMcp.Contracts;
using WpfToolsMcp.McpServer.Tools;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class ToolOptionParsersDesignTests
{
    [TestCase(null, ClickType.Single)]
    [TestCase("", ClickType.Single)]
    [TestCase("single", ClickType.Single)]
    [TestCase("left", ClickType.Single)]
    [TestCase("leftClick", ClickType.Single)]
    [TestCase("left_click", ClickType.Single)]
    [TestCase("double", ClickType.Double)]
    [TestCase("doubleClick", ClickType.Double)]
    [TestCase("double_click", ClickType.Double)]
    [TestCase("right", ClickType.Right)]
    [TestCase("rightClick", ClickType.Right)]
    [TestCase("right_click", ClickType.Right)]
    [TestCase("context", ClickType.Right)]
    [TestCase("contextMenu", ClickType.Right)]
    [TestCase("context_menu", ClickType.Right)]
    public void ParseClickType_accepts_shared_core_and_diagnostics_aliases(string? value, ClickType expected)
    {
        Assert.That(ToolOptionParsers.ParseClickType(value), Is.EqualTo(expected));
    }
}

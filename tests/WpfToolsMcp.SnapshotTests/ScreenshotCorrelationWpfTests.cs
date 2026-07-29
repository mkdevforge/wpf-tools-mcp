using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using WpfToolsMcp.Agent;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[Apartment(ApartmentState.STA)]
public sealed class ScreenshotCorrelationWpfTests
{
    [Test]
    public void Template_names_do_not_replace_the_owning_controls_explicit_identity()
    {
        const string xaml = """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             TargetType="{x:Type Button}">
                <Border x:Name="border" />
            </ControlTemplate>
            """;
        var template = (ControlTemplate)XamlReader.Parse(xaml);
        var button = new Button { Template = template };
        AutomationProperties.SetAutomationId(button, "AuthoredButton");

        Assert.That(button.ApplyTemplate(), Is.True);
        var templateBorder = (Border)template.FindName("border", button);

        Assert.Multiple(() =>
        {
            Assert.That(templateBorder.TemplatedParent, Is.SameAs(button));
            Assert.That(WpfVisualTreeInspector.GetScreenshotCorrelationIdentityRank(templateBorder), Is.Zero);
            Assert.That(WpfVisualTreeInspector.GetScreenshotCorrelationIdentityRank(button), Is.EqualTo(3));
        });

        AutomationProperties.SetName(templateBorder, "Authored template part");

        Assert.That(WpfVisualTreeInspector.GetScreenshotCorrelationIdentityRank(templateBorder), Is.EqualTo(2));

        AutomationProperties.SetAutomationId(templateBorder, "AuthoredTemplatePart");

        Assert.That(WpfVisualTreeInspector.GetScreenshotCorrelationIdentityRank(templateBorder), Is.EqualTo(3));
    }

    [Test]
    public void Data_template_children_keep_their_authored_identity()
    {
        const string xaml = """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Button x:Name="authoredChild" Content="Item action" />
            </DataTemplate>
            """;
        var template = (DataTemplate)XamlReader.Parse(xaml);
        var presenter = new ContentPresenter
        {
            Content = new object(),
            ContentTemplate = template
        };

        presenter.Measure(new Size(200, 100));
        presenter.Arrange(new Rect(0, 0, 200, 100));
        presenter.UpdateLayout();
        var child = (Button)template.FindName("authoredChild", presenter);

        Assert.Multiple(() =>
        {
            Assert.That(child.TemplatedParent, Is.SameAs(presenter));
            Assert.That(WpfVisualTreeInspector.GetScreenshotCorrelationIdentityRank(child), Is.EqualTo(2));
        });
    }
}

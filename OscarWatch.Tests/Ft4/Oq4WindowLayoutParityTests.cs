using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace OscarWatch.Tests.Ft4;

public sealed class Oq4WindowLayoutParityTests
{
    private static string ViewsDirectory([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../../OscarWatch/Views"));

    [Fact]
    public void Operating_windows_have_the_same_visual_tree_styles_and_control_order()
    {
        var ft4 = XDocument.Load(Path.Combine(ViewsDirectory(), "Ft4Window.axaml"));
        var oq4 = XDocument.Load(Path.Combine(ViewsDirectory(), "Oq4Window.axaml"));
        var original = ft4.Descendants().ToArray();
        var experimental = oq4.Descendants().ToArray();
        Assert.Equal(original.Select(e => e.Name), experimental.Select(e => e.Name));
        var geometry = new HashSet<string>
        {
            "Width", "Height", "MinWidth", "MinHeight", "Margin", "Padding",
            "ColumnDefinitions", "RowDefinitions", "ColumnSpacing", "RowSpacing", "Spacing",
            "HorizontalAlignment", "VerticalAlignment", "FontFamily", "FontWeight",
            "DockPanel.Dock", "Grid.Row", "Grid.Column", "CornerRadius", "BorderThickness"
        };
        for (var i = 0; i < original.Length; i++)
        {
            foreach (var attribute in original[i].Attributes().Where(a => geometry.Contains(a.Name.LocalName)))
                Assert.Equal(attribute.Value, experimental[i].Attribute(attribute.Name)?.Value);
        }
        XNamespace ns = "https://github.com/avaloniaui";
        Assert.True(XNode.DeepEquals(ft4.Root!.Element(ns + "Window.Styles"), oq4.Root!.Element(ns + "Window.Styles")));
    }

    [Fact]
    public void Oq4_operating_controls_and_context_menu_match_ft4()
    {
        var ft4 = XDocument.Load(Path.Combine(ViewsDirectory(), "Ft4Window.axaml"));
        var oq4 = XDocument.Load(Path.Combine(ViewsDirectory(), "Oq4Window.axaml"));
        static string?[] Labels(XDocument doc, string tag, string attribute) => doc.Descendants()
            .Where(e => e.Name.LocalName == tag).Select(e => e.Attribute(attribute)?.Value).ToArray();
        Assert.Equal(Labels(ft4, "Button", "Content"), Labels(oq4, "Button", "Content"));
        Assert.Equal(Labels(ft4, "MenuItem", "Header"), Labels(oq4, "MenuItem", "Header"));
        Assert.Equal(Labels(ft4, "CheckBox", "ToolTip.Tip"), Labels(oq4, "CheckBox", "ToolTip.Tip"));
    }
}

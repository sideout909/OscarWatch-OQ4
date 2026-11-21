using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace OscarWatch.Tests.Ft4;

public sealed class Oq4WindowLayoutParityTests
{
    private static string ViewsDirectory([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../../OscarWatch/Views"));

    [Fact]
    public void Oq4_retains_waterfall_and_both_activity_lists_with_resizable_divider()
    {
        var oq4 = XDocument.Load(Path.Combine(ViewsDirectory(), "Oq4Window.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var name in new[] { "Waterfall", "BandHeader", "ActivityList", "DecodeColumnsSplitter", "CallingHeader", "CallsToMeList" })
            Assert.Single(oq4.Descendants(), e => (string?)e.Attribute(x + "Name") == name);

        var templates = oq4.Descendants().Where(e => e.Name.LocalName == "DataTemplate").ToArray();
        var header = Assert.Single(templates, e => (string?)e.Attribute(x + "Key") == "Ft4DecodeHeaderTemplate");
        var row = Assert.Single(templates, e => (string?)e.Attribute(x + "Key") == "Ft4DecodeRowTemplate");
        static string?[] Columns(XElement template) => template.Descendants()
            .Where(e => e.Name.LocalName == "ColumnDefinition")
            .Select(e => (string?)e.Attribute("Width") + ":" + (string?)e.Attribute("MinWidth"))
            .ToArray();
        Assert.Equal(Columns(header), Columns(row));
    }

    [Fact]
    public void Oq4_retains_core_operating_controls()
    {
        var oq4 = XDocument.Load(Path.Combine(ViewsDirectory(), "Oq4Window.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var name in new[] { "MessageBox", "SendButton", "StopButton", "TuneButton", "ReportButton", "Send73Button", "EvenSlot", "HoldTx", "AutoReply", "Pounce", "TxFrequency" })
            Assert.Single(oq4.Descendants(), e => (string?)e.Attribute(x + "Name") == name);
        Assert.Contains(oq4.Descendants(), e => (string?)e.Attribute("Content") == "{loc:Localize Key=Ft4.CallCq}");
    }
}

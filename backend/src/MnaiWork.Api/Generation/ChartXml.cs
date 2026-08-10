using System.Globalization;
using System.Security;
using System.Text;

namespace MnaiWork.Api.Generation;

/// <summary>
/// Builds the DrawingML chart XML (c:chartSpace) for a native, editable PowerPoint chart.
/// Supports bar, line and pie. The output is fed into a ChartPart referenced by a graphicFrame.
/// </summary>
public static class ChartXml
{
    private const string CNs = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private const string ANs = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string RNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private static readonly string[] Palette =
    {
        "4C6EF5", "22D3EE", "F97316", "10B981", "F43F5E", "A855F7", "EAB308", "14B8A6"
    };

    public static string Build(ChartSpec chart, DeckTheme theme)
    {
        var type = (chart.Type ?? "bar").Trim().ToLowerInvariant();
        var categories = chart.Categories;
        var series = chart.Series;

        var plot = type switch
        {
            "line" => LineChart(categories, series),
            "pie" => PieChart(categories, series),
            _ => BarChart(categories, series)
        };

        // Pie has no axes; bar/line reference the two axis ids used below.
        var axes = type == "pie" ? string.Empty :
            "<c:catAx><c:axId val=\"111111111\"/><c:scaling><c:orientation val=\"minMax\"/></c:scaling>" +
            "<c:delete val=\"0\"/><c:axPos val=\"b\"/><c:crossAx val=\"222222222\"/></c:catAx>" +
            "<c:valAx><c:axId val=\"222222222\"/><c:scaling><c:orientation val=\"minMax\"/></c:scaling>" +
            "<c:delete val=\"0\"/><c:axPos val=\"l\"/><c:crossAx val=\"111111111\"/></c:valAx>";

        var legend = series.Count > 1 || type == "pie"
            ? "<c:legend><c:legendPos val=\"b\"/><c:overlay val=\"0\"/></c:legend>"
            : string.Empty;

        return
            $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            $"<c:chartSpace xmlns:c=\"{CNs}\" xmlns:a=\"{ANs}\" xmlns:r=\"{RNs}\">" +
            "<c:chart><c:autoTitleDeleted val=\"1\"/><c:plotArea><c:layout/>" +
            plot + axes +
            "</c:plotArea>" + legend +
            "<c:plotVisOnly val=\"1\"/></c:chart></c:chartSpace>";
    }

    private static string BarChart(List<string> categories, List<ChartSeriesSpec> series)
    {
        var sb = new StringBuilder();
        sb.Append("<c:barChart><c:barDir val=\"col\"/><c:grouping val=\"clustered\"/><c:varyColors val=\"0\"/>");
        for (int i = 0; i < series.Count; i++)
        {
            sb.Append(SeriesXml(i, series[i], categories, solidFill: Palette[i % Palette.Length]));
        }
        sb.Append("<c:axId val=\"111111111\"/><c:axId val=\"222222222\"/></c:barChart>");
        return sb.ToString();
    }

    private static string LineChart(List<string> categories, List<ChartSeriesSpec> series)
    {
        var sb = new StringBuilder();
        sb.Append("<c:lineChart><c:grouping val=\"standard\"/><c:varyColors val=\"0\"/>");
        for (int i = 0; i < series.Count; i++)
        {
            sb.Append(SeriesXml(i, series[i], categories, lineFill: Palette[i % Palette.Length], marker: true));
        }
        sb.Append("<c:marker val=\"1\"/><c:axId val=\"111111111\"/><c:axId val=\"222222222\"/></c:lineChart>");
        return sb.ToString();
    }

    private static string PieChart(List<string> categories, List<ChartSeriesSpec> series)
    {
        var sb = new StringBuilder();
        sb.Append("<c:pieChart><c:varyColors val=\"1\"/>");
        var first = series.Count > 0 ? series[0] : new ChartSeriesSpec();
        sb.Append("<c:ser><c:idx val=\"0\"/><c:order val=\"0\"/>");
        sb.Append(SeriesName(first.Name ?? "Series 1", 0));
        // Per-point colors.
        for (int p = 0; p < categories.Count; p++)
        {
            sb.Append($"<c:dPt><c:idx val=\"{p}\"/><c:bubble3D val=\"0\"/>" +
                      $"<c:spPr><a:solidFill><a:srgbClr val=\"{Palette[p % Palette.Length]}\"/></a:solidFill></c:spPr></c:dPt>");
        }
        sb.Append(CatData(categories));
        sb.Append(ValData(first.Values, categories.Count, 0));
        sb.Append("</c:ser><c:firstSliceAng val=\"0\"/></c:pieChart>");
        return sb.ToString();
    }

    private static string SeriesXml(int idx, ChartSeriesSpec s, List<string> categories,
        string? solidFill = null, string? lineFill = null, bool marker = false)
    {
        var sb = new StringBuilder();
        sb.Append($"<c:ser><c:idx val=\"{idx}\"/><c:order val=\"{idx}\"/>");
        sb.Append(SeriesName(s.Name ?? $"Series {idx + 1}", idx));
        if (solidFill is not null)
        {
            sb.Append($"<c:spPr><a:solidFill><a:srgbClr val=\"{solidFill}\"/></a:solidFill></c:spPr>");
        }
        else if (lineFill is not null)
        {
            sb.Append($"<c:spPr><a:ln w=\"28575\"><a:solidFill><a:srgbClr val=\"{lineFill}\"/></a:solidFill></a:ln></c:spPr>");
        }
        if (marker)
        {
            sb.Append("<c:marker><c:symbol val=\"circle\"/><c:size val=\"6\"/></c:marker>");
        }
        sb.Append(CatData(categories));
        sb.Append(ValData(s.Values, categories.Count, idx));
        sb.Append("</c:ser>");
        return sb.ToString();
    }

    // Data columns start at B (categories occupy column A).
    private static string Col(int seriesIdx) => ((char)('B' + seriesIdx)).ToString();

    private static string SeriesName(string name, int seriesIdx)
    {
        var col = Col(seriesIdx);
        return $"<c:tx><c:strRef><c:f>Sheet1!${col}$1</c:f><c:strCache><c:ptCount val=\"1\"/>" +
               $"<c:pt idx=\"0\"><c:v>{Esc(name)}</c:v></c:pt></c:strCache></c:strRef></c:tx>";
    }

    private static string CatData(List<string> categories)
    {
        var sb = new StringBuilder();
        sb.Append($"<c:cat><c:strRef><c:f>Sheet1!$A$2:$A${categories.Count + 1}</c:f>" +
                  $"<c:strCache><c:ptCount val=\"{categories.Count}\"/>");
        for (int i = 0; i < categories.Count; i++)
        {
            sb.Append($"<c:pt idx=\"{i}\"><c:v>{Esc(categories[i])}</c:v></c:pt>");
        }
        sb.Append("</c:strCache></c:strRef></c:cat>");
        return sb.ToString();
    }

    private static string ValData(List<double> values, int count, int seriesIdx)
    {
        var col = Col(seriesIdx);
        var sb = new StringBuilder();
        sb.Append($"<c:val><c:numRef><c:f>Sheet1!${col}$2:${col}${count + 1}</c:f>" +
                  $"<c:numCache><c:formatCode>General</c:formatCode><c:ptCount val=\"{count}\"/>");
        for (int i = 0; i < count; i++)
        {
            var v = i < values.Count ? values[i] : 0;
            sb.Append($"<c:pt idx=\"{i}\"><c:v>{v.ToString(CultureInfo.InvariantCulture)}</c:v></c:pt>");
        }
        sb.Append("</c:numCache></c:numRef></c:val>");
        return sb.ToString();
    }

    private static string Esc(string s) => SecurityElement.Escape(s ?? string.Empty) ?? string.Empty;
}

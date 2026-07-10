using System.Security;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using D = DocumentFormat.OpenXml.Drawing;

namespace MnaiWork.Api.Generation;

/// <summary>
/// Renders a <see cref="DeckSpec"/> into a polished, themed .pptx entirely in-process
/// (no sandbox / headless browser). Slides are composed as DrawingML shape trees so the
/// visual design is fully controlled here rather than delegated to templates.
/// </summary>
public sealed class PptxGenerator
{
    // 16:9 canvas in EMUs (1 inch = 914400 EMU).
    private const long SlideW = 12192000;
    private const long SlideH = 6858000;
    private const long Margin = 685800; // 0.75"

    public byte[] Generate(DeckSpec spec)
    {
        var theme = Themes.Get(spec.Theme);
        using var ms = new MemoryStream();

        using (var doc = PresentationDocument.Create(ms, PresentationDocumentType.Presentation))
        {
            var presentationPart = doc.AddPresentationPart();
            presentationPart.Presentation = new Presentation();

            var masterPart = presentationPart.AddNewPart<SlideMasterPart>("rIdMaster");
            var themePart = masterPart.AddNewPart<ThemePart>("rIdTheme");
            themePart.Theme = new D.Theme(ThemeXml(theme));

            var layoutPart = masterPart.AddNewPart<SlideLayoutPart>("rIdLayout");
            layoutPart.SlideLayout = new SlideLayout(LayoutXml());
            layoutPart.AddPart(masterPart); // layout -> master relationship

            masterPart.SlideMaster = new SlideMaster(MasterXml());

            var slides = spec.Slides is { Count: > 0 } ? spec.Slides : new List<SlideSpec>
            {
                new() { Layout = "title", Title = spec.Title, Subtitle = spec.Subtitle }
            };

            var slideIds = new StringBuilder();
            var masterRel = presentationPart.GetIdOfPart(masterPart);
            uint slideIdSeed = 256;

            for (int i = 0; i < slides.Count; i++)
            {
                var relId = $"rIdSlide{i + 1}";
                var slidePart = presentationPart.AddNewPart<SlidePart>(relId);
                slidePart.Slide = new Slide(BuildSlideXml(spec, slides[i], theme, i, slides.Count));
                slidePart.AddPart(layoutPart, "rIdLayout");
                slideIds.Append($"<p:sldId id=\"{slideIdSeed++}\" r:id=\"{relId}\"/>");
            }

            presentationPart.Presentation = new Presentation(
                "<p:presentation xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
                "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
                "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" saveSubsetFonts=\"1\">" +
                $"<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"{masterRel}\"/></p:sldMasterIdLst>" +
                $"<p:sldIdLst>{slideIds}</p:sldIdLst>" +
                $"<p:sldSz cx=\"{SlideW}\" cy=\"{SlideH}\" type=\"screen16x9\"/>" +
                "<p:notesSz cx=\"6858000\" cy=\"9144000\"/>" +
                "</p:presentation>");

            presentationPart.Presentation.Save();
        }

        return ms.ToArray();
    }

    // -------------------------------------------------------------------
    // Slide composition
    // -------------------------------------------------------------------

    private static string BuildSlideXml(DeckSpec spec, SlideSpec slide, DeckTheme theme, int index, int total)
    {
        var shapes = new StringBuilder();
        int id = 2;

        string layout = (slide.Layout ?? "bullets").Trim().ToLowerInvariant();
        bool dark = layout is "title" or "section" or "quote" or "closing";
        string bg = dark ? theme.CoverBg : theme.ContentBg;
        string ink = dark ? theme.CoverInk : theme.ContentInk;

        // Full-bleed background.
        shapes.Append(Rect(id++, "bg", 0, 0, SlideW, SlideH, bg));

        switch (layout)
        {
            case "title":
                shapes.Append(AccentBar(id++, Margin, In(3.55), theme.Accent));
                shapes.Append(TextBox(id++, "title", Margin, In(2.2), SlideW - 2 * Margin, In(1.5),
                    Para(Esc(slide.Title ?? spec.Title), 46, ink, true, theme.HeadingFont, "l")));
                if (!string.IsNullOrWhiteSpace(slide.Subtitle ?? spec.Subtitle))
                {
                    shapes.Append(TextBox(id++, "subtitle", Margin, In(3.75), SlideW - 2 * Margin, In(1.2),
                        Para(Esc(slide.Subtitle ?? spec.Subtitle!), 22, theme.Accent, false, theme.BodyFont, "l")));
                }
                if (!string.IsNullOrWhiteSpace(spec.Author))
                {
                    shapes.Append(TextBox(id++, "author", Margin, SlideH - In(0.9), SlideW - 2 * Margin, In(0.5),
                        Para(Esc(spec.Author!), 13, theme.Muted, false, theme.BodyFont, "l")));
                }
                break;

            case "section":
                shapes.Append(TextBox(id++, "kicker", Margin, In(2.5), SlideW - 2 * Margin, In(0.6),
                    Para($"{(index + 1):00}", 20, theme.Accent, true, theme.HeadingFont, "l")));
                shapes.Append(AccentBar(id++, Margin, In(3.15), theme.Accent));
                shapes.Append(TextBox(id++, "title", Margin, In(3.35), SlideW - 2 * Margin, In(1.6),
                    Para(Esc(slide.Title ?? ""), 40, theme.CoverInk, true, theme.HeadingFont, "l")));
                break;

            case "quote":
                shapes.Append(TextBox(id++, "mark", Margin, In(1.4), In(2), In(1.5),
                    Para("\u201C", 96, theme.Accent, true, theme.HeadingFont, "l")));
                shapes.Append(TextBox(id++, "quote", Margin, In(2.6), SlideW - 2 * Margin, In(2.4),
                    Para(Esc(slide.Quote ?? slide.Title ?? ""), 30, theme.CoverInk, false, theme.HeadingFont, "l")));
                if (!string.IsNullOrWhiteSpace(slide.Attribution))
                {
                    shapes.Append(TextBox(id++, "attr", Margin, In(5.2), SlideW - 2 * Margin, In(0.6),
                        Para("— " + Esc(slide.Attribution!), 18, theme.Accent, false, theme.BodyFont, "l")));
                }
                break;

            case "closing":
                shapes.Append(AccentBar(id++, Margin, In(3.35), theme.Accent));
                shapes.Append(TextBox(id++, "title", Margin, In(2.6), SlideW - 2 * Margin, In(1.3),
                    Para(Esc(slide.Title ?? "Thank you"), 44, theme.CoverInk, true, theme.HeadingFont, "l")));
                if (!string.IsNullOrWhiteSpace(slide.Subtitle))
                {
                    shapes.Append(TextBox(id++, "subtitle", Margin, In(3.7), SlideW - 2 * Margin, In(1.0),
                        Para(Esc(slide.Subtitle!), 20, theme.Accent, false, theme.BodyFont, "l")));
                }
                break;

            case "two-column":
                shapes.Append(HeaderBlock(ref id, slide.Title, theme));
                long colW = (SlideW - 2 * Margin - In(0.6)) / 2;
                var cols = slide.Columns is { Count: > 0 }
                    ? slide.Columns
                    : new List<ColumnSpec> { new(), new() };
                for (int c = 0; c < Math.Min(2, cols.Count); c++)
                {
                    long cx = Margin + c * (colW + In(0.6));
                    var body = new StringBuilder();
                    if (!string.IsNullOrWhiteSpace(cols[c].Heading))
                    {
                        body.Append(Para(Esc(cols[c].Heading!), 20, theme.Primary, true, theme.HeadingFont, "l"));
                    }
                    foreach (var b in cols[c].Bullets)
                    {
                        body.Append(BulletPara(Esc(b), 17, ink, theme));
                    }
                    shapes.Append(TextBox(id++, $"col{c}", cx, In(1.9), colW, In(4.4), body.ToString()));
                }
                break;

            default: // "bullets"
                shapes.Append(HeaderBlock(ref id, slide.Title, theme));
                var bullets = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(slide.Subtitle))
                {
                    bullets.Append(Para(Esc(slide.Subtitle!), 19, theme.Muted, false, theme.BodyFont, "l"));
                }
                foreach (var b in slide.Bullets)
                {
                    bullets.Append(BulletPara(Esc(b), 19, ink, theme));
                }
                shapes.Append(TextBox(id++, "body", Margin, In(1.9), SlideW - 2 * Margin, In(4.4), bullets.ToString()));
                break;
        }

        // Footer page number for content slides.
        if (!dark)
        {
            shapes.Append(TextBox(id++, "pageno", SlideW - Margin - In(1.2), SlideH - In(0.7), In(1.2), In(0.4),
                Para($"{index + 1} / {total}", 11, theme.Muted, false, theme.BodyFont, "r")));
        }

        return
            "<p:sld xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
            "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\">" +
            "<p:cSld><p:spTree>" +
            "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
            "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/>" +
            "<a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>" +
            shapes +
            "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>";
    }

    private static string HeaderBlock(ref int id, string? title, DeckTheme theme)
    {
        var sb = new StringBuilder();
        sb.Append(TextBox(id++, "title", Margin, In(0.55), SlideW - 2 * Margin, In(1.0),
            Para(Esc(title ?? ""), 30, theme.Primary, true, theme.HeadingFont, "l")));
        sb.Append(Rect(id++, "rule", Margin, In(1.55), In(1.4), 45720, theme.Accent));
        return sb.ToString();
    }

    // -------------------------------------------------------------------
    // Shape/paragraph helpers (DrawingML fragments)
    // -------------------------------------------------------------------

    private static string AccentBar(int id, long x, long y, string colorHex)
        => Rect(id, "accent", x, y, In(1.6), 64008, colorHex);

    private static string Rect(int id, string name, long x, long y, long cx, long cy, string fillHex)
        => $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>" +
           $"<p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm>" +
           "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>" +
           $"<a:solidFill><a:srgbClr val=\"{fillHex}\"/></a:solidFill></p:spPr>" +
           "<p:txBody><a:bodyPr/><a:lstStyle/><a:p/></p:txBody></p:sp>";

    private static string TextBox(int id, string name, long x, long y, long cx, long cy, string paragraphs, string anchor = "t")
        => $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr>" +
           $"<p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm>" +
           "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/></p:spPr>" +
           $"<p:txBody><a:bodyPr wrap=\"square\" anchor=\"{anchor}\" lIns=\"0\" rIns=\"0\"><a:normAutofit/></a:bodyPr>" +
           $"<a:lstStyle/>{paragraphs}</p:txBody></p:sp>";

    private static string Para(string text, int sizePt, string colorHex, bool bold, string font, string align)
        => $"<a:p><a:pPr algn=\"{align}\"/><a:r><a:rPr lang=\"en-US\" sz=\"{sizePt * 100}\" b=\"{(bold ? 1 : 0)}\" dirty=\"0\">" +
           $"<a:solidFill><a:srgbClr val=\"{colorHex}\"/></a:solidFill>" +
           $"<a:latin typeface=\"{font}\"/></a:rPr><a:t>{text}</a:t></a:r></a:p>";

    private static string BulletPara(string text, int sizePt, string colorHex, DeckTheme theme)
        => "<a:p><a:pPr marL=\"274320\" indent=\"-274320\">" +
           $"<a:spcBef><a:spcPts val=\"600\"/></a:spcBef><a:buClr><a:srgbClr val=\"{theme.Accent}\"/></a:buClr>" +
           "<a:buFont typeface=\"Arial\"/><a:buChar char=\"\u2022\"/></a:pPr>" +
           $"<a:r><a:rPr lang=\"en-US\" sz=\"{sizePt * 100}\" dirty=\"0\"><a:solidFill><a:srgbClr val=\"{colorHex}\"/></a:solidFill>" +
           $"<a:latin typeface=\"{theme.BodyFont}\"/></a:rPr><a:t>{text}</a:t></a:r></a:p>";

    private static long In(double inches) => (long)Math.Round(inches * 914400, MidpointRounding.AwayFromZero);

    private static string Esc(string s) => SecurityElement.Escape(s ?? string.Empty) ?? string.Empty;

    // -------------------------------------------------------------------
    // Master / layout / theme (minimal but valid) parts
    // -------------------------------------------------------------------

    private static string MasterXml() =>
        "<p:sldMaster xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\">" +
        "<p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg>" +
        "<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
        "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/>" +
        "<a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr></p:spTree></p:cSld>" +
        "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" " +
        "accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>" +
        "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rIdLayout\"/></p:sldLayoutIdLst>" +
        "</p:sldMaster>";

    private static string LayoutXml() =>
        "<p:sldLayout xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" type=\"blank\" preserve=\"1\">" +
        "<p:cSld name=\"Blank\"><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
        "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/>" +
        "<a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr></p:spTree></p:cSld>" +
        "<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>";

    private static string ThemeXml(DeckTheme theme)
    {
        string a1 = theme.Primary, a2 = theme.Accent, a3 = theme.Muted;
        return
        "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"MnaiWork\">" +
        "<a:themeElements><a:clrScheme name=\"MnaiWork\">" +
        "<a:dk1><a:srgbClr val=\"111111\"/></a:dk1><a:lt1><a:srgbClr val=\"FFFFFF\"/></a:lt1>" +
        $"<a:dk2><a:srgbClr val=\"{theme.CoverBg}\"/></a:dk2><a:lt2><a:srgbClr val=\"{theme.ContentBg}\"/></a:lt2>" +
        $"<a:accent1><a:srgbClr val=\"{a1}\"/></a:accent1><a:accent2><a:srgbClr val=\"{a2}\"/></a:accent2>" +
        $"<a:accent3><a:srgbClr val=\"{a3}\"/></a:accent3><a:accent4><a:srgbClr val=\"{a2}\"/></a:accent4>" +
        $"<a:accent5><a:srgbClr val=\"{a1}\"/></a:accent5><a:accent6><a:srgbClr val=\"{a3}\"/></a:accent6>" +
        "<a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>" +
        $"<a:fontScheme name=\"MnaiWork\"><a:majorFont><a:latin typeface=\"{theme.HeadingFont}\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>" +
        $"<a:minorFont><a:latin typeface=\"{theme.BodyFont}\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>" +
        "<a:fmtScheme name=\"MnaiWork\">" +
        "<a:fillStyleLst>" +
        "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>" +
        "<a:gradFill rotWithShape=\"1\"><a:gsLst><a:gs pos=\"0\"><a:schemeClr val=\"phClr\"><a:lumMod val=\"110000\"/><a:satMod val=\"105000\"/><a:tint val=\"67000\"/></a:schemeClr></a:gs>" +
        "<a:gs pos=\"50000\"><a:schemeClr val=\"phClr\"><a:lumMod val=\"105000\"/><a:satMod val=\"103000\"/><a:tint val=\"73000\"/></a:schemeClr></a:gs>" +
        "<a:gs pos=\"100000\"><a:schemeClr val=\"phClr\"><a:lumMod val=\"105000\"/><a:satMod val=\"109000\"/><a:tint val=\"81000\"/></a:schemeClr></a:gs></a:gsLst>" +
        "<a:lin ang=\"5400000\" scaled=\"0\"/></a:gradFill>" +
        "<a:gradFill rotWithShape=\"1\"><a:gsLst><a:gs pos=\"0\"><a:schemeClr val=\"phClr\"><a:satMod val=\"103000\"/><a:lumMod val=\"102000\"/><a:tint val=\"94000\"/></a:schemeClr></a:gs>" +
        "<a:gs pos=\"50000\"><a:schemeClr val=\"phClr\"><a:satMod val=\"110000\"/><a:lumMod val=\"100000\"/><a:shade val=\"100000\"/></a:schemeClr></a:gs>" +
        "<a:gs pos=\"100000\"><a:schemeClr val=\"phClr\"><a:lumMod val=\"99000\"/><a:satMod val=\"120000\"/><a:shade val=\"78000\"/></a:schemeClr></a:gs></a:gsLst>" +
        "<a:lin ang=\"5400000\" scaled=\"0\"/></a:gradFill></a:fillStyleLst>" +
        "<a:lnStyleLst>" +
        "<a:ln w=\"6350\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:prstDash val=\"solid\"/></a:ln>" +
        "<a:ln w=\"12700\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:prstDash val=\"solid\"/></a:ln>" +
        "<a:ln w=\"19050\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:prstDash val=\"solid\"/></a:ln></a:lnStyleLst>" +
        "<a:effectStyleLst>" +
        "<a:effectStyle><a:effectLst/></a:effectStyle>" +
        "<a:effectStyle><a:effectLst/></a:effectStyle>" +
        "<a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>" +
        "<a:bgFillStyleLst>" +
        "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>" +
        "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>" +
        "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:bgFillStyleLst></a:fmtScheme>" +
        "</a:themeElements></a:theme>";
    }
}

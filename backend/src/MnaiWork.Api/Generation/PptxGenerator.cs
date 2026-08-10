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

    public byte[] Generate(DeckSpec spec, IReadOnlyDictionary<string, ImageAsset>? images = null)
    {
        images ??= new Dictionary<string, ImageAsset>();
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

                // If the slide references an uploaded image, add it as a part and pass the rId in.
                ImageAsset? asset = null;
                string? imageRel = null;
                if (slides[i].ImageId is { } imgId && images.TryGetValue(imgId, out var found))
                {
                    asset = found;
                    imageRel = "rIdImg";
                    var imagePart = slidePart.AddImagePart(ImagePartTypeFor(found.ContentType), imageRel);
                    using var imgStream = new MemoryStream(found.Bytes, writable: false);
                    imagePart.FeedData(imgStream);
                }

                // If the slide has a chart, add a ChartPart and reference it from a graphicFrame.
                string? chartRel = null;
                if (string.Equals(slides[i].Layout?.Trim(), "chart", StringComparison.OrdinalIgnoreCase)
                    && slides[i].Chart is { } chart && chart.Series.Count > 0)
                {
                    chartRel = "rIdChart";
                    var chartPart = slidePart.AddNewPart<DocumentFormat.OpenXml.Packaging.ChartPart>(chartRel);
                    chartPart.FeedData(new MemoryStream(
                        System.Text.Encoding.UTF8.GetBytes(ChartXml.Build(chart, theme))));
                }

                slidePart.Slide = new Slide(BuildSlideXml(spec, slides[i], theme, i, slides.Count, imageRel, asset, chartRel));
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

    private static string BuildSlideXml(DeckSpec spec, SlideSpec slide, DeckTheme theme, int index, int total,
        string? imageRel = null, ImageAsset? asset = null, string? chartRel = null)
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

            case "cards":
                shapes.Append(HeaderBlock(ref id, slide.Title, theme));
                shapes.Append(CardsGrid(ref id, slide.Cards, theme, ink));
                break;

            case "stats":
                shapes.Append(HeaderBlock(ref id, slide.Title, theme));
                shapes.Append(StatsRow(ref id, slide.Stats, theme));
                if (!string.IsNullOrWhiteSpace(slide.Subtitle))
                {
                    shapes.Append(TextBox(id++, "statsub", Margin, In(4.7), SlideW - 2 * Margin, In(1.2),
                        Para(Esc(slide.Subtitle!), 18, theme.Muted, false, theme.BodyFont, "l")));
                }
                break;

            case "image":
                shapes.Append(HeaderBlock(ref id, slide.Title, theme));
                if (imageRel is not null && asset is not null)
                {
                    long cardX = Margin + In(0.22), cardY = In(1.95);
                    long cardW = SlideW - 2 * Margin - In(0.44), cardH = In(4.35);
                    long pad = In(0.18);
                    long captionH = !string.IsNullOrWhiteSpace(slide.Subtitle) ? In(0.5) : 0;
                    long areaX = cardX + pad, areaY = cardY + pad;
                    long areaW = cardW - 2 * pad, areaH = cardH - 2 * pad - captionH;

                    // Designed screenshot frame.
                    shapes.Append(RoundRect(id++, "imgcard", cardX, cardY, cardW, cardH, "FFFFFF", CardLine));
                    shapes.Append(Rect(id++, "imgbar", cardX, cardY, cardW, In(0.07), theme.Accent));

                    // Fit the image inside the framed area, centered, preserving aspect ratio.
                    double ar = asset.PixelWidth > 0 ? asset.PixelHeight / (double)asset.PixelWidth : 0.66;
                    long imgW = areaW, imgH = (long)(areaW * ar);
                    if (imgH > areaH) { imgH = areaH; imgW = (long)(areaH / ar); }
                    long imgX = areaX + (areaW - imgW) / 2;
                    long imgY = areaY + (areaH - imgH) / 2;
                    shapes.Append(Pic(id++, "img", imageRel, imgX, imgY, imgW, imgH));

                    if (!string.IsNullOrWhiteSpace(slide.Subtitle))
                    {
                        shapes.Append(TextBoxPadded(id++, "imgcap", cardX + pad, cardY + cardH - In(0.45),
                            cardW - 2 * pad, In(0.32),
                            Para(Esc(slide.Subtitle!), 12, theme.Muted, false, theme.BodyFont, "l"), 0));
                    }
                }
                else if (slide.Bullets.Count > 0)
                {
                    var body = new StringBuilder();
                    if (!string.IsNullOrWhiteSpace(slide.Subtitle))
                    {
                        body.Append(Para(Esc(slide.Subtitle!), 19, theme.Muted, false, theme.BodyFont, "l"));
                    }
                    foreach (var b in slide.Bullets)
                    {
                        body.Append(BulletPara(Esc(b), 19, ink, theme));
                    }
                    shapes.Append(TextBox(id++, "imgfallback", Margin, In(1.9), SlideW - 2 * Margin, In(4.4), body.ToString()));
                }
                else if (!string.IsNullOrWhiteSpace(slide.Subtitle))
                {
                    shapes.Append(TextBox(id++, "imgmissing", Margin, In(3), SlideW - 2 * Margin, In(1),
                        Para(Esc(slide.Subtitle!), 18, theme.Muted, false, theme.BodyFont, "l")));
                }
                break;

            case "image-side":
                shapes.Append(HeaderBlock(ref id, slide.Title, theme));
                if (imageRel is not null && asset is not null)
                {
                    shapes.Append(ImageSide(ref id, slide, theme, ink, imageRel, asset));
                }
                else
                {
                    var body = new StringBuilder();
                    if (!string.IsNullOrWhiteSpace(slide.Subtitle))
                    {
                        body.Append(Para(Esc(slide.Subtitle!), 19, theme.Muted, false, theme.BodyFont, "l"));
                    }
                    foreach (var b in slide.Bullets)
                    {
                        body.Append(BulletPara(Esc(b), 19, ink, theme));
                    }
                    shapes.Append(TextBox(id++, "imagefallback", Margin, In(1.9), SlideW - 2 * Margin, In(4.4), body.ToString()));
                }
                break;

            case "timeline":
                shapes.Append(HeaderBlock(ref id, slide.Title, theme));
                shapes.Append(Timeline(ref id, slide.Timeline, theme, ink));
                break;

            case "comparison":
                shapes.Append(HeaderBlock(ref id, slide.Title, theme));
                shapes.Append(Comparison(ref id, slide.Comparison, theme, ink));
                break;

            case "chart":
                shapes.Append(HeaderBlock(ref id, slide.Title, theme));
                if (chartRel is not null)
                {
                    bool hasNote = !string.IsNullOrWhiteSpace(slide.Subtitle);
                    long chartW = hasNote ? SlideW - 2 * Margin - In(3.4) : SlideW - 2 * Margin;
                    shapes.Append(GraphicFrame(id++, "chart", chartRel, Margin, In(1.95), chartW, In(4.3)));
                    if (hasNote)
                    {
                        shapes.Append(TextBox(id++, "chartnote", Margin + chartW + In(0.4), In(2.2),
                            In(3.0), In(3.6), Para(Esc(slide.Subtitle!), 16, theme.Muted, false, theme.BodyFont, "l")));
                    }
                }
                else if (!string.IsNullOrWhiteSpace(slide.Subtitle))
                {
                    shapes.Append(TextBox(id++, "chartnote", Margin, In(3), SlideW - 2 * Margin, In(1),
                        Para(Esc(slide.Subtitle!), 18, theme.Muted, false, theme.BodyFont, "l")));
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

    // -------------------------------------------------------------------
    // Cards + stats (richer content layouts)
    // -------------------------------------------------------------------

    private const string CardFill = "F5F7FB";
    private const string CardLine = "E4E8F1";

    private static string CardsGrid(ref int id, List<CardSpec> cards, DeckTheme theme, string ink)
    {
        var items = cards.Where(c => !string.IsNullOrWhiteSpace(c.Title) || !string.IsNullOrWhiteSpace(c.Description))
            .Take(6).ToList();
        if (items.Count == 0)
        {
            return string.Empty;
        }

        // Choose a column count that keeps cards comfortably sized.
        int columns = items.Count <= 3 ? items.Count : (items.Count == 4 ? 2 : 3);
        int rows = (int)Math.Ceiling(items.Count / (double)columns);

        long gridX = Margin, gridY = In(1.95);
        long gridW = SlideW - 2 * Margin;
        long gridH = In(4.35);
        long gap = In(0.28);
        long cardW = (gridW - gap * (columns - 1)) / columns;
        long cardH = (gridH - gap * (rows - 1)) / rows;

        var sb = new StringBuilder();
        for (int i = 0; i < items.Count; i++)
        {
            int r = i / columns, c = i % columns;
            long x = gridX + c * (cardW + gap);
            long y = gridY + r * (cardH + gap);

            sb.Append(RoundRect(id++, $"card{i}", x, y, cardW, cardH, CardFill, CardLine));
            // Accent top bar for a designed feel.
            sb.Append(Rect(id++, $"cardbar{i}", x, y, cardW, In(0.07), theme.Accent));

            var body = new StringBuilder();
            body.Append(Para(Esc(items[i].Title ?? ""), 18, theme.Primary, true, theme.HeadingFont, "l"));
            if (!string.IsNullOrWhiteSpace(items[i].Description))
            {
                body.Append(Para(Esc(items[i].Description!), 13, theme.Muted, false, theme.BodyFont, "l"));
            }
            sb.Append(TextBoxPadded(id++, $"cardtext{i}", x, y + In(0.18), cardW, cardH - In(0.18),
                body.ToString(), In(0.22)));
        }
        return sb.ToString();
    }

    private static string StatsRow(ref int id, List<StatSpec> stats, DeckTheme theme)
    {
        var items = stats.Where(s => !string.IsNullOrWhiteSpace(s.Value)).Take(4).ToList();
        if (items.Count == 0)
        {
            return string.Empty;
        }

        long gap = In(0.3);
        long rowW = SlideW - 2 * Margin;
        long cellW = (rowW - gap * (items.Count - 1)) / items.Count;
        long y = In(2.4);
        long cellH = In(2.0);

        var sb = new StringBuilder();
        for (int i = 0; i < items.Count; i++)
        {
            long x = Margin + i * (cellW + gap);
            sb.Append(RoundRect(id++, $"stat{i}", x, y, cellW, cellH, CardFill, CardLine));
            sb.Append(TextBoxPadded(id++, $"statval{i}", x, y + In(0.28), cellW, In(1.0),
                Para(Esc(items[i].Value ?? ""), 44, theme.Primary, true, theme.HeadingFont, "ctr"), In(0.1)));
            sb.Append(TextBoxPadded(id++, $"statlbl{i}", x, y + In(1.3), cellW, In(0.6),
                Para(Esc(items[i].Label ?? ""), 14, theme.Muted, false, theme.BodyFont, "ctr"), In(0.1)));
        }
        return sb.ToString();
    }

    private static string Timeline(ref int id, List<TimelineItemSpec> items, DeckTheme theme, string ink)
    {
        var steps = items.Where(t => !string.IsNullOrWhiteSpace(t.Title) || !string.IsNullOrWhiteSpace(t.Marker))
            .Take(5).ToList();
        if (steps.Count == 0)
        {
            return string.Empty;
        }

        long areaX = Margin, areaW = SlideW - 2 * Margin;
        long lineY = In(2.7);
        long colW = areaW / steps.Count;
        long dot = In(0.22);

        var sb = new StringBuilder();
        // The connecting line.
        sb.Append(Rect(id++, "tlline", areaX + colW / 2, lineY + dot / 2 - In(0.02), areaW - colW, In(0.04), theme.Muted));

        for (int i = 0; i < steps.Count; i++)
        {
            long cx = areaX + i * colW;
            long dotX = cx + colW / 2 - dot / 2;
            sb.Append(RoundRect(id++, $"tldot{i}", dotX, lineY, dot, dot, theme.Accent, theme.Accent));

            if (!string.IsNullOrWhiteSpace(steps[i].Marker))
            {
                sb.Append(TextBoxPadded(id++, $"tlmark{i}", cx, lineY - In(0.55), colW, In(0.5),
                    Para(Esc(steps[i].Marker!), 15, theme.Primary, true, theme.HeadingFont, "ctr"), In(0.05)));
            }

            var body = new StringBuilder();
            body.Append(Para(Esc(steps[i].Title ?? ""), 15, ink, true, theme.HeadingFont, "ctr"));
            if (!string.IsNullOrWhiteSpace(steps[i].Description))
            {
                body.Append(Para(Esc(steps[i].Description!), 12, theme.Muted, false, theme.BodyFont, "ctr"));
            }
            sb.Append(TextBoxPadded(id++, $"tltext{i}", cx, lineY + In(0.4), colW, In(2.6), body.ToString(), In(0.12)));
        }
        return sb.ToString();
    }

    private static string Comparison(ref int id, List<ComparisonItemSpec> items, DeckTheme theme, string ink)
    {
        var sides = items.Where(c => !string.IsNullOrWhiteSpace(c.Heading) || c.Points.Count > 0).Take(3).ToList();
        if (sides.Count == 0)
        {
            sides = new List<ComparisonItemSpec> { new(), new() };
        }

        long gap = In(0.3);
        long areaW = SlideW - 2 * Margin;
        long colW = (areaW - gap * (sides.Count - 1)) / sides.Count;
        long y = In(1.95);
        long colH = In(4.35);

        var sb = new StringBuilder();
        for (int i = 0; i < sides.Count; i++)
        {
            long x = Margin + i * (colW + gap);
            sb.Append(RoundRect(id++, $"cmp{i}", x, y, colW, colH, CardFill, CardLine));
            // Colored header band.
            sb.Append(RoundRect(id++, $"cmphd{i}", x, y, colW, In(0.9), theme.Primary, theme.Primary));

            var head = new StringBuilder();
            head.Append(Para(Esc(sides[i].Heading ?? ""), 19, "FFFFFF", true, theme.HeadingFont, "l"));
            if (!string.IsNullOrWhiteSpace(sides[i].Subtitle))
            {
                head.Append(Para(Esc(sides[i].Subtitle!), 12, "FFFFFF", false, theme.BodyFont, "l"));
            }
            sb.Append(TextBoxPadded(id++, $"cmphdt{i}", x, y + In(0.16), colW, In(0.8), head.ToString(), In(0.22)));

            var body = new StringBuilder();
            foreach (var p in sides[i].Points)
            {
                body.Append(BulletPara(Esc(p), 15, ink, theme));
            }
            sb.Append(TextBoxPadded(id++, $"cmpb{i}", x, y + In(1.1), colW, colH - In(1.2), body.ToString(), In(0.22)));
        }
        return sb.ToString();
    }

    private static string ImageSide(ref int id, SlideSpec slide, DeckTheme theme, string ink,
        string? imageRel, ImageAsset? asset)
    {
        long areaY = In(1.9);
        long areaH = In(4.4);
        long half = (SlideW - 2 * Margin - In(0.5)) / 2;
        long imgX = Margin, txtX = Margin + half + In(0.5);

        var sb = new StringBuilder();

        // Image panel (left). Fills the half, letterboxed inside a rounded card.
        sb.Append(RoundRect(id++, "isbg", imgX, areaY, half, areaH, CardFill, CardLine));
        if (imageRel is not null && asset is not null)
        {
            double ar = asset.PixelWidth > 0 ? asset.PixelHeight / (double)asset.PixelWidth : 0.66;
            long iw = half - In(0.2), ih = (long)(iw * ar);
            if (ih > areaH - In(0.2)) { ih = areaH - In(0.2); iw = (long)(ih / ar); }
            long ix = imgX + (half - iw) / 2, iy = areaY + (areaH - ih) / 2;
            sb.Append(Pic(id++, "isimg", imageRel, ix, iy, iw, ih));
        }

        // Text panel (right).
        var body = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(slide.Subtitle))
        {
            body.Append(Para(Esc(slide.Subtitle!), 18, theme.Primary, true, theme.HeadingFont, "l"));
        }
        foreach (var b in slide.Bullets)
        {
            body.Append(BulletPara(Esc(b), 17, ink, theme));
        }
        sb.Append(TextBox(id++, "istext", txtX, areaY + In(0.2), half, areaH - In(0.2), body.ToString()));
        return sb.ToString();
    }

    private static string RoundRect(int id, string name, long x, long y, long cx, long cy, string fillHex, string lineHex)
        => $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>" +
           $"<p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm>" +
           "<a:prstGeom prst=\"roundRect\"><a:avLst><a:gd name=\"adj\" fmla=\"val 6000\"/></a:avLst></a:prstGeom>" +
           $"<a:solidFill><a:srgbClr val=\"{fillHex}\"/></a:solidFill>" +
           $"<a:ln w=\"9525\"><a:solidFill><a:srgbClr val=\"{lineHex}\"/></a:solidFill></a:ln></p:spPr>" +
           "<p:txBody><a:bodyPr/><a:lstStyle/><a:p/></p:txBody></p:sp>";

    private static string Pic(int id, string name, string relId, long x, long y, long cx, long cy)
        => $"<p:pic><p:nvPicPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvPicPr>" +
           "<a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>" +
           $"<p:blipFill><a:blip r:embed=\"{relId}\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>" +
           $"<p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm>" +
           "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>";

    private static string GraphicFrame(int id, string name, string chartRelId, long x, long y, long cx, long cy)
        => "<p:graphicFrame><p:nvGraphicFramePr>" +
           $"<p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>" +
           $"<p:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></p:xfrm>" +
           "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\">" +
           "<c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" " +
           $"xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" r:id=\"{chartRelId}\"/>" +
           "</a:graphicData></a:graphic></p:graphicFrame>";

    private static DocumentFormat.OpenXml.Packaging.PartTypeInfo ImagePartTypeFor(string contentType)
        => contentType.ToLowerInvariant() switch
        {
            "image/png" => DocumentFormat.OpenXml.Packaging.ImagePartType.Png,
            "image/gif" => DocumentFormat.OpenXml.Packaging.ImagePartType.Gif,
            "image/bmp" => DocumentFormat.OpenXml.Packaging.ImagePartType.Bmp,
            "image/tiff" => DocumentFormat.OpenXml.Packaging.ImagePartType.Tiff,
            _ => DocumentFormat.OpenXml.Packaging.ImagePartType.Jpeg
        };

    private static string TextBoxPadded(int id, string name, long x, long y, long cx, long cy, string paragraphs, long inset)
        => $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr>" +
           $"<p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm>" +
           "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/></p:spPr>" +
           $"<p:txBody><a:bodyPr wrap=\"square\" anchor=\"t\" lIns=\"{inset}\" rIns=\"{inset}\" tIns=\"0\" bIns=\"0\"><a:normAutofit/></a:bodyPr>" +
           $"<a:lstStyle/>{paragraphs}</p:txBody></p:sp>";

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

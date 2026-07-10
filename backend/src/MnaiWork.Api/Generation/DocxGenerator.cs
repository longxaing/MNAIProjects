using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace MnaiWork.Api.Generation;

/// <summary>Renders a <see cref="DocSpec"/> into a styled .docx entirely in-process.</summary>
public sealed class DocxGenerator
{
    private const int BulletNumId = 1;
    private const int DecimalNumId = 2;

    public byte[] Generate(DocSpec spec)
    {
        var theme = Themes.Get(spec.Theme);
        using var ms = new MemoryStream();

        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document();
            var body = mainPart.Document.AppendChild(new Body());

            AddNumberingPart(mainPart, theme);

            // Cover
            body.AppendChild(StyledParagraph(spec.Title, 40, theme.Primary, bold: true, before: 240, after: 60,
                font: theme.HeadingFont));
            if (!string.IsNullOrWhiteSpace(spec.Subtitle))
            {
                body.AppendChild(StyledParagraph(spec.Subtitle!, 15, theme.Muted, bold: false, before: 0, after: 60,
                    font: theme.BodyFont));
            }
            if (!string.IsNullOrWhiteSpace(spec.Author))
            {
                body.AppendChild(StyledParagraph(spec.Author!, 11, theme.Muted, bold: false, before: 0, after: 120,
                    font: theme.BodyFont));
            }
            body.AppendChild(DividerParagraph(theme.Accent));

            foreach (var block in spec.Blocks)
            {
                RenderBlock(body, block, theme);
            }

            AddSectionProperties(body);
            mainPart.Document.Save();
        }

        return ms.ToArray();
    }

    private void RenderBlock(Body body, DocBlock block, DeckTheme theme)
    {
        switch ((block.Type ?? "paragraph").Trim().ToLowerInvariant())
        {
            case "heading1":
                body.AppendChild(StyledParagraph(block.Text ?? "", 22, theme.ContentInk, true, 300, 80, theme.HeadingFont));
                break;
            case "heading2":
                body.AppendChild(StyledParagraph(block.Text ?? "", 17, theme.Primary, true, 240, 60, theme.HeadingFont));
                break;
            case "heading3":
                body.AppendChild(StyledParagraph(block.Text ?? "", 13, theme.ContentInk, true, 200, 40, theme.HeadingFont));
                break;
            case "bullets":
                foreach (var item in block.Items)
                {
                    body.AppendChild(ListParagraph(item, BulletNumId, theme));
                }
                break;
            case "numbered":
                foreach (var item in block.Items)
                {
                    body.AppendChild(ListParagraph(item, DecimalNumId, theme));
                }
                break;
            case "quote":
                body.AppendChild(QuoteParagraph(block.Text ?? "", theme));
                break;
            case "divider":
                body.AppendChild(DividerParagraph(theme.Accent));
                break;
            case "table":
                body.AppendChild(BuildTable(block, theme));
                body.AppendChild(new Paragraph(new ParagraphProperties(
                    new SpacingBetweenLines { After = "120" })));
                break;
            default: // paragraph
                body.AppendChild(StyledParagraph(block.Text ?? "", 11, theme.ContentInk, false, 40, 120, theme.BodyFont, justify: true));
                break;
        }
    }

    // -------------------------------------------------------------------
    // Paragraph builders
    // -------------------------------------------------------------------

    private static Paragraph StyledParagraph(string text, int sizePt, string colorHex, bool bold,
        int before, int after, string font, bool justify = false)
    {
        var props = new ParagraphProperties(
            new SpacingBetweenLines { Before = before.ToString(), After = after.ToString(), Line = "276", LineRule = LineSpacingRuleValues.Auto });
        if (justify)
        {
            // Justify body prose so the right edge is even and long lines look tidy.
            props.AppendChild(new Justification { Val = JustificationValues.Both });
        }

        var run = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        run.RunProperties = RunProps(sizePt, colorHex, bold, font);

        return new Paragraph(props, run);
    }

    private static Paragraph ListParagraph(string text, int numId, DeckTheme theme)
    {
        var props = new ParagraphProperties(
            new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = numId }),
            new SpacingBetweenLines { After = "60", Line = "276", LineRule = LineSpacingRuleValues.Auto });

        var run = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve })
        {
            RunProperties = RunProps(11, theme.ContentInk, false, theme.BodyFont)
        };
        return new Paragraph(props, run);
    }

    private static Paragraph QuoteParagraph(string text, DeckTheme theme)
    {
        // CT_PPr order: pBdr, spacing, ind.
        var props = new ParagraphProperties
        {
            ParagraphBorders = new ParagraphBorders(
                new LeftBorder { Val = BorderValues.Single, Color = theme.Accent, Size = 24U, Space = 12U }),
            SpacingBetweenLines = new SpacingBetweenLines
            {
                Before = "120", After = "120", Line = "276", LineRule = LineSpacingRuleValues.Auto
            },
            Indentation = new Indentation { Left = "360" }
        };

        // CT_RPr order: rFonts, i, color, sz.
        var runProps = new RunProperties
        {
            RunFonts = new RunFonts { Ascii = theme.BodyFont, HighAnsi = theme.BodyFont },
            Italic = new Italic(),
            Color = new Color { Val = theme.Muted },
            FontSize = new FontSize { Val = (12 * 2).ToString() }
        };

        var run = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve })
        {
            RunProperties = runProps
        };
        return new Paragraph(props, run);
    }

    private static Paragraph DividerParagraph(string colorHex)
    {
        var props = new ParagraphProperties(
            new ParagraphBorders(new BottomBorder { Val = BorderValues.Single, Color = colorHex, Size = 12U, Space = 1U }),
            new SpacingBetweenLines { Before = "60", After = "180" });
        return new Paragraph(props);
    }

    private static RunProperties RunProps(int sizePt, string colorHex, bool bold, string font)
    {
        // Children must follow the CT_RPr schema order: rFonts, b, color, sz.
        var rp = new RunProperties
        {
            RunFonts = new RunFonts { Ascii = font, HighAnsi = font }
        };
        if (bold)
        {
            rp.Bold = new Bold();
        }
        rp.Color = new Color { Val = colorHex };
        rp.FontSize = new FontSize { Val = (sizePt * 2).ToString() };
        return rp;
    }

    // -------------------------------------------------------------------
    // Table
    // -------------------------------------------------------------------

    private static Table BuildTable(DocBlock block, DeckTheme theme)
    {
        var columnCount = Math.Max(
            block.Header.Count,
            block.Rows.Count > 0 ? block.Rows.Max(r => r.Count) : 0);
        if (columnCount == 0)
        {
            columnCount = 1;
        }

        var table = new Table();
        table.AppendChild(new TableProperties(
            new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" },
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Color = "E5E7EB", Size = 4U },
                new LeftBorder { Val = BorderValues.Single, Color = "E5E7EB", Size = 4U },
                new BottomBorder { Val = BorderValues.Single, Color = "E5E7EB", Size = 4U },
                new RightBorder { Val = BorderValues.Single, Color = "E5E7EB", Size = 4U },
                new InsideHorizontalBorder { Val = BorderValues.Single, Color = "E5E7EB", Size = 4U },
                new InsideVerticalBorder { Val = BorderValues.Single, Color = "E5E7EB", Size = 4U })));

        // A tblGrid (one column definition per column) is required before any rows.
        var grid = new TableGrid();
        var columnWidth = (9360 / columnCount).ToString();
        for (var i = 0; i < columnCount; i++)
        {
            grid.AppendChild(new GridColumn { Width = columnWidth });
        }
        table.AppendChild(grid);

        if (block.Header is { Count: > 0 })
        {
            var headerRow = new TableRow();
            foreach (var cell in block.Header)
            {
                headerRow.AppendChild(BuildCell(cell, theme.Primary, "FFFFFF", bold: true, theme.HeadingFont));
            }
            table.AppendChild(headerRow);
        }

        foreach (var row in block.Rows)
        {
            var tr = new TableRow();
            foreach (var cell in row)
            {
                tr.AppendChild(BuildCell(cell, null, theme.ContentInk, bold: false, theme.BodyFont));
            }
            table.AppendChild(tr);
        }

        return table;
    }

    private static TableCell BuildCell(string text, string? shadeHex, string colorHex, bool bold, string font)
    {
        // CT_TcPr order: shd before tcMar. CT_TcMar order: top, left, bottom, right.
        var cellProps = new TableCellProperties();
        if (shadeHex is not null)
        {
            cellProps.Shading = new Shading { Val = ShadingPatternValues.Clear, Fill = shadeHex };
        }
        cellProps.TableCellMargin = new TableCellMargin(
            new TopMargin { Type = TableWidthUnitValues.Dxa, Width = "60" },
            new LeftMargin { Type = TableWidthUnitValues.Dxa, Width = "108" },
            new BottomMargin { Type = TableWidthUnitValues.Dxa, Width = "60" },
            new RightMargin { Type = TableWidthUnitValues.Dxa, Width = "108" });

        var run = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve })
        {
            RunProperties = RunProps(10, colorHex, bold, font)
        };
        return new TableCell(cellProps, new Paragraph(
            new ParagraphProperties(new SpacingBetweenLines { After = "0" }), run));
    }

    // -------------------------------------------------------------------
    // Numbering + section
    // -------------------------------------------------------------------

    private static void AddNumberingPart(MainDocumentPart mainPart, DeckTheme theme)
    {
        var numberingPart = mainPart.AddNewPart<NumberingDefinitionsPart>();
        var element = new Numbering();

        // Abstract 0: bullets.
        element.AppendChild(new AbstractNum(
            new Level(
                new NumberingFormat { Val = NumberFormatValues.Bullet },
                new LevelText { Val = "\u2022" },
                new LevelJustification { Val = LevelJustificationValues.Left },
                new ParagraphProperties(new Indentation { Left = "360", Hanging = "360" }),
                new RunProperties(new RunFonts { Ascii = "Arial", HighAnsi = "Arial", Hint = FontTypeHintValues.Default }))
            { LevelIndex = 0, TemplateCode = "0A0A0A0A" })
        { AbstractNumberId = 0, Nsid = new Nsid { Val = "0A0A0A01" } });

        // Abstract 1: decimal.
        element.AppendChild(new AbstractNum(
            new Level(
                new NumberingFormat { Val = NumberFormatValues.Decimal },
                new LevelText { Val = "%1." },
                new LevelJustification { Val = LevelJustificationValues.Left },
                new ParagraphProperties(new Indentation { Left = "360", Hanging = "360" }))
            { LevelIndex = 0, TemplateCode = "0B0B0B0B", StartNumberingValue = new StartNumberingValue { Val = 1 } })
        { AbstractNumberId = 1, Nsid = new Nsid { Val = "0B0B0B01" } });

        element.AppendChild(new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = BulletNumId });
        element.AppendChild(new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = DecimalNumId });

        numberingPart.Numbering = element;
    }

    private static void AddSectionProperties(Body body)
    {
        body.AppendChild(new SectionProperties(
            new PageSize { Width = 12240U, Height = 15840U },
            // Wider side margins (1.25") shorten each line so long paragraphs read more comfortably.
            new PageMargin { Top = 1440, Right = 1800U, Bottom = 1440, Left = 1800U, Header = 720U, Footer = 720U, Gutter = 0U }));
    }
}

using MnaiWork.Api.Generation;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

var deck = new DeckSpec
{
    Title = "Quarterly Review",
    Subtitle = "FY26 Q2 Business Update",
    Author = "MnaiWork",
    Theme = "midnight",
    Slides =
    {
        new SlideSpec { Layout = "title", Title = "Quarterly Review", Subtitle = "FY26 Q2 Business Update" },
        new SlideSpec { Layout = "section", Title = "Highlights" },
        new SlideSpec
        {
            Layout = "bullets",
            Title = "Key Wins",
            Bullets = { "Revenue up 24% YoY", "Two enterprise logos signed", "NPS improved to 61" }
        },
        new SlideSpec
        {
            Layout = "two-column",
            Title = "Now vs. Before",
            Columns =
            {
                new ColumnSpec { Heading = "Now", Bullets = { "Fast", "Simple", "Delightful" } },
                new ColumnSpec { Heading = "Before", Bullets = { "Slow", "Complex", "Frustrating" } }
            }
        },
        new SlideSpec { Layout = "quote", Quote = "Design is intelligence made visible.", Attribution = "Alina Wheeler" },
        new SlideSpec { Layout = "closing", Title = "Thank you", Subtitle = "Questions?" }
    }
};

var doc = new DocSpec
{
    Title = "Project Brief",
    Subtitle = "Prepared by MnaiWork",
    Author = "MnaiWork",
    Theme = "azure",
    Blocks =
    {
        new DocBlock { Type = "heading1", Text = "Overview" },
        new DocBlock { Type = "paragraph", Text = "This document summarizes the project scope, goals and plan." },
        new DocBlock { Type = "heading2", Text = "Goals" },
        new DocBlock { Type = "bullets", Items = { "Ship v1", "Delight users", "Keep it simple" } },
        new DocBlock { Type = "heading2", Text = "Timeline" },
        new DocBlock
        {
            Type = "table",
            Header = { "Phase", "Target" },
            Rows = { new() { "Design", "July" }, new() { "Build", "August" }, new() { "Launch", "September" } }
        },
        new DocBlock { Type = "quote", Text = "Simplicity is the ultimate sophistication." },
        new DocBlock { Type = "divider" },
        new DocBlock { Type = "numbered", Items = { "Plan", "Execute", "Review" } }
    }
};

var pptx = new PptxGenerator().Generate(deck);
var docx = new DocxGenerator().Generate(doc);
File.WriteAllBytes("sample.pptx", pptx);
File.WriteAllBytes("sample.docx", docx);

var validator = new OpenXmlValidator();
var issues = 0;

using (var ms = new MemoryStream(pptx))
using (var d = PresentationDocument.Open(ms, false))
{
    foreach (var e in validator.Validate(d).Take(30))
    {
        issues++;
        Console.WriteLine($"PPTX: {e.Description}  [{e.Path?.XPath}]");
    }
}

using (var ms = new MemoryStream(docx))
using (var d = WordprocessingDocument.Open(ms, false))
{
    foreach (var e in validator.Validate(d).Take(30))
    {
        issues++;
        Console.WriteLine($"DOCX: {e.Description}  [{e.Path?.XPath}]");
    }
}

Console.WriteLine($"pptx={pptx.Length} bytes, docx={docx.Length} bytes, validationIssues={issues}");

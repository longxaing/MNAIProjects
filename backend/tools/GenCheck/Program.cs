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
            Layout = "stats",
            Title = "By the numbers",
            Stats =
            {
                new StatSpec { Value = "24%", Label = "Revenue growth YoY" },
                new StatSpec { Value = "61", Label = "Net promoter score" },
                new StatSpec { Value = "2", Label = "Enterprise logos" }
            }
        },
        new SlideSpec
        {
            Layout = "cards",
            Title = "What drove growth",
            Cards =
            {
                new CardSpec { Title = "New pricing", Description = "Simplified tiers lifted conversion across segments." },
                new CardSpec { Title = "Faster onboarding", Description = "Time-to-value dropped from days to minutes." },
                new CardSpec { Title = "Enterprise push", Description = "Dedicated team closed two flagship accounts." },
                new CardSpec { Title = "Better support", Description = "NPS climbed as response times improved." }
            }
        },
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
        new SlideSpec
        {
            Layout = "timeline",
            Title = "Roadmap",
            Timeline =
            {
                new TimelineItemSpec { Marker = "Q1", Title = "Discovery", Description = "Research and scope." },
                new TimelineItemSpec { Marker = "Q2", Title = "Build", Description = "Ship the MVP." },
                new TimelineItemSpec { Marker = "Q3", Title = "Launch", Description = "Public release." }
            }
        },
        new SlideSpec
        {
            Layout = "comparison",
            Title = "Plans",
            Comparison =
            {
                new ComparisonItemSpec { Heading = "Starter", Subtitle = "For individuals", Points = { "1 seat", "Email support" } },
                new ComparisonItemSpec { Heading = "Team", Subtitle = "For teams", Points = { "10 seats", "Priority support", "SSO" } }
            }
        },
        new SlideSpec
        {
            Layout = "chart",
            Title = "Revenue by quarter",
            Subtitle = "Steady growth across the year.",
            Chart = new ChartSpec
            {
                Type = "bar",
                Categories = { "Q1", "Q2", "Q3", "Q4" },
                Series = { new ChartSeriesSpec { Name = "Revenue", Values = { 12, 18, 24, 30 } } }
            }
        },
        new SlideSpec
        {
            Layout = "chart",
            Title = "Share of traffic",
            Chart = new ChartSpec
            {
                Type = "pie",
                Categories = { "Direct", "Search", "Social" },
                Series = { new ChartSeriesSpec { Name = "Traffic", Values = { 45, 35, 20 } } }
            }
        },
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
        new DocBlock { Type = "lead", Text = "A concise brief covering the project scope, goals and delivery plan." },
        new DocBlock { Type = "paragraph", Text = "This document summarizes the project scope, goals and plan." },
        new DocBlock { Type = "callout", Title = "Key takeaway", Text = "Ship a delightful v1 by September while keeping the scope tight." },
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
        new DocBlock { Type = "image", ImageId = "img1", Text = "Figure 1. A sample embedded image." },
        new DocBlock { Type = "numbered", Items = { "Plan", "Execute", "Review" } }
    }
};

// A minimal 2x2 red PNG to exercise image embedding.
var pngBase64 =
    "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAEklEQVR4nGP8z8Dwn4EIwDiqEAAlYgQ2Sf0nAAAAAElFTkSuQmCC";
var pngBytes = Convert.FromBase64String(pngBase64);
var (iw, ih) = ImageDimensions.Read(pngBytes);
var images = new Dictionary<string, ImageAsset> { ["img1"] = new ImageAsset(pngBytes, "image/png", iw, ih) };
deck.Slides.Add(new SlideSpec { Layout = "image", Title = "A picture", ImageId = "img1" });

var pptx = new PptxGenerator().Generate(deck, images);
var docx = new DocxGenerator().Generate(doc, images);
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

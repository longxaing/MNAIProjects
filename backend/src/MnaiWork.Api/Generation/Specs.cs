using System.Text.Json.Serialization;

namespace MnaiWork.Api.Generation;

// ---------------------------------------------------------------------------
// Presentation (PPTX) spec — the JSON contract the model fills via the tool.
// ---------------------------------------------------------------------------

public sealed class DeckSpec
{
    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public string? Author { get; set; }
    public string Theme { get; set; } = "midnight";
    public List<SlideSpec> Slides { get; set; } = new();
}

public sealed class SlideSpec
{
    /// <summary>title | section | bullets | two-column | quote | closing</summary>
    public string Layout { get; set; } = "bullets";
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public List<string> Bullets { get; set; } = new();
    public List<ColumnSpec> Columns { get; set; } = new();
    public string? Quote { get; set; }
    public string? Attribution { get; set; }
    public string? Notes { get; set; }
}

public sealed class ColumnSpec
{
    public string? Heading { get; set; }
    public List<string> Bullets { get; set; } = new();
}

// ---------------------------------------------------------------------------
// Document (DOCX) spec.
// ---------------------------------------------------------------------------

public sealed class DocSpec
{
    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public string? Author { get; set; }
    public string Theme { get; set; } = "midnight";
    public List<DocBlock> Blocks { get; set; } = new();
}

public sealed class DocBlock
{
    /// <summary>heading1 | heading2 | heading3 | paragraph | bullets | numbered | quote | table | divider</summary>
    public string Type { get; set; } = "paragraph";
    public string? Text { get; set; }
    public List<string> Items { get; set; } = new();
    public List<string> Header { get; set; } = new();
    public List<List<string>> Rows { get; set; } = new();
}

/// <summary>Palette + typography for a generated artifact.</summary>
public sealed record DeckTheme(
    string Name,
    string CoverBg,
    string CoverInk,
    string ContentBg,
    string ContentInk,
    string Primary,
    string Accent,
    string Muted,
    string HeadingFont,
    string BodyFont);

public static class Themes
{
    public static readonly DeckTheme Midnight = new(
        "midnight", "0B1020", "FFFFFF", "FFFFFF", "101828",
        "4C6EF5", "22D3EE", "667085", "Segoe UI Semibold", "Segoe UI");

    public static readonly DeckTheme Azure = new(
        "azure", "0A2540", "FFFFFF", "F5F9FF", "0A2540",
        "2563EB", "38BDF8", "5B7085", "Segoe UI Semibold", "Segoe UI");

    public static readonly DeckTheme Sunset = new(
        "sunset", "2A1220", "FFF7F2", "FFF8F5", "2A1220",
        "F97316", "FB7185", "8A6E66", "Segoe UI Semibold", "Segoe UI");

    public static readonly DeckTheme Forest = new(
        "forest", "0E1F17", "F0FFF6", "F4FBF6", "0E1F17",
        "10B981", "84CC16", "5A7264", "Segoe UI Semibold", "Segoe UI");

    public static readonly DeckTheme Mono = new(
        "mono", "111111", "FFFFFF", "FAFAFA", "111111",
        "111111", "6B7280", "9CA3AF", "Segoe UI Semibold", "Segoe UI");

    public static DeckTheme Get(string? name) => (name ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "azure" => Azure,
        "sunset" => Sunset,
        "forest" => Forest,
        "mono" => Mono,
        _ => Midnight
    };
}

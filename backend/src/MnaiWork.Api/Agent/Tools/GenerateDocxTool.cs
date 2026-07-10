using System.Text.Json;
using MnaiWork.Api.Generation;
using MnaiWork.Api.Infrastructure;
using MnaiWork.Api.Models;
using MnaiWork.Api.Storage;

namespace MnaiWork.Api.Agent.Tools;

/// <summary>Generates a Word document (.docx) from a structured block spec and stores it.</summary>
public sealed class GenerateDocxTool : IAgentTool
{
    private const string DocxContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private readonly DocxGenerator _generator;
    private readonly IFileStorage _storage;
    private readonly ILogger<GenerateDocxTool> _logger;

    public GenerateDocxTool(DocxGenerator generator, IFileStorage storage, ILogger<GenerateDocxTool> logger)
    {
        _generator = generator;
        _storage = storage;
        _logger = logger;
    }

    public string Name => "generate_docx";

    public string Description =>
        "Generate a formatted Word document (.docx) from an ordered list of content blocks. " +
        "Use 'heading1'/'heading2'/'heading3' for structure, 'paragraph' for prose, 'bullets' or " +
        "'numbered' for lists (put entries in 'items'), 'quote' for callouts, 'table' (with 'header' " +
        "and 'rows'), and 'divider' for a section break.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "title": { "type": "string", "description": "Document title, shown on the cover." },
        "subtitle": { "type": "string" },
        "author": { "type": "string" },
        "theme": { "type": "string", "enum": ["midnight", "azure", "sunset", "forest", "mono"] },
        "blocks": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "type": { "type": "string",
                "enum": ["heading1", "heading2", "heading3", "paragraph", "bullets", "numbered", "quote", "table", "divider"] },
              "text": { "type": "string", "description": "Text for headings, paragraphs and quotes." },
              "items": { "type": "array", "items": { "type": "string" }, "description": "Entries for bullets/numbered." },
              "header": { "type": "array", "items": { "type": "string" }, "description": "Table header cells." },
              "rows": { "type": "array", "items": { "type": "array", "items": { "type": "string" } },
                "description": "Table body rows." }
            },
            "required": ["type"]
          }
        }
      },
      "required": ["title", "blocks"]
    }
    """;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        DocSpec? spec;
        try
        {
            spec = arguments.Deserialize<DocSpec>(JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            return ToolResult.Fail($"Invalid arguments for generate_docx: {ex.Message}");
        }

        if (spec is null || string.IsNullOrWhiteSpace(spec.Title) || spec.Blocks.Count == 0)
        {
            return ToolResult.Fail("generate_docx requires a non-empty 'title' and at least one block.");
        }

        try
        {
            var bytes = _generator.Generate(spec);
            var fileName = EnsureExtension(spec.Title, ".docx");
            var owner = new ArtifactOwner(context.UserId, context.ThreadId);
            var artifact = await _storage.UploadAsync(owner, fileName, ArtifactKind.Docx, bytes, DocxContentType, ct);

            return ToolResult.Ok(
                $"Generated document \"{artifact.FileName}\" with {spec.Blocks.Count} block(s), " +
                $"{artifact.SizeBytes / 1024} KB. The file is attached for the user to download.",
                artifact);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DOCX generation failed for thread {ThreadId}", context.ThreadId);
            return ToolResult.Fail($"Failed to generate the document: {ex.Message}");
        }
    }

    private static string EnsureExtension(string title, string ext)
    {
        var name = title.Trim();
        if (name.Length > 80)
        {
            name = name[..80];
        }
        return name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? name : name + ext;
    }
}

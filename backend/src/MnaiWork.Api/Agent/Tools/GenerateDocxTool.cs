using System.Text.Json;
using MnaiWork.Api.Data;
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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GenerateDocxTool> _logger;

    public GenerateDocxTool(DocxGenerator generator, IFileStorage storage, IServiceScopeFactory scopeFactory,
        ILogger<GenerateDocxTool> logger)
    {
        _generator = generator;
        _storage = storage;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public string Name => "generate_docx";

    public string Description =>
        "Generate a formatted Word document (.docx) from an ordered list of content blocks. Build a " +
        "well-structured, scannable document — NOT walls of text. Use 'heading1'/'heading2'/'heading3' " +
        "for structure, 'lead' for an opening intro paragraph, 'paragraph' for prose (keep each to " +
        "2-4 sentences), 'bullets'/'numbered' for lists (entries in 'items'), 'callout' to highlight a " +
        "key takeaway (optional 'title' label + 'text'), 'quote' for quotations, 'image' to embed an " +
        "uploaded image (set 'imageId', optional caption in 'text'), 'table' (with 'header' " +
        "and 'rows'), and 'divider' for a section break. Prefer headings + short paragraphs + bullets + " +
        "tables + callouts over long uninterrupted prose.";

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
                    "description": "Ordered content. Structure with headings; break prose into short paragraphs, bullets, tables and callouts so the document is easy to scan.",
          "items": {
            "type": "object",
            "properties": {
              "type": { "type": "string",
                "enum": ["heading1", "heading2", "heading3", "lead", "paragraph", "bullets", "numbered", "quote", "callout", "image", "table", "divider"] },
              "text": { "type": "string", "description": "Text for headings, paragraphs, lead, quote, callout; or an image caption." },
              "title": { "type": "string", "description": "Optional short label for a 'callout' (e.g. 'Key takeaway')." },
              "imageId": { "type": "string", "description": "For an 'image' block: the id of an uploaded image attachment." },
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
            var images = await ResolveImagesAsync(spec, context, ct);
            var imageError = ValidateImageReferences(spec, images);
            if (imageError is not null)
            {
                return ToolResult.Fail(imageError);
            }
            var bytes = _generator.Generate(spec, images);
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

    private async Task<Dictionary<string, ImageAsset>> ResolveImagesAsync(
        DocSpec spec, ToolContext context, CancellationToken ct)
    {
        var ids = spec.Blocks.Select(b => b.ImageId).Where(id => !string.IsNullOrWhiteSpace(id))!.Cast<string>();
        using var scope = _scopeFactory.CreateScope();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        return await ThreadAttachments.ResolveImagesAsync(ids, messages, _storage, context.ThreadId, ct);
    }

    private static string? ValidateImageReferences(DocSpec spec, IReadOnlyDictionary<string, ImageAsset> images)
    {
        for (int i = 0; i < spec.Blocks.Count; i++)
        {
            var block = spec.Blocks[i];
            if (!string.Equals(block.Type?.Trim(), "image", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(block.ImageId))
            {
                return $"Block {i + 1} uses type 'image' but has no imageId. Only use image blocks when referencing an uploaded image attachment.";
            }

            if (!images.ContainsKey(block.ImageId))
            {
                return $"Block {i + 1} references imageId '{block.ImageId}', but that uploaded image could not be resolved in this conversation.";
            }
        }

        return null;
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

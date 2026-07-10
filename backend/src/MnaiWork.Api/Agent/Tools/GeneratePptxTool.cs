using System.Text.Json;
using MnaiWork.Api.Generation;
using MnaiWork.Api.Infrastructure;
using MnaiWork.Api.Models;
using MnaiWork.Api.Storage;

namespace MnaiWork.Api.Agent.Tools;

/// <summary>Generates a PowerPoint (.pptx) from a structured deck spec and stores it.</summary>
public sealed class GeneratePptxTool : IAgentTool
{
    private const string PptxContentType =
        "application/vnd.openxmlformats-officedocument.presentationml.presentation";

    private readonly PptxGenerator _generator;
    private readonly IFileStorage _storage;
    private readonly ILogger<GeneratePptxTool> _logger;

    public GeneratePptxTool(PptxGenerator generator, IFileStorage storage, ILogger<GeneratePptxTool> logger)
    {
        _generator = generator;
        _storage = storage;
        _logger = logger;
    }

    public string Name => "generate_pptx";

    public string Description =>
        "Generate a polished PowerPoint presentation (.pptx) from a structured outline. " +
        "Design one slide per key idea. Use layout 'title' for the opening slide, 'section' for chapter " +
        "dividers, 'bullets' for content, 'two-column' to compare two things, 'quote' for a highlighted " +
        "statement, and 'closing' for the final slide. Keep bullets short (max ~12 words).";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "title": { "type": "string", "description": "Deck title." },
        "subtitle": { "type": "string" },
        "author": { "type": "string" },
        "theme": { "type": "string", "enum": ["midnight", "azure", "sunset", "forest", "mono"],
          "description": "Visual theme. Default midnight." },
        "slides": {
          "type": "array",
          "description": "Ordered slides.",
          "items": {
            "type": "object",
            "properties": {
              "layout": { "type": "string", "enum": ["title", "section", "bullets", "two-column", "quote", "closing"] },
              "title": { "type": "string" },
              "subtitle": { "type": "string" },
              "bullets": { "type": "array", "items": { "type": "string" } },
              "columns": {
                "type": "array",
                "items": {
                  "type": "object",
                  "properties": {
                    "heading": { "type": "string" },
                    "bullets": { "type": "array", "items": { "type": "string" } }
                  }
                }
              },
              "quote": { "type": "string" },
              "attribution": { "type": "string" }
            },
            "required": ["layout"]
          }
        }
      },
      "required": ["title", "slides"]
    }
    """;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        DeckSpec? spec;
        try
        {
            spec = arguments.Deserialize<DeckSpec>(JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            return ToolResult.Fail($"Invalid arguments for generate_pptx: {ex.Message}");
        }

        if (spec is null || string.IsNullOrWhiteSpace(spec.Title) || spec.Slides.Count == 0)
        {
            return ToolResult.Fail("generate_pptx requires a non-empty 'title' and at least one slide.");
        }

        try
        {
            var bytes = _generator.Generate(spec);
            var fileName = EnsureExtension(spec.Title, ".pptx");
            var owner = new ArtifactOwner(context.UserId, context.ThreadId);
            var artifact = await _storage.UploadAsync(owner, fileName, ArtifactKind.Pptx, bytes, PptxContentType, ct);

            return ToolResult.Ok(
                $"Generated presentation \"{artifact.FileName}\" with {spec.Slides.Count} slide(s), " +
                $"{artifact.SizeBytes / 1024} KB. The file is attached for the user to download.",
                artifact);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PPTX generation failed for thread {ThreadId}", context.ThreadId);
            return ToolResult.Fail($"Failed to generate the presentation: {ex.Message}");
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

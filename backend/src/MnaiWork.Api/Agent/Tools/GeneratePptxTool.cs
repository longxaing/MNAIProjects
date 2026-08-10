using System.Text.Json;
using MnaiWork.Api.Data;
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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GeneratePptxTool> _logger;

    public GeneratePptxTool(PptxGenerator generator, IFileStorage storage, IServiceScopeFactory scopeFactory,
        ILogger<GeneratePptxTool> logger)
    {
        _generator = generator;
        _storage = storage;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public string Name => "generate_pptx";

    public string Description =>
        "Generate a polished PowerPoint presentation (.pptx) from a structured outline. Design one " +
        "slide per key idea and VARY the layouts for visual interest — do not make every slide 'bullets'. " +
        "Layouts: 'title' (opening), 'section' (chapter divider), 'bullets' (a few short points), " +
        "'cards' (3-6 titled cards, each with a short description — ideal for summaries, agendas, " +
        "features, pillars), 'stats' (2-4 headline metrics with big numbers), 'two-column' (compare two " +
        "things), 'quote' (a highlighted statement), 'image' (place an uploaded image full-width, set " +
        "'imageId' to an attachment id), and 'closing' (final slide). Keep text punchy: " +
        "bullets and card descriptions under ~14 words, cover/section subtitles under ~12 words.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "title": { "type": "string", "description": "Deck title." },
        "subtitle": { "type": "string", "description": "Short punchy tagline for the cover (< 12 words)." },
        "author": { "type": "string" },
        "theme": { "type": "string", "enum": ["midnight", "azure", "sunset", "forest", "mono"],
          "description": "Visual theme chosen to fit the topic. Default midnight." },
        "slides": {
          "type": "array",
          "description": "Ordered slides. Aim for 6-12. Open with 'title', use 'section' dividers, mix 'cards'/'stats'/'timeline'/'comparison'/'chart'/'two-column'/'quote' with 'bullets', and end with 'closing'.",
          "items": {
            "type": "object",
            "properties": {
              "layout": { "type": "string", "enum": ["title", "section", "bullets", "cards", "stats", "two-column", "timeline", "comparison", "image-side", "chart", "quote", "image", "closing"] },
              "title": { "type": "string" },
              "subtitle": { "type": "string" },
              "imageId": { "type": "string", "description": "For 'image'/'image-side' slides: the id of an uploaded image attachment." },
              "bullets": { "type": "array", "items": { "type": "string" },
                "description": "For 'bullets': 3-5 short points (< 14 words each)." },
              "cards": {
                "type": "array",
                "description": "For 'cards': 3-6 cards, each a short title plus an 8-16 word description.",
                "items": {
                  "type": "object",
                  "properties": {
                    "title": { "type": "string" },
                    "description": { "type": "string" }
                  }
                }
              },
              "stats": {
                "type": "array",
                "description": "For 'stats': 2-4 metrics, each a big value (e.g. '24%', '$1.2M') and a short label.",
                "items": {
                  "type": "object",
                  "properties": {
                    "value": { "type": "string" },
                    "label": { "type": "string" }
                  }
                }
              },
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
              "timeline": {
                "type": "array",
                "description": "For 'timeline': 3-5 steps in order.",
                "items": {
                  "type": "object",
                  "properties": {
                    "marker": { "type": "string", "description": "Date/phase label, e.g. 'Q1', '2024', 'Step 1'." },
                    "title": { "type": "string" },
                    "description": { "type": "string" }
                  }
                }
              },
              "comparison": {
                "type": "array",
                "description": "For 'comparison': 2-3 option columns.",
                "items": {
                  "type": "object",
                  "properties": {
                    "heading": { "type": "string" },
                    "subtitle": { "type": "string" },
                    "points": { "type": "array", "items": { "type": "string" } }
                  }
                }
              },
              "chart": {
                "type": "object",
                "description": "For 'chart': a native editable chart.",
                "properties": {
                  "type": { "type": "string", "enum": ["bar", "line", "pie"] },
                  "categories": { "type": "array", "items": { "type": "string" }, "description": "X-axis / slice labels." },
                  "series": {
                    "type": "array",
                    "description": "One or more data series (pie uses the first only).",
                    "items": {
                      "type": "object",
                      "properties": {
                        "name": { "type": "string" },
                        "values": { "type": "array", "items": { "type": "number" } }
                      }
                    }
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
            var images = await ResolveImagesAsync(spec, context, ct);
          var imageError = ValidateImageReferences(spec, images);
          if (imageError is not null)
          {
            return ToolResult.Fail(imageError);
          }
            var bytes = _generator.Generate(spec, images);
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

    private async Task<Dictionary<string, ImageAsset>> ResolveImagesAsync(
        DeckSpec spec, ToolContext context, CancellationToken ct)
    {
        var ids = spec.Slides.Select(s => s.ImageId).Where(id => !string.IsNullOrWhiteSpace(id))!.Cast<string>();
        using var scope = _scopeFactory.CreateScope();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        return await ThreadAttachments.ResolveImagesAsync(ids, messages, _storage, context.ThreadId, ct);
    }

    private static string? ValidateImageReferences(DeckSpec spec, IReadOnlyDictionary<string, ImageAsset> images)
    {
      for (int i = 0; i < spec.Slides.Count; i++)
      {
        var slide = spec.Slides[i];
        var layout = (slide.Layout ?? string.Empty).Trim();
        var usesImageLayout = string.Equals(layout, "image", StringComparison.OrdinalIgnoreCase)
          || string.Equals(layout, "image-side", StringComparison.OrdinalIgnoreCase);

        if (!usesImageLayout)
        {
          continue;
        }

        if (string.IsNullOrWhiteSpace(slide.ImageId))
        {
          return $"Slide {i + 1} uses layout '{layout}' but has no imageId. Only use 'image'/'image-side' when referencing an uploaded image attachment.";
        }

        if (!images.ContainsKey(slide.ImageId))
        {
          return $"Slide {i + 1} references imageId '{slide.ImageId}', but that uploaded image could not be resolved in this conversation.";
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

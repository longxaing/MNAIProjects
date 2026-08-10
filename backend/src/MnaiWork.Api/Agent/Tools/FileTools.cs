using System.Text;
using System.Text.Json;
using MnaiWork.Api.Data;
using MnaiWork.Api.Generation;
using MnaiWork.Api.Models;
using MnaiWork.Api.Storage;

namespace MnaiWork.Api.Agent.Tools;

/// <summary>
/// Shared helper that resolves artifacts strictly within the caller's current thread. This is the
/// security boundary for the file tools: the model can only ever reach files produced in this
/// conversation, addressed by artifact id — never by an arbitrary blob path.
/// </summary>
internal static class ThreadArtifacts
{
    public static async Task<IReadOnlyList<Artifact>> ListAsync(
        IMessageRepository messages, string threadId, CancellationToken ct)
    {
        var history = await messages.ListAsync(threadId, ct);
        return history.SelectMany(m => m.Artifacts).ToList();
    }

    public static async Task<Artifact?> FindAsync(
        IMessageRepository messages, string threadId, string artifactId, CancellationToken ct)
    {
        var all = await ListAsync(messages, threadId, ct);
        return all.FirstOrDefault(a => string.Equals(a.Id, artifactId, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Resolves user-uploaded attachments strictly within the caller's current thread.</summary>
internal static class ThreadAttachments
{
    public static async Task<IReadOnlyList<Attachment>> ListAsync(
        IMessageRepository messages, string threadId, CancellationToken ct)
    {
        var history = await messages.ListAsync(threadId, ct);
        return history.SelectMany(m => m.Attachments).ToList();
    }

    public static async Task<Attachment?> FindAsync(
        IMessageRepository messages, string threadId, string attachmentId, CancellationToken ct)
    {
        var all = await ListAsync(messages, threadId, ct);
        return all.FirstOrDefault(a => string.Equals(a.Id, attachmentId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resolves referenced image attachment ids into decoded <see cref="ImageAsset"/>s.</summary>
    public static async Task<Dictionary<string, ImageAsset>> ResolveImagesAsync(
        IEnumerable<string> imageIds, IMessageRepository messages, IFileStorage storage,
        string threadId, CancellationToken ct)
    {
        var result = new Dictionary<string, ImageAsset>(StringComparer.OrdinalIgnoreCase);
        var wanted = imageIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Count == 0)
        {
            return result;
        }

        var all = await ListAsync(messages, threadId, ct);
        foreach (var id in wanted)
        {
            var att = all.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)
                && a.Kind == AttachmentKind.Image);
            if (att is null)
            {
                continue;
            }
            var bytes = await storage.ReadBytesAsync(att.BlobPath, ct);
            if (bytes is null || bytes.Length == 0)
            {
                continue;
            }
            var (w, h) = ImageDimensions.Read(bytes);
            result[id] = new ImageAsset(bytes, att.ContentType, w, h);
        }
        return result;
    }
}

/// <summary>Lists the documents/presentations already generated in the current conversation.</summary>
public sealed class ListMyFilesTool : IAgentTool
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ListMyFilesTool(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public string Name => "list_my_files";

    public string Description =>
        "List the files (documents and presentations) already generated earlier in THIS conversation, " +
        "so you can reference or revise one. Returns each file's id, name, kind and size. " +
        "Use the returned id with read_my_file to inspect a file's contents.";

    public string ParametersSchema => """
    { "type": "object", "properties": {} }
    """;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var artifacts = await ThreadArtifacts.ListAsync(messages, context.ThreadId, ct);

        if (artifacts.Count == 0)
        {
            return ToolResult.Ok("No files have been generated in this conversation yet.");
        }

        var sb = new StringBuilder($"{artifacts.Count} file(s) in this conversation:\n");
        foreach (var a in artifacts)
        {
            sb.AppendLine($"- id={a.Id} | {a.FileName} | {a.Kind} | {a.SizeBytes / 1024} KB | {a.CreatedAt:u}");
        }
        return ToolResult.Ok(sb.ToString());
    }
}

/// <summary>Reads the text content of a previously generated file (by id) from the current conversation.</summary>
public sealed class ReadMyFileTool : IAgentTool
{
    private const int MaxChars = 20000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFileStorage _storage;
    private readonly ILogger<ReadMyFileTool> _logger;

    public ReadMyFileTool(IServiceScopeFactory scopeFactory, IFileStorage storage, ILogger<ReadMyFileTool> logger)
    {
        _scopeFactory = scopeFactory;
        _storage = storage;
        _logger = logger;
    }

    public string Name => "read_my_file";

    public string Description =>
        "Read the text content of a file generated earlier in THIS conversation, addressed by its id " +
        "(from list_my_files). Useful to revise or summarize an existing document/presentation. " +
        "Returns extracted plain text (long files are truncated).";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "fileId": { "type": "string", "description": "The artifact id returned by list_my_files." }
      },
      "required": ["fileId"]
    }
    """;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        if (!arguments.TryGetProperty("fileId", out var idProp) || idProp.GetString() is not { Length: > 0 } fileId)
        {
            return ToolResult.Fail("read_my_file requires a 'fileId' (get one from list_my_files).");
        }

        using var scope = _scopeFactory.CreateScope();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();

        // Ownership check: the artifact must belong to THIS thread. Otherwise refuse.
        var artifact = await ThreadArtifacts.FindAsync(messages, context.ThreadId, fileId, ct);
        if (artifact is null)
        {
            return ToolResult.Fail($"No file with id '{fileId}' exists in this conversation.");
        }

        try
        {
            var read = await _storage.OpenReadAsync(artifact.BlobPath, ct);
            if (read is null)
            {
                return ToolResult.Fail($"The file '{artifact.FileName}' could not be found in storage.");
            }

            using var stream = read.Value.Stream;
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();

            var text = artifact.Kind switch
            {
                ArtifactKind.Docx => DocumentTextExtractor.FromDocx(bytes),
                ArtifactKind.Pptx => DocumentTextExtractor.FromPptx(bytes),
                _ => string.Empty
            };

            if (string.IsNullOrWhiteSpace(text))
            {
                return ToolResult.Ok($"\"{artifact.FileName}\" contains no extractable text.");
            }

            var truncated = text.Length > MaxChars;
            if (truncated)
            {
                text = text[..MaxChars];
            }

            return ToolResult.Ok(
                $"Contents of \"{artifact.FileName}\"{(truncated ? " (truncated)" : "")}:\n\n{text}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "read_my_file failed for artifact {ArtifactId} in thread {ThreadId}",
                fileId, context.ThreadId);
            return ToolResult.Fail($"Failed to read the file: {ex.Message}");
        }
    }
}

/// <summary>Reads the text of a file the user uploaded to this conversation (pdf / docx / pptx).</summary>
public sealed class ReadAttachmentTool : IAgentTool
{
    private const int MaxChars = 24000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFileStorage _storage;
    private readonly ILogger<ReadAttachmentTool> _logger;

    public ReadAttachmentTool(IServiceScopeFactory scopeFactory, IFileStorage storage, ILogger<ReadAttachmentTool> logger)
    {
        _scopeFactory = scopeFactory;
        _storage = storage;
        _logger = logger;
    }

    public string Name => "read_attachment";

    public string Description =>
        "Read the text content of a file the user UPLOADED to this conversation, by its attachment id. " +
        "Works for pdf, docx and pptx. Use this to understand an uploaded document before generating a " +
        "new one from it. (For images, do not read text — reference the image id in the generate tools " +
        "to embed it.) Attachment ids and kinds are listed in the user's message.";

    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "attachmentId": { "type": "string", "description": "The id of an uploaded attachment." }
      },
      "required": ["attachmentId"]
    }
    """;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        if (!arguments.TryGetProperty("attachmentId", out var idProp) || idProp.GetString() is not { Length: > 0 } id)
        {
            return ToolResult.Fail("read_attachment requires an 'attachmentId'.");
        }

        using var scope = _scopeFactory.CreateScope();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();

        var att = await ThreadAttachments.FindAsync(messages, context.ThreadId, id, ct);
        if (att is null)
        {
            return ToolResult.Fail($"No uploaded attachment with id '{id}' exists in this conversation.");
        }
        if (att.Kind == AttachmentKind.Image)
        {
            return ToolResult.Fail(
                $"'{att.FileName}' is an image and has no text. To use it, reference imageId \"{att.Id}\" " +
                "in generate_docx (an 'image' block) or generate_pptx (an 'image' slide).");
        }

        try
        {
            var bytes = await _storage.ReadBytesAsync(att.BlobPath, ct);
            if (bytes is null)
            {
                return ToolResult.Fail($"The file '{att.FileName}' could not be found in storage.");
            }

            var text = att.Kind switch
            {
                AttachmentKind.Pdf => DocumentTextExtractor.FromPdf(bytes),
                AttachmentKind.Docx => DocumentTextExtractor.FromDocx(bytes),
                AttachmentKind.Pptx => DocumentTextExtractor.FromPptx(bytes),
                _ => string.Empty
            };

            if (string.IsNullOrWhiteSpace(text))
            {
                return ToolResult.Ok($"\"{att.FileName}\" contains no extractable text.");
            }

            var truncated = text.Length > MaxChars;
            if (truncated)
            {
                text = text[..MaxChars];
            }
            return ToolResult.Ok($"Contents of uploaded \"{att.FileName}\"{(truncated ? " (truncated)" : "")}:\n\n{text}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "read_attachment failed for {AttachmentId} in thread {ThreadId}", id, context.ThreadId);
            return ToolResult.Fail($"Failed to read the attachment: {ex.Message}");
        }
    }
}

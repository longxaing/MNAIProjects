using System.Text.Json;

namespace MnaiWork.BuildExecution;

internal static class PrimaryWorkflowContract
{
    internal const string FileName = "acceptance.json";

    internal static string Load(string frontend)
    {
        var path = Path.Combine(frontend, FileName);
        try
        {
            if (!File.Exists(path)) throw new InvalidDataException("acceptance.json is missing.");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.GetProperty("version").GetInt32() != 1)
                throw new InvalidDataException("acceptance.json version must be 1.");
            RequireText(root, "name");
            RequireText(root, "submitButton");
            var mutation = root.GetProperty("mutation");
            if (RequireText(mutation, "method") is not ("POST" or "PUT" or "PATCH"))
                throw new InvalidDataException("mutation.method must be POST, PUT, or PATCH.");
            RequireApiPath(mutation, "path");
            RequireApiPath(root, "readPath");
            var fields = root.GetProperty("fields");
            if (fields.ValueKind != JsonValueKind.Array || fields.GetArrayLength() is < 1 or > 12)
                throw new InvalidDataException("fields must contain 1 to 12 labelled text inputs.");
            var hasUniqueValue = false;
            foreach (var field in fields.EnumerateArray())
            {
                RequireText(field, "label");
                hasUniqueValue |= RequireText(field, "value").Contains("{{unique}}", StringComparison.Ordinal);
            }
            if (!hasUniqueValue || !RequireText(root, "expectedText").Contains("{{unique}}", StringComparison.Ordinal))
                throw new InvalidDataException("A field value and expectedText must contain {{unique}} to verify fresh persisted data.");
            return root.GetRawText();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or InvalidDataException or FormatException)
        {
            throw new InvalidDataException(
                $"src/frontend/{FileName}: {ex.Message} Add a real primary create/read workflow: version, name, " +
                "fields [{label,value}], submitButton, mutation {method,path}, readPath, expectedText. " +
                "The runner submits the real UI to the local API and verifies the new value after reload on desktop and mobile. " +
                "Implement missing UI handlers/API/persistence and rerun build_test_project; heading-only tests are not sufficient.", ex);
        }
    }

    private static string RequireText(JsonElement element, string property)
    {
        var value = element.GetProperty(property).GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 500)
            throw new InvalidDataException($"{property} must be nonempty and at most 500 characters.");
        return value;
    }

    private static void RequireApiPath(JsonElement element, string property)
    {
        var path = RequireText(element, property);
        if (!path.StartsWith("/api/", StringComparison.Ordinal) || path.Contains('?') || path.Contains('#'))
            throw new InvalidDataException($"{property} must be an /api/ path without a query or fragment, not a health probe.");
    }
}
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;
using D = DocumentFormat.OpenXml.Drawing;

namespace MnaiWork.Api.Generation;

/// <summary>Extracts plain text from generated .docx / .pptx bytes so the model can read them back.</summary>
public static class DocumentTextExtractor
{
    public static string FromDocx(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var doc = WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        return body is null ? string.Empty : body.InnerText;
    }

    public static string FromPptx(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var doc = PresentationDocument.Open(ms, false);
        var presentationPart = doc.PresentationPart;
        if (presentationPart?.Presentation?.SlideIdList is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var index = 1;
        foreach (var slideId in presentationPart.Presentation.SlideIdList.Elements<P.SlideId>())
        {
            if (slideId.RelationshipId?.Value is not string relId)
            {
                continue;
            }
            if (presentationPart.GetPartById(relId) is not SlidePart slidePart)
            {
                continue;
            }

            sb.AppendLine($"--- Slide {index++} ---");
            foreach (var text in slidePart.Slide.Descendants<D.Text>())
            {
                if (!string.IsNullOrWhiteSpace(text.Text))
                {
                    sb.AppendLine(text.Text);
                }
            }
        }

        return sb.ToString();
    }
}

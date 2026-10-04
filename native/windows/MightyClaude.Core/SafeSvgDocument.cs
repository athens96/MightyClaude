using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MightyClaude.Core;

/// A bounded, self-contained SVG for a native rasterizer. XML is inspected
/// before any graphics decoder sees it; no DTD, scripts or outside resources.
public sealed record SafeSvgDocument(string Xml, double Width, double Height)
{
    public static SafeSvgDocument Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length is 0 or > AgentImageSupport.MaximumImageBytes) throw new AgentImageException(data.Length == 0 ? AgentImageError.Empty : AgentImageError.TooLarge);
        if (FilePreviewClassifier.SvgLoadsExternalContent(data)) throw new AgentImageException(AgentImageError.ExternalSvg);
        var bytes = data.ToArray();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = AgentImageSupport.MaximumImageBytes, IgnoreComments = true };
        try
        {
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = XmlReader.Create(stream, settings))
            {
                var nodes = 0;
                while (reader.Read())
                {
                    if (++nodes > 100_000 || reader.Depth > 64) throw new AgentImageException(AgentImageError.TooLarge);
                    if (reader.NodeType is XmlNodeType.DocumentType or XmlNodeType.EntityReference or XmlNodeType.ProcessingInstruction) throw new AgentImageException(AgentImageError.ExternalSvg);
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (reader.NamespaceURI is not ("" or "http://www.w3.org/2000/svg") || reader.LocalName.ToLowerInvariant() is "script" or "foreignobject" or "iframe" or "object" or "embed" or "set" or "animate" or "animatetransform" or "animatemotion") throw new AgentImageException(AgentImageError.ExternalSvg);
                    if (reader.AttributeCount > 128) throw new AgentImageException(AgentImageError.TooLarge);
                    if (reader.MoveToFirstAttribute())
                    {
                        do
                        {
                            if (reader.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase) || reader.Name.Equals("xml:base", StringComparison.OrdinalIgnoreCase)) throw new AgentImageException(AgentImageError.ExternalSvg);
                        } while (reader.MoveToNextAttribute());
                        reader.MoveToElement();
                    }
                }
            }
            using var input = new MemoryStream(bytes, false); using var xml = XmlReader.Create(input, settings);
            var document = XDocument.Load(xml, LoadOptions.None);
            if (document.Root is not { } root || root.Name.LocalName != "svg") throw new AgentImageException(AgentImageError.Undecodable);
            // Dimension attributes are tiny numbers, not drawing payloads. Bound
            // them before the shared unit parser processes adversarial text.
            foreach (var name in new[] { "width", "height", "viewBox" })
                if (root.Attribute(name)?.Value.Length > 512) throw new AgentImageException(AgentImageError.TooManyPixels);
            var canonical = root.ToString(SaveOptions.DisableFormatting);
            var size = FilePreviewClassifier.SvgSize(Encoding.UTF8.GetBytes(canonical)) ?? (300.0, 150.0);
            if (!FilePreviewClassifier.IsDrawable(size.Item1, size.Item2)) throw new AgentImageException(AgentImageError.TooManyPixels);
            return new(canonical, size.Item1, size.Item2);
        }
        catch (XmlException) { throw new AgentImageException(AgentImageError.Undecodable); }
    }
}

using System.Buffers.Binary;
using System.Text;
using MightyClaude.Core;

internal static class NativeImageParityVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static byte[] Tiff(bool little, uint width, uint height)
    {
        var bytes = new byte[38]; bytes[0] = bytes[1] = little ? (byte)'I' : (byte)'M';
        void U16(int offset, ushort value) { if (little) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value); else BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset, 2), value); }
        void U32(int offset, uint value) { if (little) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value); else BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), value); }
        U16(2, 42); U32(4, 8); U16(8, 2);
        U16(10, 256); U16(12, 4); U32(14, 1); U32(18, width);
        U16(22, 257); U16(24, 4); U32(26, 1); U32(30, height);
        return bytes;
    }
    internal static Task TiffHeaderHandlesEndianAndRefusesInvalidDirectories()
    {
        foreach (var little in new[] { true, false })
            Check(AgentImageSupport.Inspect(Tiff(little, 60, 40), "image/png") == ("image/tiff", 60, 40), "TIFF is detected by bytes in either byte order");
        var oversized = Tiff(true, 9000, 30);
        try { AgentImageSupport.Inspect(oversized, "image/tiff"); Check(false, "oversized TIFF admitted"); }
        catch (AgentImageException ex) { Check(ex.Error == AgentImageError.TooManyPixels, "TIFF pixel limits apply before WIC allocation"); }
        var bad = Tiff(true, 60, 40); BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(4), uint.MaxValue);
        foreach (var data in new[] { bad, Tiff(false, 60, 40)[..20], Tiff(true, 0, 40) })
        {
            try { AgentImageSupport.Inspect(data, "image/tiff"); Check(false, "bad TIFF admitted"); }
            catch (AgentImageException ex) { Check(ex.Error == AgentImageError.Undecodable, "malformed or missing dimensions are refused safely"); }
        }
        return Task.CompletedTask;
    }
    internal static Task SvgRasterInputRefusesActiveOrExternalContent()
    {
        var safe = SafeSvgDocument.Parse("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 80 40'><defs><linearGradient id='g'/></defs><rect fill='url(#g)' width='80' height='40'/></svg>"u8);
        Check(safe.Width == 80 && safe.Height == 40 && safe.Xml.Contains("linearGradient"), "local definitions and intrinsic SVG dimensions survive canonicalization");
        foreach (var svg in new[] {
            "<!DOCTYPE svg><svg/>",
            "<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///secret'>]><svg>&x;</svg>",
            "<?xml-stylesheet href='https://example.com/style.css'?><svg/>",
            "<svg><script>alert(1)</script></svg>",
            "<svg onload='alert(1)'/>",
            "<svg><foreignObject><body/></foreignObject></svg>",
            "<svg><image href='https://example.com/private.png'/></svg>",
            "<svg><use href='&#104;ttps://example.com/shape.svg#x'/></svg>",
            "<svg><set attributeName='href' to='https://example.com'/></svg>",
            "<svg><style>@import 'https://example.com/a.css';</style></svg>",
            "<svg width='-1' height='40'/>",
            "<svg>" + string.Concat(Enumerable.Repeat("<g>", 70)) + string.Concat(Enumerable.Repeat("</g>", 70)) + "</svg>",
        })
        {
            try { SafeSvgDocument.Parse(Encoding.UTF8.GetBytes(svg)); Check(false, "unsafe SVG admitted: " + svg[..Math.Min(svg.Length, 80)]); }
            catch (AgentImageException) { }
        }
        return Task.CompletedTask;
    }
}

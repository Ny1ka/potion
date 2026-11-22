using System.IO.Compression;
using System.Text;
using System.Xml;
using Potion.Core.Engines;
using Potion.Core.Formats;

namespace Potion.Core.Documents;

/// <summary>Reads documents into the format-neutral <see cref="Document"/> (Windows): TXT, Markdown, HTML, EPUB,
/// DOCX, ODT and RTF, like the macOS readers (DOC is macOS-only; see formats.json).</summary>
public static class DocumentReader
{
    static DocumentReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Document Read(string path, FileFormat format)
    {
        var name = Path.GetFileName(path);
        try
        {
            var document = format.Id switch
            {
                "txt" => ReadText(DecodeText(File.ReadAllBytes(path))),
                "md" => ReadMarkdown(DecodeText(File.ReadAllBytes(path)), Path.GetDirectoryName(Path.GetFullPath(path))!),
                "html" => HtmlReader.Read(DecodeText(File.ReadAllBytes(path)), HtmlReader.LocalImages(Path.GetDirectoryName(Path.GetFullPath(path))!)),
                "epub" => ReadEpub(path),
                "docx" => IsEncryptedOfficeFile(path) ? throw ConversionException.CopyProtected() : DocxReader.Read(path),
                "odt" => OdtReader.Read(path),
                "rtf" => RtfReader.Read(Encoding.Latin1.GetString(File.ReadAllBytes(path))),
                _ => throw ConversionException.Unsupported(format.Name, "a document"),
            };
            document.Title ??= Path.GetFileNameWithoutExtension(path);
            return document;
        }
        catch (Exception e) when (e is not ConversionException and not OperationCanceledException and not OutOfMemoryException)
        {
            // Malformed files make the parsers throw many kinds of exceptions; to the user they're all unreadable.
            throw ConversionException.Unreadable(name);
        }
    }

    /// <summary>Password-protected Word files are an OLE container with an <c>EncryptedPackage</c> stream instead of a zip.</summary>
    public static bool IsEncryptedOfficeFile(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 })) return false;
        return data.AsSpan().IndexOf(Encoding.Unicode.GetBytes("EncryptedPackage")) >= 0;
    }

    /// <summary>UTF-8 (with or without a byte-order mark), UTF-16 with a byte-order mark, otherwise Windows Latin 1.</summary>
    public static string DecodeText(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return Encoding.UTF8.GetString(data, 3, data.Length - 3);
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(1252).GetString(data); }
    }

    /// <summary>One paragraph per line (empty lines kept, so spacing survives).</summary>
    public static Document ReadText(string text)
    {
        var document = new Document();
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n'))
            document.Blocks.Add(new Paragraph(line.Length == 0 ? [] : [new TextRun(line, TextStyle.Plain)]));
        return document;
    }

    /// <summary>Markdown (CommonMark plus GitHub tables and strikethrough) via HTML, like the macOS reader.</summary>
    public static Document ReadMarkdown(string markdown, string directory) =>
        HtmlReader.Read(MarkdownRenderer.ToHtml(markdown), HtmlReader.LocalImages(directory));

    // EPUB

    /// <summary>Font obfuscation only hides embedded fonts; anything else in encryption.xml is DRM.</summary>
    private static readonly HashSet<string> FontObfuscation = ["http://www.idpf.org/2008/embedding", "http://ns.adobe.com/pdf/enc#RC"];

    public static bool IsProtectedEpub(ZipArchive zip)
    {
        if (zip.GetEntry("META-INF/rights.xml") is not null || zip.GetEntry("META-INF/license.lcpl") is not null) return true;
        if (zip.GetEntry("META-INF/encryption.xml") is not { } entry) return false;
        var xml = new XmlDocument();
        using (var stream = entry.Open()) xml.Load(stream);
        return xml.SelectNodes("//*[local-name()='EncryptionMethod']/@Algorithm")!.Cast<XmlNode>().Any(a => !FontObfuscation.Contains(a.Value ?? ""));
    }

    public static Document ReadEpub(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (IsProtectedEpub(zip)) throw ConversionException.CopyProtected();
        var container = Xml(zip, "META-INF/container.xml");
        var opfPath = container.SelectSingleNode("//*[local-name()='rootfile']/@full-path")?.Value ?? throw new InvalidDataException("no rootfile");
        var opf = Xml(zip, opfPath);
        var opfDir = ZipDirectory(opfPath);

        var manifest = opf.SelectNodes("//*[local-name()='item']")!.Cast<XmlElement>()
            .ToDictionary(i => i.GetAttribute("id"), i => (Href: i.GetAttribute("href"), Type: i.GetAttribute("media-type")));
        var document = new Document { Title = opf.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='title']")?.InnerText };
        foreach (var idref in opf.SelectNodes("//*[local-name()='itemref']/@idref")!.Cast<XmlNode>().Select(n => n.Value!))
        {
            if (!manifest.TryGetValue(idref, out var item) || !item.Type.Contains("html")) continue;
            var chapterPath = Resolve(item.Href, opfDir);
            if (zip.GetEntry(chapterPath) is not { } chapter) continue;
            using var reader = new StreamReader(chapter.Open());
            var chapterDir = ZipDirectory(chapterPath);
            var html = HtmlReader.Read(reader.ReadToEnd(), src =>
            {
                var entry = zip.GetEntry(Resolve(src, chapterDir));
                var mime = HtmlReader.MimeForExtension(Path.GetExtension(src.Split('?', '#')[0]));
                if (entry is null || mime is null || entry.Length > 50_000_000) return null;
                using var s = entry.Open();
                using var buffer = new MemoryStream();
                s.CopyTo(buffer);
                return (buffer.ToArray(), mime);
            });
            document.Blocks.AddRange(html.Blocks);
        }
        if (document.Blocks.Count == 0) throw new InvalidDataException("no chapters");
        return document;
    }

    private static XmlDocument Xml(ZipArchive zip, string entryPath)
    {
        var entry = zip.GetEntry(entryPath) ?? throw new InvalidDataException($"missing {entryPath}");
        var xml = new XmlDocument { XmlResolver = null };
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        xml.Load(reader);
        return xml;
    }

    private static string ZipDirectory(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    /// <summary>Resolves an href inside the zip: percent-decoding, "..", and dropping any #fragment.</summary>
    public static string Resolve(string href, string directory)
    {
        var clean = Uri.UnescapeDataString(href.Split('#')[0]);
        var parts = clean.StartsWith('/') || directory.Length == 0 ? new List<string>() : directory.Split('/').ToList();
        foreach (var part in clean.Split('/'))
        {
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (part is not ("." or "")) parts.Add(part);
        }
        return string.Join('/', parts);
    }
}

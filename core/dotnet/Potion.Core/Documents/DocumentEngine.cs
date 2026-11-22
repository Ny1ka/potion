using System.Text;
using Potion.Core.Engines;
using Potion.Core.Formats;

namespace Potion.Core.Documents;

/// <summary>
/// Converts documents on Windows (matrix operations <c>ConvertDocument</c> and <c>DocumentToPdf</c>), the
/// counterpart of the Swift <c>DocumentEngine</c>: read into a <see cref="Document"/>, write the output to a hidden
/// temporary file, re-open it to check it, then save it under a name that doesn't exist yet.
/// </summary>
public sealed class DocumentEngine(ConversionMatrix? matrix = null)
{
    private readonly ConversionMatrix _matrix = matrix ?? ConversionMatrix.Shared;

    /// <summary>Outputs written so far (DOCX, RTF, ODT and PDF follow in the next chunks).</summary>
    public static readonly IReadOnlySet<string> Writable = new HashSet<string> { "txt", "md", "html", "epub" };

    public async Task<string> ConvertAsync(string input, FileFormat inputFormat, FileFormat outputFormat, string outputDirectory,
                                           CancellationToken token = default)
    {
        var conversion = _matrix.Conversion(inputFormat.Id, outputFormat.Id);
        if (conversion?.Operation is not (ConversionOperation.ConvertDocument or ConversionOperation.DocumentToPdf)
            || !Writable.Contains(outputFormat.Id))
            throw ConversionException.Unsupported(inputFormat.Name, outputFormat.Name);
        OutputFiles.CheckWritable(outputDirectory);

        var document = await Task.Run(() => DocumentReader.Read(input, inputFormat), token).ConfigureAwait(false);
        var hasText = document.PlainText.Trim().Length > 0;
        var title = Path.GetFileNameWithoutExtension(input);
        var ext = outputFormat.PreferredExtension;
        var temporary = OutputFiles.TemporaryPath(outputDirectory, ext);
        try
        {
            await File.WriteAllBytesAsync(temporary, Write(document, outputFormat.Id, title), token).ConfigureAwait(false);
            if (!Verify(temporary, outputFormat, hasText)) throw ConversionException.VerificationFailed(Path.GetFileName(input));
            return OutputFiles.Finalize(temporary, outputDirectory, title, ext);
        }
        catch (UnauthorizedAccessException) { throw ConversionException.CannotWrite(Path.GetFileName(outputDirectory)); }
        finally { OutputFiles.RemoveIfPresent(temporary); }
    }

    public static byte[] Write(Document document, string formatId, string title) => formatId switch
    {
        "txt" => Encoding.UTF8.GetBytes(PlainTextWriter.Write(document)),
        "md" => Encoding.UTF8.GetBytes(MarkdownWriter.Write(document)),
        "html" => Encoding.UTF8.GetBytes(HtmlWriter.Write(document, title)),
        "epub" => EpubWriter.Write(document, title, System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is { Length: 2 } l ? l : "en"),
        _ => throw ConversionException.Unsupported("Document", formatId.ToUpperInvariant()),
    };

    /// <summary>Re-opens the output: a valid file of the right kind, with text in it if the input had text.</summary>
    public static bool Verify(string path, FileFormat format, bool expectText)
    {
        try
        {
            return format.Id switch
            {
                "txt" or "md" => !expectText || DocumentReader.DecodeText(File.ReadAllBytes(path)).Trim().Length > 0,
                "html" => File.ReadAllText(path).Contains("<html", StringComparison.OrdinalIgnoreCase),
                _ => DocumentReader.Read(path, format) is var doc && (!expectText || doc.PlainText.Trim().Length > 0),
            };
        }
        catch (Exception e) when (e is ConversionException or IOException) { return false; }
    }
}

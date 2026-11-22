namespace Potion.Core.Engines;

public enum ConversionErrorKind
{
    ToolsMissing, Unsupported, Unreadable, CopyProtected, NoAudio, NoVideo, CannotWrite, ConversionFailed,
    VerificationFailed, InvalidPages, NotSmaller,
}

/// <summary>
/// A conversion problem shown to the user. Messages follow the design system's voice rules
/// (design-system/docs/01-brand.md) and match the Swift core word for word. <see cref="LogDetail"/> carries
/// technical output for the local log only; it is never shown as the message.
/// </summary>
public sealed class ConversionException : Exception
{
    public ConversionErrorKind Kind { get; }
    public string? LogDetail { get; }

    private ConversionException(ConversionErrorKind kind, string message, string? logDetail = null) : base(message)
    {
        Kind = kind;
        LogDetail = logDetail;
    }

    public static ConversionException ToolsMissing() =>
        new(ConversionErrorKind.ToolsMissing, "Potion's conversion tools are missing. Reinstall Potion to restore them.");
    public static ConversionException Unsupported(string from, string to) =>
        new(ConversionErrorKind.Unsupported, $"{from} can't be converted to {to}.");
    public static ConversionException Unreadable(string fileName) =>
        new(ConversionErrorKind.Unreadable, $"Can't read “{fileName}”. The file may be damaged.");
    public static ConversionException CopyProtected() =>
        new(ConversionErrorKind.CopyProtected, "This file is copy-protected, so Potion can't convert it.");
    public static ConversionException NoAudio(string fileName) =>
        new(ConversionErrorKind.NoAudio, $"“{fileName}” has no audio to convert.");
    public static ConversionException NoVideo(string fileName) =>
        new(ConversionErrorKind.NoVideo, $"“{fileName}” has no video to convert.");
    public static ConversionException CannotWrite(string folder) =>
        new(ConversionErrorKind.CannotWrite, $"Can't save to “{folder}”. Check that the folder exists and that you can write to it.");
    public static ConversionException ConversionFailed(string fileName, string detail) =>
        new(ConversionErrorKind.ConversionFailed, $"Couldn't convert “{fileName}”.", detail);
    public static ConversionException VerificationFailed(string fileName) =>
        new(ConversionErrorKind.VerificationFailed, $"The converted copy of “{fileName}” didn't check out, so it wasn't saved.");
}

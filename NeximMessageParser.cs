namespace NeximBridge;

public sealed class NeximMessageParser
{
    public ParsedNeximMessage Parse(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Length == 0)
        {
            throw new NeximMessageParseException("El mensaje Nexim no puede estar vacío.");
        }

        IReadOnlyList<string> sectionTexts = SplitSections(message);
        IReadOnlyList<string[]> sections = sectionTexts
            .Select(section => section.Split('\t'))
            .ToArray();
        string[] firstSectionFields = sections[0];

        if (string.IsNullOrEmpty(firstSectionFields[0]))
        {
            throw new NeximMessageParseException("El primer campo del mensaje debe contener EventName.");
        }

        string? seqId = firstSectionFields.Length > 1 && firstSectionFields[1].Length > 0
            ? firstSectionFields[1]
            : null;

        bool hasNumList = sectionTexts
            .Skip(1)
            .Any(section => section.StartsWith("NumList", StringComparison.Ordinal));

        return new ParsedNeximMessage(
            EventName: firstSectionFields[0],
            SeqID: seqId,
            Fields: firstSectionFields,
            RawMessage: message,
            Sections: sections,
            HasNumList: hasNumList);
    }

    private static IReadOnlyList<string> SplitSections(string message)
    {
        return message
            .Replace("\r\n", "\r", StringComparison.Ordinal)
            .Split('\r');
    }
}

public sealed record ParsedNeximMessage(
    string EventName,
    string? SeqID,
    IReadOnlyList<string> Fields,
    string RawMessage,
    IReadOnlyList<IReadOnlyList<string>> Sections,
    bool HasNumList);

public sealed class NeximMessageParseException(string message, Exception? innerException = null)
    : FormatException(message, innerException);

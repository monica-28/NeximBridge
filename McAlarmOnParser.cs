using static NeximBridge.RemainingEventFields;

namespace NeximBridge;

public sealed record McAlarmOnData(DateTimeOffset EventTimeUtc, string LineName, string MachineName,
    int ModuleNo, string ErrorCode, string SubErrorCode);

public static class McAlarmOnParser
{
    public static McAlarmOnData Parse(ParsedNeximMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string[] fields = message.RawMessage.Split('\t');
        if (message.EventName != "MCALARMON" || fields[0] != "MCALARMON" || fields.Length != 8 ||
            message.RawMessage.Contains('\r') || message.RawMessage.Contains('\n'))
            throw new NeximMessageParseException("MCALARMON: se requieren exactamente 8 campos TAB, sin registros.");
        Sequence(fields[1]);
        return new(Time(fields[2]), Text(fields[3], "LineName"), Text(fields[4], "MachineName"),
            Number(fields[5], "ModuleNo"), HexCode(fields[6], "ErrorCode"), HexCode(fields[7], "SubErrorCode"));
    }
}

using System.Globalization;

namespace NeximBridge;

public sealed record ProdCompletedIIData(DateTimeOffset EventTimeUtc, string LineName, string MachineName,
    int ModuleNo, int LaneNo, int ProductMode, string ProgramName, string PanelNo,
    int BlockCount, int BlockSkipCount, string BSInfoBit, int CycleTime);

public static class ProdCompletedIIParser
{
    public static ProdCompletedIIData Parse(ParsedNeximMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string[] fields = message.RawMessage.Split('\t');
        if (message.EventName != "PRODCOMPLETEDII" || fields.Length != 14 || fields[0] != "PRODCOMPLETEDII" ||
            fields.Any(field => field.Any(char.IsControl)))
            throw new NeximMessageParseException("PRODCOMPLETEDII requiere exactamente 14 campos TAB, sin NumList ni registros CR.");
        int seqId = Number(fields[1], "SeqID");
        if (seqId is < 1 or > 999999)
            throw new NeximMessageParseException("PRODCOMPLETEDII: SeqID debe estar entre 1 y 999999.");
        if (!DateTimeOffset.TryParseExact(fields[2], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset time))
            throw new NeximMessageParseException("PRODCOMPLETEDII: Time debe ser yyyyMMddHHmmss en UTC.");
        return new ProdCompletedIIData(time, Text(fields[3], "LineName"), Text(fields[4], "MachineName"),
            Number(fields[5], "ModuleNo"), FujiDomain.Range(Number(fields[6], "LaneNo"), 1, 2, "LaneNo"), FujiDomain.Range(Number(fields[7], "ProductMode"), 0, 1, "ProductMode"),
            Text(fields[8], "ProgramName"), Text(fields[9], "PanelNo"), FujiDomain.Range(Number(fields[10], "BlockCount"), 0, 9999, "BlockCount"),
            FujiDomain.Range(Number(fields[11], "BlockSkipCount"), 0, 9999, "BlockSkipCount"), FujiDomain.Hex(Text(fields[12], "BSInfoBit")), Number(fields[13], "CycleTime"));
    }

    private static int Number(string value, string field)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            throw new NeximMessageParseException($"PRODCOMPLETEDII: {field} no es un entero no negativo válido: '{value}'.");
        return number;
    }

    private static string Text(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new NeximMessageParseException($"PRODCOMPLETEDII: falta {field}.");
        return value;
    }
}

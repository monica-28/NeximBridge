using System.Globalization;

namespace NeximBridge;

public sealed record ProdStoppedData(DateTimeOffset EventTimeUtc, string LineName, string MachineName,
    int ModuleNo, int LaneNo, int ProductMode, string ProgramName, string PanelNo,
    int BlockCount, int BlockSkipCount);

public static class ProdStoppedParser
{
    public static ProdStoppedData Parse(ParsedNeximMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string[] fields = message.RawMessage.Split('\t');
        if (message.EventName != "PRODSTOPPED" || fields.Length != 12 || fields[0] != "PRODSTOPPED" ||
            fields.Any(field => field.Any(char.IsControl)))
            throw new NeximMessageParseException("PRODSTOPPED requiere exactamente 12 campos TAB, sin NumList ni registros CR.");
        int seqId = Number(fields[1], "SeqID");
        if (seqId is < 1 or > 999999)
            throw new NeximMessageParseException("PRODSTOPPED: SeqID debe estar entre 1 y 999999.");
        if (!DateTimeOffset.TryParseExact(fields[2], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset time))
            throw new NeximMessageParseException("PRODSTOPPED: Time debe ser yyyyMMddHHmmss en UTC.");
        return new ProdStoppedData(time, Text(fields[3], "LineName"), Text(fields[4], "MachineName"),
            Number(fields[5], "ModuleNo"), FujiDomain.Range(Number(fields[6], "LaneNo"), 1, 2, "LaneNo"), FujiDomain.Range(Number(fields[7], "ProductMode"), 0, 1, "ProductMode"),
            Text(fields[8], "ProgramName"), Text(fields[9], "PanelNo"), FujiDomain.Range(Number(fields[10], "BlockCount"), 0, 9999, "BlockCount"),
            FujiDomain.Range(Number(fields[11], "BlockSkipCount"), 0, 9999, "BlockSkipCount"));
    }

    private static int Number(string value, string field)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            throw new NeximMessageParseException($"PRODSTOPPED: {field} no es un entero no negativo válido: '{value}'.");
        return number;
    }

    private static string Text(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new NeximMessageParseException($"PRODSTOPPED: falta {field}.");
        return value;
    }
}

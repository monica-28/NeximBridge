using System.Globalization;

namespace NeximBridge;

public sealed record ProdStartedData(DateTimeOffset EventTimeUtc, string LineName, string MachineName,
    int ModuleNo, int LaneNo, int ProductMode, string ProgramName, string PanelNo);

public static class ProdStartedParser
{
    public static ProdStartedData Parse(ParsedNeximMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string[] fields = message.RawMessage.Split('\t');
        if (message.EventName != "PRODSTARTED" || fields.Length != 10 || fields[0] != "PRODSTARTED" ||
            fields.Any(field => field.Any(char.IsControl)))
            throw new NeximMessageParseException("PRODSTARTED requiere exactamente 10 campos TAB, sin NumList ni registros CR.");
        int seqId = Number(fields[1], "SeqID");
        if (seqId is < 1 or > 999999)
            throw new NeximMessageParseException("PRODSTARTED: SeqID debe estar entre 1 y 999999.");
        if (!DateTimeOffset.TryParseExact(fields[2], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset time))
            throw new NeximMessageParseException("PRODSTARTED: Time debe ser yyyyMMddHHmmss en UTC.");
        return new ProdStartedData(time, Text(fields[3], "LineName"), Text(fields[4], "MachineName"),
            Number(fields[5], "ModuleNo"), Number(fields[6], "LaneNo"), Number(fields[7], "ProductMode"),
            Text(fields[8], "ProgramName"), Text(fields[9], "PanelNo"));
    }

    private static int Number(string value, string field)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            throw new NeximMessageParseException($"PRODSTARTED: {field} no es un entero no negativo válido: '{value}'.");
        return number;
    }

    private static string Text(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new NeximMessageParseException($"PRODSTARTED: falta {field}.");
        return value;
    }
}

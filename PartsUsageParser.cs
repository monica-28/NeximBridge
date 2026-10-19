using System.Globalization;

namespace NeximBridge;

public sealed record PartsUsageData(DateTimeOffset EventTimeUtc, string LineName, string MachineName,
    int ModuleNo, int LaneNo, string ProgramName, string PanelNo, IReadOnlyList<PartsUsageDetail> Details);

public sealed record PartsUsageDetail(int StageNo, int SlotNo, string PartName, string AvlName,
    int PartsUsage, int PickupCount, int ErrorParts, int ErrorReject, int RejectParts,
    int DislodgedParts, int RescanCount, int NoPickup);

public static class PartsUsageParser
{
    public static PartsUsageData Parse(ParsedNeximMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        // Validate the original body: do not depend on the generic HasNumList flag.
        string[] records = message.RawMessage.Split('\r');
        string[] header = records[0].Split('\t');
        if (message.EventName != "PARTSUSAGE" || header.Length != 10 || header[0] != "PARTSUSAGE")
            throw new NeximMessageParseException("PARTSUSAGE requiere 9 campos de cabecera más NumList.");
        int seqId = Number(header[1], "SeqID");
        if (seqId is < 1 or > 999999)
            throw new NeximMessageParseException("PARTSUSAGE: SeqID debe estar entre 1 y 999999.");
        if (!DateTimeOffset.TryParseExact(header[2], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset time))
            throw new NeximMessageParseException("PARTSUSAGE: Time debe ser yyyyMMddHHmmss en UTC.");
        int count = Number(header[9], "NumList");
        if (records[^1] != "" || records.Length - 2 != count)
            throw new NeximMessageParseException("PARTSUSAGE: NumList no coincide con los registros terminados en CR.");

        List<PartsUsageDetail> details = [];
        for (int index = 1; index <= count; index++)
        {
            string[] fields = records[index].Split('\t');
            if (fields.Length != 12)
                throw new NeximMessageParseException($"PARTSUSAGE: registro {index} debe contener exactamente 12 campos.");
            details.Add(new PartsUsageDetail(
                Number(fields[0], "StageNo"), Number(fields[1], "SlotNo"),
                Text(fields[2], "PartName"), Text(fields[3], "AVLName"),
                Number(fields[4], "PartsUsage"), Number(fields[5], "PickupCount"),
                Number(fields[6], "ErrorParts"), Number(fields[7], "ErrorReject"),
                Number(fields[8], "RejectParts"), Number(fields[9], "DislodgedParts"),
                Number(fields[10], "Rescancount"), Number(fields[11], "NoPickup")));
        }
        return new PartsUsageData(time, Text(header[3], "LineName"), Text(header[4], "MachineName"),
            Number(header[5], "ModuleNo"), FujiDomain.Range(Number(header[6], "LaneNo"), 1, 2, "LaneNo"),
            Text(header[7], "ProgramName"), Text(header[8], "PanelNo"), details);
    }

    private static int Number(string value, string field)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result))
            throw new NeximMessageParseException($"PARTSUSAGE: {field} no es un entero no negativo válido: '{value}'.");
        return result;
    }

    private static string Text(string value, string field)
    {
        if (value.Any(char.IsControl))
            throw new NeximMessageParseException($"PARTSUSAGE: {field} contiene caracteres de control.");
        return value;
    }
}

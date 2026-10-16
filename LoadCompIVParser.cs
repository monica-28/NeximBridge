using static NeximBridge.RemainingEventFields;

namespace NeximBridge;

public sealed record LoadCompIVData(DateTimeOffset EventTimeUtc, string LineName, string MachineName,
    int ModuleNo, IReadOnlyList<LoadCompIVDetail> Details);
public sealed record LoadCompIVDetail(int StageNo, int SlotNo, string PartNo, string FeederID, string ReelID, string Vendor, string LotNo, string DateCode, string LightingClass, int Quantity, int SupplyMethod);

public static class LoadCompIVParser
{
    public static LoadCompIVData Parse(ParsedNeximMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string[] records = message.RawMessage.Split('\r');
        string[] header = records[0].Split('\t');
        if (message.EventName != "LOADCOMPIV" || header[0] != "LOADCOMPIV" || header.Length != 7)
            throw new NeximMessageParseException("LOADCOMPIV: se requieren 7 campos de cabecera incluyendo NumList.");
        Sequence(header[1]);
        DateTimeOffset time = Time(header[2]);
        string line = Text(header[3], "LineName"), machine = Text(header[4], "MachineName");
        int module = Number(header[5], "ModuleNo"), count = Number(header[6], "NumList");
        if (records[^1] != "" || records.Length - 2 != count)
            throw new NeximMessageParseException("LOADCOMPIV: NumList no coincide con los registros terminados en CR.");
        List<LoadCompIVDetail> details = [];
        for (int i = 1; i <= count; i++)
        {
            string[] fields = records[i].Split('\t');
            if (fields.Length != 11)
                throw new NeximMessageParseException($"LOADCOMPIV: registro {i} requiere exactamente 11 campos.");
            LoadCompIVDetail detail = new(Number(fields[0], "StageNo"),
                Number(fields[1], "SlotNo"),
                Text(fields[2], "PartNo"),
                Text(fields[3], "FeederID"),
                Text(fields[4], "ReelID"),
                Text(fields[5], "Vendor"),
                Text(fields[6], "LotNo"),
                Text(fields[7], "DateCode", 50),
                Text(fields[8], "LightingClass"),
                Number(fields[9], "Quantity"),
                Number(fields[10], "SupplyMethod"));
            if (detail.SupplyMethod is not (0 or 1))
                throw new NeximMessageParseException("LOADCOMPIV: SupplyMethod debe ser 0 o 1.");
            details.Add(detail);
        }
        return new(time, line, machine, module, details.AsReadOnly());
    }
}

using static NeximBridge.RemainingEventFields;

namespace NeximBridge;

public sealed record ChangeCompIIData(DateTimeOffset EventTimeUtc, string LineName, string MachineName,
    int ModuleNo, IReadOnlyList<ChangeCompIIDetail> Details);
public sealed record ChangeCompIIDetail(int StageNo, int SlotNo, string PartNo, string FeederID, string OldReelID, string NewReelID, int NewReelQuantity);

public static class ChangeCompIIParser
{
    public static ChangeCompIIData Parse(ParsedNeximMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string[] records = message.RawMessage.Split('\r');
        string[] header = records[0].Split('\t');
        if (message.EventName != "CHANGECOMPII" || header[0] != "CHANGECOMPII" || header.Length != 7)
            throw new NeximMessageParseException("CHANGECOMPII: se requieren 7 campos de cabecera incluyendo NumList.");
        Sequence(header[1]);
        DateTimeOffset time = Time(header[2]);
        string line = Text(header[3], "LineName"), machine = Text(header[4], "MachineName");
        int module = Number(header[5], "ModuleNo"), count = Number(header[6], "NumList");
        if (records[^1] != "" || records.Length - 2 != count)
            throw new NeximMessageParseException("CHANGECOMPII: NumList no coincide con los registros terminados en CR.");
        List<ChangeCompIIDetail> details = [];
        for (int i = 1; i <= count; i++)
        {
            string[] fields = records[i].Split('\t');
            if (fields.Length != 7)
                throw new NeximMessageParseException($"CHANGECOMPII: registro {i} requiere exactamente 7 campos.");
            ChangeCompIIDetail detail = new(Number(fields[0], "StageNo"),
                Number(fields[1], "SlotNo"),
                Text(fields[2], "PartNo"),
                Text(fields[3], "FeederID"),
                Text(fields[4], "OldReelID"),
                Text(fields[5], "NewReelID"),
                Number(fields[6], "NewReelQuantity"));
            details.Add(detail);
        }
        return new(time, line, machine, module, details.AsReadOnly());
    }
}

using static NeximBridge.RemainingEventFields;

namespace NeximBridge;

public sealed record UnloadCompData(DateTimeOffset EventTimeUtc, string LineName, string MachineName,
    int ModuleNo, IReadOnlyList<UnloadCompDetail> Details);
public sealed record UnloadCompDetail(int StageNo, int SlotNo, string PartNo, string FeederID, string ReelID, int Quantity, int RemainingTime);

public static class UnloadCompParser
{
    public static UnloadCompData Parse(ParsedNeximMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string[] records = message.RawMessage.Split('\r');
        string[] header = records[0].Split('\t');
        if (message.EventName != "UNLOADCOMP" || header[0] != "UNLOADCOMP" || header.Length != 7)
            throw new NeximMessageParseException("UNLOADCOMP: se requieren 7 campos de cabecera incluyendo NumList.");
        Sequence(header[1]);
        DateTimeOffset time = Time(header[2]);
        string line = Text(header[3], "LineName"), machine = Text(header[4], "MachineName");
        int module = Number(header[5], "ModuleNo"), count = Number(header[6], "NumList");
        if (records[^1] != "" || records.Length - 2 != count)
            throw new NeximMessageParseException("UNLOADCOMP: NumList no coincide con los registros terminados en CR.");
        List<UnloadCompDetail> details = [];
        for (int i = 1; i <= count; i++)
        {
            string[] fields = records[i].Split('\t');
            if (fields.Length != 7)
                throw new NeximMessageParseException($"UNLOADCOMP: registro {i} requiere exactamente 7 campos.");
            UnloadCompDetail detail = new(Number(fields[0], "StageNo"),
                Number(fields[1], "SlotNo"),
                Text(fields[2], "PartNo"),
                Text(fields[3], "FeederID"),
                Text(fields[4], "ReelID"),
                Number(fields[5], "Quantity"),
                FujiDomain.Range(Number(fields[6], "RemainingTime"), 10, 999999, "RemainingTime"));
            details.Add(detail);
        }
        return new(time, line, machine, module, details.AsReadOnly());
    }
}

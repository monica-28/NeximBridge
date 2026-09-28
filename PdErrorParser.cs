using System.Globalization;

namespace NeximBridge;

public sealed record PdErrorData(DateTimeOffset EventTimeUtc, string LineName, string MachineName,
    int ModuleNo, int LaneNo, string ProgramName, IReadOnlyList<PdErrorDetail> Details);

public sealed record PdErrorDetail(int StageNo, int SlotNo, string PartName, string AvlName,
    int HeadNo, int HolderNo, int NozzleNo, int NozzlePitNo, int BoardNo,
    string Reference, string ErrorCode, string SubErrorCode);

public static class PdErrorParser
{
    public static PdErrorData Parse(ParsedNeximMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string[] records = message.RawMessage.Split('\r');
        string[] header = records[0].Split('\t');
        // Fuji 4.52.1: eight header fields followed by NumList. No PanelNo.
        if (message.EventName != "PDERROR" || header[0] != "PDERROR" || header.Length != 9)
            throw new NeximMessageParseException("PDERROR requiere exactamente 8 campos de cabecera más NumList, sin PanelNo.");
        int seq = Number(header[1], "SeqID");
        if (seq is < 1 or > 999999)
            throw new NeximMessageParseException("PDERROR: SeqID debe estar entre 1 y 999999.");
        if (!DateTimeOffset.TryParseExact(header[2], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset time))
            throw new NeximMessageParseException("PDERROR: Time debe ser yyyyMMddHHmmss en UTC.");
        int count = Number(header[^1], "NumList");
        if (records[^1] != "" || records.Length - 2 != count)
            throw new NeximMessageParseException("PDERROR: NumList no coincide con los registros terminados en CR.");
        List<PdErrorDetail> details = [];
        for (int i = 1; i <= count; i++)
        {
            string[] fields = records[i].Split('\t');
            if (fields.Length != 12)
                throw new NeximMessageParseException($"PDERROR: registro {i} debe contener exactamente 12 campos.");
            details.Add(new PdErrorDetail(Number(fields[0], "StageNo"), Number(fields[1], "SlotNo"),
                Text(fields[2], "PartName"), Text(fields[3], "AVLName"), Number(fields[4], "HeadNo"),
                Number(fields[5], "HolderNo"), Number(fields[6], "NozzleNo"), Number(fields[7], "NozzlePitNo"),
                Number(fields[8], "BoardNo"), Text(fields[9], "Reference"),
                ErrorCode(fields[10], "ErrorCode"), ErrorCode(fields[11], "SubErrorCode")));
        }
        return new PdErrorData(time, Text(header[3], "LineName"), Text(header[4], "MachineName"),
            Number(header[5], "ModuleNo"), Number(header[6], "LaneNo"), Text(header[7], "ProgramName"), details);
    }

    private static int Number(string value, string field)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            throw new NeximMessageParseException($"PDERROR: {field} no es un entero no negativo válido: '{value}'.");
        return number;
    }

    private static string Text(string value, string field)
    {
        if (value.Any(char.IsControl))
            throw new NeximMessageParseException($"PDERROR: {field} contiene caracteres de control.");
        return value;
    }

    private static string ErrorCode(string value, string field)
    {
        if (value.Length != 8 || value.Any(c => !Uri.IsHexDigit(c)))
            throw new NeximMessageParseException($"PDERROR: {field} debe contener 8 dígitos hexadecimales.");
        return value; // Preserve codes verbatim; do not derive a description.
    }
}

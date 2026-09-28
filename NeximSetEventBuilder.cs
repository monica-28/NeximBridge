using System.Globalization;
namespace NeximBridge;

public sealed class NeximSetEventBuilder
{
    // Fuji 4.8.1: NumList is a value; every record ends in CR.
    public byte[] Build(string seqId, IEnumerable<string> events, string machineName = "")
    {
        NeximInitializationProtocol.ValidateSequence(seqId);
        NeximInitializationProtocol.ValidateField(machineName);
        ArgumentNullException.ThrowIfNull(events);
        string[] records = events.ToArray();
        if (records.Length == 0 || records.Distinct(StringComparer.Ordinal).Count() != records.Length)
            throw new ArgumentException("La lista de eventos debe ser no vacía y sin duplicados.", nameof(events));
        foreach (string name in records) NeximInitializationProtocol.ValidateEvent(name);
        return NeximInitializationProtocol.Frame(
            $"SETEV\t{seqId}\t{machineName}\t{records.Length.ToString(CultureInfo.InvariantCulture)}\r" +
            string.Concat(records.Select(name => $"{name}\t1\r")));
    }
}

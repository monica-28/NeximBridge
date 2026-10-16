using System.Globalization;
namespace NeximBridge;

public sealed class NeximSetEventBuilder
{
    // Fuji 4.8.1: NumList is a value; every record ends in CR.
    public byte[] Build(string seqId, IEnumerable<string> events, string machineName = "")
        => BuildSubscriptions(seqId, events.Select(name => new NeximEventSubscription { Name = name, Ack = true }), machineName);

    public byte[] BuildSubscriptions(string seqId, IEnumerable<NeximEventSubscription> events, string machineName = "")
    {
        NeximInitializationProtocol.ValidateSequence(seqId);
        NeximInitializationProtocol.ValidateField(machineName);
        ArgumentNullException.ThrowIfNull(events);
        NeximEventSubscription[] records = events.ToArray();
        if (records.Length == 0 || records.Select(record => record.Name).Distinct(StringComparer.Ordinal).Count() != records.Length)
            throw new ArgumentException("La lista de eventos debe ser no vacía y sin duplicados.", nameof(events));
        foreach (var record in records) NeximInitializationProtocol.ValidateEvent(record.Name);
        return NeximInitializationProtocol.Frame(
            $"SETEV\t{seqId}\t{machineName}\t{records.Length.ToString(CultureInfo.InvariantCulture)}\r" +
            string.Concat(records.Select(record => $"{record.Name}\t{(record.Ack ? 1 : 0)}\r")));
    }
}

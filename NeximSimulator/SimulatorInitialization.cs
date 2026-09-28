using System.Globalization;
using NeximBridge;

namespace NeximSimulator;

public sealed class SimulatorInitialization
{
    // Accepted subscriptions; the single PARTSUSAGE sample runs after initialization.
    private static readonly HashSet<string> SupportedEvents = new(StringComparer.Ordinal)
    {
        "PARTSUSAGE", "PDERROR", "PRODSTARTED", "PRODCOMPLETEDII", "PRODSTOPPED",
        "LOADCOMPIV", "CHANGECOMPII", "UNLOADCOMP", "MCALARMON", "MCALARMOFF", "RECOVERYUP", "KEEPALIVE"
    };
    public bool IsReceiving { get; private set; }
    public IReadOnlyDictionary<string, bool> Events { get; private set; } = new Dictionary<string, bool>();

    public async Task RunAsync(Stream stream, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        IsReceiving = false;
        Events = new Dictionary<string, bool>();
        NeximFrameReader reader = new();
        try
        {
            string set = await ReadAsync();
            string[] header = set.Split('\r')[0].Split('\t');
            ValidateEnvelope(header);
            bool valid = header[0] == "SETEV" && TryReadEvents(set, out _);
            await ReplyAsync(header, valid);
            if (!valid) throw new NeximInitializationException("SETEV inválido o comando fuera de orden.");
            TryReadEvents(set, out Dictionary<string, bool> subscriptions);

            string start = await ReadAsync();
            string[] startFields = start.Split('\t');
            ValidateEnvelope(startFields);
            valid = startFields.Length == 3 && startFields[0] == "STARTEV" &&
                startFields[2] == header[2] && startFields[1] != header[1];
            await ReplyAsync(startFields, valid);
            if (!valid) throw new NeximInitializationException("STARTEV inválido o comando fuera de orden.");
            Events = subscriptions;
            IsReceiving = true;
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        async Task<string> ReadAsync()
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            return await reader.ReadMessageAsync(stream, deadline.Token);
        }
        async Task ReplyAsync(string[] fields, bool accepted)
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            await stream.WriteAsync(NeximInitializationProtocol.Acknowledgement(fields[0], fields[1], fields[2], accepted), deadline.Token);
        }
    }

    private static void ValidateEnvelope(string[] fields)
    {
        if (fields.Length < 3 || fields[0] is not ("SETEV" or "STARTEV"))
            throw new NeximInitializationException("No se puede confirmar una cabecera sin comando, SeqID y MachineName.");
        NeximInitializationProtocol.ValidateSequence(fields[1]);
        NeximInitializationProtocol.ValidateField(fields[2]);
    }

    private static bool TryReadEvents(string message, out Dictionary<string, bool> events)
    {
        events = new(StringComparer.Ordinal);
        string[] records = message.Split('\r');
        string[] header = records[0].Split('\t');
        if (header.Length != 4 || !int.TryParse(header[3], NumberStyles.None, CultureInfo.InvariantCulture, out int count) ||
            count < 1 || records.Length - 2 != count || records[^1] != "") return false;
        foreach (string record in records.Skip(1).Take(count))
        {
            string[] fields = record.Split('\t');
            if (fields.Length != 2 || !SupportedEvents.Contains(fields[0]) || fields[1] is not ("0" or "1") ||
                !events.TryAdd(fields[0], fields[1] == "1")) return false;
        }
        return true;
    }
}

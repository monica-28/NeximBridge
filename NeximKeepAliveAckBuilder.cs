namespace NeximBridge;

public sealed class NeximKeepAliveAckBuilder
{
    public byte[] Build(string seqId)
    {
        NeximInitializationProtocol.ValidateSequence(seqId);
        return NeximInitializationProtocol.Frame($"KEEPALIVE_ACK\t{seqId}");
    }

    public static string Validate(string rawMessage)
    {
        string[] fields = rawMessage.Split('\t');
        if (fields.Length != 2 || fields[0] != "KEEPALIVE" || rawMessage.Contains('\r') || rawMessage.Contains('\n'))
            throw new NeximMessageParseException("KEEPALIVE requiere exactamente dos campos TAB, sin CR ni LF.");
        try { NeximInitializationProtocol.ValidateSequence(fields[1]); }
        catch (ArgumentException error) { throw new NeximMessageParseException("KEEPALIVE: SeqID invalido.", error); }
        return fields[1];
    }

    public async Task RespondAsync(Stream stream, string rawMessage,
        Func<CancellationToken, Task> persist, ILogger logger, CancellationToken stoppingToken)
    {
        string seqId = Validate(rawMessage);
        await stream.WriteAsync(Build(seqId), stoppingToken);
        await stream.FlushAsync(stoppingToken);
        logger.LogInformation("KEEPALIVE recibido y confirmado con SeqID {SeqID}.", seqId);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        try
        {
            Task persistence = persist(deadline.Token);
            // Bound reception even if an underlying provider is slow to honour cancellation.
            _ = persistence.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await persistence.WaitAsync(deadline.Token);
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "No se pudo persistir KEEPALIVE SeqID={SeqID}; ACK ya enviado.", seqId);
        }
    }
}

using System.Text;

namespace NeximBridge;

public sealed class ProdCompletedIIAckSender(NeximAckBuilder builder, ILogger<ProdCompletedIIAckSender> logger)
{
    public async Task SendAsync(Stream stream, ParsedNeximMessage message, int result,
        CancellationToken cancellationToken = default)
    {
        if (message.EventName != "PRODCOMPLETEDII")
            throw new NeximAckException("Este emisor solo admite PRODCOMPLETEDII.");
        byte[] frame = builder.Build(message, result);
        logger.LogInformation("TX PRODCOMPLETEDII_ACK SeqID={SeqID} Result={Result} Machine={Machine}",
            message.SeqID, result, message.Fields[4]);
        string visible = Encoding.UTF8.GetString(frame, 4, frame.Length - 4)
            .Replace("\t", "\\t").Replace("\u0002", "STX").Replace("\u0003", "ETX");
        logger.LogInformation("TX frame LengthBE={LengthBE} {Frame}",
            Convert.ToHexString(frame.AsSpan(0, 4)), visible);
        // Use only the caller's existing session; this class never opens a connection.
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}

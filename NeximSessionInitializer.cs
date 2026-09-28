using System.Globalization;
namespace NeximBridge;

public enum NeximSessionState { Connected, WaitingSetEventAck, WaitingStartEventAck, Receiving, Failed }

public sealed class NeximSessionInitializer(
    NeximFrameReader reader, NeximSetEventBuilder setEventBuilder, NeximStartEventBuilder startEventBuilder)
{
    private int sequence;
    public NeximSessionState State { get; private set; } = NeximSessionState.Connected;
    public async Task InitializeAsync(Stream stream, IEnumerable<string> events, string machineName,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        State = NeximSessionState.Connected;
        try
        {
            if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
                throw new ArgumentOutOfRangeException(nameof(timeout));
            string setSequence = NextSequence();
            byte[] setFrame = setEventBuilder.Build(setSequence, events, machineName);
            string startSequence = NextSequence();
            byte[] startFrame = startEventBuilder.Build(startSequence, machineName);
            State = NeximSessionState.WaitingSetEventAck;
            await ExchangeAsync(setFrame, "SETEV", setSequence);
            State = NeximSessionState.WaitingStartEventAck;
            await ExchangeAsync(startFrame, "STARTEV", startSequence);
            State = NeximSessionState.Receiving;
        }
        catch
        {
            State = NeximSessionState.Failed;
            // Do not reuse a partial or late response in another exchange.
            stream.Dispose();
            throw;
        }
        async Task ExchangeAsync(byte[] frame, string command, string seqId)
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            try
            {
                await stream.WriteAsync(frame, deadline.Token);
                string reply = await reader.ReadMessageAsync(stream, deadline.Token);
                NeximInitializationProtocol.ValidateAcknowledgement(reply, command, seqId, machineName);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new NeximInitializationException($"Timeout esperando {command}_ACK.", exception);
            }
        }
    }
    private string NextSequence()
    {
        sequence = sequence == 999999 ? 1 : sequence + 1;
        return sequence.ToString(CultureInfo.InvariantCulture);
    }
}

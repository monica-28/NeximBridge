using System.Buffers.Binary;
using System.Text;

namespace NeximBridge;

public sealed class NeximKeepAliveAckBuilder
{
    private const byte Stx = 0x02;
    private const byte Etx = 0x03;
    private const char FieldSeparator = '\t';
    private const string EventName = "KEEPALIVE_ACK";

    public byte[] Build(string seqId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seqId);

        if (seqId.Contains(FieldSeparator) || seqId.Contains((char)Stx) || seqId.Contains((char)Etx))
        {
            throw new ArgumentException(
                "SeqID no puede contener TAB, STX ni ETX.",
                nameof(seqId));
        }

        byte[] messageBytes = Encoding.UTF8.GetBytes(
            $"{EventName}{FieldSeparator}{seqId}");
        int payloadLength = checked(messageBytes.Length + 2);
        byte[] frame = new byte[sizeof(int) + payloadLength];

        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, sizeof(int)), payloadLength);
        frame[sizeof(int)] = Stx;
        messageBytes.CopyTo(frame, sizeof(int) + 1);
        frame[^1] = Etx;

        return frame;
    }
}

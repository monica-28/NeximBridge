using System.Buffers.Binary;
using System.Text;

namespace NeximSimulator;

internal sealed class SimulatorFrameBuilder
{
    private const byte Stx = 0x02;
    private const byte Etx = 0x03;

    public byte[] Build(string eventName, string seqId, params string[] fields)
    {
        string content = string.Join('\t', new[] { eventName, seqId }.Concat(fields));
        byte[] contentBytes = Encoding.UTF8.GetBytes(content);
        int payloadLength = checked(contentBytes.Length + 2);
        byte[] frame = new byte[sizeof(int) + payloadLength];

        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, sizeof(int)), payloadLength);
        frame[sizeof(int)] = Stx;
        contentBytes.CopyTo(frame, sizeof(int) + 1);
        frame[^1] = Etx;

        return frame;
    }
}

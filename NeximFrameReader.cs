using System.Buffers.Binary;
using System.Text;

namespace NeximBridge;

public sealed class NeximFrameReader
{
    private const int HeaderSize = 4;
    private const byte Stx = 0x02;
    private const byte Etx = 0x03;

    public async Task<string> ReadMessageAsync(
        Stream networkStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(networkStream);

        byte[] header = new byte[HeaderSize];
        await ReadExactlyAsync(networkStream, header, "la cabecera de longitud", cancellationToken, allowCleanEof: true);

        int payloadLength = BinaryPrimitives.ReadInt32BigEndian(header);
        if (payloadLength < 2 || payloadLength > 16 * 1024 * 1024)
        {
            throw new NeximFrameFormatException(
                $"La longitud del payload debe estar entre 2 y 16777216 bytes (límite local), pero fue {payloadLength}.");
        }

        byte[] payload = new byte[payloadLength];
        await ReadExactlyAsync(networkStream, payload, "el payload", cancellationToken);

        if (payload[0] != Stx)
        {
            throw new NeximFrameFormatException(
                $"El payload debe comenzar con STX (0x{Stx:X2}), pero comienza con 0x{payload[0]:X2}.");
        }

        int lastByteIndex = payload.Length - 1;
        if (payload[lastByteIndex] != Etx)
        {
            throw new NeximFrameFormatException(
                $"El payload debe terminar con ETX (0x{Etx:X2}), pero termina con 0x{payload[lastByteIndex]:X2}.");
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(payload, 1, payload.Length - 2);
        }
        catch (DecoderFallbackException exception)
        {
            throw new NeximFrameFormatException("El payload no contiene texto UTF-8 válido.", exception);
        }
    }

    private static async Task ReadExactlyAsync(
        Stream networkStream,
        byte[] buffer,
        string partName,
        CancellationToken cancellationToken,
        bool allowCleanEof = false)
    {
        int offset = 0;

        while (offset < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int bytesRead = await networkStream.ReadAsync(
                buffer.AsMemory(offset, buffer.Length - offset),
                cancellationToken);

            if (bytesRead == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (allowCleanEof && offset == 0)
                    throw new NeximConnectionClosedException();
                throw new NeximFrameFormatException(
                    $"La conexión se cerró antes de completar {partName}: " +
                    $"se recibieron {offset} de {buffer.Length} bytes.");
            }

            offset += bytesRead;
        }
    }
}

public sealed class NeximFrameFormatException(string message, Exception? innerException = null)
    : FormatException(message, innerException);

public sealed class NeximConnectionClosedException()
    : IOException("Conexión con Nexim cerrada por el extremo remoto.");

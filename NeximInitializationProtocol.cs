using System.Buffers.Binary;
using System.Globalization;
using System.Text;
namespace NeximBridge;

// Initial process only: Fuji Host Interface 2.2.0, sections 4.1 and 4.8.
public static class NeximInitializationProtocol
{
    public static byte[] Frame(string content)
    {
        byte[] data = Encoding.UTF8.GetBytes(content);
        byte[] frame = new byte[checked(data.Length + 6)];
        BinaryPrimitives.WriteInt32BigEndian(frame, data.Length + 2);
        frame[4] = 2;
        data.CopyTo(frame, 5);
        frame[^1] = 3;
        return frame;
    }
    public static void ValidateField(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Any(char.IsControl)) throw new ArgumentException("Un campo no puede contener caracteres de control.");
    }
    public static void ValidateSequence(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Any(c => c is < '0' or > '9') ||
            !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id is < 1 or > 999999)
            throw new ArgumentException("SeqID debe estar entre 1 y 999999.");
    }
    public static void ValidateEvent(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => !(c is >= 'A' and <= 'Z' or >= '0' and <= '9')) ||
            value.EndsWith("ACK", StringComparison.Ordinal))
            throw new ArgumentException("EventName debe ser un nombre de evento, nunca un ACK.");
    }
    public static byte[] Acknowledgement(string command, string seqId, string machineName, bool accepted)
    {
        if (command is not ("SETEV" or "STARTEV")) throw new ArgumentException("Comando desconocido.");
        ValidateSequence(seqId);
        ValidateField(machineName);
        return Frame($"{command}_ACK\t{seqId}\t{machineName}\t{(accepted ? "0" : "1")}");
    }
    public static void ValidateAcknowledgement(string message, string command, string seqId, string machineName)
    {
        string[] fields = message.Split('\t');
        if (fields.Length != 4 || fields[0] != command + "_ACK" || fields[1] != seqId ||
            fields[2] != machineName || fields[3] != "0")
            throw new NeximInitializationException($"Respuesta inválida a {command}: se requieren EventName, SeqID y MachineName coincidentes y Result=0.");
    }
}
public sealed class NeximInitializationException(string message, Exception? innerException = null)
    : IOException(message, innerException);

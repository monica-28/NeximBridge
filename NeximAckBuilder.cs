using System.Globalization;

namespace NeximBridge;

public sealed class NeximAckBuilder
{
    private const byte Stx = 0x02;
    private const byte Etx = 0x03;
    private const char FieldSeparator = '\t';

    private static readonly HashSet<string> AcknowledgedEventNames = new(StringComparer.Ordinal)
    {
        "PARTSUSAGE",
        "PDERROR",
        "PRODSTARTED",
        "PRODCOMPLETEDII",
        "PRODSTOPPED",
        "MCALARMON",
        "MCALARMOFF",
    };

    public byte[] Build(ParsedNeximMessage message, int result = 0)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (result is not (0 or 1))
            throw new NeximAckException("Result debe ser 0 o 1.");

        if (!AcknowledgedEventNames.Contains(message.EventName))
        {
            throw new NeximAckException(
                $"El evento '{message.EventName}' no admite ACK en esta versión.");
        }

        if (string.IsNullOrWhiteSpace(message.SeqID))
        {
            throw new NeximAckException(
                $"El evento '{message.EventName}' no contiene SeqID.");
        }

        if (message.SeqID.Contains(FieldSeparator) ||
            message.SeqID.Contains((char)Stx) ||
            message.SeqID.Contains((char)Etx))
        {
            throw new NeximAckException("El SeqID no puede contener TAB, STX ni ETX.");
        }

        string resultText = result.ToString(CultureInfo.InvariantCulture);
        if (message.EventName == "PRODSTOPPED")
        {
            if (message.Fields.Count < 12)
                throw new NeximAckException("PRODSTOPPED no contiene la cabecera necesaria para confirmar el evento.");
            string[] fields = ["PRODSTOPPED_ACK", message.SeqID, resultText,
                message.Fields[4], message.Fields[5], message.Fields[6], message.Fields[7], message.Fields[8], message.Fields[9], message.Fields[10], message.Fields[11]];
            if (fields.Any(field => field.Any(char.IsControl)))
                throw new NeximAckException("Los campos de PRODSTOPPED_ACK no pueden contener caracteres de control.");
            return NeximInitializationProtocol.Frame(string.Join(FieldSeparator, fields));
        }
        if (message.EventName == "PRODCOMPLETEDII")
        {
            if (message.Fields.Count < 10)
                throw new NeximAckException("PRODCOMPLETEDII no contiene la cabecera necesaria para confirmar el evento.");
            string[] fields = ["PRODCOMPLETEDII_ACK", message.SeqID, resultText,
                message.Fields[4], message.Fields[5], message.Fields[6], message.Fields[7], message.Fields[8], message.Fields[9]];
            if (fields.Any(field => field.Any(char.IsControl)))
                throw new NeximAckException("Los campos de PRODCOMPLETEDII_ACK no pueden contener caracteres de control.");
            return NeximInitializationProtocol.Frame(string.Join(FieldSeparator, fields));
        }
        if (message.EventName == "PRODSTARTED")
        {
            if (message.Fields.Count < 10)
                throw new NeximAckException("PRODSTARTED no contiene la cabecera necesaria para confirmar el evento.");
            string[] fields = ["PRODSTARTED_ACK", message.SeqID, resultText,
                message.Fields[4], message.Fields[5], message.Fields[6], message.Fields[7], message.Fields[8], message.Fields[9]];
            if (fields.Any(field => field.Any(char.IsControl)))
                throw new NeximAckException("Los campos de PRODSTARTED_ACK no pueden contener caracteres de control.");
            return NeximInitializationProtocol.Frame(string.Join(FieldSeparator, fields));
        }
        if (message.EventName == "PDERROR")
        {
            // Fuji 4.52.2, p. 154: seven fields. There is NO PanelNo in this ACK.
            if (message.Fields.Count < 8)
                throw new NeximAckException("PDERROR no contiene la cabecera necesaria para confirmar el evento.");
            string[] fields = ["PDERROR_ACK", message.SeqID, resultText,
                message.Fields[4], message.Fields[5], message.Fields[6], message.Fields[7]];
            if (fields.Any(field => field.Any(char.IsControl)))
                throw new NeximAckException("Los campos de PDERROR_ACK no pueden contener caracteres de control.");
            return NeximInitializationProtocol.Frame(string.Join(FieldSeparator, fields));
        }
        if (message.EventName == "PARTSUSAGE")
        {
            // Fuji 4.32.2: echo the header, even when detail parsing failed.
            // Do not invent missing identity fields to construct a negative ACK.
            if (message.Fields.Count < 9)
                throw new NeximAckException("PARTSUSAGE no contiene la cabecera necesaria para confirmar el evento.");
            string[] fields = ["PARTSUSAGE_ACK", message.SeqID, resultText,
                message.Fields[4], message.Fields[5], message.Fields[6], message.Fields[7], message.Fields[8]];
            if (fields.Any(field => field.Any(char.IsControl)))
                throw new NeximAckException("Los campos de PARTSUSAGE_ACK no pueden contener caracteres de control.");
            return NeximInitializationProtocol.Frame(string.Join(FieldSeparator, fields));
        }

        if (message.EventName is "MCALARMON" or "MCALARMOFF")
        {
            if (message.Fields.Count < 6)
                throw new NeximAckException("La alarma no contiene los campos necesarios para su ACK.");
            string[] fields = [message.EventName + "_ACK", message.SeqID, resultText,
                message.Fields[4], message.Fields[5]];
            if (fields.Any(field => field.Any(char.IsControl)))
                throw new NeximAckException("Los campos del ACK no pueden contener caracteres de control.");
            return NeximInitializationProtocol.Frame(string.Join(FieldSeparator, fields));
        }
        throw new NeximAckException($"Sin ACK especifico para {message.EventName}.");
    }
}

public sealed class NeximAckException(string message)
    : InvalidOperationException(message);

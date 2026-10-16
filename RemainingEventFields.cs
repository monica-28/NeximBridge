using System.Globalization;

namespace NeximBridge;

// Validation primitives only; field positions belong to each specific parser.
internal static class RemainingEventFields
{
    public static int Number(string value, string field)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            throw new NeximMessageParseException($"{field}: entero no negativo inválido '{value}'.");
        return number;
    }

    public static void Sequence(string value)
    {
        if (Number(value, "SeqID") is < 1 or > 999999)
            throw new NeximMessageParseException("SeqID debe estar entre 1 y 999999.");
    }

    public static DateTimeOffset Time(string value)
    {
        if (!DateTimeOffset.TryParseExact(value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset time))
            throw new NeximMessageParseException("Time debe ser yyyyMMddHHmmss en UTC.");
        return time;
    }

    public static string Text(string value, string field, int maxLength = 100)
    {
        if (value.Any(char.IsControl) || value.Length > maxLength)
            throw new NeximMessageParseException($"{field}: caracteres de control o longitud superior a {maxLength}.");
        return value;
    }

    public static string HexCode(string value, string field)
    {
        if (value.Length != 8 || value.Any(c => !Uri.IsHexDigit(c)))
            throw new NeximMessageParseException($"{field} debe contener exactamente 8 caracteres hexadecimales.");
        return value;
    }
}

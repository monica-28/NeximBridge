namespace NeximBridge;

internal static class FujiDomain
{
    public static int Range(int value, int minimum, int maximum, string field)
    {
        if (value < minimum || value > maximum)
            throw new NeximMessageParseException($"{field} debe estar entre {minimum} y {maximum}.");
        return value;
    }

    public static string Hex(string value)
    {
        if (value.Any(c => !(c is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f')))
            throw new NeximMessageParseException("BSInfoBit debe contener caracteres hexadecimales ASCII.");
        return value;
    }
}

using System.Security.Cryptography;
using System.Text;

namespace NeximBridge;

public static class RawEventDeduplication
{
    public static bool Supports(string eventName) => eventName is
        "PARTSUSAGE" or "PDERROR" or "PRODSTARTED" or "PRODCOMPLETEDII" or "PRODSTOPPED" or
        "LOADCOMPIV" or "CHANGECOMPII" or "UNLOADCOMP" or "MCALARMON" or "MCALARMOFF";

    public static byte[]? ComputeHash(string eventName, string rawMessage)
    {
        ArgumentNullException.ThrowIfNull(rawMessage);
        return Supports(eventName) ? SHA256.HashData(Encoding.UTF8.GetBytes(rawMessage)) : null;
    }
}

public sealed record RawEventInsertResult(bool IsDuplicate, long RawEventId, string ParseStatus,
    string EventName, string? SeqId, string RawMessage);

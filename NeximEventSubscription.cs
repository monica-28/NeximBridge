namespace NeximBridge;

public sealed record NeximEventSubscription
{
    public string Name { get; init; } = "";
    public bool Ack { get; init; }
}

public static class NeximAckPolicy
{
    public static bool RequiresAck(IEnumerable<NeximEventSubscription> events, string eventName) =>
        events.Any(subscription => subscription.Name == eventName && subscription.Ack);
}

namespace NeximBridge;
public sealed class NeximStartEventBuilder
{
    public byte[] Build(string seqId, string machineName = "")
    {
        NeximInitializationProtocol.ValidateSequence(seqId);
        NeximInitializationProtocol.ValidateField(machineName);
        return NeximInitializationProtocol.Frame($"STARTEV\t{seqId}\t{machineName}");
    }
}

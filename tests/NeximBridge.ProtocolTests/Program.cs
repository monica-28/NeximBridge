using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NeximBridge;
using NeximSimulator;

// Dependency-free automated runner. Nonzero exit code on any failed test.
List<(string Name, Func<Task> Run)> tests = [];
void Test(string name, Func<Task> run) => tests.Add((name, run));
void Sync(string name, Action run) => Test(name, () => { run(); return Task.CompletedTask; });

Test("TCP: mixed subscription flags survive complete initialization", async () =>
{
    using Pair pair = await Pair.OpenAsync();
    NeximSessionInitializer initializer = NewInitializer();
    NeximEventSubscription[] events = [new() { Name = "PARTSUSAGE", Ack = true },
        new() { Name = "LOADCOMPIV", Ack = false }, new() { Name = "CHANGECOMPII", Ack = false },
        new() { Name = "UNLOADCOMP", Ack = false }, new() { Name = "MCALARMON", Ack = true },
        new() { Name = "MCALARMOFF", Ack = true }];
    SimulatorInitialization simulator = new();
    await Task.WhenAll(
        initializer.InitializeSubscriptionsAsync(pair.ClientStream, events, "NXT1", TimeSpan.FromSeconds(3), pair.Token),
        simulator.RunAsync(pair.ServerStream, TimeSpan.FromSeconds(3), pair.Token));
    Equal(NeximSessionState.Receiving, initializer.State);
    foreach (var subscription in events) Equal(subscription.Ack, simulator.Events[subscription.Name]);
});

Sync("SETEV: golden bytes, NumList and final CR", () =>
{
    byte[] actual = new NeximSetEventBuilder().Build("1", ["PARTSUSAGE", "PDERROR"]);
    EqualBytes(Wire("SETEV\t1\t\t2\rPARTSUSAGE\t1\rPDERROR\t1\r"), actual);
    Equal(actual.Length - 4, BinaryPrimitives.ReadInt32BigEndian(actual));
});
Sync("SETEV: named machine and UTF-8 byte length", () =>
    EqualBytes(Wire("SETEV\t999999\tMáquina\t1\rMCALARMON\t1\r"),
        new NeximSetEventBuilder().Build("999999", ["MCALARMON"], "Máquina")));
Sync("STARTEV: empty and named MachineName", () =>
{
    EqualBytes(Wire("STARTEV\t2\t"), new NeximStartEventBuilder().Build("2"));
    EqualBytes(Wire("STARTEV\t2\tNXT"), new NeximStartEventBuilder().Build("2", "NXT"));
});
foreach (string command in new[] { "SETEV", "STARTEV" })
foreach (bool ok in new[] { true, false })
    Sync($"{command}_ACK: all four official fields, Result={(ok ? 0 : 1)}", () =>
        EqualBytes(Wire($"{command}_ACK\t1\t\t{(ok ? 0 : 1)}"),
            NeximInitializationProtocol.Acknowledgement(command, "1", "", ok)));
foreach (string seq in new[] { "0", "1000000", "-1", "abc", "1\r", "" })
    Sync($"Reject invalid SeqID [{seq.Trim()}]", () =>
        Throws<ArgumentException>(() => new NeximStartEventBuilder().Build(seq)));
Sync("Reject invalid subscriptions and injected fields", () =>
{
    foreach (string[] events in new string[][] { [], ["SETEV_ACK"], ["PARTSUSAGE\r"], ["PDERROR", "PDERROR"] })
        Throws<ArgumentException>(() => new NeximSetEventBuilder().Build("1", events));
    Throws<ArgumentException>(() => new NeximStartEventBuilder().Build("1", "A\rB"));
});
Test("Framing: every byte fragmented, including multibyte UTF-8", async () =>
{
    using FragmentedStream stream = new(Wire("SETEV_ACK\t1\tMáquina\t0"));
    Equal("SETEV_ACK\t1\tMáquina\t0", await new NeximFrameReader().ReadMessageAsync(stream));
});
Test("Framing: coalesced frames do not consume next message", async () =>
{
    using MemoryStream stream = new([.. Wire("STARTEV_ACK\t2\t\t0"), .. Wire("KEEPALIVE\t3")]);
    NeximFrameReader reader = new();
    Equal("STARTEV_ACK\t2\t\t0", await reader.ReadMessageAsync(stream));
    Equal("KEEPALIVE\t3", await reader.ReadMessageAsync(stream));
});
foreach (int length in new[] { -1, 0, 1, int.MaxValue })
    Test($"Framing: reject length {length}", async () =>
    {
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, length);
        await ThrowsAsync<NeximFrameFormatException>(() => new NeximFrameReader().ReadMessageAsync(new MemoryStream(header)));
    });
foreach (string mutation in new[] { "STX", "ETX", "UTF8", "partial header", "partial body" })
    Test($"Framing: reject {mutation}", async () =>
    {
        byte[] frame = Wire("test");
        if (mutation == "STX") frame[4] = 0;
        if (mutation == "ETX") frame[^1] = 0;
        if (mutation == "UTF8") frame[5] = 0xff;
        if (mutation == "partial header") frame = frame[..2];
        if (mutation == "partial body") frame = frame[..^1];
        await ThrowsAsync<NeximFrameFormatException>(() => new NeximFrameReader().ReadMessageAsync(new MemoryStream(frame)));
    });
Test("TCP: official exchange in order, no STARTEV before SETEV_ACK; first event preserved", async () =>
{
    using Pair pair = await Pair.OpenAsync();
    NeximSessionInitializer initializer = NewInitializer();
    Task client = initializer.InitializeAsync(pair.ClientStream, ["PDERROR"], "NXT", TimeSpan.FromSeconds(3), pair.Token);
    Equal("SETEV\t1\tNXT\t1\rPDERROR\t1\r", await Read(pair.ServerStream, pair.Token));
    Equal(NeximSessionState.WaitingSetEventAck, initializer.State);
    await Task.Delay(50, pair.Token);
    Equal(0, pair.Server.Available);
    await pair.ServerStream.WriteAsync(Wire("SETEV_ACK\t1\tNXT\t0"), pair.Token);
    Equal("STARTEV\t2\tNXT", await Read(pair.ServerStream, pair.Token));
    Equal(NeximSessionState.WaitingStartEventAck, initializer.State);
    await pair.ServerStream.WriteAsync(Wire("STARTEV_ACK\t2\tNXT\t0").Concat(Wire("KEEPALIVE\t3")).ToArray(), pair.Token);
    await client;
    Equal(NeximSessionState.Receiving, initializer.State);
    Equal("KEEPALIVE\t3", await Read(pair.ClientStream, pair.Token));
});
foreach (string stage in new[] { "SETEV", "STARTEV" })
foreach (string fault in new[] { "Result=1", "SeqID", "MachineName", "missing MachineName", "wrong command", "disconnect", "timeout", "partial timeout", "cancel" })
    Test($"TCP: {stage} / {fault} closes session, never Receiving", async () =>
    {
        using Pair pair = await Pair.OpenAsync();
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(pair.Token);
        NeximSessionInitializer initializer = NewInitializer();
        Task task = initializer.InitializeAsync(pair.ClientStream, ["PDERROR"], "", TimeSpan.FromMilliseconds(300), stop.Token);
        Equal("SETEV\t1\t\t1\rPDERROR\t1\r", await Read(pair.ServerStream, pair.Token));
        string seq = "1";
        if (stage == "STARTEV")
        {
            await pair.ServerStream.WriteAsync(Wire("SETEV_ACK\t1\t\t0"), pair.Token);
            Equal("STARTEV\t2\t", await Read(pair.ServerStream, pair.Token));
            seq = "2";
        }
        string? response = fault switch
        {
            "Result=1" => $"{stage}_ACK\t{seq}\t\t1",
            "SeqID" => $"{stage}_ACK\t999\t\t0",
            "MachineName" => $"{stage}_ACK\t{seq}\tOTHER\t0",
            "missing MachineName" => $"{stage}_ACK\t{seq}\t0",
            "wrong command" => $"OTHER_ACK\t{seq}\t\t0",
            _ => null
        };
        if (response is not null) await pair.ServerStream.WriteAsync(Wire(response), pair.Token);
        if (fault == "partial timeout") await pair.ServerStream.WriteAsync(Wire($"{stage}_ACK\t{seq}\t\t0")[..6], pair.Token);
        if (fault == "disconnect") pair.Server.Dispose();
        if (fault == "cancel") stop.Cancel();
        Exception failure = await Failure(task);
        if (fault == "cancel") Check(failure is OperationCanceledException, "Cancellation must propagate");
        else if (fault == "disconnect") Check(failure is NeximFrameFormatException or IOException, "Expected disconnect failure");
        else Check(failure is NeximInitializationException, "Expected initialization failure");
        if (fault.Contains("timeout")) Check(failure.InnerException is OperationCanceledException, "Expected deadline cancellation");
        Equal(NeximSessionState.Failed, initializer.State);
        Check(!pair.ClientStream.CanRead, "Failed stream must be closed");
        if (fault != "disconnect")
        {
            byte[] tail = new byte[100];
            Equal(0, await pair.ServerStream.ReadAsync(tail, pair.Token));
        }
    });
Test("TCP: real simulator completes initialization and retains subscriptions", async () =>
{
    using Pair pair = await Pair.OpenAsync();
    SimulatorInitialization simulator = new();
    NeximSessionInitializer client = NewInitializer();
    Task server = simulator.RunAsync(pair.ServerStream, TimeSpan.FromSeconds(3), pair.Token);
    Check(!simulator.IsReceiving, "Simulator started early");
    await client.InitializeAsync(pair.ClientStream, ["PDERROR", "PARTSUSAGE"], "", TimeSpan.FromSeconds(3), pair.Token);
    await server;
    Check(simulator.IsReceiving, "Simulator did not start");
    Equal(2, simulator.Events.Count);
    Check(simulator.Events["PDERROR"], "Subscription ACK not retained");
});
Test("Simulator: gate stays closed after SETEV until STARTEV; ACK=0 accepted", async () =>
{
    using Pair pair = await Pair.OpenAsync();
    SimulatorInitialization simulator = new();
    Task server = simulator.RunAsync(pair.ServerStream, TimeSpan.FromSeconds(3), pair.Token);
    await pair.ClientStream.WriteAsync(Wire("SETEV\t10\tNXT\t1\rPDERROR\t0\r"), pair.Token);
    Equal("SETEV_ACK\t10\tNXT\t0", await Read(pair.ClientStream, pair.Token));
    Check(!simulator.IsReceiving && !server.IsCompleted, "Events enabled too early");
    Equal(0, pair.Client.Available);
    await pair.ClientStream.WriteAsync(Wire("STARTEV\t11\tNXT"), pair.Token);
    Equal("STARTEV_ACK\t11\tNXT\t0", await Read(pair.ClientStream, pair.Token));
    await server;
    Check(simulator.IsReceiving && !simulator.Events["PDERROR"], "Invalid subscription state");
});
foreach (string request in new[] {
    "SETEV\t1\t\t2\rPDERROR\t1\r", "SETEV\t1\t\t1\rPDERROR\t1",
    "SETEV\t1\t\t1\rPDERROR\t2\r", "SETEV\t1\t\t1\rPDERROR_ACK\t1\r",
    "SETEV\t1\t\t1\rUNKNOWN\t1\r", "SETEV\t1\t\t2\rPDERROR\t1\rPDERROR\t1\r",
    "SETEV\t1\t\t0\r", "STARTEV\t1\t" })
    Test($"Simulator: rejects malformed/out-of-order request {request.Replace('\r', '|')}", async () =>
    {
        using Pair pair = await Pair.OpenAsync();
        SimulatorInitialization simulator = new();
        Task task = simulator.RunAsync(pair.ServerStream, TimeSpan.FromSeconds(3), pair.Token);
        await pair.ClientStream.WriteAsync(Wire(request), pair.Token);
        Equal($"{request.Split('\t')[0]}_ACK\t1\t\t1", await Read(pair.ClientStream, pair.Token));
        await ThrowsAsync<NeximInitializationException>(() => task);
        Check(!simulator.IsReceiving, "Rejected session enabled events");
    });
Test("TCP: fresh connection repeats both exchanges using fresh sequence IDs", async () =>
{
    NeximSessionInitializer initializer = NewInitializer();
    for (int attempt = 0; attempt < 2; attempt++)
    {
        using Pair pair = await Pair.OpenAsync();
        Task task = initializer.InitializeAsync(pair.ClientStream, ["PDERROR"], "", TimeSpan.FromSeconds(3), pair.Token);
        int seq = attempt * 2 + 1;
        Equal($"SETEV\t{seq}\t\t1\rPDERROR\t1\r", await Read(pair.ServerStream, pair.Token));
        await pair.ServerStream.WriteAsync(Wire($"SETEV_ACK\t{seq}\t\t0"), pair.Token);
        Equal($"STARTEV\t{seq + 1}\t", await Read(pair.ServerStream, pair.Token));
        await pair.ServerStream.WriteAsync(Wire($"STARTEV_ACK\t{seq + 1}\t\t0"), pair.Token);
        await task;
    }
});
foreach (string start in new[] { "STARTEV\t2\tOTHER", "STARTEV\t1\t", "STARTEV\t2\t\textra", "SETEV\t2\t\t1\rPDERROR\t1\r" })
    Test("Simulator: rejects invalid STARTEV with official Result=1", async () =>
    {
        using Pair pair = await Pair.OpenAsync();
        SimulatorInitialization simulator = new();
        Task task = simulator.RunAsync(pair.ServerStream, TimeSpan.FromSeconds(3), pair.Token);
        await pair.ClientStream.WriteAsync(Wire("SETEV\t1\t\t1\rPDERROR\t1\r"), pair.Token);
        Equal("SETEV_ACK\t1\t\t0", await Read(pair.ClientStream, pair.Token));
        await pair.ClientStream.WriteAsync(Wire(start), pair.Token);
        string[] fields = start.Split('\t');
        Equal($"{fields[0]}_ACK\t{fields[1]}\t{fields[2]}\t1", await Read(pair.ClientStream, pair.Token));
        await ThrowsAsync<NeximInitializationException>(() => task);
        Check(!simulator.IsReceiving, "Invalid STARTEV enabled reception");
    });
foreach (bool afterSet in new[] { false, true })
foreach (bool disconnect in new[] { false, true })
    Test($"Simulator: {(afterSet ? "STARTEV" : "SETEV")} {(disconnect ? "disconnect" : "timeout")}", async () =>
    {
        using Pair pair = await Pair.OpenAsync();
        SimulatorInitialization simulator = new();
        Task task = simulator.RunAsync(pair.ServerStream, TimeSpan.FromMilliseconds(300), pair.Token);
        if (afterSet)
        {
            await pair.ClientStream.WriteAsync(Wire("SETEV\t1\t\t1\rPDERROR\t1\r"), pair.Token);
            Equal("SETEV_ACK\t1\t\t0", await Read(pair.ClientStream, pair.Token));
        }
        if (disconnect) pair.Client.Dispose();
        Exception failure = await Failure(task);
        Check(disconnect ? failure is NeximFrameFormatException or IOException : failure is OperationCanceledException,
            "Expected disconnect or timeout");
        Check(!simulator.IsReceiving && !pair.ServerStream.CanRead, "Failed simulator session remains open");
    });
int failed = 0;
foreach (var test in tests)
{
    try { await test.Run().WaitAsync(TimeSpan.FromSeconds(10)); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception exception) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {exception}"); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

static NeximSessionInitializer NewInitializer() => new(new(), new(), new());
static Task<string> Read(Stream stream, CancellationToken token) => new NeximFrameReader().ReadMessageAsync(stream, token);
// Independent fixture framing, never uses the production frame builder.
static byte[] Wire(string text)
{
    byte[] payload = Encoding.UTF8.GetBytes("\x02" + text + "\x03");
    byte[] header = BitConverter.GetBytes(payload.Length);
    if (BitConverter.IsLittleEndian) Array.Reverse(header);
    return [.. header, .. payload];
}
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected [{expected}], actual [{actual}]");
static void EqualBytes(byte[] expected, byte[] actual) => Check(expected.SequenceEqual(actual), "Wire bytes differ");
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
static async Task<Exception> Failure(Task task)
{
    try { await task; } catch (Exception exception) { return exception; }
    throw new Exception("Expected failure");
}
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    Exception failure = await Failure(action());
    Check(failure is T, $"Expected {typeof(T).Name}, got {failure.GetType().Name}");
}
sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
}
sealed class Pair : IDisposable
{
    public TcpClient Client { get; } = new();
    public TcpClient Server { get; private set; } = null!;
    private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
    public CancellationToken Token => deadline.Token;
    public NetworkStream ClientStream { get; private set; } = null!;
    public NetworkStream ServerStream { get; private set; } = null!;
    public static async Task<Pair> OpenAsync()
    {
        Pair pair = new();
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        await pair.Client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, pair.Token);
        pair.Server = await listener.AcceptTcpClientAsync(pair.Token);
        pair.ClientStream = pair.Client.GetStream();
        pair.ServerStream = pair.Server.GetStream();
        return pair;
    }
    public void Dispose() { Client.Dispose(); Server.Dispose(); deadline.Dispose(); }
}

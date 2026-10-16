using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using NeximBridge;

internal static class ConnectionTests
{
    internal const string Parts = "PARTSUSAGE\t2001\t20261016103000\tLINE1\tNXT1\t1\t1\tDEDUP_TEST\tDEDUP001\t1\r" +
        "1\t5\tRES-10K\tRES-10K-AVL\t100\t102\t2\t1\t1\t0\t0\t0\r";

    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("Frame EOF: empty stream is typed clean close", async () =>
        {
            var error = await Failure(new NeximFrameReader().ReadMessageAsync(new MemoryStream()));
            Check(error is NeximConnectionClosedException, "Empty stream treated as malformed");
        });
        foreach (int count in new[] { 1, 2, 3 })
            yield return ($"Frame EOF: header {count}/4 is truncated", async () =>
            {
                var error = await Failure(new NeximFrameReader().ReadMessageAsync(new MemoryStream(new byte[count])));
                Check(error is NeximFrameFormatException && error.Message.Contains($"{count} de 4"), "Missing header byte counts");
            });
        foreach (int count in new[] { 0, 1, 2 })
            yield return ($"Frame EOF: complete header, body {count}/3 is truncated", async () =>
            {
                byte[] frame = NeximInitializationProtocol.Frame("X");
                var error = await Failure(new NeximFrameReader().ReadMessageAsync(new MemoryStream(frame[..(4 + count)])));
                Check(error is NeximFrameFormatException && error.Message.Contains($"{count} de 3"), "Body EOF classified as clean");
            });
        yield return ("Frame EOF: complete frame unchanged, following EOF is clean", async () =>
        {
            using MemoryStream stream = new(NeximInitializationProtocol.Frame("PARTSUSAGE\t1\tMáquina"));
            Equal("PARTSUSAGE\t1\tMáquina", await new NeximFrameReader().ReadMessageAsync(stream));
            Check(await Failure(new NeximFrameReader().ReadMessageAsync(stream)) is NeximConnectionClosedException, "EOF after frame");
        });
        foreach (int bytes in new[] { 0, 2, 5 })
            yield return ($"Frame cancellation during read with {bytes} bytes is never framing/EOF", async () =>
            {
                using TcpListener listener = Listen(); using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
                using TcpClient client = new(); await client.ConnectAsync(IPAddress.Loopback, Port(listener), deadline.Token);
                using TcpClient server = await listener.AcceptTcpClientAsync(deadline.Token);
                if (bytes > 0) await server.GetStream().WriteAsync(NeximInitializationProtocol.Frame("X")[..bytes], deadline.Token);
                using CancellationTokenSource cancel = new();
                Task read = new NeximFrameReader().ReadMessageAsync(client.GetStream(), cancel.Token);
                cancel.Cancel();
                Check(await Failure(read.WaitAsync(deadline.Token)) is OperationCanceledException, "Cancellation converted to error");
            });
        yield return ("TCP backoff is bounded: 1,2,5,10,30,30; reset starts at 1", () =>
        {
            Equal("1,2,5,10,30,30,30,30", string.Join(',', Enumerable.Range(0, 8).Select(n => NeximTcpClientService.RetryDelay(n).TotalSeconds)));
            Equal(TimeSpan.FromSeconds(1), NeximTcpClientService.RetryDelay(0));
            return Task.CompletedTask;
        });
        yield return ("TCP: concurrent ConnectAsync callers publish only one session; stale Disconnect cannot close replacement", async () =>
        {
            using TcpListener listener = Listen(); using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            Capture<NeximTcpClientService> log = new(); using NeximTcpClientService service = new(Config(Port(listener)), log);
            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => service.ConnectAsync(deadline.Token)));
            using TcpClient first = await listener.AcceptTcpClientAsync(deadline.Token);
            Check(!listener.Pending(), "Multiple sessions connected"); Equal(1, log.Count("Conectado a Nexim"));
            NetworkStream oldStream = service.NetworkStream!;
            service.Disconnect(oldStream); Check(!oldStream.CanRead, "Old stream not disposed");
            await service.ConnectAsync(deadline.Token); using TcpClient second = await listener.AcceptTcpClientAsync(deadline.Token);
            NetworkStream replacement = service.NetworkStream!;
            service.Disconnect(oldStream); Check(ReferenceEquals(replacement, service.NetworkStream), "Stale disconnect retired replacement");
            await service.StopAsync(deadline.Token);
            Check(!replacement.CanRead && service.NetworkStream is null, "Shutdown left stream open");
            Check(await Failure(service.ConnectAsync()) is OperationCanceledException, "Connect after shutdown allowed");
            Check(!listener.Pending(), "Connected after shutdown");
        });
        yield return ("TCP refused: concise warnings, increasing delays, cancellable retry and no stack trace", async () =>
        {
            using TcpListener reserve = Listen(); int port = Port(reserve); reserve.Stop();
            Capture<NeximTcpClientService> log = new(); using NeximTcpClientService service = new(Config(port), log);
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(8));
            await service.StartAsync(default);
            try
            {
                await Until(() => log.Items.Any(e => e.Text.Contains("Nuevo intento en 2 s.")), deadline.Token);
                Check(log.Items.Any(e => e.Text.Contains("Nuevo intento en 1 s.")), "First retry not one second");
                Check(log.Items.All(e => e.Level != LogLevel.Error && e.Error is null), "Expected refusal logged stack/error");
            }
            finally { await service.StopAsync(deadline.Token); }
            Check(service.ExecuteTask!.IsCompletedSuccessfully, "Retry task did not stop cleanly");
            Equal(2, log.Items.Count(e => e.Level == LogLevel.Warning));
        });
        yield return ("TCP shutdown racing queued ConnectAsync calls retires the client and prevents later connects", async () =>
        {
            using TcpListener listener = Listen(); using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
            using NeximTcpClientService service = new(Config(Port(listener)), new Capture<NeximTcpClientService>());
            Task[] attempts = Enumerable.Range(0, 20).Select(_ => service.ConnectAsync(deadline.Token)).ToArray();
            await service.StopAsync(deadline.Token);
            foreach (Task attempt in attempts)
            {
                try { await attempt; }
                catch (OperationCanceledException) { }
            }
            Check(service.NetworkStream is null && !service.IsConnected, "Published session after shutdown");
            Check(await Failure(service.ConnectAsync()) is OperationCanceledException, "Connect after shutdown accepted");
            int accepted = 0;
            while (listener.Pending())
            {
                using TcpClient peer = await listener.AcceptTcpClientAsync(deadline.Token);
                accepted++;
                try { Equal(0, await peer.GetStream().ReadAsync(new byte[1], deadline.Token)); }
                catch (IOException error) when (error.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset }) { }
            }
            Check(accepted <= 1, "More than one in-flight session");
        });
        yield return ("TCP unexpected configuration failure keeps exception/stack", async () =>
        {
            Capture<NeximTcpClientService> log = new(); using NeximTcpClientService service = new(Config(0), log);
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
            await service.StartAsync(default);
            try { await Until(() => log.Items.Any(e => e.Level == LogLevel.Error && e.Error is InvalidOperationException), deadline.Token); }
            finally { await service.StopAsync(deadline.Token); }
        });
        foreach (int bytes in new[] { 0, 2, 4, 5 })
            yield return ($"Worker EOF after {bytes} frame bytes: correct log severity and session disposal", async () =>
            {
                using TcpListener listener = Listen(); var config = Config(Port(listener));
                Capture<Worker> log = new(); using NeximTcpClientService service = new(config, new Capture<NeximTcpClientService>());
                using Worker worker = CreateWorker(service, config, log);
                using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
                await service.StartAsync(default); await worker.StartAsync(default);
                try
                {
                    using TcpClient peer = await listener.AcceptTcpClientAsync(deadline.Token);
                    await Handshake(peer.GetStream(), deadline.Token);
                    NetworkStream old = service.NetworkStream!;
                    if (bytes > 0) await peer.GetStream().WriteAsync(NeximInitializationProtocol.Frame("X")[..bytes], deadline.Token);
                    peer.Dispose();
                    await Until(() => !old.CanRead, deadline.Token);
                    if (bytes == 0)
                    {
                        Equal(1, log.Count("Conexión con Nexim cerrada por el extremo remoto."));
                        Check(log.Items.All(e => e.Level != LogLevel.Error && e.Error is null), "Clean EOF logged as error/stack");
                    }
                    else
                    {
                        Equal(0, log.Count("Conexión con Nexim cerrada por el extremo remoto."));
                        Equal(1, log.Items.Count(e => e.Level == LogLevel.Error && e.Error is NeximFrameFormatException));
                    }
                }
                finally { await Stop(worker, service); }
                Check(worker.ExecuteTask!.IsCompletedSuccessfully, "Worker task not completed");
            });
        yield return ("Worker reconnects after listener restart, repeats SETEV/STARTEV and receives valid event", async () =>
        {
            using TcpListener listener = Listen(); var config = Config(Port(listener));
            Capture<Worker> workerLog = new(); Capture<NeximTcpClientService> tcpLog = new();
            using NeximTcpClientService service = new(config, tcpLog); using Worker worker = CreateWorker(service, config, workerLog);
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
            await service.StartAsync(default); await worker.StartAsync(default);
            try
            {
                using TcpClient first = await listener.AcceptTcpClientAsync(deadline.Token);
                string firstSeq = await Handshake(first.GetStream(), deadline.Token);
                NetworkStream old = service.NetworkStream!;
                listener.Stop(); first.Dispose();
                await Until(() => tcpLog.Items.Any(e => e.Text.Contains("Nuevo intento en 2 s.")), deadline.Token);
                Check(!old.CanRead, "Old read stream still open");
                // A listener originally bound to port 0 gets a different port if restarted.
                using TcpListener restarted = new(IPAddress.Loopback, config.GetValue<int>("Nexim:Port"));
                restarted.Start();
                using TcpClient second = await restarted.AcceptTcpClientAsync(deadline.Token);
                string secondSeq = await Handshake(second.GetStream(), deadline.Token);
                Check(firstSeq != secondSeq, "Previous handshake reused");
                Check(!ReferenceEquals(old, service.NetworkStream), "Old stream reused");
                await second.GetStream().WriteAsync(NeximInitializationProtocol.Frame(Parts), deadline.Token);
                Equal("PARTSUSAGE_ACK\t2001\t0\tNXT1\t1\t1\tDEDUP_TEST\tDEDUP001",
                    await new NeximFrameReader().ReadMessageAsync(second.GetStream(), deadline.Token));
                Check(!restarted.Pending(), "Concurrent extra session");
                second.Dispose();
                await Until(() => tcpLog.Count("Reintentando conexión en 1 s.") == 2, deadline.Token);
                Equal(2, workerLog.Count("SETEV y STARTEV confirmados."));
                Equal(2, workerLog.Count("Conexión con Nexim cerrada por el extremo remoto."));
                Check(workerLog.Items.All(e => e.Error is null), "EOF logged with exception");
            }
            finally { await Stop(worker, service); }
        });
        foreach (string phase in new[] { "idle", "SETEV", "STARTEV", "receive", "partial header", "partial body", "service first" })
            yield return ($"Shutdown {phase}: pending reads finish, stream closed, no reconnect or error logs", async () =>
            {
                using TcpListener listener = Listen(); var config = Config(Port(listener));
                Capture<Worker> log = new(); Capture<NeximTcpClientService> tcpLog = new();
                using NeximTcpClientService service = new(config, tcpLog); using Worker worker = CreateWorker(service, config, log);
                using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
                await worker.StartAsync(default);
                try
                {
                    if (phase != "idle")
                    {
                        await service.StartAsync(default);
                        using TcpClient peer = await listener.AcceptTcpClientAsync(deadline.Token);
                        if (phase is "SETEV" or "STARTEV")
                        {
                            string set = await new NeximFrameReader().ReadMessageAsync(peer.GetStream(), deadline.Token);
                            if (phase == "STARTEV")
                            {
                                await peer.GetStream().WriteAsync(NeximInitializationProtocol.Acknowledgement("SETEV", set.Split('\t')[1], "", true), deadline.Token);
                                await new NeximFrameReader().ReadMessageAsync(peer.GetStream(), deadline.Token);
                            }
                        }
                        else
                        {
                            await Handshake(peer.GetStream(), deadline.Token);
                            await Until(() => log.Count("SETEV y STARTEV confirmados.") == 1, deadline.Token);
                        }
                        if (phase.StartsWith("partial", StringComparison.Ordinal))
                            await peer.GetStream().WriteAsync(NeximInitializationProtocol.Frame("X")[..(phase == "partial header" ? 2 : 5)], deadline.Token);
                        if (phase == "service first") await service.StopAsync(deadline.Token);
                        await worker.StopAsync(deadline.Token);
                        try { Check(await peer.GetStream().ReadAsync(new byte[1], deadline.Token) == 0, "Client socket remains open"); }
                        catch (IOException error) when (error.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
                        { /* Closing a socket with outstanding/unread data may send RST on Windows. */ }
                    }
                }
                finally { await Stop(worker, service); }
                Check(service.NetworkStream is null, "Stream not cleared");
                Check(worker.ExecuteTask!.IsCompletedSuccessfully, "Worker left running/faulted");
                Check(service.ExecuteTask is null || service.ExecuteTask.IsCompletedSuccessfully, "TCP task left running/faulted");
                Check(log.Items.All(e => e.Error is null && e.Level != LogLevel.Error), "Worker shutdown logged error");
                Check(tcpLog.Items.All(e => e.Error is null && e.Level != LogLevel.Error), "TCP shutdown logged error");
                Check(!listener.Pending(), "Reconnected during shutdown");
            });
    }

    private static async Task Stop(Worker worker, NeximTcpClientService service)
    {
        using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
        await worker.StopAsync(cleanup.Token);
        await service.StopAsync(cleanup.Token);
    }

    internal static Worker CreateWorker(NeximTcpClientService tcp, IConfiguration config, ILogger<Worker> log,
        NeximEventProcessor? processor = null, PartsUsageHandler? parts = null, LoadCompIVHandler? load = null)
    {
        NeximRepository repository = new(config);
        processor ??= new(new ProcessedRepository(), NullLogger<NeximEventProcessor>.Instance);
        RemainingEventProcessor remaining = new(processor,
            load ?? new(repository, NullLogger<LoadCompIVHandler>.Instance),
            new(repository, NullLogger<ChangeCompIIHandler>.Instance), new(repository, NullLogger<UnloadCompHandler>.Instance),
            new(repository, NullLogger<McAlarmOnHandler>.Instance), new(repository, NullLogger<McAlarmOffHandler>.Instance),
            new(new(), NullLogger<McAlarmOnAckSender>.Instance), new(new(), NullLogger<McAlarmOffAckSender>.Instance));
        return new(log, tcp, new(), new(new(), new(), new()), config, new(), repository, processor,
            parts ?? new(repository, NullLogger<PartsUsageHandler>.Instance), new(new(), NullLogger<PartsUsageAckSender>.Instance),
            new(repository, NullLogger<PdErrorHandler>.Instance), new(new(), NullLogger<PdErrorAckSender>.Instance),
            new(repository, NullLogger<ProdStoppedHandler>.Instance), new(new(), NullLogger<ProdStoppedAckSender>.Instance),
            new(repository, NullLogger<ProdCompletedIIHandler>.Instance), new(new(), NullLogger<ProdCompletedIIAckSender>.Instance),
            new(repository, NullLogger<ProdStartedHandler>.Instance), new(new(), NullLogger<ProdStartedAckSender>.Instance), remaining, new());
    }
    internal static IConfiguration Config(int port) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Nexim:Host"] = "127.0.0.1", ["Nexim:Port"] = port.ToString(),
        ["Nexim:Events:0:Name"] = "PARTSUSAGE", ["Nexim:Events:0:Ack"] = "true",
        ["Nexim:Events:1:Name"] = "LOADCOMPIV", ["Nexim:Events:1:Ack"] = "false"
    }).Build();
    internal static TcpListener Listen() { TcpListener listener = new(IPAddress.Loopback, 0); listener.Start(); return listener; }
    internal static int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;
    internal static async Task<string> Handshake(NetworkStream stream, CancellationToken token)
    {
        NeximFrameReader reader = new(); string set = await reader.ReadMessageAsync(stream, token);
        string sequence = set.Split('\t')[1];
        Equal($"SETEV\t{sequence}\t\t2\rPARTSUSAGE\t1\rLOADCOMPIV\t0\r", set);
        await stream.WriteAsync(NeximInitializationProtocol.Acknowledgement("SETEV", sequence, "", true), token);
        string start = await reader.ReadMessageAsync(stream, token); string startSequence = start.Split('\t')[1];
        Equal($"STARTEV\t{startSequence}\t", start); Check(startSequence != sequence, "SeqID reused");
        await stream.WriteAsync(NeximInitializationProtocol.Acknowledgement("STARTEV", startSequence, "", true), token);
        return sequence;
    }
    internal static async Task Until(Func<bool> condition, CancellationToken token)
    { while (!condition()) await Task.Delay(10, token); }
    internal sealed class Capture<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, string Text, Exception? Error)> Items = new();
        public int Count(string prefix) => Items.Count(e => e.Text.StartsWith(prefix, StringComparison.Ordinal));
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
            => Items.Enqueue((level, format(state, error), error));
    }
    private sealed class ProcessedRepository : IRawEventRepository
    {
        public Task<RawEventInsertResult> InsertRawEventOrGetExistingAsync(string name, string? seq, string raw,
            DateTimeOffset at, CancellationToken cancellationToken = default) => Task.FromResult(new RawEventInsertResult(true, 1, "PROCESSED", name, seq, raw));
        public Task<long> InsertRawEvent(string name, string? seq, string raw, DateTimeOffset at, string status, CancellationToken cancellationToken = default)
            => throw new Exception("Unexpected legacy RAW insert");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual) => Check(Equals(expected, actual), $"Expected {expected}; actual {actual}");
    private static async Task<Exception> Failure(Task task)
    { try { await task; } catch (Exception error) { return error; } throw new Exception("Expected failure"); }
}

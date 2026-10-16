using System.Net.Sockets;

namespace NeximBridge;

public sealed class NeximTcpClientService(IConfiguration configuration, ILogger<NeximTcpClientService> logger)
    : BackgroundService
{
    private readonly object syncRoot = new();
    private readonly SemaphoreSlim connectGate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private TcpClient? tcpClient;
    private NetworkStream? networkStream;
    private TaskCompletionSource? disconnected;

    // Connected is only a published session indicator, not a socket liveness probe.
    // The single receiving Worker observes EOF/errors and retires that exact stream.
    public bool IsConnected { get { lock (syncRoot) return networkStream is not null; } }
    public NetworkStream? NetworkStream { get { lock (syncRoot) return networkStream; } }
    public bool IsStopping => shutdown.IsCancellationRequested;

    internal static TimeSpan RetryDelay(int attempt) => TimeSpan.FromSeconds(attempt switch
    {
        <= 0 => 1,
        1 => 2,
        2 => 5,
        3 => 10,
        _ => 30
    });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, shutdown.Token);
        CancellationToken token = lifetime.Token;
        int attempt = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                bool unavailable = false;
                NetworkStream? sessionStream = null;
                try
                {
                    await ConnectAsync(token);
                    attempt = 0;
                    Task closed;
                    lock (syncRoot)
                    {
                        sessionStream = networkStream;
                        closed = disconnected?.Task ?? Task.CompletedTask;
                    }
                    // No Poll/read task competes with Worker for the socket or EOF.
                    await closed.WaitAsync(token);
                }
                catch (Exception) when (token.IsCancellationRequested) { break; }
                catch (SocketException error) when (error.SocketErrorCode is
                    SocketError.ConnectionRefused or SocketError.TimedOut or SocketError.HostNotFound or
                    SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.NetworkDown or
                    SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.TryAgain)
                {
                    unavailable = true;
                }
                catch (Exception error)
                {
                    logger.LogError(error, "Error inesperado en la conexión TCP con Nexim.");
                }
                finally { if (sessionStream is not null) Disconnect(sessionStream); }

                token.ThrowIfCancellationRequested();
                TimeSpan delay = RetryDelay(attempt);
                attempt = Math.Min(attempt + 1, 4);
                if (unavailable)
                    logger.LogWarning("No se pudo conectar a Nexim {Host}:{Port}. Nuevo intento en {Seconds} s.",
                        configuration["Nexim:Host"], configuration["Nexim:Port"], delay.TotalSeconds);
                else
                    logger.LogInformation("Reintentando conexión en {Seconds} s.", delay.TotalSeconds);
                await Task.Delay(delay, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { shutdown.Cancel(); Disconnect(); }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        CancellationToken token = lifetime.Token;
        await connectGate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (IsConnected) return;
            string host = configuration["Nexim:Host"]
                ?? throw new InvalidOperationException("Falta la configuración Nexim:Host.");
            int port = configuration.GetValue<int?>("Nexim:Port")
                ?? throw new InvalidOperationException("Falta la configuración Nexim:Port.");
            if (port is < 1 or > 65535)
                throw new InvalidOperationException("La configuración Nexim:Port debe estar entre 1 y 65535.");

            TcpClient client = new();
            try
            {
                await client.ConnectAsync(host, port, token);
                NetworkStream stream = client.GetStream();
                lock (syncRoot)
                {
                    token.ThrowIfCancellationRequested();
                    tcpClient = client;
                    networkStream = stream;
                    disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                logger.LogInformation("Conectado a Nexim en {Host}:{Port}.", host, port);
            }
            catch { client.Dispose(); throw; }
        }
        finally { connectGate.Release(); }
    }

    public void Disconnect(NetworkStream? expectedStream = null)
    {
        bool hadSession;
        lock (syncRoot)
        {
            if (expectedStream is not null && !ReferenceEquals(networkStream, expectedStream)) return;
            hadSession = tcpClient is not null;
            // Dispose before releasing the slot for a replacement session.
            try { networkStream?.Dispose(); }
            finally
            {
                tcpClient?.Dispose();
                tcpClient = null;
                networkStream = null;
                disconnected?.TrySetResult();
            }
        }
        if (hadSession) logger.LogInformation("Desconectado de Nexim.");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        shutdown.Cancel();
        Disconnect();
        await base.StopAsync(cancellationToken);
        // Wait for an in-flight public ConnectAsync to release its unpublished client.
        await connectGate.WaitAsync(cancellationToken);
        connectGate.Release();
    }

    public override void Dispose()
    {
        shutdown.Cancel();
        Disconnect();
        base.Dispose();
    }
}

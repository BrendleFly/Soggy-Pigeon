using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using SoggyPigeon.Protocol;

namespace SoggyPigeon.Server;

/// <summary>The whole v1 server wired together: TCP control + UDP rendezvous/relay.</summary>
public sealed class PigeonServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _clientTasks = new();
    private readonly object _tasksGate = new();

    public Hub Hub { get; }
    public ManifestStore Manifest { get; }
    public TokenStore Tokens { get; }
    public Rendezvous Rendezvous { get; }
    public TcpListener Listener { get; }
    public int TcpPort { get; }
    public int UdpPort { get; }
    public X509Certificate2? TlsCert { get; }

    private PigeonServer(ManifestStore manifest, TokenStore tokens, int tcpPort, int udpPort,
        ChaosConfig? chaos = null, X509Certificate2? tlsCert = null)
    {
        Manifest = manifest;
        Tokens = tokens;
        Hub = new Hub(manifest, tokens);
        Listener = new TcpListener(IPAddress.Any, tcpPort == 0 ? 0 : tcpPort);
        Listener.Start();
        TcpPort = ((IPEndPoint)Listener.LocalEndpoint).Port;
        Rendezvous = new Rendezvous(udpPort, Hub, chaos);
        UdpPort = Rendezvous.LocalEndPoint.Port;
        TlsCert = tlsCert;
        // relay address handed to clients: same host the TCP client reached us on
        Hub.RelayEndpoint = $"<server>:{UdpPort}";
    }

    /// <summary>Bind and start. tcpPort/udpPort 0 = ephemeral (tests).</summary>
    public static PigeonServer Start(int tcpPort = 0, int udpPort = 0,
        string? dataDir = null, ManifestStore? manifest = null, ChaosConfig? chaos = null,
        X509Certificate2? tlsCert = null)
    {
        manifest ??= ManifestStore.Default();
        var tokens = new TokenStore(dataDir ?? Path.Combine(Path.GetTempPath(), "soggy-pigeon-server"));
        var server = new PigeonServer(manifest, tokens, tcpPort, udpPort, chaos, tlsCert);
        _ = Task.Run(server.Rendezvous.RunAsync);
        _ = Task.Run(() => server.AcceptLoopAsync(server._cts.Token));
        _ = Task.Run(() => server.SweepLoopAsync(server._cts.Token));
        return server;
    }

    /// <summary>Publish the relay endpoint for a specific advertised host (tests use loopback).</summary>
    public void AdvertiseRelayHost(string host) => Hub.RelayEndpoint = $"{host}:{UdpPort}";

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await Listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) when (!ct.IsCancellationRequested)
            {
                // transient accept error (e.g. pending-queue burst): log and keep
                // serving — this loop used to exit permanently on one error,
                // leaving a server that answered nothing while still "running"
                Console.WriteLine($"[server] accept error (continuing): {nameof(SocketException)}");
                continue;
            }
            var conn = new ClientConnection(tcp, Hub, Tokens, TlsCert);
            var task = Task.Run(() => conn.RunAsync(ct), ct);
            lock (_tasksGate) { _clientTasks.Add(task); _clientTasks.RemoveAll(t => t.IsCompleted); }
        }
    }

    private async Task SweepLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                Hub.SweepSilent(Proto.QueueTimeoutSeconds);
        }
        catch (OperationCanceledException) { }
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        Listener.Stop();
        Rendezvous.Dispose();
        Task[] pending;
        lock (_tasksGate) pending = _clientTasks.ToArray();
        try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

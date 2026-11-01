using System.Net.Security;
using System.Net.Sockets;
using SoggyPigeon.App.Infrastructure;
using SoggyPigeon.Protocol;

namespace SoggyPigeon.App.Services;

/// <summary>
/// The app's half of the pigeon-1 control plane: connect, greet, fetch the
/// manifest, queue, and surface paired/peer/counts/session events. Reconnect
/// is explicit (press Search again) — v1 keeps no background session.
/// </summary>
public sealed class PigeonClient : ObservableObject
{
    private TcpClient? _tcp;
    private JsonLineConnection? _json;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private Task? _heartbeatLoop;
    /// <summary>Liveness the app can trust: TcpClient.Connected is a cached
    /// last-I/O flag that stays true after a silent disconnect, so IsConnected
    /// must not derive from it (a stale true makes Search skip reconnecting).</summary>
    private volatile bool _alive;
    /// <summary>Serializes ConnectAsync against itself: a second Search during
    /// a slow connect used to tear the first attempt's sockets apart.</summary>
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    public static PigeonClient Instance { get; } = new();

    public bool IsConnected => _alive && _json is not null;

    private bool _isConnecting;
    /// <summary>True while a connect attempt is in flight — Search disables itself so a second press can't interleave.</summary>
    public bool IsConnecting
    {
        get => _isConnecting;
        private set { if (Set(ref _isConnecting, value)) Raise(nameof(IsIdle)); }
    }
    public bool IsIdle => !IsConnected && !IsConnecting;

    // ---- events (raised on the read loop thread; VMs marshal to UI) ----
    public event Action<WelcomeMsg>? Welcome;
    public event Action<ManifestDoc>? Manifest;
    public event Action<QueuedMsg>? Queued;
    public event Action<CountsMsg>? Counts;
    public event Action<PairedMsg>? Paired;
    public event Action<PeerMsg>? Peer;
    public event Action<SessionEndMsg>? SessionEnd;
    public event Action<ErrorMsg>? Error;
    public event Action? Disconnected;

    public async Task ConnectAsync(string hostPort, string token, string name, string? tlsPin = null)
    {
        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectAsync().ConfigureAwait(false);
            IsConnecting = true;
            var cts = new CancellationTokenSource();
            _cts = cts;
            // hard budget for TCP connect + TLS handshake: a firewalled-silent
            // host used to hold the UI ~21s (OS default) with Search still live
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            budget.CancelAfter(TimeSpan.FromSeconds(6));

            TcpClient? tcp = null;
            try
            {
                var parts = hostPort.Split(':');
                var host = parts[0];
                var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : Proto.DefaultTcpPort;

                tcp = new TcpClient();
                await tcp.ConnectAsync(host, port, budget.Token).ConfigureAwait(false);
                _tcp = tcp;

                Stream stream = tcp.GetStream();
                if (!string.IsNullOrWhiteSpace(tlsPin))
                {
                    // pinned self-signed server (§4.1 pattern): accept ONLY the cert
                    // whose SHA-256 equals the pin — chain errors are expected and ignored
                    var ssl = new SslStream(stream, false, TlsPin.Validator(tlsPin));
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = host,
                    }, budget.Token).ConfigureAwait(false);
                    stream = ssl;
                }
                _json = new JsonLineConnection(stream, tcp);

                await SendAsync(new HelloMsg { Token = token, Name = name }).ConfigureAwait(false);

                _alive = true;
                _readLoop = Task.Run(() => ReadLoopAsync(cts.Token));
                _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(cts.Token));
            }
            catch
            {
                // dispose everything this attempt created — the singleton fields
                // only take ownership once the connection is fully usable.
                // Null _cts BEFORE disposing: a concurrent DisconnectAsync reading
                // the field must never Cancel() a disposed source.
                _alive = false;
                if (!ReferenceEquals(_tcp, tcp)) tcp?.Dispose();
                _tcp = null;
                _json = null;
                if (ReferenceEquals(_cts, cts)) _cts = null;
                cts.Dispose();
                throw;
            }
            finally
            {
                IsConnecting = false;
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        _alive = false;
        IsConnecting = false;
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        // capture-and-null atomically so a concurrent ConnectAsync failure
        // cannot double-dispose this CTS
        var cts = _cts;
        _cts = null;
        try { await Task.WhenAll(_readLoop ?? Task.CompletedTask, _heartbeatLoop ?? Task.CompletedTask).ConfigureAwait(false); } catch { }
        _json?.Dispose();
        _tcp?.Dispose();
        _json = null;
        _tcp = null;
        _readLoop = null;
        _heartbeatLoop = null;
        try { cts?.Dispose(); } catch (ObjectDisposedException) { }
        Raise(nameof(IsConnected));
        Raise(nameof(IsIdle));
    }

    public Task SendAsync(object msg)
        => _json is { } json ? json.SendAsync(msg) : Task.CompletedTask;

    public Task RequestManifestAsync() => SendAsync(new ManifestReqMsg());

    public Task EnterQueueAsync(QueueGame[] games, string mode, string policy,
        int delay = 2, int digestEvery = 1)
        => SendAsync(new EnterQueueMsg { Games = games, Mode = mode, Policy = policy,
            Delay = delay, DigestEvery = digestEvery });

    public Task LeaveQueueAsync() => SendAsync(new LeaveQueueMsg());

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var received = await _json!.ReceiveAsync(ct).ConfigureAwait(false);
                if (received is null) break;
                Dispatch(received.Value.Msg);
            }
        }
        catch { /* connection died or shutdown */ }
        finally
        {
            _alive = false;
            // bound properties must change on the UI thread (Avalonia rule)
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Raise(nameof(IsConnected));
                Raise(nameof(IsIdle));
            });
            Disconnected?.Invoke();
        }
    }

    private void Dispatch(object msg)
    {
        switch (msg)
        {
            case WelcomeMsg m: Welcome?.Invoke(m); break;
            case ManifestMsg m: Manifest?.Invoke(m.Doc); break;
            case QueuedMsg m: Queued?.Invoke(m); break;
            case CountsMsg m: Counts?.Invoke(m); break;
            case PairedMsg m: Paired?.Invoke(m); break;
            case PeerMsg m: Peer?.Invoke(m); break;
            case SessionEndMsg m: SessionEnd?.Invoke(m); break;
            case ErrorMsg m: Error?.Invoke(m); break;
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        // first beat immediately: a client that connects and queues must not
        // spend its first 10s relying on the construction-time timestamp
        await SendAsync(new HeartbeatMsg()).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Proto.HeartbeatSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await SendAsync(new HeartbeatMsg()).ConfigureAwait(false);
        }
        catch { }
    }
}

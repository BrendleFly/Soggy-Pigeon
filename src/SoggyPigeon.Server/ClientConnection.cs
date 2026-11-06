using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using SoggyPigeon.Protocol;

namespace SoggyPigeon.Server;

/// <summary>One TCP client: reads messages until close, dispatches to the Hub.</summary>
public sealed class ClientConnection
{
    private readonly TcpClient _tcp;
    private readonly Hub _hub;
    private readonly TokenStore _tokens;
    private readonly X509Certificate2? _tlsCert;
    private JsonLineConnection? _json;
    private ClientState? _state;

    public ClientConnection(TcpClient tcp, Hub hub, TokenStore tokens, X509Certificate2? tlsCert = null)
    {
        _tcp = tcp;
        _hub = hub;
        _tokens = tokens;
        _tlsCert = tlsCert;
    }

    public IPEndPoint RemoteEndPoint => (IPEndPoint)(_tcp.Client.RemoteEndPoint ?? new IPEndPoint(IPAddress.None, 0));

    public async Task RunAsync(CancellationToken ct)
    {
        SslStream? ssl = null;   // hoisted for the finally's dispose
        try
        {
            Stream stream = _tcp.GetStream();
            if (_tlsCert is not null)
            {
                // handshake budget: a half-open client (SYN accepted, handshake
                // never completes) used to park a task + SslStream forever
                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshakeCts.CancelAfter(TimeSpan.FromSeconds(15));
                ssl = new SslStream(stream, false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _tlsCert,
                }, handshakeCts.Token).ConfigureAwait(false);
                stream = ssl;
            }
            _json = new JsonLineConnection(stream, _tcp);
            while (!ct.IsCancellationRequested)
            {
                (string Type, object Msg)? received;
                try
                {
                    received = await _json.ReceiveAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
                {
                    // malformed line from this client: tell it, then close it —
                    // the old filter let the exception escape unobserved
                    await TrySendAsync(new ErrorMsg { Kind = "bad_message", Detail = "unparsable line" }).ConfigureAwait(false);
                    break;
                }
                if (received is null) break;
                if (_state is not null) _hub.Touch(_state);   // any traffic = alive
                await DispatchAsync(received.Value.Type, received.Value.Msg).ConfigureAwait(false);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested || !_tcp.Connected)
        {
            // normal close path
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException
                                   or System.Security.Authentication.AuthenticationException)
        {
            // transport death mid-session: same reap path
        }
        finally
        {
            if (_state is not null) _hub.ClientDisconnected(_state);
            // dispose the TLS wrapper itself: _tcp.Dispose() releases the
            // socket but not the SslStream's pooled buffers — a failed
            // handshake used to leak one per scanning connection
            try { ssl?.Dispose(); } catch { }
            try { _tcp.Dispose(); } catch { }
        }
    }

    public Task TrySendAsync(object msg)
        => _json is null ? Task.CompletedTask : SafeSend(msg);

    private async Task SafeSend(object msg)
    {
        try { await _json!.SendAsync(msg).ConfigureAwait(false); }
        catch { /* connection dying; the read loop will reap it */ }
    }

    private async Task DispatchAsync(string type, object msg)
    {
        switch (msg)
        {
            case HelloMsg hello:
                if (_state is not null)
                {
                    // one hello per connection: a second would orphan the first
                    // ClientState in the hub's online set (inflating counts)
                    await TrySendAsync(new ErrorMsg { Kind = "bad_message", Detail = "already greeted" }).ConfigureAwait(false);
                    return;
                }
                if (hello.Proto != Proto.Name)
                {
                    await TrySendAsync(new ErrorMsg
                    {
                        Kind = "version_mismatch",
                        Detail = $"server speaks {Proto.Name}, client sent {hello.Proto}",
                    }).ConfigureAwait(false);
                    return;
                }
                if (hello.Token.Length < 16)
                {
                    await TrySendAsync(new ErrorMsg { Kind = "bad_token", Detail = "token too short" }).ConfigureAwait(false);
                    return;
                }
                if (NameFilter.IsOffensive(hello.Name))
                {
                    // The app filters at edit time too, but this is the check
                    // that actually matters — a modified client can bypass it.
                    await TrySendAsync(new ErrorMsg { Kind = "bad_name", Detail = "screen name not allowed: pick another" }).ConfigureAwait(false);
                    return;
                }
                _state = new ClientState { Connection = this, Token = hello.Token, Name = SanitizeName(hello.Name) };
                _hub.ClientConnected(_state);
                await TrySendAsync(_hub.HandleHello(_state)).ConfigureAwait(false);
                break;

            case ManifestReqMsg:
                if (_state is null) break;
                await TrySendAsync(_hub.HandleManifestReq()).ConfigureAwait(false);
                break;

            case EnterQueueMsg enter:
                if (_state is null)
                {
                    await RequireStateAsync().ConfigureAwait(false);
                    break;
                }
                if (_hub.HandleEnterQueue(_state, enter) is { } reply)
                    await TrySendAsync(reply).ConfigureAwait(false);
                break;

            case LeaveQueueMsg:
                if (_state is null) break;
                _hub.HandleLeaveQueue(_state);
                break;

            case CreateLobbyMsg createLobby:
                if (_state is null) break;
                _hub.HandleCreateLobby(_state, createLobby);
                break;

            case JoinLobbyMsg joinLobby:
                if (_state is null) break;
                _hub.HandleJoinLobby(_state, joinLobby);
                break;

            case LeaveLobbyMsg:
                if (_state is null) break;
                _hub.HandleLeaveLobby(_state);
                break;

            case LobbyReadyMsg lobbyReady:
                if (_state is null) break;
                _hub.HandleLobbyReady(_state, lobbyReady);
                break;

            case HeartbeatMsg:
                if (_state is null) break;
                _hub.HandleHeartbeat(_state);
                break;

            case PlayingMsg playing:
                if (_state is null) break;
                _hub.HandlePlaying(_state, playing.Session);
                break;

            case ResultMsg result:
                if (_state is null) break;
                _hub.HandleResult(_state, result.Session, result.Outcome, result.DurationS);
                break;
        }
    }

    /// <summary>hello must arrive before anything else.</summary>
    private async Task RequireStateAsync()
    {
        await TrySendAsync(new ErrorMsg { Kind = "no_hello", Detail = "say hello first" }).ConfigureAwait(false);
    }

    private static string SanitizeName(string raw)
    {
        var name = raw.Trim();
        if (name.Length > 15) name = name[..15];
        if (name.Length < 3) name = name.Length == 0 ? "player" : name.PadRight(3, '-');
        return name;
    }
}

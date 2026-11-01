namespace SoggyPigeon.Protocol;

/// <summary>Frozen protocol constants for Soggy Pigeon v1.</summary>
public static class Proto
{
    /// <summary>Wire protocol version. Bump on any breaking message change.</summary>
    public const string Name = "pigeon-1";

    /// <summary>Client application version reported in hello.</summary>
    public const string ClientVersion = "0.1.0";

    /// <summary>First byte of every UDP datagram (punch, ping, relay).</summary>
    public const byte DatagramMagic = (byte)'P';

    /// <summary>UDP datagram payload types.</summary>
    public enum DatagramType : byte
    {
        Punch = 1,      // client -> server rendezvous: "I exist for this session"
        Seen   = 2,     // server -> client: your public endpoint (payload: ip+port)
        Hi     = 3,     // client -> peer: direct punch attempt
        Ping   = 4,     // client -> peer: echo request (payload: 8-byte ticks)
        Pong   = 5,     // peer -> client: echo reply (payload: unchanged)
        Relay  = 6,     // client -> server: forward payload to peer
        RelayFrom = 7, // server -> client: relayed payload from peer
    }

    /// <summary>Default TCP (control) port for the server.</summary>
    public const int DefaultTcpPort = 7200;

    /// <summary>Default UDP port for the server's punch rendezvous + relay.</summary>
    public const int DefaultUdpPort = 7201;

    // ---- timings (seconds unless noted) ---------------------------------

    public const int HeartbeatSeconds = 10;        // client heartbeat while queued
    public const int QueueTimeoutSeconds = 30;     // drop queued player after silence
    public const double PunchMaxSeconds = 10.0;    // give up direct punch, use relay
    public const double PunchIntervalSeconds = 0.1;
    public const int PunchPingSamples = 6;         // echo pings; keep lowest
    public const int StatusPollHz = 5;             // app polls the game status file
    public const int SessionIdBytes = 16;          // random bytes in a session id
    public const int RelaySessionTagBytes = 8;     // session tag in datagram header
}

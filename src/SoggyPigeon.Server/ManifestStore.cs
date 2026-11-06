using System.Text.Json;
using SoggyPigeon.Protocol;

namespace SoggyPigeon.Server;

/// <summary>Loads and serves the game manifest (§4.4: the library is data).</summary>
public sealed class ManifestStore
{
    private ManifestDoc _doc;

    public ManifestStore(ManifestDoc doc) { _doc = doc; }

    public ManifestDoc Current => _doc;

    public static ManifestStore LoadFile(string path)
    {
        var json = File.ReadAllText(path);
        var doc = JsonSerializer.Deserialize<ManifestDoc>(json, PigeonJson.Options)
                  ?? throw new InvalidDataException($"manifest at {path} is empty");
        Validate(doc);
        return new ManifestStore(doc);
    }

    /// <summary>The manifest the v1 dev server ships: two mock games (pipeline
    /// proof + per-game params proof) plus the HSFA entry (Hyper Street Fighter
    /// Zero, the Japanese title — internal id stays "hsfa") the real port will
    /// claim when it exists.</summary>
    public static ManifestStore Default() => new(new ManifestDoc
    {
        Rev = 2,
        Games = new[]
        {
            new ManifestGame
            {
                Id = "mock",
                Name = "MockFighter (pipeline test, not a real game)",
                Fingerprint = "mock-0.1.0",
                Hints = new GameHints { Folders = new[] { "%LOCALAPPDATA%/MockFighter" }, Exe = new[] { "MockFighter.exe" } },
                Params = System.Array.Empty<string>(),
            },
            new ManifestGame
            {
                Id = "kart",
                Name = "MockKart (params test, not a real game)",
                Fingerprint = "kart-0.1.0",
                Hints = new GameHints { Folders = new[] { "%LOCALAPPDATA%/MockKart" }, Exe = new[] { "MockKart.exe" } },
                Params = new[] { "laps", "class" },
                ParamsDefault = "laps=2;class=medium",
            },
            new ManifestGame
            {
                Id = "pong",
                Name = "Pong: The Next Level",
                Fingerprint = "pong-0.0.3-sp1",
                Hints = new GameHints { Folders = new[] { "%LOCALAPPDATA%/PongRecomp" }, Exe = new[] { "Pong_Recompiled.exe" } },
                Params = System.Array.Empty<string>(),
            },
            new ManifestGame
            {
                Id = "hsfa",
                Name = "Hyper Street Fighter Zero",
                Fingerprint = "hsfa-0.1.0-dev",
                Hints = new GameHints { Folders = new[] { "%LOCALAPPDATA%/HSFAPort" }, Exe = new[] { "hsfa_port.exe" } },
                Params = new[] { "chars", "ismode" },
            },
        },
    });

    static void Validate(ManifestDoc doc)
    {
        if (doc.Proto != Proto.Name)
            throw new InvalidDataException($"manifest proto '{doc.Proto}' != '{Proto.Name}'");
        var seen = new HashSet<string>();
        foreach (var g in doc.Games)
        {
            if (string.IsNullOrWhiteSpace(g.Id) || !seen.Add(g.Id))
                throw new InvalidDataException($"manifest game id '{g.Id}' missing or duplicate");
            if (string.IsNullOrWhiteSpace(g.Fingerprint))
                throw new InvalidDataException($"manifest game '{g.Id}' has no fingerprint");
        }
    }
}

# Soggy Pigeon

Play games online with strangers. It's currently only been tested on Windows 11, but should work on Windows 10 as well. The app never contains or distributes games: it only finds and launches the ones already on your computer.

## List of Compatible Games

- Pong - The Next Level (PSX, U); [Pong-The-Next-Level-Decomp](https://github.com/BrendleFly/Pong-The-Next-Level-Decomp)

## Features

- **Random Search**: Choose which games you want to find a match in. Soggy Pigeon will randomly choose an available game and player online.

- **History**: Your past matches: who you played, the outcome, how long it lasted.

- **Settings**: Your player name, the server address, and connection settings.

- **Controls**: Mouse and game controller support.

## Where the project is today

- It's early days. The app barely works, and only one game is currently compatible.

## Running and building

You need the free [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet build SoggyPigeon.sln
```

- **App:** `src/SoggyPigeon.App/bin/Debug/net8.0/SoggyPigeon.exe`
- **Server:** `dotnet run --project src/SoggyPigeon.Server` (listens on port 7200)

## Repository Contents

| Folder | What it is |
|---|---|
| `src/SoggyPigeon.App` | Main application |
| `src/SoggyPigeon.Server` | The matchmaker: pairs players, helps them connect, bridges traffic when needed |
| `src/SoggyPigeon.Protocol` | The shared "language" the app, the server and the games use to talk to each other |

## Credits

- [SDL2](https://github.com/libsdl-org/SDL) — gamepad support (zlib licence,
  shipped as `src/SoggyPigeon.App/native/SDL2.dll`)
- [Avalonia](https://avaloniaui.net/) — the app's interface toolkit
- Built with [.NET 8](https://dotnet.microsoft.com/)

## Legal note

This repository contains **no game code, art, music, or data** from any games. You must own the original games, and extract the data from them yourself. If I have time later, I'll explain how to do this, or link to a guide.

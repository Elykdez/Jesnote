# Jasnote

<p align="center">
  <a href="https://github.com/Elykdez/Jasnote"><img src="src\Resources\Icons\logo.png" alt="Jasnote" width="96" /></a>
</p>
<p align="center">
  <p align="center"><strong>A desktop app for opening and inspecting super large JSON | JSONL documents.</strong></p>
</p>
<p align="center">
  English | <a href="./README_CN.md">中文</a>
</p>
<p align="center">
  <a href="https://github.com/Elykdez/Jasnote/actions/workflows/ci-cd.yml"><img alt="CI/CD" src="https://github.com/Elykdez/Jasnote/actions/workflows/ci-cd.yml/badge.svg" /></a>
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8.0-512BD4" />
  <img alt="WinForms" src="https://img.shields.io/badge/UI-WinForms-0078D4" />
  <img alt="JSON" src="https://img.shields.io/badge/data-JSON-2F7D32" />
  <img alt="Windows" src="https://img.shields.io/badge/platform-Windows-0078D6" />
  <a href="https://github.com/Elykdez/Jasnote/releases"><img alt="Release" src="https://img.shields.io/github/v/release/Elykdez/Jasnote?label=release" /></a>
</p>

> Inspired by [Janice](https://github.com/ErikKalkoken/Janice).

- Built with a custom virtualized tree view so large files can be browsed without creating a UI node for every JSON element.

## Features

- Browse JSON data as an expandable tree.
- Open files from the file picker, clipboard, drag and drop, or a command line argument.
- Search keys and values with wildcard patterns.
- Export the selected JSON branch to a new file or copy it to the clipboard.
- View very large JSON files, like >2GB documents with 100MB+ elements without heavy memory issues or UI freezes.
- Switch between light and dark themes.
- Switch between different localizations (English, Simplified Chinese).

## Requirements

- Windows 10 with .NET Desktop Runtime 8 or newer.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or newer for building from source.

## Run (Windows)

Download the latest packaged build from the [releases page](https://github.com/Elykdez/jasnote/releases), extract it, then run `run.bat` from the extracted folder.

To run from source:

```powershell
.\run.bat
```

You can also open a file directly:

```powershell
.\run.bat .\sample.json
```

Equivalent `dotnet` command:

```powershell
dotnet run --project .\src\Jasnote.csproj -- .\sample.json
```

## Build

Restore dependencies:

```powershell
dotnet restore .\src\Jasnote.csproj
```

Build a release binary:

```powershell
dotnet build .\src\Jasnote.csproj -c Release
```

The build output is written to:

```text
src\bin\Release\net8.0-windows\
```

You can also build the solution file if your SDK supports `.slnx`:

```powershell
dotnet build .\Jasnote.slnx -c Release
```

## Plan

- Improve UI
- Add OSX support
- Add more localizations
- Add more Json related tools

## Attributions

- GPT 5.5 for code assistance and documentation.

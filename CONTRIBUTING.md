# Contributing to AutoMarket Pro

Thank you for your interest in contributing! This guide covers development setup, the build workflow, and how to cut a release.

## Prerequisites

- **.NET 10.0 SDK**
- A working **Dalamud** development environment (XIVLauncher or XIV on Mac)
- **Git**

The project targets `net10.0-windows` and references Dalamud API level 15. The Dalamud library path is resolved automatically from the csproj based on your OS:

- **Windows**: `%APPDATA%\XIVLauncher\addon\Hooks\dev\`
- **macOS**: `~/Library/Application Support/XIV on Mac/dalamud/Hooks/dev/`

## Getting Started

```bash
git clone https://github.com/bimilbimil/AutomarketPro.git
cd AutomarketPro
make build
```

`make build` compiles a Debug build and copies the plugin files to your Dalamud dev plugins directory so you can test immediately.

## Project Structure

```
AutomarketPro/
├── AutomarketPro.cs          # Plugin entry point, service wiring
├── Core/
│   └── Configuration.cs      # Persisted settings
├── Models/
│   ├── ScannedItem.cs        # Item data model
│   └── RunSummary.cs         # Automation run results
├── Services/
│   ├── MarketScanner.cs      # Inventory scan + Universalis price fetching
│   └── RetainerAutomation.cs # Orchestrates full cycle and clear cycle
├── Automation/
│   ├── ItemListing.cs        # All retainer UI interactions (list, vendor, clear)
│   └── RetainerInteraction.cs# Retainer bell / SelectString / window navigation
└── UI/
    └── MainWindow.cs         # ImGui window and all tabs
```

## Makefile Targets

| Target | Description |
|--------|-------------|
| `make build` | Compile (Debug) and deploy to local Dalamud plugins dir |
| `make build-only` | Compile without deploying |
| `make deploy` | Copy already-built files to plugins dir |
| `make package RELEASE_TAG=vX.Y.Z` | Create `dist/AutomarketPro.zip` for release |
| `make package-dev` | Create `dist/AutomarketPro-dev.zip` |
| `make clean` | Remove build artifacts |
| `make rebuild` | Clean then build |
| `make info` | Show build and install paths |

## Release Workflow

Follow these steps in order to avoid version mismatches:

1. **Bump `AutomarketPro.json`** — update `AssemblyVersion` manually (the Makefile only auto-bumps `repo.json`):
   ```json
   "AssemblyVersion": "1.0.0.X"
   ```

2. **Build a Release binary:**
   ```bash
   dotnet build -c Debug
   ```

3. **Package with the release tag** — this also auto-increments `repo.json` AssemblyVersion, updates the timestamp, and sets the download URLs:
   ```bash
   make package RELEASE_TAG=v1.X.Y
   ```

4. **Commit and push everything** (source changes + both json files + `dist/` is gitignored):
   ```bash
   git add .
   git commit -m "release vX.Y.Z"
   git push
   ```

5. **Create a GitHub Release** tagged `v1.X.Y` and upload `dist/AutomarketPro.zip`.

Dalamud detects updates via the `AssemblyVersion` in `repo.json`, which is hosted at:
```
https://raw.githubusercontent.com/bimilbimil/AutomarketPro/main/repo.json
```

## Testing

Enable **Debug Logs** in the plugin Settings tab. All automation output appears in the **Debug** tab in real time. Useful areas to watch:

- **Scan**: item discovery, Universalis API responses, profit decisions
- **Listing**: retainer opening, context menu interactions, slot counting
- **Clear**: withdrawal flow, inventory/retainer bag space checks

## Dependencies

| Package | Version | Source |
|---------|---------|--------|
| ECommons | 3.2.1.6 | NuGet |
| ImGui.NET | 1.89.9.1 | NuGet |
| Dalamud + FFXIVClientStructs | (from Dalamud lib path) | Local ref |

## Code Guidelines

- Follow standard C# conventions
- All game UI interactions must run on the **framework thread** via `RunOnFrameworkThreadAsync` — never call `Thread.Sleep` inside a framework thread lambda
- Read stale-prone game memory (e.g. `RetainerManager.MarketItemCount`) once at session start and track changes locally
- Keep comments minimal — only when the *why* is non-obvious

## Submitting Changes

1. Branch from `main`
2. Make and test your changes
3. Open a pull request with a clear description

## Questions?

Open an issue on [GitHub](https://github.com/bimilbimil/AutomarketPro/issues).

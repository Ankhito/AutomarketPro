# AutoMarket Pro

Automates inventory scanning, marketboard listing, vendor selling, and retainer clearing using Universalis data and retainer control.

## Features

- **Automated Inventory Scanning**: Scans your inventory for all sellable items
- **Market Price Lookup**: Uses Universalis API to fetch current market prices
- **Smart Listing**: Automatically lists profitable items on the Market Board with auto-undercut
- **Vendor Automation**: Sells unprofitable items to vendors via retainers
- **Price Management**: Adjusts prices on already-listed items to stay competitive
- **Retainer Clearing**: Pulls all listed market items back to your inventory or retainer bag in one click
- **Retainer Management**: Cycles through all available retainers automatically
- **Ignore List**: Exclude specific items from processing
- **Configurable Settings**: Customize undercut amounts, profit thresholds, delays, and more
- **Debug Logging**: Comprehensive logging for troubleshooting

## Installation

### Method 1: Custom Plugin Repository (Recommended)

1. Open Dalamud Settings with `/xlsettings` in-game
2. Navigate to the **Experimental** tab
3. Under **Custom Plugin Repositories**, click **+** to add a new entry
4. Paste the repository URL:
   ```
   https://raw.githubusercontent.com/bimilbimil/AutomarketPro/main/repo.json
   ```
5. Click **Save**
6. Find **AutoMarket Pro** in the plugin list and install it

### Method 2: Manual Installation

1. Download the latest `AutomarketPro.zip` from [GitHub Releases](https://github.com/bimilbimil/AutomarketPro/releases)
2. Extract into your Dalamud plugins directory:
   - **Windows (XIVLauncher)**: `%APPDATA%\XIVLauncher\addon\Hooks\dev\plugins`
   - **macOS (XIV on Mac)**: `~/Library/Application Support/XIV on Mac/dalamud/Hooks/dev/plugins`
3. Reload Dalamud or restart the game

### Method 3: Build from Source

See [CONTRIBUTING.md](CONTRIBUTING.md) for development setup and build instructions.

## Usage

Open the plugin with `/automarket` or through the Dalamud plugin menu.

### Control Bar Buttons

All three quick-action buttons are always visible at the top of the window:

| Button | Description |
|--------|-------------|
| **[>] Start Full Cycle** | Scan inventory, then list and/or vendor items across all retainers |
| **[S] Scan Only** | Scan inventory and fetch prices without touching retainers |
| **[C] Clear** | Pull all listed market items back from retainers (see Clear tab for options) |

> **Tip:** Keep your mouse still while automation is running — moving it can interfere with UI interactions.

### Full Cycle Workflow

1. Stand at your retainer bell and open the Retainer List
2. Configure your settings (see below)
3. Click **[>] Start Full Cycle**
4. The plugin will scan your inventory, fetch market prices, then cycle through each retainer to list profitable items and vendor unprofitable ones

### Clearing Retainers

The **[C] Clear** button pulls all currently listed market items back from your retainers. Before using it:

1. Open the **Clear** tab to configure options
2. Stand at your retainer bell and open the Retainer List
3. Click **[C] Clear**

**Clear tab options:**

- **Return to Retainer Inventory** — When unchecked (default), withdrawn items go directly to your inventory. When checked, items go to the retainer's bag instead.
- **Exclude retainers** — All retainers are cleared by default. Check a retainer's box to skip it.
- Retainers that don't exist are always skipped automatically.
- If the destination (your inventory or the retainer's bag) fills up, clearing stops early and a chat message tells you why.

### Commands

| Command | Description |
|---------|-------------|
| `/automarket` | Open the main UI |
| `/automarket start` | Start full automation cycle |
| `/automarket stop` | Stop automation |
| `/automarket pause` | Pause/resume automation |
| `/automarket summary` | Show last run summary |
| `/automarket config` | Open settings tab |

## Configuration

### Market Board Settings

- **Undercut Amount**: How much to undercut the lowest listed price (default: 1 gil)
- **Min Profit Threshold**: Minimum profit over vendor price required to list on MB (default: 100 gil)
- **Auto-undercut**: Automatically undercut the lowest market price when listing

### Automation Settings

- **Action Delay**: Delay between individual automation steps (default: 300ms)
- **Retainer Delay**: Delay between switching retainers (default: 1200ms)
- **List Only Mode**: List all items on MB regardless of profitability
- **Vendor Only Mode**: Vendor all items regardless of profitability
- **Manage Listed Items**: Adjust prices on already-listed retainer items to stay competitive before listing new ones

### Filter Settings

- **Skip HQ Items**: Ignore high-quality items during scanning
- **Skip Collectables**: Ignore collectable items during scanning
- **Skip Gear**: Ignore gear items during scanning
- **Data Center Scan**: Fetch prices from the entire data center instead of your home world only

### Ignore List

Items added to the Ignore List are permanently excluded from all automation. Use the **Ignore** tab to add or remove items by scanning your current inventory.

## Troubleshooting

Enable **Debug Logs** in the Settings tab to see detailed output in the Debug tab. This captures:
- Inventory scan results
- Universalis API responses
- Retainer UI interactions
- Pricing decisions

Common issues:
- **Nothing happens when starting**: Make sure the Retainer List window is open in-game before clicking Start or Clear.
- **Items not listing**: Check the Scan Results tab to confirm items were found and have a market price above your profit threshold.
- **Clear stops early**: Your inventory or retainer bag is full — free up space and run Clear again to continue.

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for development setup, build instructions, and contribution guidelines.

## Repository

GitHub: https://github.com/bimilbimil/AutomarketPro

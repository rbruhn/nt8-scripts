# nt8-scripts

NinjaTrader 8 indicators for entering trades with ATM strategies.

| Script | What it does | Guide |
|---|---|---|
| [PATsButtons](PATsButtons.cs) | Four buttons in Chart Trader that enter off the signal bar, size the trade from your dollar risk, pick a matching ATM template, and move the ATM's stop to the signal bar. | [docs/PATsButtons.md](docs/PATsButtons.md) |
| [ATMClickEntry](ATMClickEntry.cs) | Shift-click to buy, Ctrl-click to sell at the clicked price, using the ATM template selected in Chart Trader. | [docs/ATMClickEntry.md](docs/ATMClickEntry.md) |

![PATsButtons inside Chart Trader](images/patsbuttons.png)

## Install

### Option 1: import the add-on (easiest)

| Script | Download |
|---|---|
| PATsButtons v2.0 | [PATsButtons_v2.0.zip](https://github.com/rbruhn/nt8-scripts/raw/main/addons/PATsButtons_v2.0.zip) |
| ATMClickEntry v1.0 | [ATMClickEntry_v1.0.zip](https://github.com/rbruhn/nt8-scripts/raw/main/addons/ATMClickEntry_v1.0.zip) |

1. Download the zip. Don't unzip it.
2. In the NinjaTrader Control Center, go to **Tools → Import → NinjaScript Add-On…** and
   pick the zip. NinjaTrader installs and compiles it.
3. Open a chart with **Chart Trader** visible and add the indicator.

### Option 2: copy the .cs file

Each script is a single `.cs` file with no other dependencies. Use this if you want to
read or change the code.

1. Download the `.cs` file into `Documents\NinjaTrader 8\bin\Custom\Indicators\`.
2. In NinjaTrader, open **New → NinjaScript Editor** and press **F5** to compile.
3. Open a chart with **Chart Trader** visible and add the indicator.

### Updating or switching

Remove the old copy before installing a new one, or NinjaTrader will report duplicate
names:
- installed from a zip → **Tools → Remove NinjaScript Assembly**
- installed as a `.cs` → delete it from `bin\Custom\Indicators\` (or in the NinjaScript Editor)

## Disclaimer

These scripts place real orders on whatever account Chart Trader has selected. Test on
**Sim101** or **Playback101** first. Use at your own risk.

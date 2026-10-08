# nt8-scripts

NinjaTrader 8 indicators for entering trades with ATM strategies.

| Script | What it does | Guide |
|---|---|---|
| [PATsButtons](PATsButtons.cs) | Four buttons in Chart Trader that enter off the signal bar, size the trade from your dollar risk, pick a matching ATM template, and move the ATM's stop to the signal bar. | [docs/PATsButtons.md](docs/PATsButtons.md) |
| [ATMClickEntry](ATMClickEntry.cs) | Shift-click to buy, Ctrl-click to sell at the clicked price, using the ATM template selected in Chart Trader. | [docs/ATMClickEntry.md](docs/ATMClickEntry.md) |

![PATsButtons inside Chart Trader](images/patsbuttons.png)

## Install

Each script is a single `.cs` file with no other dependencies.

1. Download the `.cs` file into `Documents\NinjaTrader 8\bin\Custom\Indicators\`.
2. In NinjaTrader, open **New → NinjaScript Editor** and press **F5** to compile.
3. Open a chart with **Chart Trader** visible and add the indicator.

If you have an older copy installed as an add-on (a `.dll`), remove it first with
**Tools → Remove NinjaScript Assembly**, or NinjaTrader will report duplicate names.

## Disclaimer

These scripts place real orders on whatever account Chart Trader has selected. Test on
**Sim101** or **Playback101** first. Use at your own risk.

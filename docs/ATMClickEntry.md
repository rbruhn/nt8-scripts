# ATMClickEntry

A NinjaTrader 8 indicator for placing an entry **at a price you click on the chart**, with
the ATM template selected in Chart Trader attached.

- **Shift + left-click** → buy at the clicked price
- **Ctrl + left-click** → sell at the clicked price (or **Ctrl+Shift + left-click**, see
  [Hot keys](#hot-keys))

No buttons. It uses whatever Chart Trader has selected: account, instrument, ATM
template and quantity.

> **About ATM template names:** names like `2C-16S-4P-1R` in this guide are only
> examples from the author's setup (2 contracts, 16-tick stop,
> 4-tick target, 1 runner). ATM templates are your own: create them in Chart Trader's
> **ATM Strategy** dropdown (**Custom…**), set the contracts, stop and targets to suit
> your trading, and name them whatever you like.

---

## Quick start

1. Download [`ATMClickEntry_v1.0.zip`](https://github.com/rbruhn/nt8-scripts/raw/main/addons/ATMClickEntry_v1.0.zip),
   then in the NinjaTrader Control Center go to **Tools → Import → NinjaScript Add-On…**
   and pick the zip. (Or copy [`ATMClickEntry.cs`](../ATMClickEntry.cs) into
   `Documents\NinjaTrader 8\bin\Custom\Indicators\` and press **F5** in the NinjaScript
   Editor.)
2. Open a chart with **Chart Trader** visible and add the **ATMClickEntry** indicator.
3. In Chart Trader, pick the account, instrument, **ATM Strategy** and order quantity.
4. Hold Shift (buy) or Ctrl (sell) and left-click the price where you want in.

---

## Hot keys

The indicator has one setting, under **Hot Keys** in its settings window (right-click
the chart → **Indicators…** → **ATMClickEntry**):

- **Use Ctrl+Shift+Click to Sell** (off by default). Tick it if you use Ctrl+click for
  something else, like moving the chart. Then:
  - **Shift + click** buys, as before.
  - **Ctrl+Shift + click** sells.
  - A plain **Ctrl + click** is left alone; no order.

The keys must match exactly:

| You hold | Setting off | Setting on |
|---|---|---|
| Shift | Buy | Buy |
| Ctrl | Sell | nothing |
| Ctrl + Shift | nothing | Sell |
| anything with Alt | nothing | nothing |

It doesn't matter which key you press first, or whether you use the left or right
Ctrl/Shift; only what's held when the mouse button goes down counts.

---

## Stop or limit?

You don't choose. It compares the clicked price with the middle of the bid and ask:

| You click | Buy | Sell |
|---|---|---|
| **Above** the bid/ask middle | Buy **stop** | Sell **limit** |
| **Below** the bid/ask middle | Buy **limit** | Sell **stop** |

In other words: clicking where price has to *move to* gives a stop (breakout); clicking
where price has to *come back to* gives a limit (pullback).

### Example

ES is trading 5000.00 bid / 5000.25 ask (middle 5000.125). Chart Trader has an ATM
template selected (here an example one named `2C-16S-4P-1R`: 16-tick stop, 4-tick target)
and quantity 2.

- **Shift-click at 5002.00** → buy **stop** for 2 at 5002.00. When it fills, the ATM places
  its 16-tick stop and 4-tick target.
- **Shift-click at 4998.00** → buy **limit** for 2 at 4998.00, waiting for a pullback.
- **Ctrl-click at 4997.50** → sell **stop** for 2 at 4997.50.

The clicked price is rounded to the nearest tick.

---

## Good to know

- **Nothing happens?** It needs an ATM template selected in Chart Trader's **ATM
  Strategy** dropdown (not *None*) and an account. If either is missing, the click is
  ignored without a message.
- **ES chart, MES order:** the order goes to the instrument selected in **Chart Trader**,
  not the chart's own. You can watch ES and trade MES. The stop/limit choice uses the
  chart's bid and ask, which is fine when both move together, as ES and MES do.
- **Orders are TIF Day.**
- **Extra keys block the click.** Holding a key the setting doesn't use (both Shift and
  Ctrl with the setting off, or Alt at any time) does nothing, so a stray key can't place
  the wrong side.
- **Right-click menus** are ignored. NinjaTrader sends a fake click when its right-click
  menu closes, and ATMClickEntry filters it out.
- Every order is a real order on the selected account. Try it on **Sim101** first.

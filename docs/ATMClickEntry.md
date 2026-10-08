# ATMClickEntry

A NinjaTrader 8 indicator for placing an entry **at a price you click on the chart**, with
the ATM template selected in Chart Trader attached.

- **Shift + left-click** → buy at the clicked price
- **Ctrl + left-click** → sell at the clicked price

No buttons and no settings. It uses whatever Chart Trader has selected: account,
instrument, ATM template and quantity.

---

## Quick start

1. Download [`ATMClickEntry.cs`](../ATMClickEntry.cs) into
   `Documents\NinjaTrader 8\bin\Custom\Indicators\`, open the **NinjaScript Editor**, and
   press **F5** to compile.
2. Open a chart with **Chart Trader** visible and add the **ATMClickEntry** indicator.
3. In Chart Trader, pick the account, instrument, **ATM Strategy** and order quantity.
4. Hold Shift or Ctrl and left-click the price where you want in.

---

## Stop or limit?

You don't choose. It compares the clicked price with the middle of the bid and ask:

| You click | Buy (Shift) | Sell (Ctrl) |
|---|---|---|
| **Above** the bid/ask middle | Buy **stop** | Sell **limit** |
| **Below** the bid/ask middle | Buy **limit** | Sell **stop** |

In other words: clicking where price has to *move to* gives a stop (breakout); clicking
where price has to *come back to* gives a limit (pullback).

### Example

ES is trading 5000.00 bid / 5000.25 ask (middle 5000.125). Chart Trader has
`2C-16S-4P-1R` selected and quantity 2.

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
- **Holding both Shift and Ctrl** does nothing, so a stray key can't place the wrong side.
- **Right-click menus** are ignored. NinjaTrader sends a fake click when its right-click
  menu closes, and ATMClickEntry filters it out.
- Every order is a real order on the selected account. Try it on **Sim101** first.

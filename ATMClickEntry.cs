// ATMClickEntry.cs
// NinjaTrader 8 Indicator
//
// FEATURES:
//   Ctrl  + Left-Click on chart → Sell at clicked price
//   Shift + Left-Click on chart → Buy  at clicked price
//
//   Order type is determined automatically:
//     Buy:  Stop if clicked price >= Bid/Ask midpoint | Limit if below
//     Sell: Stop if clicked price <= Bid/Ask midpoint | Limit if above
//
//   Uses the ATM template and instrument currently selected in Chart Trader.
//
// INSTALL:
//   1. NinjaScript Editor → New → Indicator → delete default code → paste this file
//   2. F5 to compile
//   3. Add indicator to a chart that has Chart Trader open
//   4. Select your saved ATM template in the Chart Trader dropdown before clicking

#region Using declarations
using System;
using System.Windows;
using System.Windows.Input;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class ATMClickEntry : Indicator
    {
        #region Private fields
        private ChartScale  _chartScale;
        private ChartTrader _chartTrader;
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Ctrl+Click to Sell, Shift+Click to Buy — uses selected ATM template and Chart Trader instrument.";
                Name        = "ATMClickEntry";
                Calculate   = Calculate.OnBarClose;
                IsOverlay   = true;
            }
            else if (State == State.Historical)
            {
                if (ChartControl != null)
                    ChartControl.Dispatcher.InvokeAsync(() =>
                    {
                        // Cache Chart Trader reference for account/instrument lookup
                        _chartTrader = Window.GetWindow(ChartControl.Parent)
                                             ?.FindFirst("ChartWindowChartTraderControl") as ChartTrader;

                        // Cache ChartScale for Y → price conversion
                        foreach (ChartScale scale in ChartPanel.Scales)
                            if (scale.ScaleJustification == ScaleJustification)
                                _chartScale = scale;

                        ChartControl.MouseDown += OnChartMouseDown;
                    });
            }
            else if (State == State.Terminated)
            {
                if (ChartControl != null)
                {
                    try
                    {
                        if (!ChartControl.Dispatcher.HasShutdownStarted)
                            ChartControl.Dispatcher.Invoke(() =>
                                ChartControl.MouseDown -= OnChartMouseDown);
                    }
                    catch (Exception) { }
                }
            }
        }

        protected override void OnBarUpdate() { }

        // ─────────────────────────────────────────────────────────────────────
        // MOUSE HANDLER
        // ─────────────────────────────────────────────────────────────────────

        private void OnChartMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Only react to a genuine left-click
            if (e.ChangedButton != MouseButton.Left) return;
            if (e.LeftButton   != MouseButtonState.Pressed) return;

            // Ignore ghost events from NT8's right-click context menu being
            // dismissed (both buttons report Pressed simultaneously in that case)
            if (e.RightButton == MouseButtonState.Pressed) return;

            bool ctrlHeld  = Keyboard.IsKeyDown(Key.LeftCtrl)  || Keyboard.IsKeyDown(Key.RightCtrl);
            bool shiftHeld = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

            // Require exactly one modifier — ignore if both or neither are held
            if (ctrlHeld == shiftHeld) return;

            // Y coordinate → price (DPI-aware)
            if (_chartScale == null) return;
            double rawY         = e.GetPosition(ChartControl as IInputElement).Y;
            double pixelY       = ChartingExtensions.ConvertToVerticalPixels(rawY, ChartControl.PresentationSource);
            double clickedPrice = _chartScale.GetValueByY((float)pixelY);

            // Bid/Ask midpoint to determine Stop vs Limit
            double midpoint = (GetCurrentBid() + GetCurrentAsk()) / 2.0;

            if (shiftHeld)
            {
                // Shift + Click = BUY
                OrderType oType = clickedPrice >= midpoint ? OrderType.StopMarket : OrderType.Limit;
                double cp = clickedPrice;
                TriggerCustomEvent(o => SubmitAtmOrder(OrderAction.Buy, oType, cp), null);
                e.Handled = true;
            }
            else if (ctrlHeld)
            {
                // Ctrl + Click = SELL
                OrderType oType = clickedPrice <= midpoint ? OrderType.StopMarket : OrderType.Limit;
                double cp = clickedPrice;
                TriggerCustomEvent(o => SubmitAtmOrder(OrderAction.Sell, oType, cp), null);
                e.Handled = true;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // ORDER SUBMISSION
        // ─────────────────────────────────────────────────────────────────────

        private void SubmitAtmOrder(OrderAction action, OrderType orderType, double basePrice)
        {
            try
            {
                NinjaTrader.Gui.NinjaScript.AtmStrategy.AtmStrategySelector atmSelector = null;
                NinjaTrader.NinjaScript.AtmStrategy selectedAtmStrategy                 = null;
                Account    account            = null;
                Instrument selectedInstrument = null;
                string     selectorText       = string.Empty;
                bool       hasStrategy        = false;

                ChartControl.Dispatcher.Invoke(() =>
                {
                    // ATM template
                    var found = Window.GetWindow(ChartControl.Parent)
                                      ?.FindFirst("ChartTraderControlATMStrategySelector");
                    atmSelector = found as NinjaTrader.Gui.NinjaScript.AtmStrategy.AtmStrategySelector;

                    if (atmSelector != null)
                    {
                        selectedAtmStrategy = atmSelector.SelectedAtmStrategy;
                        hasStrategy         = selectedAtmStrategy != null;
                        selectorText        = atmSelector.Text ?? string.Empty;
                    }

                    // Account and instrument from Chart Trader
                    if (_chartTrader != null)
                    {
                        account            = _chartTrader.Account;
                        selectedInstrument = _chartTrader.Instrument;
                    }
                });

                if (!hasStrategy || account == null) return;

                // Parse the actual template name from the selector Text property.
                // (.Name always returns the generic "AtmStrategy" class name, not
                // the user's saved template name.)
                string templateName = string.Empty;
                int idx = selectorText.IndexOf("Template:");
                if (idx >= 0)
                    templateName = selectorText.Substring(idx + "Template:".Length).Trim();
                if (string.IsNullOrEmpty(templateName)) return;

                // Use Chart Trader's selected instrument; fall back to chart's own
                Instrument tradeInstrument = selectedInstrument ?? Instrument;

                // Snap clicked price to the trade instrument's own tick size
                double tickSize = tradeInstrument.MasterInstrument.TickSize;
                double price    = Math.Round(basePrice / tickSize) * tickSize;

                double limitPrice = orderType == OrderType.Limit      ? price : 0;
                double stopPrice  = orderType == OrderType.StopMarket ? price : 0;

                // Read quantity from Chart Trader quantity selector
                int qty = 1;
                ChartControl.Dispatcher.Invoke(() =>
                {
                    var qtySelector = Window.GetWindow(ChartControl.Parent)
                                           ?.FindFirst("ChartTraderControlQuantitySelector")
                                           as NinjaTrader.Gui.Tools.QuantityUpDown;
                    if (qtySelector != null) qty = qtySelector.Value;
                });

                // CRITICAL: order name MUST be "Entry" for StartAtmStrategy to attach
                Order entryOrder = account.CreateOrder(
                    tradeInstrument,
                    action,
                    orderType,
                    OrderEntry.Manual,
                    TimeInForce.Day,
                    qty,
                    limitPrice,
                    stopPrice,
                    string.Empty,
                    "Entry",
                    Core.Globals.MaxDate,
                    null);

                NinjaTrader.NinjaScript.AtmStrategy.StartAtmStrategy(
                    selectedAtmStrategy, entryOrder);

                account.Submit(new Order[] { entryOrder });
            }
            catch (Exception) { }
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private ATMClickEntry[] cacheATMClickEntry;
		public ATMClickEntry ATMClickEntry()
		{
			return ATMClickEntry(Input);
		}

		public ATMClickEntry ATMClickEntry(ISeries<double> input)
		{
			if (cacheATMClickEntry != null)
				for (int idx = 0; idx < cacheATMClickEntry.Length; idx++)
					if (cacheATMClickEntry[idx] != null &&  cacheATMClickEntry[idx].EqualsInput(input))
						return cacheATMClickEntry[idx];
			return CacheIndicator<ATMClickEntry>(new ATMClickEntry(), input, ref cacheATMClickEntry);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.ATMClickEntry ATMClickEntry()
		{
			return indicator.ATMClickEntry(Input);
		}

		public Indicators.ATMClickEntry ATMClickEntry(ISeries<double> input )
		{
			return indicator.ATMClickEntry(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.ATMClickEntry ATMClickEntry()
		{
			return indicator.ATMClickEntry(Input);
		}

		public Indicators.ATMClickEntry ATMClickEntry(ISeries<double> input )
		{
			return indicator.ATMClickEntry(input);
		}
	}
}

#endregion

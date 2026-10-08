// PATsButtons.cs
// NinjaTrader 8 Indicator
//
// Four entry buttons (Cur Bar / Prev Bar, Long / Short) that:
//   1. Read the signal bar and place a stop entry beyond it (Entry Offset ticks).
//   2. Size the trade from Max Risk ($) and the distance to the signal-bar stop.
//   3. Pick the largest chosen ATM template that fits and start it on the entry.
//   4. After the fill, move every ATM stop to the signal-bar stop (Stop Offset ticks).
//
// Uses the account and instrument selected in Chart Trader.
// Full design: plan.md
//
// INSTALL:
//   1. Copy to Documents\NinjaTrader 8\bin\Custom\Indicators\
//   2. NinjaScript Editor → F5 to compile
//   3. Add to a chart with Chart Trader open, set Max Risk ($) and choose 1-5 ATM templates

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    [TypeConverter("NinjaTrader.NinjaScript.Indicators.PATsButtonsSettingsConverter")]
    public class PATsButtons : Indicator
    {

        #region ATM template files

        public const string NoTemplate = "None";

        public enum PanelLocation
        {
            ChartTrader,        // inside Chart Trader, above Chart Jot
            Chart               // bottom-right of the chart
        }

        public enum SizingMode
        {
            RiskBased,          // contracts from Max Risk, ATM from the chosen templates
            UseChartTrader      // ATM and quantity from Chart Trader
        }
        public const int    AtmChoiceCount = 5;

        // What PATsButtons needs to know about one saved ATM template.
        public class AtmTemplateInfo
        {
            public string Name;
            public int    Contracts;          // <EntryQuantity>, or the bracket quantities added up
            public int    FirstTargetTicks;   // closest target among the brackets
            public bool   IsTickMode;         // targets are in ticks (CalculationMode = Ticks)
        }

        // Reads ATM template XML files from Documents\NinjaTrader 8\templates\AtmStrategy\
        public static class AtmTemplateFiles
        {
            public static string Folder
            {
                get { return Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "templates", "AtmStrategy"); }
            }

            // Returns null if the file is missing or can't be read.
            public static AtmTemplateInfo Read(string templateName)
            {
                try
                {
                    string file = Path.Combine(Folder, templateName + ".xml");
                    if (!File.Exists(file)) return null;

                    XmlDocument doc = new XmlDocument();
                    doc.Load(file);

                    AtmTemplateInfo info = new AtmTemplateInfo();
                    info.Name             = templateName;
                    info.FirstTargetTicks = 0;

                    foreach (XmlNode bracket in doc.SelectNodes("//Brackets/Bracket"))
                    {
                        XmlNode qtyNode    = bracket.SelectSingleNode("Quantity");
                        XmlNode targetNode = bracket.SelectSingleNode("Target");

                        int qty;
                        if (qtyNode != null && int.TryParse(qtyNode.InnerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out qty))
                            info.Contracts += qty;

                        double target;
                        if (targetNode != null && double.TryParse(targetNode.InnerText, NumberStyles.Float, CultureInfo.InvariantCulture, out target))
                        {
                            int targetTicks = (int)Math.Round(target);
                            if (targetTicks > 0 && (info.FirstTargetTicks == 0 || targetTicks < info.FirstTargetTicks))
                                info.FirstTargetTicks = targetTicks;
                        }
                    }

                    // The template's order quantity; the bracket total is only a fallback
                    XmlNode entryQty = doc.SelectSingleNode("//AtmStrategy/EntryQuantity");
                    int qtyValue;
                    if (entryQty != null && int.TryParse(entryQty.InnerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out qtyValue) && qtyValue > 0)
                        info.Contracts = qtyValue;

                    XmlNode modeNode = doc.SelectSingleNode("//AtmStrategy/CalculationMode");
                    info.IsTickMode  = modeNode == null || modeNode.InnerText.Trim() == "Ticks";

                    return info.Contracts > 0 ? info : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            public static List<AtmTemplateInfo> ReadAll()
            {
                List<AtmTemplateInfo> all = new List<AtmTemplateInfo>();
                try
                {
                    if (!Directory.Exists(Folder)) return all;
                    foreach (string file in Directory.GetFiles(Folder, "*.xml"))
                    {
                        AtmTemplateInfo info = Read(Path.GetFileNameWithoutExtension(file));
                        if (info != null) all.Add(info);
                    }
                }
                catch (Exception) { }
                return all;
            }
        }

        // Dropdown for "ATM 1".."ATM 5": every saved template, shown as "Name (N)", plus "None".
        public class AtmChoiceConverter : StringConverter
        {
            public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
            public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return true; }

            public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
            {
                List<string> choices = new List<string>();
                choices.Add(NoTemplate);
                foreach (AtmTemplateInfo info in AtmTemplateFiles.ReadAll().OrderBy(t => t.Contracts).ThenBy(t => t.Name))
                    choices.Add(ChoiceLabel(info));
                return new StandardValuesCollection(choices);
            }
        }

        public static string ChoiceLabel(AtmTemplateInfo info)
        {
            return string.Format("{0} ({1})", info.Name, info.Contracts);
        }

        // "2C-16S-4P-1R (2)" -> "2C-16S-4P-1R"
        public static string TemplateNameFromChoice(string choice)
        {
            if (string.IsNullOrEmpty(choice) || choice == NoTemplate) return null;
            return Regex.Replace(choice, @"\s\(\d+\)$", string.Empty).Trim();
        }

        #endregion

        #region Private fields

        private const string PanelTag = "PATsButtonsPanel";

        private const int NoButton      = 0;
        private const int CurBarLong    = 1;
        private const int CurBarShort   = 2;
        private const int PrevBarLong   = 3;
        private const int PrevBarShort  = 4;

        private readonly object gate = new object();

        // UI (UI thread only)
        private Grid        chartGrid;
        private StackPanel  panel;
        private Grid          chartTraderGrid;    // set when the panel is inside Chart Trader
        private RowDefinition chartTraderRow;
        private bool          panelRequested;
        private FontFamily    chartTraderFont;
        private TextBlock   noticeText;
        private TextBlock   prevText;
        private TextBlock   curText;
        private Button      curLongButton;
        private Button      curShortButton;
        private Button      prevLongButton;
        private Button      prevShortButton;
        private ChartTrader chartTraderControl;
        private NinjaTrader.Gui.NinjaScript.AtmStrategy.AtmStrategySelector atmSelectorControl;
        private NinjaTrader.Gui.Tools.QuantityUpDown quantityControl;

        // Chart Trader selection, refreshed on the UI thread
        private volatile Account    selectedAccount;
        private volatile Instrument selectedInstrument;
        private volatile NinjaTrader.NinjaScript.AtmStrategy selectedAtm;   // Use Chart Trader mode
        private volatile string     selectedAtmName;
        private volatile int        selectedQuantity;
        private Dictionary<string, AtmTemplateInfo> chartTraderTemplates = new Dictionary<string, AtmTemplateInfo>();

        // ATM templates chosen in settings
        private List<AtmTemplateInfo> atmChoices = new List<AtmTemplateInfo>();   // sorted, smallest first
        private volatile string   setupProblem;

        // Button state
        private volatile bool isLive;
        private volatile int  armedButton;      // button waiting to place its entry
        private volatile int  workingButton;    // button whose entry order is working
        private volatile bool cancelWanted;

        // Current trade (guarded by gate)
        private Order      entryOrder;
        private Account    tradeAccount;
        private Instrument tradeInstrument;
        private bool       tradeIsLong;
        private double     signalBarStop;
        private volatile bool tradeOpen;        // entry has filled (fully or partly)
        private DateTime   fillTime;
        private bool       positionSeen;
        private bool       firstTargetFilled;

        // ATM stop orders seen for the current trade
        private HashSet<Order>            stopsHandled = new HashSet<Order>();
        private Dictionary<Order, double> stopFloor    = new Dictionary<Order, double>();
        private Dictionary<Order, double> stopRequest  = new Dictionary<Order, double>();
        private Dictionary<Order, DateTime> stopRequestTime = new Dictionary<Order, DateTime>();

        private Account watchedAccount;
        private volatile bool instrumentFlat = true;

        // Display
        private string flashMessage;          // UI thread
        private int    flashToken;
        private string shownPrevLine = string.Empty;
        private string shownCurLine  = string.Empty;
        private double markPrice     = double.NaN;    // guarded by gate
        private int    markBar       = -1;
        private SharpDX.Direct2D1.Brush markBrushDx;

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "Signal-bar entry buttons with risk sizing, ATM templates per contract count, and the ATM stop moved to the signal bar.";
                Name                     = "PATsButtons";
                Calculate                = Calculate.OnEachTick;
                IsOverlay                = true;
                DisplayInDataBox         = false;
                DrawOnPricePanel         = true;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = false;

                Sizing                   = SizingMode.RiskBased;
                MaxRisk                  = 0;
                EntryOffsetTicks         = 1;
                StopOffsetTicks          = 1;
                UseLimitWhenPastEntry    = false;

                Atm1 = Atm2 = Atm3 = Atm4 = Atm5 = NoTemplate;

                UseGoodClose             = true;
                GoodCloseFraction        = 0.34;
                UseFiveToOne             = false;

                LockInitialStop          = false;

                ShowBarRange             = false;
                BarRangeFontSize         = 13;

                ShowTargetMark           = false;
                TargetFillPrediction     = 5;
                TargetMarkThickness      = 2;
                TargetMarkWidth          = 1.0;
                TargetMarkBrush          = Brushes.Yellow;

                DetailedLogging          = false;
                ButtonPanelLocation      = PanelLocation.ChartTrader;
            }
            else if (State == State.DataLoaded)
            {
                LoadAtmChoices();
            }
            else if (State == State.Historical)
            {
                // Only once per instance; Historical can fire again on a Playback reload
                if (ChartControl != null && !panelRequested)
                {
                    panelRequested = true;
                    ChartControl.Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            BuildPanel();
                        }
                        catch (Exception ex)
                        {
                            Say("Couldn't build the button panel: " + ex.Message);
                            panel = null;
                        }
                        ReadChartTraderSelection();
                    });
                }
            }
            else if (State == State.Realtime)
            {
                isLive = true;
                RefreshButtons();
            }
            else if (State == State.Terminated)
            {
                StopWatchingAccount();

                if (markBrushDx != null)
                {
                    markBrushDx.Dispose();
                    markBrushDx = null;
                }

                // Never block here. During a Playback jump or workspace switch the UI
                // thread can be waiting on this call, and a blocking Dispatcher.Invoke
                // deadlocks NinjaTrader. Remove directly if we're on the UI thread,
                // otherwise queue it. A newly loaded copy also removes leftovers by tag.
                if (ChartControl != null && panelRequested)
                {
                    try
                    {
                        System.Windows.Threading.Dispatcher ui = ChartControl.Dispatcher;
                        if (ui.CheckAccess())
                            RemovePanel();
                        else if (!ui.HasShutdownStarted)
                            ui.InvokeAsync(() => RemovePanel());
                    }
                    catch (Exception) { }
                }
            }
        }

        #region Setup

        private void LoadAtmChoices()
        {
            string[] chosen = { Atm1, Atm2, Atm3, Atm4, Atm5 };
            setupProblem = null;
            atmChoices.Clear();

            if (Sizing == SizingMode.UseChartTrader)
            {
                Say("Sizing: Use Chart Trader. ATM and quantity come from Chart Trader; Max Risk and the ATM templates are ignored.");
                return;
            }

            Dictionary<int, int> pickByQuantity = new Dictionary<int, int>();   // contracts -> ATM number
            for (int i = 0; i < chosen.Length; i++)
            {
                string name = TemplateNameFromChoice(chosen[i]);
                if (name == null) continue;

                AtmTemplateInfo info = AtmTemplateFiles.Read(name);
                if (info == null)
                {
                    setupProblem = string.Format("ATM {0}: template '{1}' was not found or can't be read.", i + 1, name);
                    continue;
                }

                int other;
                if (pickByQuantity.TryGetValue(info.Contracts, out other))
                {
                    setupProblem = string.Format("ATM {0} and ATM {1} both have {2} contract{3}. Pick templates with different quantities.",
                        other, i + 1, info.Contracts, info.Contracts == 1 ? "" : "s");
                    continue;
                }
                pickByQuantity[info.Contracts] = i + 1;

                if (!info.IsTickMode)
                    Say(string.Format("Note: template '{0}' doesn't use ticks; the 5:1 filter is skipped for it.", name));
                atmChoices.Add(info);
            }

            atmChoices.Sort((a, b) => a.Contracts.CompareTo(b.Contracts));
            foreach (AtmTemplateInfo info in atmChoices)
                Say(string.Format("ATM {0}  ({1} contract{2}, first target {3} ticks)",
                    info.Name, info.Contracts, info.Contracts == 1 ? "" : "s", info.FirstTargetTicks));

            if (atmChoices.Count == 0 && setupProblem == null)
                setupProblem = "Choose at least one ATM template (2. ATM Templates).";

            if (setupProblem != null)
                Say("NOT READY: " + setupProblem);
        }

        // The largest chosen template that fits, or null if even the smallest doesn't.
        private AtmTemplateInfo TemplateFor(int contracts)
        {
            AtmTemplateInfo best = null;
            foreach (AtmTemplateInfo info in atmChoices)
                if (info.Contracts <= contracts)
                    best = info;
            return best;
        }

        #endregion

        #region Buttons and panel (UI thread)

        private void BuildPanel()
        {
            if (ChartControl == null) return;

            bool inChartTrader = ButtonPanelLocation == PanelLocation.ChartTrader && FindChartTraderGrid() != null;
            if (ButtonPanelLocation == PanelLocation.ChartTrader && !inChartTrader)
                Say("Chart Trader not found; buttons placed on the chart instead.");

            panel = new StackPanel { Tag = PanelTag, Orientation = Orientation.Vertical };

            Brush textColor;
            if (inChartTrader)
            {
                // Same look as Chart Trader's own labels ("Account", "Instrument").
                // If no label is found, leave it null so the text inherits Chart Trader's style.
                TextBlock label = FindChartTraderLabel();
                textColor = label != null && label.Foreground != null ? FrozenCopy(label.Foreground) : null;
                chartTraderFont = label != null ? label.FontFamily : null;
                panel.Margin = new Thickness(0, 10, 0, 4);
            }
            else
            {
                // Same text color as the chart's own labels (light on dark charts, dark on light ones)
                textColor = FrozenCopy(ChartControl.Properties != null && ChartControl.Properties.ChartText != null
                    ? ChartControl.Properties.ChartText
                    : Brushes.White);
                panel.HorizontalAlignment = HorizontalAlignment.Right;
                panel.VerticalAlignment   = VerticalAlignment.Bottom;
                panel.Margin              = new Thickness(10, 10, 48, 24);
            }

            noticeText = MakeTextLine(Brushes.OrangeRed, inChartTrader);
            prevText   = MakeTextLine(textColor, inChartTrader);
            curText    = MakeTextLine(textColor, inChartTrader);
            panel.Children.Add(Fit(noticeText, inChartTrader));
            panel.Children.Add(Fit(prevText,   inChartTrader));
            panel.Children.Add(Fit(curText,    inChartTrader));

            // Two equal columns; in Chart Trader they stretch with its width
            Grid buttons = new Grid();
            buttons.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            buttons.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            curLongButton   = MakeButton(buttons, 0, 0, CurBarLong,   inChartTrader);
            curShortButton  = MakeButton(buttons, 1, 0, CurBarShort,  inChartTrader);
            prevLongButton  = MakeButton(buttons, 0, 1, PrevBarLong,  inChartTrader);
            prevShortButton = MakeButton(buttons, 1, 1, PrevBarShort, inChartTrader);
            panel.Children.Add(buttons);

            if (inChartTrader)
            {
                PlaceInChartTrader();
                Detail("Button panel added to Chart Trader");
            }
            else
                PlaceOnChart();

            ApplyButtonLook();
        }

        // A Chart Trader button by its label, e.g. "Buy Mkt". UI thread.
        private Button FindChartTraderButton(string label)
        {
            if (chartTraderControl == null) return null;
            TextBlock text = FindTextBlock(chartTraderControl, label, 0);
            DependencyObject node = text;
            for (int i = 0; node != null && i < 8; i++)
            {
                Button b = node as Button;
                if (b != null) return b;
                node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
            }
            return null;
        }

        // Chart Trader's "Account" label (falls back to "Instrument"). UI thread.
        private TextBlock FindChartTraderLabel()
        {
            if (chartTraderControl == null) return null;
            TextBlock found = FindTextBlock(chartTraderControl, "Account", 0);
            return found ?? FindTextBlock(chartTraderControl, "Instrument", 0);
        }

        private static TextBlock FindTextBlock(DependencyObject node, string text, int depth)
        {
            if (node == null || depth > 12) return null;
            TextBlock tb = node as TextBlock;
            if (tb != null && tb.Text != null && tb.Text.Trim() == text) return tb;

            // VisualTreeHelper only accepts visuals; anything else throws
            if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
            {
                int count = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < count; i++)
                {
                    TextBlock hit = FindTextBlock(VisualTreeHelper.GetChild(node, i), text, depth + 1);
                    if (hit != null) return hit;
                }
            }
            foreach (object child in LogicalTreeHelper.GetChildren(node))
            {
                DependencyObject d = child as DependencyObject;
                if (d == null || d is Visual || d is System.Windows.Media.Media3D.Visual3D) continue;    // searched above
                TextBlock hit = FindTextBlock(d, text, depth + 1);
                if (hit != null) return hit;
            }
            return null;
        }

        private static Brush FrozenCopy(Brush brush)
        {
            Brush copy = brush.CloneCurrentValue();
            if (copy.CanFreeze) copy.Freeze();
            return copy;
        }

        private void PlaceOnChart()
        {
            chartGrid = ChartControl.Parent as Grid;
            if (chartGrid == null)
            {
                Window window = Window.GetWindow(ChartControl);
                if (window != null)
                    chartGrid = window.FindName("ChartWindowGrid") as Grid;
            }
            if (chartGrid == null)
            {
                Say("Couldn't find the chart grid; buttons not added.");
                panel = null;
                return;
            }

            // Remove panels left behind by an earlier load
            for (int i = chartGrid.Children.Count - 1; i >= 0; i--)
            {
                FrameworkElement old = chartGrid.Children[i] as FrameworkElement;
                if (old != null && PanelTag.Equals(old.Tag))
                {
                    chartGrid.Children.RemoveAt(i);
                    Detail("Removed a leftover button panel");
                }
            }
            chartGrid.Children.Add(panel);
        }

        private Grid FindChartTraderGrid()
        {
            if (chartTraderControl == null)
            {
                Window window = Window.GetWindow(ChartControl.Parent);
                if (window != null)
                    chartTraderControl = window.FindFirst("ChartWindowChartTraderControl") as ChartTrader;
            }
            return chartTraderControl != null ? chartTraderControl.Content as Grid : null;
        }

        // Inserts a row into Chart Trader's layout just above Chart Jot (or at the
        // bottom if Chart Jot isn't there), shifting the rows below it down by one.
        private void PlaceInChartTrader()
        {
            Grid grid = FindChartTraderGrid();

            // Remove a panel (and its row) left behind by an earlier load
            for (int i = grid.Children.Count - 1; i >= 0; i--)
            {
                FrameworkElement old = grid.Children[i] as FrameworkElement;
                if (old != null && PanelTag.Equals(old.Tag))
                {
                    int oldRow = Grid.GetRow(old);
                    grid.Children.RemoveAt(i);
                    if (oldRow < grid.RowDefinitions.Count)
                    {
                        grid.RowDefinitions.RemoveAt(oldRow);
                        ShiftRows(grid, oldRow, -1);
                    }
                    Detail("Removed a leftover button panel from Chart Trader");
                }
            }

            int insertAt = FindChartJotRow(grid);
            if (insertAt < 0) insertAt = grid.RowDefinitions.Count;

            ShiftRows(grid, insertAt, +1);
            chartTraderRow = new RowDefinition { Height = GridLength.Auto };
            grid.RowDefinitions.Insert(insertAt, chartTraderRow);

            Grid.SetRow(panel, insertAt);
            Grid.SetColumnSpan(panel, Math.Max(1, grid.ColumnDefinitions.Count));
            grid.Children.Add(panel);
            chartTraderGrid = grid;
        }

        // Moves every child at or below fromRow by delta rows (and stretches spans across it).
        private void ShiftRows(Grid grid, int fromRow, int delta)
        {
            foreach (UIElement child in grid.Children)
            {
                if (child == panel) continue;
                int row  = Grid.GetRow(child);
                int span = Grid.GetRowSpan(child);
                if (row >= fromRow)
                    Grid.SetRow(child, Math.Max(0, row + delta));
                else if (row + span > fromRow && span + delta >= 1)
                    Grid.SetRowSpan(child, span + delta);
            }
        }

        // Row of the element that holds the "Chart Jot" button, or -1.
        private static int FindChartJotRow(Grid grid)
        {
            foreach (UIElement child in grid.Children)
                if (HoldsChartJot(child, 0))
                    return Grid.GetRow(child);
            return -1;
        }

        private static bool HoldsChartJot(object node, int depth)
        {
            if (node == null || depth > 6) return false;

            ContentControl cc = node as ContentControl;
            if (cc != null && cc.Content is string && ((string)cc.Content).Trim() == "Chart Jot") return true;

            TextBlock tb = node as TextBlock;
            if (tb != null && tb.Text != null && tb.Text.Trim() == "Chart Jot") return true;

            DependencyObject d = node as DependencyObject;
            if (d == null) return false;
            foreach (object child in LogicalTreeHelper.GetChildren(d))
                if (HoldsChartJot(child, depth + 1))
                    return true;
            return false;
        }

        private void RemovePanel()
        {
            try
            {
                if (chartTraderGrid != null && panel != null)
                {
                    chartTraderGrid.Children.Remove(panel);
                    int index = chartTraderRow != null ? chartTraderGrid.RowDefinitions.IndexOf(chartTraderRow) : -1;
                    if (index >= 0)
                    {
                        chartTraderGrid.RowDefinitions.RemoveAt(index);
                        ShiftRows(chartTraderGrid, index, -1);
                    }
                }
                else if (chartGrid != null && panel != null && chartGrid.Children.Contains(panel))
                    chartGrid.Children.Remove(panel);
            }
            catch (Exception) { }

            panel           = null;
            chartTraderGrid = null;
            chartTraderRow  = null;
        }

        // In Chart Trader, text shrinks to fit when Chart Trader is narrow.
        private static UIElement Fit(TextBlock text, bool inChartTrader)
        {
            if (!inChartTrader) return text;
            return new Viewbox
            {
                Child               = text,
                Stretch             = System.Windows.Media.Stretch.Uniform,
                StretchDirection    = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left
            };
        }

        private TextBlock MakeTextLine(Brush color, bool inChartTrader)
        {
            TextBlock line = new TextBlock
            {
                Background          = Brushes.Transparent,
                FontSize            = BarRangeFontSize,
                Padding             = new Thickness(inChartTrader ? 3 : 6, 1, 6, 1),
                Margin              = new Thickness(3, 0, 3, 2),
                HorizontalAlignment = inChartTrader ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                Visibility          = Visibility.Collapsed
            };
            if (color != null) line.Foreground = color;     // null = inherit Chart Trader's color

            // On the chart: Consolas. In Chart Trader: Chart Trader's own font.
            if (!inChartTrader)
                line.FontFamily = new FontFamily("Consolas");
            else if (chartTraderFont != null)
                line.FontFamily = chartTraderFont;
            return line;
        }

        private Button MakeButton(Grid host, int row, int column, int id, bool inChartTrader)
        {
            Button b = new Button
            {
                Background          = Brushes.Gray,
                Foreground          = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment   = VerticalAlignment.Center
            };
            if (inChartTrader)
            {
                // Fills half of Chart Trader's width; the label shrinks if it gets narrow.
                // Text size and height copy Chart Trader's own "Buy Mkt" button.
                Button native = FindChartTraderButton("Buy Mkt");
                b.Margin    = new Thickness(2);
                b.MinHeight = native != null && native.ActualHeight > 0 ? native.ActualHeight : 30;
                b.Padding   = new Thickness(4, 2, 4, 2);
                if (native != null) b.FontSize = native.FontSize;
                b.Content   = new Viewbox
                {
                    Child            = new TextBlock { Text = IdleLabel(id) },   // inherits the button's font
                    Stretch          = System.Windows.Media.Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly
                };
            }
            else
            {
                b.Margin    = new Thickness(3);
                b.FontSize  = 14;
                b.Width     = 155;
                b.MinHeight = 40;
                b.Content   = IdleLabel(id);
            }
            b.Click += (sender, args) => OnEntryButton(id);
            Grid.SetRow(b, row);
            Grid.SetColumn(b, column);
            host.Children.Add(b);
            return b;
        }

        private static string IdleLabel(int id)
        {
            switch (id)
            {
                case CurBarLong:   return "Cur Bar Long";
                case CurBarShort:  return "Cur Bar Short";
                case PrevBarLong:  return "Prev Bar Long";
                case PrevBarShort: return "Prev Bar Short";
            }
            return string.Empty;
        }

        private static bool IsLongButton(int id) { return id == CurBarLong || id == PrevBarLong; }
        private static bool IsPrevButton(int id) { return id == PrevBarLong || id == PrevBarShort; }

        private bool ButtonsLocked
        {
            get { return tradeOpen || !instrumentFlat; }
        }

        private void OnEntryButton(int id)
        {
            ReadChartTraderSelection();

            if (!isLive)
            {
                Say("Click ignored: not ready yet (still loading).");
                return;
            }
            if (setupProblem != null)
            {
                Say("Click ignored: " + setupProblem);
                return;
            }

            // An entry order is working: any button cancels it
            if (workingButton != NoButton && !tradeOpen)
            {
                cancelWanted = true;
                TriggerCustomEvent(o => RunCycle(true), null);
                return;
            }

            if (ButtonsLocked)
            {
                FlashLocked(ButtonFor(id));
                return;
            }

            if (armedButton == id)
            {
                armedButton = NoButton;
                Say(IdleLabel(id) + " disarmed");
            }
            else
            {
                armedButton = id;
                Detail(IdleLabel(id) + " armed");
                if (IsPrevButton(id))
                    TriggerCustomEvent(o => RunCycle(true), null);
            }
            ApplyButtonLook();
        }

        private Button ButtonFor(int id)
        {
            switch (id)
            {
                case CurBarLong:   return curLongButton;
                case CurBarShort:  return curShortButton;
                case PrevBarLong:  return prevLongButton;
                case PrevBarShort: return prevShortButton;
            }
            return null;
        }

        // Shows a short message above the buttons for a few seconds. UI thread.
        private async void FlashNotice(string message)
        {
            int token = ++flashToken;
            flashMessage = message;
            ApplyButtonLook();
            await Task.Delay(4000);
            if (token != flashToken) return;     // a newer message replaced this one
            flashMessage = null;
            ApplyButtonLook();
        }

        private void FlashNoticeFromAnyThread(string message)
        {
            if (ChartControl == null) return;
            ChartControl.Dispatcher.InvokeAsync(() => FlashNotice(message));
        }

        private async void FlashLocked(Button b)
        {
            if (b == null) return;
            b.Background = Brushes.OrangeRed;
            await Task.Delay(300);
            ApplyButtonLook();
        }

        private void RefreshButtons()
        {
            if (ChartControl == null) return;
            ChartControl.Dispatcher.InvokeAsync(() => ApplyButtonLook());
        }

        // Colors: Gray = not ready, skin default = idle, Green = armed, Orange = this button's
        // entry is working, DimGray = another button's entry is working, Red = trade open.
        private void ApplyButtonLook()
        {
            if (panel == null) return;

            int  working = workingButton;
            int  armed   = armedButton;
            bool locked  = ButtonsLocked;

            for (int id = CurBarLong; id <= PrevBarShort; id++)
            {
                Button b = ButtonFor(id);
                if (b == null) continue;

                string label = IdleLabel(id);
                Brush  color;

                if (!isLive || setupProblem != null)
                    color = Brushes.Gray;
                else if (locked)
                    color = Brushes.Red;
                else if (working != NoButton)
                {
                    color = id == working ? Brushes.DarkOrange : Brushes.DimGray;
                    if (id == working) label = "Cancel Order";
                }
                else if (id == armed)
                {
                    color = Brushes.Green;
                    label = IsLongButton(id) ? "Cancel Long" : "Cancel Short";
                }
                else
                    color = null;      // idle: NinjaTrader's own button style, like Chart Trader's

                SetLabel(b, label);
                if (color == null)
                {
                    // Follow the skin (whatever colors the user's NinjaTrader theme uses)
                    b.ClearValue(Control.BackgroundProperty);
                    b.ClearValue(Control.ForegroundProperty);
                }
                else
                {
                    b.Background = color;
                    b.Foreground = Brushes.White;
                }
            }

            string notice = setupProblem ?? flashMessage;
            noticeText.Text       = notice ?? string.Empty;
            noticeText.Visibility = notice != null ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void SetLabel(Button b, string label)
        {
            Viewbox box = b.Content as Viewbox;
            TextBlock text = box != null ? box.Child as TextBlock : null;
            if (text != null)
                text.Text = label;
            else
                b.Content = label;
        }

        private void ShowPreviewLines(string prevLine, string curLine)
        {
            if (panel == null) return;
            Visibility v = ShowBarRange ? Visibility.Visible : Visibility.Collapsed;
            prevText.Text       = prevLine;
            curText.Text        = curLine;
            prevText.Visibility = v;
            curText.Visibility  = v;
        }

        // Chart Trader's account and instrument. UI thread only.
        private void ReadChartTraderSelection()
        {
            if (ChartControl == null) return;
            try
            {
                if (chartTraderControl == null)
                {
                    Window window = Window.GetWindow(ChartControl.Parent);
                    if (window != null)
                        chartTraderControl = window.FindFirst("ChartWindowChartTraderControl") as ChartTrader;
                }
                if (chartTraderControl != null)
                {
                    selectedAccount    = chartTraderControl.Account;
                    selectedInstrument = chartTraderControl.Instrument;
                }
                if (Sizing == SizingMode.UseChartTrader)
                    ReadChartTraderAtmAndQuantity();
            }
            catch (Exception) { }
        }

        // Chart Trader's ATM dropdown and quantity box. UI thread only.
        private void ReadChartTraderAtmAndQuantity()
        {
            if (atmSelectorControl == null || quantityControl == null)
            {
                Window window = Window.GetWindow(ChartControl.Parent);
                if (window == null) return;
                if (atmSelectorControl == null)
                    atmSelectorControl = window.FindFirst("ChartTraderControlATMStrategySelector") as NinjaTrader.Gui.NinjaScript.AtmStrategy.AtmStrategySelector;
                if (quantityControl == null)
                    quantityControl = window.FindFirst("ChartTraderControlQuantitySelector") as NinjaTrader.Gui.Tools.QuantityUpDown;
            }

            NinjaTrader.Gui.NinjaScript.AtmStrategy.AtmStrategySelector selector = atmSelectorControl;
            if (selector != null)
            {
                selectedAtm = selector.SelectedAtmStrategy;

                // SelectedAtmStrategy.Name is always "AtmStrategy"; the template name is in the text
                string text = selector.Text ?? string.Empty;
                int    at   = text.IndexOf("Template:");
                selectedAtmName = at >= 0 ? text.Substring(at + "Template:".Length).Trim() : string.Empty;
            }

            if (quantityControl != null)
                selectedQuantity = quantityControl.Value;
        }

        #endregion

        #region Bar processing (data thread)

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 2 || State != State.Realtime) return;
            RunCycle(false);
        }

        // Runs on each tick, and right away after a button click (via TriggerCustomEvent).
        private void RunCycle(bool fromClick)
        {
            try
            {
                if (cancelWanted)
                {
                    cancelWanted = false;
                    CancelWorkingEntry();
                }

                CheckPosition();

                int armed = armedButton;
                if (armed != NoButton && workingButton == NoButton && !tradeOpen)
                {
                    // Prev Bar: the bar that already closed, right away.
                    // Cur Bar: wait for the bar that was printing to close.
                    if (IsPrevButton(armed) || (!fromClick && IsFirstTickOfBar))
                        PlaceEntry(armed);
                }

                UpdateDisplay();
            }
            catch (Exception ex)
            {
                Say("Error: " + ex.Message);
            }
        }

        private Instrument TradeInstrumentNow()
        {
            Instrument instr = selectedInstrument;
            return instr ?? Instrument;
        }

        private void PlaceEntry(int id)
        {
            armedButton = NoButton;
            bool   isLong = IsLongButton(id);
            string label  = IdleLabel(id);

            Account    account = selectedAccount;
            Instrument instr   = TradeInstrumentNow();

            if (account == null)
            {
                Skip(label, "no Chart Trader account found. Open Chart Trader on this chart.");
                return;
            }
            if (!IsFlat(account, instr))
            {
                Skip(label, "a position is already open on " + instr.FullName + ".");
                return;
            }

            double high  = High[1];
            double low   = Low[1];
            double close = Close[1];
            Detail(string.Format("{0}: signal bar H={1} L={2} C={3}", label, high, low, close));

            if (UseGoodClose && !PassesGoodClose(isLong, high, low, close))
            {
                Skip(label, "signal bar failed the good close filter.");
                FlashNoticeFromAnyThread("Failed Good Close Fraction");
                return;
            }

            SizeResult size = SizeTrade(high, low, instr, true);
            if (size.SkipReason != null)
            {
                Skip(label, size.SkipReason);
                return;
            }

            MasterInstrument mi    = instr.MasterInstrument;
            double           tick  = mi.TickSize;
            double entryPrice = mi.RoundToTickSize(isLong ? high + EntryOffsetTicks * tick : low  - EntryOffsetTicks * tick);
            double stopPrice  = mi.RoundToTickSize(isLong ? low  - StopOffsetTicks  * tick : high + StopOffsetTicks  * tick);

            // A stop entry has to be beyond the market or it gets rejected.
            // If price is already past the entry: skip, or use a GTC limit at the entry.
            double bid, ask;
            ReadBidAsk(instr, out bid, out ask);
            bool pastEntry = isLong ? ask > 0 && entryPrice <= ask
                                    : bid > 0 && entryPrice >= bid;
            if (pastEntry && !UseLimitWhenPastEntry)
            {
                Skip(label, string.Format("price ({0}) is already {1} the entry ({2}).",
                    isLong ? ask : bid, isLong ? "at or above" : "at or below", entryPrice));
                return;
            }
            OrderType   entryType  = pastEntry ? OrderType.Limit   : OrderType.StopMarket;
            TimeInForce entryTif   = pastEntry ? TimeInForce.Gtc   : TimeInForce.Day;
            double      limitPrice = pastEntry ? entryPrice        : 0;
            double      stopEntry  = pastEntry ? 0                 : entryPrice;

            if (Sizing == SizingMode.UseChartTrader)
            {
                if (size.Template == null && UseFiveToOne)
                    Say("Note: couldn't read the Chart Trader ATM's template file; the 5:1 filter was skipped.");
                else if (size.Template != null && size.Template.Contracts != size.Contracts)
                    Say(string.Format("Note: Chart Trader quantity {0} differs from ATM {1}'s {2} contracts.",
                        size.Contracts, size.AtmName, size.Template.Contracts));
            }

            try
            {
                WatchAccount(account);

                Order order = account.CreateOrder(
                    instr,
                    isLong ? OrderAction.Buy : OrderAction.Sell,
                    entryType,
                    OrderEntry.Manual,
                    entryTif,
                    size.Contracts,
                    limitPrice,
                    stopEntry,
                    string.Empty,
                    "Entry",                    // must be "Entry" for the ATM to attach
                    NinjaTrader.Core.Globals.MaxDate,
                    null);

                lock (gate)
                {
                    entryOrder        = order;
                    tradeAccount      = account;
                    tradeInstrument   = instr;
                    tradeIsLong       = isLong;
                    signalBarStop     = stopPrice;
                    tradeOpen         = false;
                    positionSeen      = false;
                    firstTargetFilled = false;
                    stopsHandled.Clear();
                    stopFloor.Clear();
                    stopRequest.Clear();
                    stopRequestTime.Clear();
                }
                workingButton = id;

                if (size.AtmObject != null)
                    NinjaTrader.NinjaScript.AtmStrategy.StartAtmStrategy(size.AtmObject, order);
                else
                    NinjaTrader.NinjaScript.AtmStrategy.StartAtmStrategy(size.AtmName, order);
                account.Submit(new[] { order });

                Say(string.Format("{0}: {1} {2} {3} at {4}, stop {5} ({6} ticks), ATM {7}, {8}",
                    label, isLong ? "BUY" : "SELL", size.Contracts,
                    pastEntry ? "LIMIT entry (GTC, price already past the entry)" : "stop entry",
                    entryPrice, stopPrice,
                    size.RiskTicks, size.AtmName, instr.FullName));
            }
            catch (Exception ex)
            {
                lock (gate) entryOrder = null;
                workingButton = NoButton;
                Say(label + ": order failed: " + ex.Message);
            }
            RefreshButtons();
        }

        private void Skip(string label, string reason)
        {
            Say(label + " skipped: " + reason);
            RefreshButtons();
        }

        private void CancelWorkingEntry()
        {
            Order   order;
            Account account;
            lock (gate)
            {
                order   = entryOrder;
                account = tradeAccount;
            }
            if (order == null || account == null) return;

            if (order.Filled > 0)
            {
                Say("Entry already partly filled; not cancelled.");
                return;
            }
            if (order.OrderState == OrderState.Filled || order.OrderState == OrderState.Cancelled
                || order.OrderState == OrderState.Rejected)
                return;

            account.Cancel(new[] { order });
            Say("Cancel sent for the working entry.");
        }

        // Unlocks the buttons once the account is flat on the traded instrument.
        private void CheckPosition()
        {
            Account    account;
            Instrument instr;
            bool       open;
            lock (gate)
            {
                open    = tradeOpen;
                account = open ? tradeAccount    : selectedAccount;
                instr   = open ? tradeInstrument : TradeInstrumentNow();
            }

            bool flat = IsFlat(account, instr);
            bool changed = false;

            if (open)
            {
                lock (gate)
                {
                    if (!flat)
                        positionSeen = true;
                    else if (positionSeen || (DateTime.Now - fillTime).TotalSeconds > 5)
                    {
                        tradeOpen     = false;
                        entryOrder    = null;
                        workingButton = NoButton;
                        stopsHandled.Clear();
                        stopFloor.Clear();
                        stopRequest.Clear();
                        stopRequestTime.Clear();
                        changed = true;
                    }
                }
                if (changed) Say("Flat. Buttons unlocked.");
            }

            if (flat != instrumentFlat)
            {
                instrumentFlat = flat;
                changed = true;
            }
            if (changed) RefreshButtons();
        }

        private static bool IsFlat(Account account, Instrument instr)
        {
            if (account == null || instr == null) return true;
            lock (account.Positions)
            {
                foreach (Position p in account.Positions)
                    if (p.Instrument != null && p.Instrument.FullName == instr.FullName)
                        return p.MarketPosition == MarketPosition.Flat;
            }
            return true;
        }

        private void ReadBidAsk(Instrument instr, out double bid, out double ask)
        {
            bid = 0;
            ask = 0;
            if (instr.FullName == Instrument.FullName)
            {
                bid = GetCurrentBid();
                ask = GetCurrentAsk();
                return;
            }
            MarketData md = instr.MarketData;
            if (md != null)
            {
                if (md.Bid != null) bid = md.Bid.Price;
                if (md.Ask != null) ask = md.Ask.Price;
            }
        }

        #endregion

        #region Sizing and filters

        private class SizeResult
        {
            public int             RangeTicks;
            public int             RiskTicks;
            public AtmTemplateInfo Template;       // template file info (null if unknown)
            public int             Contracts;
            public string          AtmName;
            public NinjaTrader.NinjaScript.AtmStrategy AtmObject;   // Use Chart Trader mode only
            public double          RiskDollars;    // for all contracts
            public string          SkipReason;
            public string          ShortReason;    // for the preview line
        }

        // Same for long and short: risk = bar range + entry offset + stop offset.
        private SizeResult SizeTrade(double high, double low, Instrument instr, bool logDetail)
        {
            SizeResult r     = new SizeResult();
            double     tick  = instr.MasterInstrument.TickSize;
            double     value = instr.MasterInstrument.PointValue;

            r.RangeTicks = (int)Math.Round((high - low) / tick);
            r.RiskTicks  = r.RangeTicks + EntryOffsetTicks + StopOffsetTicks;

            if (Sizing == SizingMode.UseChartTrader)
                return SizeFromChartTrader(r, tick * value, logDetail);

            if (MaxRisk <= 0)
            {
                r.SkipReason  = "Max Risk ($) is not set.";
                r.ShortReason = "set Max Risk";
                return r;
            }

            double riskPerContract = r.RiskTicks * tick * value;
            int    contracts       = riskPerContract > 0 ? (int)Math.Floor(MaxRisk / riskPerContract + 1e-9) : 0;
            if (logDetail) Detail(string.Format("Sizing: risk {0} ticks = ${1:0.00} per contract, Max Risk ${2:0.00} -> {3} contracts",
                r.RiskTicks, riskPerContract, MaxRisk, contracts));

            if (contracts < 1)
            {
                r.SkipReason  = string.Format("1 contract would risk ${0:0.00}, more than Max Risk ${1:0.00}.", riskPerContract, MaxRisk);
                r.ShortReason = "risk";
                return r;
            }

            r.Template = TemplateFor(contracts);
            if (r.Template == null)
            {
                r.SkipReason  = atmChoices.Count == 0
                    ? "no ATM template chosen."
                    : string.Format("risk allows {0} contract{1}; the smallest ATM template is {2}.",
                        contracts, contracts == 1 ? "" : "s", atmChoices[0].Contracts);
                r.ShortReason = atmChoices.Count == 0 ? "no ATM" : "risk";
                return r;
            }
            r.Contracts   = r.Template.Contracts;
            r.AtmName     = r.Template.Name;
            r.RiskDollars = riskPerContract * r.Contracts;

            CheckFiveToOne(r);
            return r;
        }

        // Use Chart Trader mode: no sizing, ATM and quantity as selected in Chart Trader.
        private SizeResult SizeFromChartTrader(SizeResult r, double tickValue, bool logDetail)
        {
            NinjaTrader.NinjaScript.AtmStrategy atm = selectedAtm;
            string name = selectedAtmName;
            int    qty  = selectedQuantity;

            if (atm == null)
            {
                r.SkipReason  = "no ATM strategy selected in Chart Trader.";
                r.ShortReason = "no ATM";
                return r;
            }
            if (qty < 1)
            {
                r.SkipReason  = "Chart Trader quantity is 0.";
                r.ShortReason = "qty 0";
                return r;
            }

            r.AtmObject   = atm;
            r.AtmName     = string.IsNullOrEmpty(name) ? "(Chart Trader ATM)" : name;
            r.Contracts   = qty;
            r.RiskDollars = r.RiskTicks * tickValue * qty;
            r.Template    = ChartTraderTemplateInfo(name);

            if (logDetail)
                Detail(string.Format("Sizing (Chart Trader): {0} contracts, risk {1} ticks = ${2:0.00} total, ATM {3}",
                    qty, r.RiskTicks, r.RiskDollars, r.AtmName));

            CheckFiveToOne(r);
            return r;
        }

        // Template file info for the ATM picked in Chart Trader, read once per name.
        private AtmTemplateInfo ChartTraderTemplateInfo(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            AtmTemplateInfo info;
            if (!chartTraderTemplates.TryGetValue(name, out info))
            {
                info = AtmTemplateFiles.Read(name);
                chartTraderTemplates[name] = info;
            }
            return info;
        }

        private void CheckFiveToOne(SizeResult r)
        {
            if (!UseFiveToOne || r.Template == null || !r.Template.IsTickMode) return;
            int firstTarget = r.Template.FirstTargetTicks;
            if (firstTarget > 0 && r.RiskTicks > 5 * firstTarget)
            {
                r.SkipReason  = string.Format("stop {0} ticks is more than 5 x the {1}-tick first target.", r.RiskTicks, firstTarget);
                r.ShortReason = "5:1";
            }
        }

        // Long: close in the top GoodCloseFraction of the bar. Short: in the bottom fraction.
        private bool PassesGoodClose(bool isLong, double high, double low, double close)
        {
            double range = high - low;
            if (range <= 0) return false;
            double whereClosed = (close - low) / range;      // 0 = at the low, 1 = at the high
            return isLong ? whereClosed >= 1.0 - GoodCloseFraction
                          : whereClosed <= GoodCloseFraction;
        }

        #endregion

        #region Display (bar range lines and target mark)

        private void UpdateDisplay()
        {
            Instrument instr = TradeInstrumentNow();

            if (ShowBarRange && panel != null)
            {
                string prevLine = "Prev: " + PreviewText(High[1], Low[1], instr);
                string curLine  = "Cur:  " + PreviewText(High[0], Low[0], instr);
                if (prevLine != shownPrevLine || curLine != shownCurLine)
                {
                    shownPrevLine = prevLine;
                    shownCurLine  = curLine;
                    ChartControl.Dispatcher.InvokeAsync(() => ShowPreviewLines(prevLine, curLine));
                }
            }

            double mark = double.NaN;
            if (ShowTargetMark)
            {
                double tick     = instr.MasterInstrument.TickSize;
                double distance = (EntryOffsetTicks + TargetFillPrediction) * tick;
                if (Close[0] > Open[0])
                    mark = instr.MasterInstrument.RoundToTickSize(High[0] + distance);
                else if (Close[0] < Open[0])
                    mark = instr.MasterInstrument.RoundToTickSize(Low[0] - distance);
            }
            lock (gate)
            {
                markPrice = mark;
                markBar   = CurrentBar;
            }
        }

        // "14 → 16t · 2c" or "14 → 22t · skip (5:1)"
        private string PreviewText(double high, double low, Instrument instr)
        {
            SizeResult size = SizeTrade(high, low, instr, false);
            string head = string.Format("{0} → {1}t · ", size.RangeTicks, size.RiskTicks);
            if (size.SkipReason != null)
                return head + "skip (" + size.ShortReason + ")";
            if (Sizing == SizingMode.UseChartTrader)
                return head + string.Format(CultureInfo.InvariantCulture, "${0:0}", size.RiskDollars);
            return head + size.Contracts + "c";
        }

        public override void OnRenderTargetChanged()
        {
            if (markBrushDx != null)
            {
                markBrushDx.Dispose();
                markBrushDx = null;
            }
            if (RenderTarget != null && TargetMarkBrush != null)
                markBrushDx = TargetMarkBrush.ToDxBrush(RenderTarget);
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            ReadChartTraderSelection();

            if (!ShowTargetMark || !isLive || markBrushDx == null || ChartBars == null) return;

            double price;
            int    bar;
            lock (gate)
            {
                price = markPrice;
                bar   = markBar;
            }
            if (double.IsNaN(price) || bar < 0) return;

            float x     = chartControl.GetXByBarIndex(ChartBars, bar);
            float width = (float)Math.Max(2.0, chartControl.GetBarPaintWidth(ChartBars) * TargetMarkWidth);
            float y     = chartScale.GetYByValue(price);

            RenderTarget.DrawLine(
                new SharpDX.Vector2(x - width / 2f, y),
                new SharpDX.Vector2(x + width / 2f, y),
                markBrushDx,
                TargetMarkThickness);
        }

        #endregion

        #region Account events (entry fill, ATM stops)

        private void WatchAccount(Account account)
        {
            if (watchedAccount == account) return;
            StopWatchingAccount();
            watchedAccount = account;
            watchedAccount.OrderUpdate += OnAccountOrderUpdate;
        }

        private void StopWatchingAccount()
        {
            if (watchedAccount != null)
                watchedAccount.OrderUpdate -= OnAccountOrderUpdate;
            watchedAccount = null;
        }

        private void OnAccountOrderUpdate(object sender, OrderEventArgs e)
        {
            Order order = e.Order;
            if (order == null) return;

            bool   refresh   = false;
            double movePrice = double.NaN;
            Account account  = null;

            lock (gate)
            {
                if (entryOrder != null && order == entryOrder)
                {
                    if (e.OrderState == OrderState.Filled || e.OrderState == OrderState.PartFilled)
                    {
                        if (!tradeOpen)
                        {
                            tradeOpen     = true;
                            fillTime      = DateTime.Now;
                            positionSeen  = false;
                            workingButton = NoButton;
                            refresh       = true;
                            Say(string.Format("Entry filled at {0}. Buttons locked.", e.AverageFillPrice));
                        }
                    }
                    else if (e.OrderState == OrderState.Cancelled || e.OrderState == OrderState.Rejected)
                    {
                        if (order.Filled == 0)
                        {
                            entryOrder    = null;
                            workingButton = NoButton;
                            refresh       = true;
                            Say("Entry " + e.OrderState.ToString().ToLower() + ". Buttons reset.");
                        }
                    }
                }
                else if (tradeOpen && IsPartOfTrade(order))
                {
                    if (order.Name.StartsWith("Stop"))
                    {
                        movePrice = DecideStopPrice(order, e.OrderState, e.StopPrice);
                        account   = tradeAccount;
                    }
                    else if (order.Name == "Target1" && e.OrderState == OrderState.Filled && !firstTargetFilled)
                    {
                        firstTargetFilled = true;
                        if (LockInitialStop) Say("Target 1 filled. Stop lock released.");
                    }
                }
            }

            // Change the order outside the lock
            if (!double.IsNaN(movePrice) && account != null)
            {
                try
                {
                    order.StopPriceChanged = movePrice;
                    account.Change(new[] { order });
                }
                catch (Exception ex)
                {
                    Say("Stop change failed: " + ex.Message);
                }
            }

            if (refresh) RefreshButtons();
        }

        private bool IsPartOfTrade(Order order)
        {
            return tradeAccount != null && tradeInstrument != null
                && order.Account == tradeAccount
                && order.Instrument != null
                && order.Instrument.FullName == tradeInstrument.FullName;
        }

        // Called under gate. Returns the price to change the stop to, or NaN for no change.
        private double DecideStopPrice(Order stop, OrderState state, double price)
        {
            if (state != OrderState.Accepted && state != OrderState.Working) return double.NaN;

            double tick = tradeInstrument.MasterInstrument.TickSize;
            double half = tick / 2.0;

            // Waiting for our own change to show up. If it hasn't after 3 seconds
            // (e.g. NinjaTrader rejected it), stop waiting.
            double requested;
            if (stopRequest.TryGetValue(stop, out requested))
            {
                bool arrived = Math.Abs(price - requested) < half;
                bool gaveUp  = (DateTime.Now - stopRequestTime[stop]).TotalSeconds > 3;
                if (!arrived && !gaveUp) return double.NaN;
                stopRequest.Remove(stop);
                stopRequestTime.Remove(stop);
                if (gaveUp && !arrived)
                    Say(string.Format("{0} change to {1} didn't go through; it's at {2}.", stop.Name, requested, price));
            }

            // First sighting: move the ATM's stop to the signal-bar stop
            if (!stopsHandled.Contains(stop))
            {
                stopsHandled.Add(stop);
                stopFloor[stop] = signalBarStop;

                if (Math.Abs(price - signalBarStop) < half) return double.NaN;

                bool loosens = tradeIsLong ? signalBarStop < price : signalBarStop > price;
                if (loosens)
                    Say(string.Format("Note: the ATM's {0} at {1} is tighter than the signal-bar stop; moving it out to {2}. Use a template with a wider stop.",
                        stop.Name, price, signalBarStop));

                if (!StopIsValid(signalBarStop))
                {
                    Say(string.Format("Couldn't move {0} to {1}: price is already past it. The ATM's stop at {2} stays.",
                        stop.Name, signalBarStop, price));
                    stopFloor[stop] = price;
                    return double.NaN;
                }

                stopRequest[stop]     = signalBarStop;
                stopRequestTime[stop] = DateTime.Now;
                Say(string.Format("{0} moved from {1} to the signal-bar stop {2}.", stop.Name, price, signalBarStop));
                return signalBarStop;
            }

            // Lock Initial Stop: until Target 1 fills, tighter is kept, looser snaps back
            if (!LockInitialStop || firstTargetFilled) return double.NaN;

            double floor;
            if (!stopFloor.TryGetValue(stop, out floor)) return double.NaN;

            bool tighter = tradeIsLong ? price > floor + half : price < floor - half;
            bool looser  = tradeIsLong ? price < floor - half : price > floor + half;

            if (tighter)
            {
                stopFloor[stop] = price;
                Say(string.Format("{0} tightened to {1}; that's the new limit.", stop.Name, price));
                return double.NaN;
            }
            if (looser)
            {
                if (!StopIsValid(floor))
                {
                    Say(string.Format("{0} lock: couldn't snap back to {1}; price is already past it.", stop.Name, floor));
                    return double.NaN;
                }
                stopRequest[stop]     = floor;
                stopRequestTime[stop] = DateTime.Now;
                Say(string.Format("{0} lock: move to {1} snapped back to {2}.", stop.Name, price, floor));
                return floor;
            }
            return double.NaN;
        }

        // A sell stop (long trade) must be below the bid; a buy stop (short trade) above the ask.
        private bool StopIsValid(double stopPrice)
        {
            double bid, ask;
            ReadBidAsk(tradeInstrument, out bid, out ask);
            if (tradeIsLong)  return bid <= 0 || stopPrice < bid;
            return ask <= 0 || stopPrice > ask;
        }

        #endregion

        #region Logging

        private void Say(string message)
        {
            Print("PATsButtons: " + message);
        }

        private void Detail(string message)
        {
            if (DetailedLogging)
                Print("PATsButtons [detail]: " + message);
        }

        #endregion

        #region Properties

        // 1. Risk
        [RefreshProperties(RefreshProperties.All)]
        [Display(Name = "Sizing", Order = 0, GroupName = "1. Risk",
            Description = "RiskBased: contracts from Max Risk, ATM from the templates chosen below. UseChartTrader: ATM and quantity as selected in Chart Trader; Max Risk and the templates are ignored.")]
        public SizingMode Sizing { get; set; }

        [Range(0, double.MaxValue)]
        [Display(Name = "Max Risk ($)", Order = 1, GroupName = "1. Risk",
            Description = "Risk-based sizing only. Most dollars to risk per trade; sets the number of contracts. No trades while it's 0.")]
        public double MaxRisk { get; set; }

        [Range(0, 100)]
        [Display(Name = "Entry Offset Ticks", Order = 2, GroupName = "1. Risk",
            Description = "Ticks beyond the signal bar's high (long) or low (short) for the stop entry.")]
        public int EntryOffsetTicks { get; set; }

        [Range(0, 100)]
        [Display(Name = "Stop Offset Ticks", Order = 3, GroupName = "1. Risk",
            Description = "Ticks beyond the signal bar's other end where the ATM's stop gets moved.")]
        public int StopOffsetTicks { get; set; }

        [Display(Name = "Use Limit When Price Is Past Entry", Order = 4, GroupName = "1. Risk",
            Description = "If price is already beyond the entry, place a GTC limit at the entry instead of skipping. It waits for a pullback until it fills or you cancel it.")]
        public bool UseLimitWhenPastEntry { get; set; }

        // 2. ATM templates: any quantities; the largest that fits the risk is used
        [TypeConverter(typeof(AtmChoiceConverter))]
        [Display(Name = "ATM 1", Order = 1, GroupName = "2. ATM Templates",
            Description = "An ATM template the indicator may use. Pick 1 to 5 with different contract quantities, in any order; the largest that fits Max Risk is used.")]
        public string Atm1 { get; set; }

        [TypeConverter(typeof(AtmChoiceConverter))]
        [Display(Name = "ATM 2", Order = 2, GroupName = "2. ATM Templates",
            Description = "An ATM template the indicator may use. Pick 1 to 5 with different contract quantities, in any order; the largest that fits Max Risk is used.")]
        public string Atm2 { get; set; }

        [TypeConverter(typeof(AtmChoiceConverter))]
        [Display(Name = "ATM 3", Order = 3, GroupName = "2. ATM Templates",
            Description = "An ATM template the indicator may use. Pick 1 to 5 with different contract quantities, in any order; the largest that fits Max Risk is used.")]
        public string Atm3 { get; set; }

        [TypeConverter(typeof(AtmChoiceConverter))]
        [Display(Name = "ATM 4", Order = 4, GroupName = "2. ATM Templates",
            Description = "An ATM template the indicator may use. Pick 1 to 5 with different contract quantities, in any order; the largest that fits Max Risk is used.")]
        public string Atm4 { get; set; }

        [TypeConverter(typeof(AtmChoiceConverter))]
        [Display(Name = "ATM 5", Order = 5, GroupName = "2. ATM Templates",
            Description = "An ATM template the indicator may use. Pick 1 to 5 with different contract quantities, in any order; the largest that fits Max Risk is used.")]
        public string Atm5 { get; set; }

        // 3. Filters
        [Display(Name = "Use Good Close", Order = 1, GroupName = "3. Filters",
            Description = "Skip if the signal bar didn't close near its high (long) or low (short).")]
        public bool UseGoodClose { get; set; }

        [Range(0.0, 1.0)]
        [Display(Name = "Good Close Fraction", Order = 2, GroupName = "3. Filters",
            Description = "Long: close must be in the top part of the bar this size. Short: the bottom part.")]
        public double GoodCloseFraction { get; set; }

        [Display(Name = "Use 5:1 Filter", Order = 3, GroupName = "3. Filters",
            Description = "For Apex Legacy accounts' 5:1 risk-to-reward rule. Skip if entry-to-stop ticks are more than 5 x the ATM template's first target.")]
        public bool UseFiveToOne { get; set; }

        // 4. Stop
        [Display(Name = "Lock Initial Stop", Order = 1, GroupName = "4. Stop",
            Description = "Until Target 1 fills, the stop can be tightened but not loosened.")]
        public bool LockInitialStop { get; set; }

        // 5. Bar range lines
        [Display(Name = "Show Bar Range", Order = 1, GroupName = "5. Bar Range",
            Description = "Prev and Cur lines above the buttons: bar range, risk, and contracts.")]
        public bool ShowBarRange { get; set; }

        [Range(6, 40)]
        [Display(Name = "Font Size", Order = 2, GroupName = "5. Bar Range")]
        public int BarRangeFontSize { get; set; }

        // 6. Target Fill Prediction mark
        [Display(Name = "Show Target Mark", Order = 1, GroupName = "6. Target Fill Prediction",
            Description = "Tick mark on the printing bar where the first target should fill.")]
        public bool ShowTargetMark { get; set; }

        [Range(1, 200)]
        [Display(Name = "Target Fill Prediction", Order = 2, GroupName = "6. Target Fill Prediction",
            Description = "Ticks beyond the entry price where the first target should fill. Line = bar high/low + Entry Offset + this value.")]
        public int TargetFillPrediction { get; set; }

        [Range(1, 10)]
        [Display(Name = "Thickness", Order = 3, GroupName = "6. Target Fill Prediction")]
        public int TargetMarkThickness { get; set; }

        [Range(0.5, 5.0)]
        [Display(Name = "Width (x bar width)", Order = 4, GroupName = "6. Target Fill Prediction",
            Description = "1 = as wide as the bar, 2 = twice as wide.")]
        public double TargetMarkWidth { get; set; }

        [XmlIgnore]
        [Display(Name = "Color", Order = 5, GroupName = "6. Target Fill Prediction")]
        public Brush TargetMarkBrush { get; set; }

        [Browsable(false)]
        public string TargetMarkBrushSerializable
        {
            get { return Serialize.BrushToString(TargetMarkBrush); }
            set { TargetMarkBrush = Serialize.StringToBrush(value); }
        }

        // 8. Panel
        [Display(Name = "Panel Location", Order = 1, GroupName = "8. Panel",
            Description = "ChartTrader: inside Chart Trader, above Chart Jot. Chart: bottom-right of the chart. Falls back to the chart if Chart Trader isn't found.")]
        public PanelLocation ButtonPanelLocation { get; set; }

        // 7. Logging
        [Display(Name = "Detailed Logging", Order = 1, GroupName = "7. Logging",
            Description = "Adds step-by-step lines (signal bar, sizing math) to the Output window.")]
        public bool DetailedLogging { get; set; }

        #endregion
    }

    // Hides Max Risk and the ATM template choices in the settings window when
    // Sizing = UseChartTrader. They come back (with their values) on RiskBased.
    // Same pattern as NinjaTrader's Pivots indicator: mark them not browsable
    // instead of removing them.
    public class PATsButtonsSettingsConverter : IndicatorBaseConverter
    {
        private static readonly string[] RiskBasedOnly =
        {
            "MaxRisk", "Atm1", "Atm2", "Atm3", "Atm4", "Atm5"
        };

        public override bool GetPropertiesSupported(ITypeDescriptorContext context) { return true; }

        public override PropertyDescriptorCollection GetProperties(ITypeDescriptorContext context, object value, Attribute[] attributes)
        {
            PropertyDescriptorCollection all = base.GetPropertiesSupported(context)
                ? base.GetProperties(context, value, attributes)
                : TypeDescriptor.GetProperties(value, attributes);

            PATsButtons indicator = value as PATsButtons;
            if (all == null || indicator == null || indicator.Sizing != PATsButtons.SizingMode.UseChartTrader)
                return all;

            PropertyDescriptorCollection adjusted = new PropertyDescriptorCollection(null);
            foreach (PropertyDescriptor pd in all)
            {
                if (Array.IndexOf(RiskBasedOnly, pd.Name) >= 0)
                    adjusted.Add(new PropertyDescriptorExtended(pd, o => value, null, new Attribute[] { new BrowsableAttribute(false) }));
                else
                    adjusted.Add(pd);
            }
            return adjusted;
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private PATsButtons[] cachePATsButtons;
		public PATsButtons PATsButtons()
		{
			return PATsButtons(Input);
		}

		public PATsButtons PATsButtons(ISeries<double> input)
		{
			if (cachePATsButtons != null)
				for (int idx = 0; idx < cachePATsButtons.Length; idx++)
					if (cachePATsButtons[idx] != null &&  cachePATsButtons[idx].EqualsInput(input))
						return cachePATsButtons[idx];
			return CacheIndicator<PATsButtons>(new PATsButtons(), input, ref cachePATsButtons);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.PATsButtons PATsButtons()
		{
			return indicator.PATsButtons(Input);
		}

		public Indicators.PATsButtons PATsButtons(ISeries<double> input )
		{
			return indicator.PATsButtons(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.PATsButtons PATsButtons()
		{
			return indicator.PATsButtons(Input);
		}

		public Indicators.PATsButtons PATsButtons(ISeries<double> input )
		{
			return indicator.PATsButtons(input);
		}
	}
}

#endregion

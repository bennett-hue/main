using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Input;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;

// Aliases to prevent WPF/SharpDX conflicts
using SWM = System.Windows.Media;
using SDX = SharpDX;
using D2D = SharpDX.Direct2D1;
using DW  = SharpDX.DirectWrite;

namespace NinjaTrader.NinjaScript.Indicators
{
    // ===== ENUM =====
    public enum BK_OffsetMode
    {
        Ticks,
        ATR
    }

    // ===== INDICATOR =====
    public class BK_MultiCrosshairSL_Helper : Indicator
    {
        // ---------------- Inputs ----------------
        [NinjaScriptProperty, Display(Name = "Offset Mode", GroupName = "Stops/Targets", Order = 0)]
        public BK_OffsetMode Mode { get; set; } = BK_OffsetMode.Ticks;

        [NinjaScriptProperty, Display(Name = "Stop Offset (ticks or ATR)", GroupName = "Stops/Targets", Order = 1)]
        public double StopOffset { get; set; } = 40;

        [NinjaScriptProperty, Display(Name = "Target Offset (ticks or ATR)", GroupName = "Stops/Targets", Order = 2)]
        public double TargetOffset { get; set; } = 40;

        [NinjaScriptProperty, Display(Name = "Show Both Sides (Long & Short)", GroupName = "Stops/Targets", Order = 3)]
        public bool ShowBothSides { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "Show Vertical Guide", GroupName = "Visual", Order = 10)]
        public bool ShowVertical { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "Show Labels", GroupName = "Visual", Order = 11)]
        public bool ShowLabels { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "Swing Strength (bars)", GroupName = "Context", Order = 20)]
        public int SwingStrength { get; set; } = 5;

        [NinjaScriptProperty, Display(Name = "ATR Period", GroupName = "Context", Order = 21)]
        public int AtrPeriod { get; set; } = 14;

        // ---------------- Internals ----------------
        private ATR atrStop;
        private Swing swing;
        private float mouseX = float.NaN;
        private float mouseY = float.NaN;
        private bool  isInside;

        private SWM.SolidColorBrush entryBrushWpf, guideBrushWpf, slBrushWpf, tpBrushWpf, swingBrushWpf;
        private D2D.Brush entryDx, guideDx, slDx, tpDx, swingDx, textDx;
        private DW.TextFormat textFormat;

        // ---------------- Lifecycle ----------------
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "BK_MultiCrosshairSL_Helper";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                IsSuspendedWhileInactive = true;

                entryBrushWpf = MakeBrush(SWM.Colors.White);
                guideBrushWpf = MakeBrush(SWM.Colors.Gray);
                slBrushWpf    = MakeBrush(SWM.Colors.OrangeRed);
                tpBrushWpf    = MakeBrush(SWM.Colors.LightGreen);
                swingBrushWpf = MakeBrush(SWM.Colors.Goldenrod);
            }
            else if (State == State.DataLoaded)
            {
                atrStop = ATR(AtrPeriod);
                swing   = Swing(SwingStrength);
            }
            else if (State == State.Historical)
            {
                if (ChartControl != null)
                {
                    ChartControl.MouseMove  += OnChartMouseMove;
                    ChartControl.MouseLeave += OnChartMouseLeave;
                }
            }
            else if (State == State.Terminated)
            {
                if (ChartControl != null)
                {
                    ChartControl.MouseMove  -= OnChartMouseMove;
                    ChartControl.MouseLeave -= OnChartMouseLeave;
                }
                DisposeDx();
            }
        }

        private SWM.SolidColorBrush MakeBrush(SWM.Color c)
        {
            var b = new SWM.SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private void OnChartMouseLeave(object sender, MouseEventArgs e)
        {
            isInside = false;
            mouseX = mouseY = float.NaN;
            ChartControl?.InvalidateVisual();
        }

        private void OnChartMouseMove(object sender, MouseEventArgs e)
        {
            if (ChartPanel == null || ChartControl == null) return;
            var p = e.GetPosition(ChartControl.ChartPanels[ChartPanel.PanelIndex]);
            isInside = p.X >= 0 && p.Y >= 0 && p.X <= ChartPanel.W && p.Y <= ChartPanel.H;
            mouseX = (float)p.X;
            mouseY = (float)p.Y;
            ChartControl.InvalidateVisual();
        }

        protected override void OnBarUpdate() { }

        // ---------------- Rendering ----------------
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (ChartPanel == null || chartScale == null || RenderTarget == null) return;
            if (!isInside || float.IsNaN(mouseX) || float.IsNaN(mouseY)) return;
            if (CurrentBar < AtrPeriod + 5) return;

            EnsureDxBrushes();

            float  absX       = (float)(ChartPanel.X + mouseX);
            double entryPrice = chartScale.GetValueByY(mouseY);

            // Use current bar for ATR calculations (live crosshair uses current data)
            int barsAgo = 0;

            double lastSwingHigh = double.NaN, lastSwingLow = double.NaN;

            // Look back from current bar to find last swing high/low
            for (int lookBack = 0; lookBack <= Math.Min(CurrentBar, 200); lookBack++)
            {
                if (double.IsNaN(lastSwingHigh))
                {
                    double swHigh = swing.SwingHigh[lookBack];
                    if (swHigh != 0 && !double.IsNaN(swHigh))
                        lastSwingHigh = swHigh;
                }
                if (double.IsNaN(lastSwingLow))
                {
                    double swLow = swing.SwingLow[lookBack];
                    if (swLow != 0 && !double.IsNaN(swLow))
                        lastSwingLow = swLow;
                }
                if (!double.IsNaN(lastSwingHigh) && !double.IsNaN(lastSwingLow))
                    break;
            }

            double atrVal        = atrStop[0];
            double slOffsetPrice = Mode == BK_OffsetMode.Ticks ? StopOffset * TickSize : StopOffset * atrVal;
            double tpOffsetPrice = Mode == BK_OffsetMode.Ticks ? TargetOffset * TickSize : TargetOffset * atrVal;

            double longSL  = entryPrice - slOffsetPrice;
            double longTP  = entryPrice + tpOffsetPrice;
            double shortSL = entryPrice + slOffsetPrice;
            double shortTP = entryPrice - tpOffsetPrice;

            float x0 = ChartPanel.X;
            float x1 = ChartPanel.X + ChartPanel.W;
            float yEntry   = (float)chartScale.GetYByValue(entryPrice);
            float yLongSL  = (float)chartScale.GetYByValue(longSL);
            float yLongTP  = (float)chartScale.GetYByValue(longTP);
            float yShortSL = (float)chartScale.GetYByValue(shortSL);
            float yShortTP = (float)chartScale.GetYByValue(shortTP);

            if (ShowVertical)
                RenderTarget.DrawLine(new SDX.Vector2(absX, ChartPanel.Y),
                                      new SDX.Vector2(absX, ChartPanel.Y + ChartPanel.H),
                                      guideDx, 1f);

            RenderTarget.DrawLine(new SDX.Vector2(x0, yEntry),   new SDX.Vector2(x1, yEntry),   entryDx, 1.5f);
            RenderTarget.DrawLine(new SDX.Vector2(x0, yLongSL),  new SDX.Vector2(x1, yLongSL),  slDx,    1.2f);
            RenderTarget.DrawLine(new SDX.Vector2(x0, yLongTP),  new SDX.Vector2(x1, yLongTP),  tpDx,    1.2f);

            if (ShowBothSides)
            {
                RenderTarget.DrawLine(new SDX.Vector2(x0, yShortSL), new SDX.Vector2(x1, yShortSL), slDx, 1.2f);
                RenderTarget.DrawLine(new SDX.Vector2(x0, yShortTP), new SDX.Vector2(x1, yShortTP), tpDx, 1.2f);
            }

            if (!double.IsNaN(lastSwingHigh))
                RenderTarget.DrawLine(new SDX.Vector2(x0, (float)chartScale.GetYByValue(lastSwingHigh)),
                                      new SDX.Vector2(x1, (float)chartScale.GetYByValue(lastSwingHigh)), swingDx, 1f);
            if (!double.IsNaN(lastSwingLow))
                RenderTarget.DrawLine(new SDX.Vector2(x0, (float)chartScale.GetYByValue(lastSwingLow)),
                                      new SDX.Vector2(x1, (float)chartScale.GetYByValue(lastSwingLow)), swingDx, 1f);

            if (ShowLabels)
            {
                string F(double p) => Instrument.MasterInstrument.FormatPrice(p);
                DrawLabel($"Entry {F(entryPrice)} | Mode {(Mode == BK_OffsetMode.Ticks ? "Ticks" : "ATR")}  SL {StopOffset:0.##}  TP {TargetOffset:0.##}", ChartPanel.X + 8, ChartPanel.Y + 8);
                DrawLabel($"LONG  SL {F(longSL)}  TP {F(longTP)}", ChartPanel.X + 8, ChartPanel.Y + 28);
                if (ShowBothSides)
                    DrawLabel($"SHORT SL {F(shortSL)}  TP {F(shortTP)}", ChartPanel.X + 8, ChartPanel.Y + 48);
                if (!double.IsNaN(lastSwingHigh) || !double.IsNaN(lastSwingLow))
                    DrawLabel($"Swing H {(double.IsNaN(lastSwingHigh) ? "—" : F(lastSwingHigh))}  L {(double.IsNaN(lastSwingLow) ? "—" : F(lastSwingLow))}", ChartPanel.X + 8, ChartPanel.Y + 68);
            }
        }

        private void DrawLabel(string text, float x, float y)
        {
            if (textFormat == null) return;
            using (var layout = new DW.TextLayout(Core.Globals.DirectWriteFactory, text, textFormat, 1200, textFormat.FontSize + 6))
                RenderTarget.DrawTextLayout(new SDX.Vector2(x, y), layout, textDx);
        }

        // ---------------- DX helpers ----------------
        private void EnsureDxBrushes()
        {
            if (entryDx != null) return;
            entryDx = ToDxBrush(entryBrushWpf);
            guideDx = ToDxBrush(guideBrushWpf);
            slDx    = ToDxBrush(slBrushWpf);
            tpDx    = ToDxBrush(tpBrushWpf);
            swingDx = ToDxBrush(swingBrushWpf);
            textDx  = new D2D.SolidColorBrush(RenderTarget, new SDX.Color4(1f, 1f, 1f, 0.82f));
            textFormat = new DW.TextFormat(Core.Globals.DirectWriteFactory, "Segoe UI", 12);
        }

        public override void OnRenderTargetChanged()
        {
            DisposeDx();
            base.OnRenderTargetChanged();
        }

        private void DisposeDx()
        {
            entryDx?.Dispose(); guideDx?.Dispose();
            slDx?.Dispose(); tpDx?.Dispose();
            swingDx?.Dispose(); textDx?.Dispose();
            textFormat?.Dispose();
            entryDx = guideDx = slDx = tpDx = swingDx = textDx = null;
            textFormat = null;
        }

        private D2D.Brush ToDxBrush(SWM.SolidColorBrush b)
        {
            var c = b.Color;
            return new D2D.SolidColorBrush(RenderTarget, new SDX.Color4(c.ScR, c.ScG, c.ScB, c.ScA));
        }
    }

    // DO NOT close the namespace before the auto-generated region.
    // The auto-generated cache region needs to stay inside this namespace.
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private BK_MultiCrosshairSL_Helper[] cacheBK_MultiCrosshairSL_Helper;
		public BK_MultiCrosshairSL_Helper BK_MultiCrosshairSL_Helper(BK_OffsetMode mode, double stopOffset, double targetOffset, bool showBothSides, bool showVertical, bool showLabels, int swingStrength, int atrPeriod)
		{
			return BK_MultiCrosshairSL_Helper(Input, mode, stopOffset, targetOffset, showBothSides, showVertical, showLabels, swingStrength, atrPeriod);
		}

		public BK_MultiCrosshairSL_Helper BK_MultiCrosshairSL_Helper(ISeries<double> input, BK_OffsetMode mode, double stopOffset, double targetOffset, bool showBothSides, bool showVertical, bool showLabels, int swingStrength, int atrPeriod)
		{
			if (cacheBK_MultiCrosshairSL_Helper != null)
				for (int idx = 0; idx < cacheBK_MultiCrosshairSL_Helper.Length; idx++)
					if (cacheBK_MultiCrosshairSL_Helper[idx] != null && cacheBK_MultiCrosshairSL_Helper[idx].Mode == mode && cacheBK_MultiCrosshairSL_Helper[idx].StopOffset == stopOffset && cacheBK_MultiCrosshairSL_Helper[idx].TargetOffset == targetOffset && cacheBK_MultiCrosshairSL_Helper[idx].ShowBothSides == showBothSides && cacheBK_MultiCrosshairSL_Helper[idx].ShowVertical == showVertical && cacheBK_MultiCrosshairSL_Helper[idx].ShowLabels == showLabels && cacheBK_MultiCrosshairSL_Helper[idx].SwingStrength == swingStrength && cacheBK_MultiCrosshairSL_Helper[idx].AtrPeriod == atrPeriod && cacheBK_MultiCrosshairSL_Helper[idx].EqualsInput(input))
						return cacheBK_MultiCrosshairSL_Helper[idx];
			return CacheIndicator<BK_MultiCrosshairSL_Helper>(new BK_MultiCrosshairSL_Helper(){ Mode = mode, StopOffset = stopOffset, TargetOffset = targetOffset, ShowBothSides = showBothSides, ShowVertical = showVertical, ShowLabels = showLabels, SwingStrength = swingStrength, AtrPeriod = atrPeriod }, input, ref cacheBK_MultiCrosshairSL_Helper);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.BK_MultiCrosshairSL_Helper BK_MultiCrosshairSL_Helper(BK_OffsetMode mode, double stopOffset, double targetOffset, bool showBothSides, bool showVertical, bool showLabels, int swingStrength, int atrPeriod)
		{
			return indicator.BK_MultiCrosshairSL_Helper(Input, mode, stopOffset, targetOffset, showBothSides, showVertical, showLabels, swingStrength, atrPeriod);
		}

		public Indicators.BK_MultiCrosshairSL_Helper BK_MultiCrosshairSL_Helper(ISeries<double> input , BK_OffsetMode mode, double stopOffset, double targetOffset, bool showBothSides, bool showVertical, bool showLabels, int swingStrength, int atrPeriod)
		{
			return indicator.BK_MultiCrosshairSL_Helper(input, mode, stopOffset, targetOffset, showBothSides, showVertical, showLabels, swingStrength, atrPeriod);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.BK_MultiCrosshairSL_Helper BK_MultiCrosshairSL_Helper(BK_OffsetMode mode, double stopOffset, double targetOffset, bool showBothSides, bool showVertical, bool showLabels, int swingStrength, int atrPeriod)
		{
			return indicator.BK_MultiCrosshairSL_Helper(Input, mode, stopOffset, targetOffset, showBothSides, showVertical, showLabels, swingStrength, atrPeriod);
		}

		public Indicators.BK_MultiCrosshairSL_Helper BK_MultiCrosshairSL_Helper(ISeries<double> input , BK_OffsetMode mode, double stopOffset, double targetOffset, bool showBothSides, bool showVertical, bool showLabels, int swingStrength, int atrPeriod)
		{
			return indicator.BK_MultiCrosshairSL_Helper(input, mode, stopOffset, targetOffset, showBothSides, showVertical, showLabels, swingStrength, atrPeriod);
		}
	}
}

#endregion

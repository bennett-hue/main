using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;

namespace NinjaTrader.NinjaScript.Strategies
{
    public enum BK_LongTargetMode { R1, R2 }
    public enum BK_ShortTargetMode { S1, S2 }

    public class BK_PivotReversal : Strategy
    {
        // ---------------- Inputs ----------------
        [NinjaScriptProperty, Display(Name = "Pivot Range", GroupName = "Pivots", Order = 0)]
        public PivotRange PivotRangeType { get; set; } = PivotRange.Daily;

        [NinjaScriptProperty, Display(Name = "HLC Source", GroupName = "Pivots", Order = 1)]
        public HLCCalculationMode HlcMode { get; set; } = HLCCalculationMode.DailyBars;

        [NinjaScriptProperty, Display(Name = "Long Target", GroupName = "Targets", Order = 10)]
        public BK_LongTargetMode LongTarget { get; set; } = BK_LongTargetMode.R1;

        [NinjaScriptProperty, Display(Name = "Short Target", GroupName = "Targets", Order = 11)]
        public BK_ShortTargetMode ShortTarget { get; set; } = BK_ShortTargetMode.S1;

        [NinjaScriptProperty, Display(Name = "Retest Tolerance (ticks)", GroupName = "Setup", Order = 20)]
        public int RetestToleranceTicks { get; set; } = 4;

        [NinjaScriptProperty, Display(Name = "Max Bars For Retest", GroupName = "Setup", Order = 21)]
        public int MaxBarsForRetest { get; set; } = 10;

        [NinjaScriptProperty, Display(Name = "Stop Buffer (ticks)", GroupName = "Setup", Order = 22)]
        public int StopBufferTicks { get; set; } = 4;

        [NinjaScriptProperty, Display(Name = "Quantity", GroupName = "Setup", Order = 23)]
        public int Quantity { get; set; } = 1;

        [NinjaScriptProperty, Display(Name = "Enable Trading", GroupName = "Setup", Order = 24)]
        public bool EnableTrading { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "Draw Markers", GroupName = "Visual", Order = 30)]
        public bool DrawMarkers { get; set; } = true;

        // ---------------- Internals ----------------
        private Pivots pivots;

        private enum SetupState { None, BullReversal, BearReversal, Done }
        private SetupState state;
        private double reversalLow;
        private double reversalHigh;
        private int reversalBar;
        private double sessionPP;

        // ---------------- Lifecycle ----------------
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                            = "BK_PivotReversal";
                Description                     = "Reversal at PP with retest entry. Targets R1/R2 (long) or S1/S2 (short). Stop = setup extreme + buffer.";
                Calculate                       = Calculate.OnBarClose;
                EntriesPerDirection             = 1;
                EntryHandling                   = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy    = true;
                ExitOnSessionCloseSeconds       = 30;
                IsFillLimitOnTouch              = false;
                MaximumBarsLookBack             = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution             = OrderFillResolution.Standard;
                Slippage                        = 0;
                StartBehavior                   = StartBehavior.WaitUntilFlat;
                TimeInForce                     = TimeInForce.Gtc;
                TraceOrders                     = false;
                RealtimeErrorHandling           = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling              = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade             = 20;
                IncludeCommission               = true;
            }
            else if (State == State.DataLoaded)
            {
                pivots     = Pivots(PivotRangeType, HlcMode, 0, 0, 0, 20);
                state      = SetupState.None;
                sessionPP  = double.NaN;
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade) return;

            double pp = pivots.PP[0];
            if (pp == 0 || double.IsNaN(pp)) return;

            double r1 = pivots.R1[0];
            double r2 = pivots.R2[0];
            double s1 = pivots.S1[0];
            double s2 = pivots.S2[0];

            // Reset state at the start of each new pivot session.
            if (!double.IsNaN(sessionPP) && Math.Abs(pp - sessionPP) > TickSize / 2.0)
                state = SetupState.None;
            sessionPP = pp;

            double tol = RetestToleranceTicks * TickSize;
            double buf = StopBufferTicks * TickSize;

            switch (state)
            {
                case SetupState.None:
                    DetectReversal(pp);
                    break;

                case SetupState.BullReversal:
                    HandleBullSetup(pp, r1, r2, tol, buf);
                    break;

                case SetupState.BearReversal:
                    HandleBearSetup(pp, s1, s2, tol, buf);
                    break;

                case SetupState.Done:
                    break;
            }
        }

        // ---------------- Setup detection ----------------
        private void DetectReversal(double pp)
        {
            // Bullish rejection: bar dipped to/through PP and closed back above it.
            if (Low[0] <= pp && Close[0] > pp)
            {
                state         = SetupState.BullReversal;
                reversalLow   = Low[0];
                reversalBar   = CurrentBar;
                if (DrawMarkers)
                    Draw.ArrowUp(this, "rev_" + CurrentBar, true, 0, Low[0] - 2 * TickSize, Brushes.LimeGreen);
                return;
            }

            // Bearish rejection: bar pushed to/through PP and closed back below it.
            if (High[0] >= pp && Close[0] < pp)
            {
                state         = SetupState.BearReversal;
                reversalHigh  = High[0];
                reversalBar   = CurrentBar;
                if (DrawMarkers)
                    Draw.ArrowDown(this, "rev_" + CurrentBar, true, 0, High[0] + 2 * TickSize, Brushes.OrangeRed);
            }
        }

        private void HandleBullSetup(double pp, double r1, double r2, double tol, double buf)
        {
            // Invalidate if a bar closes back below PP, or the retest window expires.
            if (Close[0] < pp) { state = SetupState.None; return; }
            if (CurrentBar - reversalBar > MaxBarsForRetest) { state = SetupState.None; return; }
            if (CurrentBar == reversalBar) return; // wait at least one bar

            // Retest = bar's low pulled back into PP zone but bar held and closed bullish above PP.
            bool touched   = Low[0] <= pp + tol && Low[0] >= pp - tol;
            bool confirmed = Close[0] > pp && Close[0] >= Open[0];

            if (touched && confirmed)
            {
                double stop   = Math.Min(reversalLow, Low[0]) - buf;
                double target = LongTarget == BK_LongTargetMode.R1 ? r1 : r2;

                if (target > Close[0] && stop < Close[0])
                {
                    if (EnableTrading)
                    {
                        SetStopLoss("BK_PivotLong", CalculationMode.Price, stop, false);
                        SetProfitTarget("BK_PivotLong", CalculationMode.Price, target);
                        EnterLong(Quantity, "BK_PivotLong");
                    }
                    if (DrawMarkers)
                        Draw.ArrowUp(this, "entry_" + CurrentBar, true, 0, Low[0] - 4 * TickSize, Brushes.Cyan);
                    state = SetupState.Done;
                }
            }
        }

        private void HandleBearSetup(double pp, double s1, double s2, double tol, double buf)
        {
            if (Close[0] > pp) { state = SetupState.None; return; }
            if (CurrentBar - reversalBar > MaxBarsForRetest) { state = SetupState.None; return; }
            if (CurrentBar == reversalBar) return;

            bool touched   = High[0] >= pp - tol && High[0] <= pp + tol;
            bool confirmed = Close[0] < pp && Close[0] <= Open[0];

            if (touched && confirmed)
            {
                double stop   = Math.Max(reversalHigh, High[0]) + buf;
                double target = ShortTarget == BK_ShortTargetMode.S1 ? s1 : s2;

                if (target < Close[0] && stop > Close[0])
                {
                    if (EnableTrading)
                    {
                        SetStopLoss("BK_PivotShort", CalculationMode.Price, stop, false);
                        SetProfitTarget("BK_PivotShort", CalculationMode.Price, target);
                        EnterShort(Quantity, "BK_PivotShort");
                    }
                    if (DrawMarkers)
                        Draw.ArrowDown(this, "entry_" + CurrentBar, true, 0, High[0] + 4 * TickSize, Brushes.Magenta);
                    state = SetupState.Done;
                }
            }
        }
    }
}

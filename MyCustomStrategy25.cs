#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class MyCustomStrategy25 : Strategy
    {
        private double sessionHigh = 0;
        private double sessionLow = double.MaxValue;
        private double previousBarSessionHigh = 0;
        private double previousBarSessionLow = double.MaxValue;
        private DateTime currentSessionStartDate = DateTime.MinValue;  // Track session by start date
        private DateTime lastTradeTime = DateTime.MinValue;
        private int tradesThisSession = 0;
        private bool sessionActive = false;

        // ================================
        //            INPUTS
        // ================================
        [NinjaScriptProperty]
        [Display(Name = "Profit Target (Ticks)", Order = 1, GroupName = "Parameters")]
        public int ProfitTargetTicks { get; set; } = 150;

        [NinjaScriptProperty]
        [Display(Name = "Stop Loss (Ticks)", Order = 2, GroupName = "Parameters")]
        public int StopLossTicks { get; set; } = 75;

        [NinjaScriptProperty]
        [Display(Name = "Enable Long Trades", Order = 3, GroupName = "Parameters")]
        public bool EnableLongs { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Enable Short Trades", Order = 4, GroupName = "Parameters")]
        public bool EnableShorts { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Session Start Time (HHmm)", Description = "Format: HHmm - Examples: 1=12:01AM, 30=12:30AM, 100=1:00AM, 930=9:30AM, 1800=6:00PM", Order = 5, GroupName = "Time Window")]
        public int StartTime { get; set; } = 1800;  // 6 PM

        [NinjaScriptProperty]
        [Display(Name = "Session End Time (HHmm)", Description = "Format: HHmm - Examples: 1=12:01AM, 300=3:00AM, 1700=5:00PM. Can be less than Start for overnight sessions.", Order = 6, GroupName = "Time Window")]
        public int EndTime { get; set; } = 1700;    // 5 PM next day

        [NinjaScriptProperty]
        [Display(Name = "Minimum Breakout Distance (Ticks)", Order = 10, GroupName = "Filters")]
        public int MinBreakoutTicks { get; set; } = 5;

        [NinjaScriptProperty]
        [Display(Name = "Cooldown Between Trades (Minutes)", Order = 11, GroupName = "Filters")]
        public int CooldownMinutes { get; set; } = 30;

        [NinjaScriptProperty]
        [Display(Name = "Max Trades Per Session", Order = 12, GroupName = "Filters")]
        public int MaxTradesPerSession { get; set; } = 3;


        // ================================
        //        INITIALIZATION
        // ================================
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "MyCustomStrategy25";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
            }
        }


        // ================================
        //         TIME LOGIC (FIXED)
        // ================================
        private bool IsSessionStart()
        {
            int currentTime = ToTime(Time[0]);
            int prevTime = CurrentBar > 0 ? ToTime(Time[1]) : 0;

            // Session starts when we cross the StartTime
            // For 1800 start: we cross from <1800 to >=1800
            if (StartTime > EndTime) // Overnight session
            {
                // Check if we just crossed into start time
                if (currentTime >= StartTime && prevTime < StartTime)
                    return true;
            }
            else // Day session
            {
                if (currentTime >= StartTime && prevTime < StartTime)
                    return true;
            }

            return false;
        }

        private bool InTimeWindow()
        {
            int t = ToTime(Time[0]);
            bool inWindow;

            if (StartTime > EndTime) // Overnight session (e.g., 1800 to 1700)
            {
                inWindow = (t >= StartTime || t <= EndTime);
            }
            else // Day session
            {
                // Handle bar boundaries: include a bar if it contains the start time
                // This handles cases like StartTime=1 (12:01 AM) with 30-minute bars at time=0
                // Include bar if: 1) at or after start, OR 2) bar might contain start time (same hour)
                bool atOrAfterStart = (t >= StartTime) || (StartTime > t && StartTime - t < 100);
                bool atOrBeforeEnd = (t <= EndTime);

                inWindow = atOrAfterStart && atOrBeforeEnd;
            }

            // Uncomment for detailed debugging:
            // Print(String.Format("{0}: InTimeWindow check - t={1}, Start={2}, End={3}, Result={4}",
            //     Time[0], t, StartTime, EndTime, inWindow));

            return inWindow;
        }

        /// <summary>
        /// Gets the session start date for the current bar.
        /// For overnight sessions, returns the date when the session started.
        /// Handles year boundaries correctly.
        /// </summary>
        private DateTime GetSessionStartDate()
        {
            int currentTime = ToTime(Time[0]);

            if (StartTime > EndTime) // Overnight session
            {
                if (currentTime >= StartTime)
                {
                    // Session started today
                    return Time[0].Date;
                }
                else if (currentTime <= EndTime)
                {
                    // Session started yesterday
                    return Time[0].Date.AddDays(-1);
                }
                else
                {
                    // In gap between sessions (after EndTime, before StartTime)
                    // Return previous session start date to avoid premature reset
                    return Time[0].Date.AddDays(-1);
                }
            }
            else // Day session
            {
                if (currentTime >= StartTime && currentTime <= EndTime)
                {
                    // Within session
                    return Time[0].Date;
                }
                else
                {
                    // Outside session
                    if (currentTime < StartTime)
                        return Time[0].Date; // Session will start later today
                    else
                        return Time[0].Date.AddDays(1); // Session will start tomorrow
                }
            }
        }


        // ================================
        //             MAIN LOGIC
        // ================================
        protected override void OnBarUpdate()
        {
            if (CurrentBar < 1)
                return;

            // ================================
            //     SESSION RESET
            // ================================
            // Reset ONLY when we cross the start time
            if (IsSessionStart())
            {
                currentSessionStartDate = GetSessionStartDate();
                sessionHigh = High[0];
                sessionLow = Low[0];
                previousBarSessionHigh = High[0];
                previousBarSessionLow = Low[0];
                tradesThisSession = 0;
                sessionActive = true;

                Print(String.Format("{0}: *** NEW SESSION (Start Date {1:yyyy-MM-dd}) *** H/L = {2:F2} / {3:F2}",
                    Time[0], currentSessionStartDate, sessionHigh, sessionLow));
            }

            if (!InTimeWindow())
            {
                sessionActive = false;
                Print(String.Format("{0}: Outside time window. CurrentTime={1}, StartTime={2}, EndTime={3}",
                    Time[0], ToTime(Time[0]), StartTime, EndTime));
                return;
            }

            if (!sessionActive)
            {
                Print(String.Format("{0}: Session not active (waiting for session start)", Time[0]));
                return;
            }

            // ================================
            //  UPDATE SESSION HIGH/LOW
            // ================================
            // Save previous bar's levels
            previousBarSessionHigh = sessionHigh;
            previousBarSessionLow = sessionLow;

            // Update current session levels
            if (High[0] > sessionHigh)
            {
                Print(String.Format("{0}: New session HIGH: {1:F2} (was {2:F2})",
                    Time[0], High[0], sessionHigh));
                sessionHigh = High[0];
            }
            if (Low[0] < sessionLow)
            {
                Print(String.Format("{0}: New session LOW: {1:F2} (was {2:F2})",
                    Time[0], Low[0], sessionLow));
                sessionLow = Low[0];
            }

            // ================================
            //         TRADE FILTERS
            // ================================
            if (tradesThisSession >= MaxTradesPerSession)
                return;

            if (CooldownMinutes > 0 && lastTradeTime != DateTime.MinValue)
            {
                TimeSpan elapsed = Time[0] - lastTradeTime;
                if (elapsed.TotalMinutes < CooldownMinutes)
                    return;
            }

            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            // ================================
            //     LONG ENTRY (Breakout Above Previous Session High)
            // ================================
            if (EnableLongs)
            {
                double breakoutTicks = (High[0] - previousBarSessionHigh) / TickSize;

                if (breakoutTicks >= MinBreakoutTicks)
                {
                    EnterLong();
                    SetProfitTarget(CalculationMode.Ticks, ProfitTargetTicks);
                    SetStopLoss(CalculationMode.Ticks, StopLossTicks);

                    lastTradeTime = Time[0];
                    tradesThisSession++;

                    Print(String.Format("{0}: >>> LONG #{1} >>> Broke {2:F2} by {3:F1} ticks @ {4:F2}",
                        Time[0], tradesThisSession, previousBarSessionHigh, breakoutTicks, Close[0]));
                }
            }

            // ================================
            //     SHORT ENTRY (Breakout Below Previous Session Low)
            // ================================
            if (EnableShorts)
            {
                double breakoutTicks = (previousBarSessionLow - Low[0]) / TickSize;

                if (breakoutTicks >= MinBreakoutTicks)
                {
                    EnterShort();
                    SetProfitTarget(CalculationMode.Ticks, ProfitTargetTicks);
                    SetStopLoss(CalculationMode.Ticks, StopLossTicks);

                    lastTradeTime = Time[0];
                    tradesThisSession++;

                    Print(String.Format("{0}: >>> SHORT #{1} >>> Broke {2:F2} by {3:F1} ticks @ {4:F2}",
                        Time[0], tradesThisSession, previousBarSessionLow, breakoutTicks, Close[0]));
                }
            }
        }
    }
}

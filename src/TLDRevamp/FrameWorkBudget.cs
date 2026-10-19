using System;
using System.Diagnostics;

namespace TLDRevamp
{
    /// Repeated callers in one frame share an allowance, but one indivisible operation can always make progress.
    internal sealed class FrameWorkBudget
    {
        private int _frame = -1, _steps;
        private long _spent, _limit;
        internal void Begin(int frame, double milliseconds)
        {
            if (_frame != frame) { _frame = frame; _spent = 0; _steps = 0; }
            _limit = double.IsNaN(milliseconds) ? 0 : (long)(Math.Min(1000, Math.Max(0, milliseconds)) * Stopwatch.Frequency / 1000);
        }
        internal bool CanRun => _steps == 0 || _spent < _limit;
        internal void Charge(long ticks) { _spent += Math.Max(0, ticks); _steps++; }
    }
}

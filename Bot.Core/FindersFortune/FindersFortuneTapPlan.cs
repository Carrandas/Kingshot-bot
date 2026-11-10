using OpenCvSharp;

namespace Bot.Core.FindersFortune
{
    /// <summary>
    /// Keeps item taps off the "Proceed?" buttons. Some items sit exactly where Give Up, Extra Time
    /// and its Confirm appear when the timer hits 0, so one late tap could lose the stage or spend bells.
    /// </summary>
    public static class FindersFortuneTapPlan
    {
        public const int MinSecondsForDangerousSpot = 6;

        private static readonly Rect[] ProceedButtons =
        {
            new(70, 890, 276, 100),  // Give Up on the "Time's Up!" layout
            new(372, 890, 278, 100), // Extra Time on the "Time's Up!" layout
            new(70, 995, 276, 96),  // Give Up: no confirmation at all
            new(372, 995, 278, 96), // Extra Time
            new(362, 727, 318, 96), // Extra Time's Confirm
        };

        public static bool IsOverProceedButton(int x, int y) => ProceedButtons.Any(b => b.Contains(new Point(x, y)));

        /// <summary>Whether a tap here is safe now. Reading the timer wrong must not reach a button.</summary>
        public static bool IsSafeNow(int x, int y, int seconds, bool timerRed) =>
            !IsOverProceedButton(x, y) || (seconds >= MinSecondsForDangerousSpot && !timerRed);

        /// <summary>
        /// The taps to send together now. Dangerous spots go alone, only with time to spare and after
        /// the safe ones: a batch straddling 0 s could otherwise tap Extra Time and then Confirm.
        /// </summary>
        public static IReadOnlyList<T> NextBatch<T>(IReadOnlyList<T> items, Func<T, (int X, int Y)> point, int seconds, bool timerRed)
        {
            var safe = items.Where(i => !IsOverProceedButton(point(i).X, point(i).Y)).ToList();
            if (safe.Count > 0)
            {
                return safe;
            }

            var ready = items.Where(i => IsSafeNow(point(i).X, point(i).Y, seconds, timerRed)).Take(1).ToList();
            return ready;
        }
    }
}

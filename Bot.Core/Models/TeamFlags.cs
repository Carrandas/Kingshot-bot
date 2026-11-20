using System.Collections.Generic;
using System.Linq;

namespace Bot.Core.Models
{
    /// <summary>
    /// Up to <see cref="Slots"/> deploy flags for joins / reinforcements, each slot set (1-12) or not (0).
    /// The n-th march takes the n-th set flag; with fewer flags than marches they repeat from the first
    /// (flags 1, 2, 3 set: marches 1-6 use 1, 2, 3, 1, 2, 3).
    /// </summary>
    public static class TeamFlags
    {
        public const int Slots = 6;

        /// <summary>The set flags in slot order (0 and values outside 1-12 are left out)</summary>
        public static List<int> Set(IEnumerable<int> slots) => slots.Where(IsFlag).ToList();

        /// <summary>The flag for march <paramref name="index"/> (0-based); <paramref name="fallback"/> when no slot is set</summary>
        public static int ForMarch(IReadOnlyList<int> setFlags, int index, int fallback) =>
            setFlags.Count == 0 ? fallback : setFlags[((index % setFlags.Count) + setFlags.Count) % setFlags.Count];

        public static bool IsFlag(int team) => team is >= 1 and <= MaxFlag;

        /// <summary>The game has 12 deploy flags</summary>
        public const int MaxFlag = 12;
    }
}

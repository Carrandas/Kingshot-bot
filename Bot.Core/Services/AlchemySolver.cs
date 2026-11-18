using System;
using System.Collections.Generic;
using System.Linq;

namespace Bot.Core.Services
{
    /// <summary>Brew colours of the Alchemy Junction minigame, from the liquid's HSV hue</summary>
    public enum BrewColour
    {
        Red,
        Orange,
        Yellow,
        Green,
        Cyan,
        Blue,
        Purple,
        Pink
    }

    /// <summary>
    /// The Alchemy Junction minigame (Brew Workshop): fill a row of slots with the tray's jars, one jar per Magic Brew.
    /// Once the row is full, the right jars stay (green platform) and the wrong ones go back, leaving a splash of their
    /// colour in that slot's history: that colour is not the slot's. A colour-only Mastermind, solved by never
    /// putting a jar where its colour already failed.
    /// </summary>
    public static class AlchemySolver
    {
        /// <summary>OpenCV hue (0-179) to colour. Measured: red 175-178, pink 162-167, purple 144-145, blue 107-108, green 52-55, yellow 21-23</summary>
        public static BrewColour Classify(int hue) => hue switch
        {
            >= 172 or <= 5 => BrewColour.Red,
            <= 14 => BrewColour.Orange,
            <= 35 => BrewColour.Yellow,
            <= 80 => BrewColour.Green,
            <= 95 => BrewColour.Cyan,
            <= 128 => BrewColour.Blue,
            <= 154 => BrewColour.Purple,
            _ => BrewColour.Pink
        };

        /// <summary>
        /// Which tray jar goes in each open slot (left to right): an assignment where no slot gets a colour that already
        /// failed there. Null when none exists (stale exclusions): the caller drops them and places the jars in order.
        /// </summary>
        public static int[]? Plan(IReadOnlyList<int> openSlots, IReadOnlyList<BrewColour> jars,
            IReadOnlyDictionary<int, HashSet<BrewColour>> excluded)
        {
            var result = new int[openSlots.Count];
            var used = new bool[jars.Count];
            return Assign(0, openSlots, jars, excluded, used, result) ? result : null;
        }

        private static bool Assign(int i, IReadOnlyList<int> openSlots, IReadOnlyList<BrewColour> jars,
            IReadOnlyDictionary<int, HashSet<BrewColour>> excluded, bool[] used, int[] result)
        {
            if (i == openSlots.Count) return true;
            excluded.TryGetValue(openSlots[i], out var notHere);
            var tried = new HashSet<BrewColour>();
            for (int j = 0; j < jars.Count; j++)
            {
                // jars of one colour are interchangeable: try each colour once per slot
                if (used[j] || (notHere != null && notHere.Contains(jars[j])) || !tried.Add(jars[j])) continue;
                used[j] = true;
                result[i] = j;
                if (Assign(i + 1, openSlots, jars, excluded, used, result)) return true;
                used[j] = false;
            }
            return false;
        }

        /// <summary>Adds the colours of a failed row: each slot that did not turn green rules its colour out there</summary>
        public static void AddFailures(Dictionary<int, HashSet<BrewColour>> excluded, IReadOnlyDictionary<int, BrewColour> attempt,
            IReadOnlyCollection<int> greenSlots)
        {
            foreach (var (slot, colour) in attempt.Where(a => !greenSlots.Contains(a.Key)))
            {
                if (!excluded.TryGetValue(slot, out var set)) excluded[slot] = set = new HashSet<BrewColour>();
                set.Add(colour);
            }
        }

        /// <summary>Union of two exclusion maps (the visible history and the saved one)</summary>
        public static Dictionary<int, HashSet<BrewColour>> Merge(IReadOnlyDictionary<int, HashSet<BrewColour>> a,
            IReadOnlyDictionary<int, HashSet<BrewColour>> b)
        {
            var merged = a.ToDictionary(kv => kv.Key, kv => new HashSet<BrewColour>(kv.Value));
            foreach (var (slot, colours) in b)
            {
                if (!merged.TryGetValue(slot, out var set)) merged[slot] = set = new HashSet<BrewColour>();
                set.UnionWith(colours);
            }
            return merged;
        }
    }
}

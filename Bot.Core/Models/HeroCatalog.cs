using System;
using System.Linq;

namespace Bot.Core.Models
{
    /// <summary>The game's heroes by name (read off the Hero screen's title on 2026-11-12: 37 heroes in the ring of the &gt; arrow)</summary>
    public static class HeroCatalog
    {
        public static readonly string[] Names =
        {
            "Alcar", "Amadeus", "Amane", "Ava", "Charles", "Chenko", "Diana", "Diego", "Edwin", "Eric", "Fahd", "Forrest", "Gordon",
            "Helga", "Hilde", "Howard", "Jabel", "Jaeger", "Liz", "Long Fei", "Luna", "Margot", "Marlin", "Olive", "Petra", "Quinn",
            "Rosa", "Saul", "Seth", "Sophia", "Thrud", "Triton", "Vivian", "Wee & Woo", "Yang", "Yeonwoo", "Zoe"
        };

        /// <summary>Lower-case letters only: "Wee &amp; Woo" and "wee woo" compare equal</summary>
        public static string Normalize(string name) => new string((name ?? string.Empty).Where(char.IsLetter).ToArray()).ToLowerInvariant();

        /// <summary>
        /// The title read by OCR is the wanted hero: the same letters, or one letter off for names of 5+ letters
        /// (the neighbours in the catalog differ by more than one letter).
        /// </summary>
        public static bool SameHero(string read, string wanted)
        {
            string a = Normalize(read), b = Normalize(wanted);
            if (a.Length == 0 || b.Length == 0) return false;
            if (a == b) return true;
            return b.Length >= 5 && Math.Abs(a.Length - b.Length) <= 1 && EditDistance(a, b) <= 1;
        }

        private static int EditDistance(string a, string b)
        {
            var row = Enumerable.Range(0, b.Length + 1).ToArray();
            for (int i = 1; i <= a.Length; i++)
            {
                int diagonal = row[0];
                row[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int above = row[j];
                    row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                    diagonal = above;
                }
            }
            return row[b.Length];
        }
    }
}

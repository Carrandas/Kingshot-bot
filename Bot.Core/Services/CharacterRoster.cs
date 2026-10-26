using Bot.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bot.Core.Services
{
    /// <summary>
    /// Keeps <see cref="AccountSettings.Characters"/> in line with the rows found on the Characters screen.
    /// Names come from OCR, so they are compared loosely: the alliance tag is ignored (it changes when a character
    /// switches alliance) and I/l/1/| count as the same letter ("Ilsa" reads as "llsa" or "IIsa").
    /// </summary>
    public static class CharacterRoster
    {
        /// <summary>Runs roster changes on the UI thread: the GUI binds to the Characters collection. Inline by default.</summary>
        public static Action<Action> Dispatch { get; set; } = action => action();

        /// <summary>Raised after a scan changed an account's character list, so the GUI can save the config.</summary>
        public static event Action<AccountSettings>? Changed;

        private static readonly Regex AllianceTag = new Regex(@"^\s*[\[\(\{].{1,6}?[\]\)\}]\s*");

        /// <summary>Comparison key for an OCR'd name: no alliance tag, lower case, I/l/1/| folded to 'l', O/0 folded to 'o', letters and digits only.</summary>
        public static string NormalizeKey(string name)
        {
            var withoutTag = AllianceTag.Replace(name ?? string.Empty, string.Empty);
            var sb = new StringBuilder(withoutTag.Length);
            foreach (var c in withoutTag.ToLowerInvariant())
            {
                if (c == 'i' || c == '1' || c == '|' || c == '!') sb.Append('l');
                else if (c == '0') sb.Append('o');
                else if (char.IsLetterOrDigit(c)) sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>True when two OCR'd names are the same character: equal keys, or one character off for names of 5+ characters.</summary>
        public static bool SameName(string a, string b)
        {
            var ka = NormalizeKey(a);
            var kb = NormalizeKey(b);
            if (ka.Length == 0 || kb.Length == 0) return false;
            if (ka == kb) return true;
            return Math.Min(ka.Length, kb.Length) >= 5 && Levenshtein(ka, kb) <= 1;
        }

        public static CharacterEntry? Find(AccountSettings account, string name, int? kingdom)
        {
            return account.Characters.FirstOrDefault(c =>
                SameName(c.Name, name) && (!c.Kingdom.HasValue || !kingdom.HasValue || c.Kingdom == kingdom));
        }

        /// <summary>Characters not in the list yet are enabled: a character only stops being run once it is unticked in the settings.</summary>
        public static bool IsEnabled(AccountSettings account, string name, int? kingdom)
        {
            return Find(account, name, kingdom)?.Enabled ?? true;
        }

        /// <summary>
        /// Adds characters that are not in the list yet and refreshes the name/kingdom of known ones (the alliance tag can change).
        /// Never touches Enabled: that is the user's choice.
        /// </summary>
        public static void Merge(AccountSettings account, IEnumerable<(string Name, int? Kingdom)> scanned)
        {
            var rows = scanned.Where(r => NormalizeKey(r.Name).Length > 0).ToList();
            if (rows.Count == 0) return;

            Dispatch(() =>
            {
                bool changed = false;
                foreach (var (name, kingdom) in rows)
                {
                    var existing = Find(account, name, kingdom);
                    if (existing == null)
                    {
                        account.Characters.Add(new CharacterEntry { Name = name, Kingdom = kingdom, Enabled = true });
                        changed = true;
                        continue;
                    }
                    // Only a real change (e.g. a new alliance tag), not an OCR variant of the same name such as "llsa" for "Ilsa"
                    if (NormalizeKey(existing.Name) != NormalizeKey(name) || !TagOf(existing.Name).Equals(TagOf(name), StringComparison.OrdinalIgnoreCase))
                    {
                        existing.Name = name;
                        changed = true;
                    }
                    if (kingdom.HasValue && existing.Kingdom != kingdom) { existing.Kingdom = kingdom; changed = true; }
                }
                if (changed) Changed?.Invoke(account);
            });
        }

        // Character active in the game on each instance, as Change Account last saw it
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (string Name, int? Kingdom)> _active = new();

        public static void SetActive(int instanceNumber, string name, int? kingdom) => _active[instanceNumber] = (name, kingdom);

        public static string? GetActiveName(int instanceNumber) => _active.TryGetValue(instanceNumber, out var a) ? a.Name : null;

        /// <summary>
        /// Account Detection found the game account ID: store it on the active character, so the dashboard can find
        /// that character's gates and run history.
        /// </summary>
        public static void LinkAccountId(AccountSettings account, string accountId)
        {
            if (string.IsNullOrEmpty(accountId) || !_active.TryGetValue(account.InstanceNumber, out var active)) return;
            Dispatch(() =>
            {
                var entry = Find(account, active.Name, active.Kingdom);
                if (entry == null || entry.AccountId == accountId) return;
                entry.AccountId = accountId;
                Changed?.Invoke(account);
            });
        }

        private static string TagOf(string name) => AllianceTag.Match(name ?? string.Empty).Value.Trim();

        private static int Levenshtein(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                (prev, cur) = (cur, prev);
            }
            return prev[b.Length];
        }
    }
}

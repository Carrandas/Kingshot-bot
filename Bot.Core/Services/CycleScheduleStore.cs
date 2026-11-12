using System;
using System.Globalization;
using System.IO;

namespace Bot.Core.Services
{
    /// <summary>
    /// Saves when the next cycle is due (data/cycle_next.txt, UTC), so a restart of the app carries on with the countdown
    /// ("next cycle in 78 minutes") instead of starting a cycle at once.
    /// </summary>
    public static class CycleScheduleStore
    {
        /// <summary>Overridable for tests</summary>
        internal static string FilePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "data", "cycle_next.txt");

        public static void Save(DateTime? nextUtc)
        {
            try
            {
                if (nextUtc is not DateTime next)
                {
                    if (File.Exists(FilePath)) File.Delete(FilePath);
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, next.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            }
            catch (IOException)
            {
                // the countdown is a convenience: without the file a restart simply runs a cycle at once
            }
            catch (UnauthorizedAccessException)
            {
                // same: a read-only data folder only costs the resume
            }
        }

        public static DateTime? Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                return DateTime.TryParse(File.ReadAllText(FilePath).Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var next) ? next : null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        /// <summary>How long is left of a saved countdown; null when there is none or it has run out (a cycle is due now)</summary>
        public static TimeSpan? Remaining(DateTime? savedUtc, DateTime nowUtc) =>
            savedUtc is DateTime saved && saved > nowUtc ? saved - nowUtc : null;
    }
}

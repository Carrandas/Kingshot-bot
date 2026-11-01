using System;

namespace Bot.Core.Utils
{
    /// <summary>Match timeout for every Regex in the bot (they run on OCR text, so a runaway match must not hang a task).</summary>
    internal static class RegexTimeouts
    {
        public static readonly TimeSpan Default = TimeSpan.FromSeconds(2);
    }
}

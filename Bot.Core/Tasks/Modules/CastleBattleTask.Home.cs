using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Services;
using Bot.Core.Utils;
using OpenCvSharp;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Rectangle = System.Drawing.Rectangle;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// The city's spot before the teleport (prep): read from the map's coordinate pill ("#196 X:588 Y:592") right after
    /// Town > World, which centres the map on our city. Saved per character with a screenshot, so the city can move back
    /// after the battle (by hand for now; there is no go-back button for a battle in our own kingdom, user 2026-11-21).
    /// When the teleport back is automated: the old spot can be taken by a beast or a Terror, then retry later.
    /// </summary>
    public partial class CastleBattleTask
    {
        internal static readonly Rectangle CoordinatesArea = new(245, 1050, 215, 38);   // "#196 X:588 Y:592", without the magnifier and the star

        /// <summary>The coordinates in the map's pill (white text on green); null when unreadable</summary>
        internal static (int X, int Y)? ReadCityCoordinates(byte[] screenshot) =>
            ParseCoordinates(MaskedTextOcr.Read(screenshot, CoordinatesArea, new Scalar(0, 0, 200), new Scalar(180, 70, 255), "0123456789#XY:"));

        /// <summary>"#196 X:588 Y:592" → (588, 592): the last two numbers after the kingdom's</summary>
        internal static (int X, int Y)? ParseCoordinates(string text)
        {
            var numbers = Regex.Matches(text ?? "", @"\d+", RegexOptions.None, RegexTimeouts.Default)
                .Select(m => int.TryParse(m.Value, out int n) ? n : -1).ToList();
            if (numbers.Count < 3) return null;
            int x = numbers[^2], y = numbers[^1];
            return x is >= 0 and <= MaxCoordinate && y is >= 0 and <= MaxCoordinate ? (x, y) : null;
        }

        private const int MaxCoordinate = 1200;

        /// <summary>Before the teleport: Town > World, read and save where the city stands (kept when this battle's spot is saved already)</summary>
        private async Task<string> SaveHomeSpotAsync(AccountSettings account, string character, CastleBattleSettings settings, LogService logger, CancellationToken cancellationToken)
        {
            int instance = account.InstanceNumber;
            var locator = new LocatorService(logger, account, TaskType);
            await locator.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
            await locator.EnsureViewAsync(ViewType.MapView, instance, cancellationToken);
            await Task.Delay(1000, cancellationToken);

            var shot = await TakeScreenshotAsync(instance, logger);
            var spot = shot == null ? null : ReadCityCoordinates(shot);
            await SaveDebugScreenshotAsync(instance, logger, $"home-{CharacterRoster.NormalizeKey(character)}-{DateTime.Now:yyyyMMdd-HHmmss}.png", Path.Combine("screenshots", "castlebattle"));
            if (spot is not { } s)
            {
                logger.LogWarning($"[{character}] Castle Battle: could not read the city's coordinates before the teleport (screenshot in logs/screenshots/castlebattle)");
                return "home spot not read; ";
            }
            var battle = CastleBattleStatus.BattleStartUtc ?? DateTime.UtcNow;
            if (!settings.SetHomeSpotFor(character, s.X, s.Y, battle)) return $"home spot {settings.HomeSpotFor(character)} (saved before); ";
            logger.LogInfo($"[{character}] Castle Battle: city stands at X:{s.X} Y:{s.Y} before the teleport");
            return $"home spot X:{s.X} Y:{s.Y}; ";
        }
    }
}

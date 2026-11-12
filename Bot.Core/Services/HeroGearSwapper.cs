using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Utils;
using OpenCvSharp;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace Bot.Core.Services
{
    /// <summary>
    /// Moves the gear from one hero to another on the Hero screens: Heroes (bottom bar of the map) > first card > Gear tab,
    /// then the &gt; arrow steps through the heroes (about 1 s each, the ring wraps) until the title shows the wanted name;
    /// Unequip All on the first hero, Quick Equip on the second (it dresses the hero with the best free gear).
    /// Both buttons are greyed out when there is nothing to do, so a repeated swap changes nothing.
    /// Taps are only made on a hero whose title was read, so the gear never goes to a neighbour.
    /// </summary>
    public sealed class HeroGearSwapper
    {
        private static readonly Point HeroesButton = new(189, 1240);
        private static readonly Point FirstCard = new(110, 230);
        private static readonly Point GearTab = new(587, 1240);
        private static readonly Point NextArrow = new(665, 705);
        private static readonly Point UnequipAll = new(200, 1112);
        private static readonly Point QuickEquip = new(517, 1112);
        private static readonly Point Back = new(48, 40);

        // White title text between the back arrow and the top right; the S-rank badge beside it is gold, so the mask drops it
        private static readonly Rectangle TitleArea = new(130, 5, 460, 65);
        private static readonly Scalar TitleLow = new(0, 0, 238), TitleHigh = new(180, 40, 255);
        private const string TitleLetters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz& ";

        private const int MaxSteps = 40;   // the ring has 37 heroes
        private static readonly TimeSpan StepDelay = TimeSpan.FromMilliseconds(1100);

        private readonly Func<Point, Task> _tap;
        private readonly Func<Task<byte[]?>> _screenshot;
        private readonly LogService _logger;
        private readonly CancellationToken _token;

        public HeroGearSwapper(Func<Point, Task> tap, Func<Task<byte[]?>> screenshot, LogService logger, CancellationToken token)
        {
            _tap = tap;
            _screenshot = screenshot;
            _logger = logger;
            _token = token;
        }

        /// <summary>The hero named in the title of a Hero screen's screenshot ("" when none reads)</summary>
        public static string ReadTitle(byte[] screenshot) =>
            MaskedTextOcr.Read(screenshot, TitleArea, TitleLow, TitleHigh, TitleLetters);

        /// <summary>Gear from <paramref name="from"/> to <paramref name="to"/>; null when done, else why not. Ends on the map.</summary>
        public async Task<string?> MoveGearAsync(string from, string to)
        {
            string? problem = null;
            try
            {
                await TapAsync(HeroesButton, 2500);
                await TapAsync(FirstCard, 2500);
                await TapAsync(GearTab, 1500);
                problem = await PressOnAsync(from, UnequipAll) ?? await PressOnAsync(to, QuickEquip);
            }
            finally
            {
                // hero screen > list > map (a screen that is not there ignores the tap on its back arrow area at worst)
                await TapAsync(Back, 1500);
                await TapAsync(Back, 1500);
            }
            return problem;
        }

        /// <summary>Steps to the hero and taps the button there</summary>
        private async Task<string?> PressOnAsync(string hero, Point button)
        {
            if (!await FindHeroAsync(hero)) return $"hero {hero} not found";
            await TapAsync(button, 2000);
            return null;
        }

        private async Task<bool> FindHeroAsync(string hero)
        {
            string last = "";
            for (int step = 0; step <= MaxSteps; step++)
            {
                _token.ThrowIfCancellationRequested();
                var shot = await _screenshot();
                last = shot == null ? "" : ReadTitle(shot);
                if (HeroCatalog.SameHero(last, hero)) return true;
                await TapAsync(NextArrow, (int)StepDelay.TotalMilliseconds);
            }
            _logger.LogWarning($"Hero gear: {hero} not found in {MaxSteps} steps (last title read: '{last}')");
            return false;
        }

        private async Task TapAsync(Point point, int delayMs)
        {
            await _tap(point);
            await Task.Delay(delayMs, _token);
        }
    }
}

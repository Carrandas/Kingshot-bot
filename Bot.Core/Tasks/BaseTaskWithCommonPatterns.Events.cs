using Bot.Core.Logging;
using Bot.Core.Tasks.Modules;
using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Tasks
{
    /// <summary>The city's (or map's) Events screen with its scrolling tab strip, shared by the event tasks (Governor Duel, Champion's Way)</summary>
    public abstract partial class BaseTaskWithCommonPatterns
    {
        // The Events icon, shared with Alliance Mobilization (animated, often with a red dot)
        private static readonly string EventsIconTemplate = Path.Combine("alliancemobilization", AllianceMobilizationTask.EventsButtonTemplate);
        /// <summary>The Events screen's tab strip (labels of the unselected tabs)</summary>
        protected static readonly Rectangle EventsTabStripArea = new(0, 90, 720, 110);
        // 260 px per page: 8 pages reach the strip's far end (Champion's Way sat ~9 tabs in on llsa, past 4 pages; 2026-11-25)
        private const int MaxEventsStripPages = 8;

        /// <summary>Taps the Events icon (top right, city or map); false when it isn't found</summary>
        protected async Task<bool> OpenEventsScreenAsync(int instance, LogService logger, CancellationToken ct)
        {
            var icon = (found: false, rect: Rectangle.Empty, confidence: 0.0);
            for (int attempt = 0; attempt < 3 && !icon.found; attempt++)
            {
                if (attempt > 0) await Task.Delay(1000, ct);
                icon = await MatchAsync(EventsIconTemplate, AllianceMobilizationTask.EventsButtonArea, AllianceMobilizationTask.EventsThreshold, instance, logger);
            }
            if (!icon.found)
            {
                logger.LogWarning($"{Name}: no Events icon (best {icon.confidence:F2})");
                return false;
            }
            await ClickCenterInRectAsync(instance, logger, icon.rect);
            await Task.Delay(2500, ct);
            return true;
        }

        /// <summary>
        /// On the Events screen: the tab whose label matches <paramref name="tabTemplate"/>. The screen opens on the tab used
        /// last; otherwise the strip is paged from its start (slow drags: it shifts as events come and go) for the label.
        /// </summary>
        protected async Task<bool> OpenEventsTabAsync(string tabTemplate, double threshold, Func<Task<bool>> isOnTab, int instance, LogService logger, CancellationToken ct)
        {
            // opened on the wanted tab: its label is hidden (a selected tab shows only its icon) and the page can draw a moment late
            if (await isOnTab() || await WaitForAsync(isOnTab, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(750), ct)) return true;
            // two passes over the strip: one pass missed Champion's Way's label once while the next found it (2026-11-25)
            for (int pass = 0; pass < 2; pass++)
            {
                var tab = await PageStripForTabAsync(tabTemplate, threshold, instance, logger, ct);
                if (tab != null)
                {
                    await ClickCenterInRectAsync(instance, logger, tab.Value);
                    return await WaitForAsync(isOnTab, TimeSpan.FromSeconds(4), TimeSpan.FromMilliseconds(500), ct);
                }
            }
            return await isOnTab();
        }

        /// <summary>Drags the strip to its start, then pages it (slow drags) until the label shows; null when it never did</summary>
        private async Task<Rectangle?> PageStripForTabAsync(string tabTemplate, double threshold, int instance, LogService logger, CancellationToken ct)
        {
            for (int i = 0; i < 3; i++)
            {
                await SwipeAsync(instance, logger, new Point(150, 150), new Point(620, 150), 400);
                await Task.Delay(500, ct);
            }
            for (int page = 0; page < MaxEventsStripPages; page++)
            {
                var tab = await MatchAsync(tabTemplate, EventsTabStripArea, threshold, instance, logger);
                if (tab.found) return tab.rect;
                await SwipeAsync(instance, logger, new Point(620, 150), new Point(360, 150), 1500);
                await Task.Delay(800, ct);
            }
            return null;
        }
    }
}

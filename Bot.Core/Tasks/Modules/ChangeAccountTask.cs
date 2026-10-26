using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.ImageDetection;
using Bot.Core.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Tesseract;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Runs the task list once on every enabled character of the instance's game login.
    /// TaskManager puts this task at both ends of the list:
    /// - at the start it scans the Characters screen and, if the active character is disabled, switches to an enabled one;
    /// - at the end it switches to the next enabled character that has not run yet this cycle and restarts the list.
    /// The scan also refreshes <see cref="AccountSettings.Characters"/>, which the settings show with a tick per character.
    /// </summary>
    public class ChangeAccountTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.ChangeAccount;
        public override string Name => "Change Account";

        // Normalized names of the characters whose task list has started this cycle, per instance
        private static readonly ConcurrentDictionary<int, HashSet<string>> _startedThisCycle = new();

        // UI element coordinates
        private static readonly Point ProfileButtonPoint = new Point(40, 41);
        private static readonly Rectangle SettingsButtonArea = new Rectangle(560, 1221, 117, 38);  // 560,1221 to 677,1259
        private static readonly Rectangle CharactersButtonArea = new Rectangle(53, 293, 73, 72);   // 53,293 to 126,365
        private static readonly Rectangle CharacterListArea = new Rectangle(60, 220, 600, 900);    // Characters panel below "Create new character"
        private const int ListBottomY = 1100;                                                      // the panel ends here; lower rows need a scroll

        // Each character row has a "Town Center Level" label: 0.975-1.00 on beige and yellow (active) rows, <= 0.55 elsewhere
        private const string RowLabelTemplate = "town-center-level.png";
        private const double RowLabelThreshold = 0.8;
        // Kingdom headers: the word "Kingdom" (the "#754" after it differs per header), 0.98-1.00 on both header colours, <= 0.52 elsewhere
        private const string KingdomTemplate = "kingdom.png";
        private const double KingdomThreshold = 0.75;
        // Green tick on the active row: 0.996 there, <= 0.36 on the other rows
        private const double TickThreshold = 0.8;

        // Offsets from the row label's top-left corner (label at x 228)
        private const int RowTopOffset = -56, RowHeight = 141;          // row card
        private const int NameTopOffset = -40, NameHeight = 31;         // name line above the label (text rows 6-29; the level badge starts at 31)
        private static readonly (int Left, int Width) NameColumn = (226, 326);  // starts right of the avatar frame, ends left of the tick
        private const int KingdomDigitsOffset = 22, KingdomDigitsWidth = 80;    // digits after "Kingdom #", from the template's right edge
        private static readonly (int Left, int Width) TapColumn = (230, 250);

        protected override string GetImageFolderName() => "ChangeAccount";

        private sealed record CharacterRow(string Name, int? Kingdom, Rectangle Bounds, Rectangle TapArea, bool IsActive);

        public override async Task<bool> CanExecuteAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken = default)
        {
            await Task.Delay(1, cancellationToken);
            return true;
        }

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken,
            bool isReRun = false,
            IUserNotificationService? userNotifications = null)
        {
            try
            {
                var started = _startedThisCycle.GetOrAdd(account.InstanceNumber, _ => new HashSet<string>());

                var openError = await OpenCharactersScreenAsync(account, logger, cancellationToken);
                if (openError != null) return TaskExecutionDetails.Failed(openError);

                var rows = await ScanCharactersAsync(account, logger, cancellationToken);
                var active = rows.FirstOrDefault(r => r.IsActive);
                if (active == null || CharacterRoster.NormalizeKey(active.Name).Length == 0)
                {
                    await CloseCharactersScreenAsync(account, logger, cancellationToken);
                    return TaskExecutionDetails.Failed(rows.Count == 0
                        ? "No characters found on the Characters screen"
                        : "Could not tell which character is active");
                }

                CharacterRoster.Merge(account, rows.Select(r => (r.Name, r.Kingdom)));

                var activeKey = CharacterRoster.NormalizeKey(active.Name);
                bool activeEnabled = CharacterRoster.IsEnabled(account, active.Name, active.Kingdom);
                logger.LogInfo($"[{account.AccountName}] Active character: {active.Name}{KingdomSuffix(active.Kingdom)} ({(activeEnabled ? "enabled" : "disabled")})");

                // Start of the list: run the tasks on the active character
                if (activeEnabled && !started.Contains(activeKey))
                {
                    started.Add(activeKey);
                    await CloseCharactersScreenAsync(account, logger, cancellationToken);
                    return TaskExecutionDetails.Succeeded($"running tasks on {active.Name}");
                }

                // End of the list (or a disabled character is active): go to the next enabled character that has not run yet
                var next = rows.FirstOrDefault(r =>
                    !r.IsActive &&
                    CharacterRoster.NormalizeKey(r.Name).Length > 0 &&
                    !started.Contains(CharacterRoster.NormalizeKey(r.Name)) &&
                    CharacterRoster.IsEnabled(account, r.Name, r.Kingdom));

                if (next == null)
                {
                    await CloseCharactersScreenAsync(account, logger, cancellationToken);
                    if (!activeEnabled)
                    {
                        return TaskExecutionDetails.Failed($"{active.Name} is disabled and no other enabled character is left to switch to");
                    }
                    return TaskExecutionDetails.Succeeded($"all enabled characters done ({started.Count})");
                }

                logger.LogInfo($"[{account.AccountName}] Switching from {active.Name} to {next.Name}{KingdomSuffix(next.Kingdom)}...");
                if (!await ClickRandomInRectAsync(account.InstanceNumber, logger, next.TapArea))
                {
                    return TaskExecutionDetails.Failed("Failed to click character row");
                }
                await Task.Delay(1000, cancellationToken);

                logger.LogInfo($"[{account.AccountName}] Looking for confirm button...");
                if (!await FindAndClickImageAsync("confirm.png", account.InstanceNumber, logger, threshold: 0.7))
                {
                    return TaskExecutionDetails.Failed("Could not find confirm button");
                }

                logger.LogInfo($"[{account.AccountName}] Waiting for loading screen...");
                await Task.Delay(3000, cancellationToken);  // Initial delay to ensure loading screen appears

                // Clear cached account ID for this instance since we've switched accounts
                AccountDetectionTask.ClearCacheForInstance(account.InstanceNumber);
                logger.LogInfo($"[{account.AccountName}] Cleared cached account ID for instance {account.InstanceNumber}");

                // Signal that tasks need to be restarted from loading screen
                return new TaskExecutionDetails(true, message: $"Switched to {next.Name}, restarting tasks...", requiresTaskRestart: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error during account change: {ex.Message}");
                return TaskExecutionDetails.Failed($"Error during account change: {ex.Message}");
            }
        }

        private static string KingdomSuffix(int? kingdom) => kingdom.HasValue ? $" (#{kingdom})" : string.Empty;

        /// <summary>Profile > Settings > Characters. Returns an error message, or null when the Characters screen is open.</summary>
        private async Task<string?> OpenCharactersScreenAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            var locator = new LocatorService(logger, account);
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);

            logger.LogInfo($"[{account.AccountName}] Clicking profile button...");
            if (!await ClickAsync(account.InstanceNumber, logger, ProfileButtonPoint))
            {
                return "Failed to click profile button";
            }
            await Task.Delay(1000, cancellationToken);

            if (!await FindAndClickWithRecoveryAsync(account, logger, "settings.png", SettingsButtonArea, cancellationToken))
            {
                return "Could not find settings button";
            }
            await Task.Delay(1000, cancellationToken);

            if (!await FindAndClickWithRecoveryAsync(account, logger, "characters.png", CharactersButtonArea, cancellationToken))
            {
                return "Could not find characters button";
            }
            await Task.Delay(1000, cancellationToken);
            return null;
        }

        private async Task<bool> FindAndClickWithRecoveryAsync(AccountSettings account, LogService logger, string template, Rectangle searchArea, CancellationToken cancellationToken)
        {
            logger.LogInfo($"[{account.AccountName}] Looking for {template}...");
            if (await FindAndClickImageAsync(template, account.InstanceNumber, logger, threshold: 0.7, searchArea: searchArea))
            {
                return true;
            }

            logger.LogError($"[{account.AccountName}] Error: Failed to find {template}, calling locator module for recovery");
            try
            {
                await new LocatorService(logger, account).EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                logger.LogInfo($"[{account.AccountName}] Locator service completed view recovery, retrying {template} detection");
                if (await FindAndClickImageAsync(template, account.InstanceNumber, logger, threshold: 0.7, searchArea: searchArea))
                {
                    return true;
                }
                logger.LogError($"[{account.AccountName}] Could not find {template} even after locator recovery");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Locator service failed: {ex.Message}");
            }
            return false;
        }

        private async Task CloseCharactersScreenAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            await new LocatorService(logger, account).EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
        }

        /// <summary>
        /// Reads the visible rows of the Characters screen. The list can take a moment to fill, so it looks up to 3 times
        /// until the active row (green tick) is found.
        /// </summary>
        private async Task<List<CharacterRow>> ScanCharactersAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            var rows = new List<CharacterRow>();
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot != null)
                {
                    rows = ReadRows(screenshot, account, logger);
                    if (rows.Any(r => r.IsActive)) break;
                }
                logger.LogInfo($"[{account.AccountName}] Characters list not ready (attempt {attempt}/3, {rows.Count} rows)");
                await Task.Delay(1000, cancellationToken);
            }

            foreach (var row in rows)
            {
                logger.LogInfo($"[{account.AccountName}] Character row: '{row.Name}'{KingdomSuffix(row.Kingdom)}{(row.IsActive ? " [active]" : "")}");
            }
            return rows;
        }

        private List<CharacterRow> ReadRows(byte[] screenshot, AccountSettings account, LogService logger)
        {
            var labels = FindAll(screenshot, RowLabelTemplate, RowLabelThreshold);
            var headers = FindAll(screenshot, KingdomTemplate, KingdomThreshold);

            Point? tickCenter = null;
            var tick = _templateMatcher!.MatchTemplate(screenshot, Path.Combine(ImageTemplateFolder, "tick.png"),
                account.InstanceNumber, threshold: TickThreshold, searchArea: CharacterListArea);
            if (tick.found)
            {
                tickCenter = new Point(tick.matchRect.X + tick.matchRect.Width / 2, tick.matchRect.Y + tick.matchRect.Height / 2);
            }

            using var nameOcr = new OCRService(logger, new OCRConfiguration { ScaleFactor = 2.0f, PageSegMode = PageSegMode.SingleLine });
            using var digitOcr = new OCRService(logger, new OCRConfiguration { ScaleFactor = 2.0f, PageSegMode = PageSegMode.SingleLine, CharacterWhitelist = "0123456789" });

            using var image = OpenCvSharp.Cv2.ImDecode(screenshot, OpenCvSharp.ImreadModes.Color);

            // Kingdom number right of "Kingdom #": white digits on the tan header
            var kingdoms = headers
                .Select(h => (h.Y, Number: ParseInt(ReadMasked(image, digitOcr,
                    new Rectangle(h.Right + KingdomDigitsOffset, h.Y, KingdomDigitsWidth, h.Height),
                    new OpenCvSharp.Scalar(0, 0, 225), new OpenCvSharp.Scalar(180, 60, 255)))))
                .ToList();

            var rows = new List<CharacterRow>();
            foreach (var label in labels.OrderBy(l => l.Y))
            {
                var bounds = new Rectangle(CharacterListArea.X + 13, label.Y + RowTopOffset, 573, RowHeight);
                if (bounds.Bottom > ListBottomY)
                {
                    logger.LogWarning($"[{account.AccountName}] Character row at y={label.Y} runs past the visible list: rows below it need a scroll and are not handled yet");
                    continue;
                }

                var nameArea = new Rectangle(NameColumn.Left, label.Y + NameTopOffset, NameColumn.Width, NameHeight);
                // Dark brown name on a beige (V ~220) or yellow (V 255) row: keep the dark pixels
                var name = Regex.Replace(ReadMasked(image, nameOcr, nameArea,
                    new OpenCvSharp.Scalar(0, 0, 0), new OpenCvSharp.Scalar(180, 255, 175)), @"\s+", " ").Trim();
                name = FixAllianceTag(name);
                int? kingdom = kingdoms.Where(k => k.Y < bounds.Y).OrderByDescending(k => k.Y).Select(k => k.Number).FirstOrDefault();
                bool isActive = tickCenter.HasValue && bounds.Contains(tickCenter.Value);
                var tapArea = new Rectangle(TapColumn.Left, label.Y + NameTopOffset + 5, TapColumn.Width, 80);

                rows.Add(new CharacterRow(name, kingdom, bounds, tapArea, isActive));
            }
            return rows;
        }

        /// <summary>OCR of the pixels inside an HSV range, as white text on black (the OCR preprocessing loses coloured text on coloured rows).</summary>
        private static string ReadMasked(OpenCvSharp.Mat image, OCRService ocr, Rectangle area, OpenCvSharp.Scalar lower, OpenCvSharp.Scalar upper)
        {
            var roi = new OpenCvSharp.Rect(area.X, area.Y, area.Width, area.Height).Intersect(new OpenCvSharp.Rect(0, 0, image.Width, image.Height));
            if (roi.Width <= 0 || roi.Height <= 0) return string.Empty;
            using var region = new OpenCvSharp.Mat(image, roi);
            using var hsv = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.CvtColor(region, hsv, OpenCvSharp.ColorConversionCodes.BGR2HSV);
            using var mask = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.InRange(hsv, lower, upper, mask);
            using var bgr = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.CvtColor(mask, bgr, OpenCvSharp.ColorConversionCodes.GRAY2BGR);
            return ocr.ExtractTextFromScreenArea(bgr.ToBytes(".png"), new Rectangle(0, 0, roi.Width, roi.Height));
        }

        /// <summary>
        /// The OCR misreads the "]" of a 3-letter alliance tag as l, I, 1, | or J ("[KFClAsli") or drops it ("[KFCAsli").
        /// Both become "[KFC]Asli".
        /// </summary>
        private static string FixAllianceTag(string name)
        {
            if (name.Length > 5 && name[0] == '[' && name.IndexOf(']') < 0)
            {
                return "lI1|J)".IndexOf(name[4]) >= 0
                    ? name.Substring(0, 4) + "]" + name.Substring(5)
                    : name.Substring(0, 4) + "]" + name.Substring(4);
            }
            return name;
        }

        private static int? ParseInt(string text)
        {
            var digits = new string(text.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var n) ? n : null;
        }

        /// <summary>All non-overlapping matches of a template (grayscale) inside the Characters list.</summary>
        private List<Rectangle> FindAll(byte[] screenshot, string templateName, double threshold)
        {
            var found = new List<Rectangle>();
            using var image = OpenCvSharp.Cv2.ImDecode(screenshot, OpenCvSharp.ImreadModes.Grayscale);
            using var template = OpenCvSharp.Cv2.ImRead(Path.Combine(ImageTemplateFolder, templateName), OpenCvSharp.ImreadModes.Grayscale);
            if (image.Empty() || template.Empty()) return found;

            var area = new OpenCvSharp.Rect(CharacterListArea.X, CharacterListArea.Y, CharacterListArea.Width, CharacterListArea.Height)
                .Intersect(new OpenCvSharp.Rect(0, 0, image.Width, image.Height));
            using var region = new OpenCvSharp.Mat(image, area);
            using var result = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.MatchTemplate(region, template, result, OpenCvSharp.TemplateMatchModes.CCoeffNormed);

            while (true)
            {
                OpenCvSharp.Cv2.MinMaxLoc(result, out _, out double max, out _, out OpenCvSharp.Point loc);
                if (max < threshold) break;
                found.Add(new Rectangle(area.X + loc.X, area.Y + loc.Y, template.Width, template.Height));
                // Blank out this match so the next MinMaxLoc finds another row
                var blank = new OpenCvSharp.Rect(loc.X - template.Width / 2, loc.Y - template.Height / 2, template.Width, template.Height)
                    .Intersect(new OpenCvSharp.Rect(0, 0, result.Width, result.Height));
                result[blank].SetTo(new OpenCvSharp.Scalar(-1));
            }
            return found;
        }

        // Clear the started characters of an instance (called when instance shuts down, i.e. at the end of a cycle)
        public static void ClearInstanceTracking(int instanceNumber)
        {
            _startedThisCycle.TryRemove(instanceNumber, out _);
        }

        public static void ResetInstanceTracking()
        {
            _startedThisCycle.Clear();
        }

        protected override Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            if (!Directory.Exists(ImageTemplateFolder))
            {
                Directory.CreateDirectory(ImageTemplateFolder);
            }
            return Task.CompletedTask;
        }
    }
}

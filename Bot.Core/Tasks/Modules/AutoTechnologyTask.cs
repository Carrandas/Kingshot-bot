using System;
using System.Threading;
using System.Threading.Tasks;
using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Services;
using Bot.Core.Utils;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Drawing;
using Tesseract;

namespace Bot.Core.Tasks.Modules
{
    public class AutoTechnologyTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.AutoTechnology;
        public override string Name => "Auto Technology";

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                logger.LogInfo($"[{account.AccountName}] Starting Auto Technology task");

                // Get settings
                var settings = GetTaskSettings(account);
                
                // First, look for and click menu-arrow.png
                var menuArrowFound = await FindAndClickImageAsync("menu-arrow.png", account.InstanceNumber, logger, 
                    threshold: 0.7);
                
                if (!menuArrowFound)
                {
                    logger.LogError($"[{account.AccountName}] Could not find menu-arrow.png");
                    return TaskExecutionDetails.Failed("Could not find menu arrow");
                }

                logger.LogInfo($"[{account.AccountName}] Clicked menu arrow, checking for idle status");
                await Task.Delay(1000, cancellationToken);

                // Use OCR to read text at coordinates 196,817 245,845 looking for "idle"
                var ocrText = await ReadTextFromSpecificRegionAsync(account.InstanceNumber, logger, 
                    new Rectangle(196, 817, 49, 28));

                if (string.IsNullOrEmpty(ocrText) || !ocrText.ToLower().Contains("idle"))
                {
                    logger.LogInfo($"[{account.AccountName}] 'Idle' not detected (OCR result: '{ocrText}'), clicking 467,551 and moving to next module");
                    
                    // Click 467,551 if idle is not detected
                    await ClickAsync(account.InstanceNumber, logger, new Point(467, 551));
                    await Task.Delay(500, cancellationToken);
                    
                    return new TaskExecutionDetails(true, message: "Technology not idle, skipped to next module");
                }

                logger.LogInfo($"[{account.AccountName}] 'Idle' detected, proceeding with technology research");
                
                // Click 407,818 since idle was detected
                await ClickAsync(account.InstanceNumber, logger, new Point(407, 818));
                await Task.Delay(500, cancellationToken);

                // Look for research-icon.png and click it (up to 2 seconds)
                if (!await ClickResearchIconAsync(account.InstanceNumber, logger, cancellationToken))
                {
                    logger.LogError($"[{account.AccountName}] Could not find research-icon.png within 2 seconds");
                    return TaskExecutionDetails.Failed("Could not find research icon");
                }

                logger.LogInfo($"[{account.AccountName}] Found and clicked research icon");
                await Task.Delay(1000, cancellationToken);

                // Click on the appropriate research type based on user settings
                var researchType = GetResearchTypeButton(settings.ResearchType);
                if (researchType == null)
                {
                    logger.LogError($"[{account.AccountName}] Invalid research type: {settings.ResearchType}");
                    return TaskExecutionDetails.Failed("Invalid research type configured");
                }
                var (researchTypeLocation, researchTypeName) = researchType.Value;

                logger.LogInfo($"[{account.AccountName}] Clicking {researchTypeName} research type at {researchTypeLocation}");
                await ClickAsync(account.InstanceNumber, logger, researchTypeLocation);
                await Task.Delay(1000, cancellationToken);

                // Use improved OCR to detect "x/x" pattern and find the highest one on screen
                logger.LogInfo($"[{account.AccountName}] Scanning for x/x progress patterns using improved OCR");
                
                var progressPattern = await FindProgressPatternWithImprovedOCRAsync(account.InstanceNumber, logger);
                
                if (progressPattern.HasValue)
                {
                    logger.LogInfo($"[{account.AccountName}] Found progress pattern, clicking at {progressPattern.Value}");
                    await ClickAsync(account.InstanceNumber, logger, progressPattern.Value);
                    await Task.Delay(1000, cancellationToken);
                    
                    return new TaskExecutionDetails(true, message: $"Successfully started {researchTypeName} research");
                }
                else
                {
                    logger.LogWarning($"[{account.AccountName}] Could not find any x/x progress pattern on screen");
                    return TaskExecutionDetails.Failed("Could not find research progress pattern");
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogInfo($"[{account.AccountName}] Auto Technology task was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in Auto Technology task: {ex.Message}");
                return TaskExecutionDetails.Failed($"Auto Technology failed: {ex.Message}");
            }
        }

        private async Task<bool> ClickResearchIconAsync(int instanceNumber, LogService logger, CancellationToken cancellationToken)
        {
            var researchIconFound = false;
            var attempts = 0;
            while (!researchIconFound && attempts < 4)
            {
                researchIconFound = await FindAndClickImageAsync("research-icon.png", instanceNumber, logger,
                    threshold: 0.7);
                if (!researchIconFound)
                {
                    await Task.Delay(500, cancellationToken);
                    attempts++;
                }
            }
            return researchIconFound;
        }

        private static (Point location, string name)? GetResearchTypeButton(ResearchType researchType)
        {
            switch (researchType)
            {
                case ResearchType.Growth:
                    return (new Point(130, 116), "Growth");
                case ResearchType.Economy:
                    return (new Point(361, 123), "Economy");
                case ResearchType.Battle:
                    return (new Point(585, 120), "Battle");
                default:
                    return null;
            }
        }

        private static Point? ScanForHighestProgressPattern(Bitmap bitmap, OcrConfiguration ocrConfig, Regex regex, LogService logger)
        {
            Point? highestMatch = null;
            int highestY = int.MaxValue;

            // Scan in larger, more targeted regions
            for (int y = 100; y < Math.Min(bitmap.Height - 100, 700); y += 60) // Larger steps
            {
                for (int x = 50; x < Math.Min(bitmap.Width - 150, 650); x += 80) // Larger regions
                {
                    var region = new Rectangle(x, y, 120, 50); // Larger OCR regions
                    if (!IsProgressPatternAt(bitmap, region, ocrConfig, regex, logger, out var ocrText))
                    {
                        continue;
                    }

                    // Check if this is higher on screen (lower Y value)
                    if (y < highestY)
                    {
                        highestY = y;
                        highestMatch = new Point(x + region.Width / 2, y + region.Height / 2);
                        logger.LogInfo($"🎯 New highest progress pattern at Y={y}: {ocrText}");
                    }
                }
            }
            return highestMatch;
        }

        /// <summary>OCRs one region and logs what it found. True when the text looks like an x/x progress pattern.</summary>
        private static bool IsProgressPatternAt(Bitmap bitmap, Rectangle region, OcrConfiguration ocrConfig, Regex regex, LogService logger, out string ocrText)
        {
            ocrText = string.Empty;
            try
            {
                ocrText = OcrBitmapRegion(bitmap, region, ocrConfig, logger);

                if (string.IsNullOrEmpty(ocrText))
                {
                    // Log empty results occasionally for debugging scan coverage
                    if ((region.X + region.Y) % 240 == 0) // Log every few empty regions
                    {
                        logger.LogInfo($"📋 Empty OCR result at {region}");
                    }
                    return false;
                }

                // Clean up the OCR text
                string originalText = ocrText;
                ocrText = ocrText.Trim().Replace(" ", "").Replace("\n", "").Replace("\r", "");

                // Debug logging - show all non-empty OCR results
                if (ocrText.Length > 0)
                {
                    logger.LogInfo($"OCR extracted text: '{ocrText}' (original: '{originalText}') at {region}");
                }

                if (regex.IsMatch(ocrText))
                {
                    logger.LogInfo($"✅ REGEX MATCH! Found progress pattern '{ocrText}' at region {region}");
                    return true;
                }

                if (ocrText.Length > 0)
                {
                    LogPatternAnalysis(ocrText, region, logger);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning($"OCR error at region {region}: {ex.Message}");
            }
            return false;
        }

        private static void LogPatternAnalysis(string ocrText, Rectangle region, LogService logger)
        {
            // Enhanced debugging for pattern analysis
            bool hasSlash = ocrText.Contains("/");
            bool hasDigit = ocrText.Any(char.IsDigit);
            bool startsWithDigit = char.IsDigit(ocrText[0]);

            logger.LogInfo($"📊 OCR Analysis: '{ocrText}' | HasSlash: {hasSlash} | HasDigit: {hasDigit} | StartsWithDigit: {startsWithDigit}");

            // Try some pattern variations to see what might work
            var relaxedPatterns = new[]
            {
                @"\d+/\d+",        // Basic x/x
                @"\d+\/\d+",       // Escaped slash
                @"\d+.\d+",        // Any character between digits
                @"\d+[\s/]\d+",    // Space or slash between digits
            };

            foreach (var pattern in relaxedPatterns.Where(pattern => Regex.IsMatch(ocrText, pattern, RegexOptions.None, Bot.Core.Utils.RegexTimeouts.Default)))
            {
                logger.LogInfo($"🔍 Alternative pattern '{pattern}' matches: {ocrText}");
            }

            if (hasSlash || (hasDigit && ocrText.Length >= 3))
            {
                logger.LogInfo($"🔍 Potential pattern candidate: '{ocrText}' at {region}");
            }
        }

        private static string OcrBitmapRegion(Bitmap bitmap, Rectangle region, OcrConfiguration ocrConfig, LogService logger)
        {
            using var ocr = new OcrService(logger, ocrConfig);

            // Convert region to byte array for OCR
            using var regionMs = new System.IO.MemoryStream();
            using (var regionBitmap = bitmap.Clone(region, bitmap.PixelFormat))
            {
                regionBitmap.Save(regionMs, System.Drawing.Imaging.ImageFormat.Png);
            }
            return ocr.ExtractTextFromScreenArea(regionMs.ToArray(), new Rectangle(0, 0, region.Width, region.Height));
        }

        private async Task<Point?> FindProgressPatternWithImprovedOCRAsync(int instanceNumber, LogService logger)
        {
            try
            {
                var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
                if (screenshot == null)
                {
                    logger.LogError("Could not take screenshot for progress pattern detection");
                    return null;
                }

                using var ms = new System.IO.MemoryStream(screenshot);
                using var bitmap = new Bitmap(ms);
                
                // Create optimized OCR configuration specifically for x/x patterns
                var ocrConfig = new OcrConfiguration
                {
                    ScaleFactor = 5, // Higher scale for better recognition
                    AdaptiveC = 8, // Better contrast adaptation
                    MedianBlurKernelSize = 3, // Reduce noise
                    CharacterWhitelist = "0123456789/", // Only numbers and slash
                    PageSegMode = PageSegMode.SingleWord // Better for short patterns
                };

                var regex = new Regex(@"\b\d+/\d+\b", RegexOptions.IgnoreCase, Bot.Core.Utils.RegexTimeouts.Default);

                logger.LogInfo($"Starting improved OCR scan for x/x patterns...");

                var highestMatch = ScanForHighestProgressPattern(bitmap, ocrConfig, regex, logger);

                if (highestMatch.HasValue)
                {
                    logger.LogInfo($"Selected highest progress pattern at {highestMatch.Value}");
                    return highestMatch;
                }
                else
                {
                    logger.LogWarning("No x/x progress patterns found with improved OCR, trying adjacent region detection...");
                    
                    // Try a different approach - look for adjacent regions with numbers
                    var adjacentMatch = await FindAdjacentNumberPatternsAsync(instanceNumber, logger);
                    
                    if (adjacentMatch.HasValue)
                    {
                        logger.LogInfo($"Found adjacent number pattern at {adjacentMatch.Value}");
                        return adjacentMatch;
                    }
                    
                    logger.LogWarning("No progress patterns found with any method");
                    return null;
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"Error in improved OCR progress pattern detection: {ex.Message}");
                return null;
            }
        }

        private static Point? FindAdjacentPatternAt(Bitmap bitmap, int x, int y, OcrConfiguration numberOcrConfig, OcrConfiguration slashOcrConfig, LogService logger)
        {
            try
            {
                // Check three adjacent small regions: [num][/][num]
                var leftRegion = new Rectangle(x, y, 30, 25);
                var middleRegion = new Rectangle(x + 25, y, 20, 25);
                var rightRegion = new Rectangle(x + 40, y, 30, 25);

                string leftText = OcrBitmapRegion(bitmap, leftRegion, numberOcrConfig, logger).Trim(); // number
                string middleText = OcrBitmapRegion(bitmap, middleRegion, slashOcrConfig, logger).Trim(); // slash
                string rightText = OcrBitmapRegion(bitmap, rightRegion, numberOcrConfig, logger).Trim(); // number

                // Check if we found a pattern
                bool hasLeftNum = !string.IsNullOrEmpty(leftText) && leftText.All(char.IsDigit);
                bool hasSlash = middleText.Contains("/");
                bool hasRightNum = !string.IsNullOrEmpty(rightText) && rightText.All(char.IsDigit);

                if (hasLeftNum || hasSlash || hasRightNum)
                {
                    logger.LogInfo($"🔍 Adjacent regions at Y={y}: Left='{leftText}' Middle='{middleText}' Right='{rightText}'");
                    logger.LogInfo($"    Analysis: LeftNum={hasLeftNum} Slash={hasSlash} RightNum={hasRightNum}");
                }

                if (hasLeftNum && hasSlash && hasRightNum)
                {
                    string fullPattern = $"{leftText}/{rightText}";
                    logger.LogInfo($"🎯 Found adjacent pattern: {fullPattern} at Y={y}");
                    return new Point(x + 35, y + 12); // Center of the pattern
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Error in adjacent pattern detection at {x},{y}: {ex.Message}");
            }
            return null;
        }

        private async Task<Point?> FindAdjacentNumberPatternsAsync(int instanceNumber, LogService logger)
        {
            try
            {
                logger.LogInfo("🔍 Trying adjacent number pattern detection...");
                
                var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
                if (screenshot == null) return null;

                using var ms = new System.IO.MemoryStream(screenshot);
                using var bitmap = new Bitmap(ms);

                var numberOcrConfig = new OcrConfiguration
                {
                    ScaleFactor = 4,
                    AdaptiveC = 7,
                    MedianBlurKernelSize = 1,
                    CharacterWhitelist = "0123456789", // Only numbers
                    PageSegMode = PageSegMode.SingleChar
                };

                var slashOcrConfig = new OcrConfiguration
                {
                    ScaleFactor = 4,
                    AdaptiveC = 7,
                    MedianBlurKernelSize = 1,
                    CharacterWhitelist = "/", // Only slash
                    PageSegMode = PageSegMode.SingleChar
                };

                // Look for patterns where we have: [number] [slash] [number] in nearby regions
                for (int y = 100; y < Math.Min(bitmap.Height - 50, 600); y += 40)
                {
                    for (int x = 50; x < Math.Min(bitmap.Width - 150, 600); x += 60)
                    {
                        var found = FindAdjacentPatternAt(bitmap, x, y, numberOcrConfig, slashOcrConfig, logger);
                        if (found.HasValue) return found;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                logger.LogError($"Error in adjacent pattern detection: {ex.Message}");
                return null;
            }
        }

        private async Task<string> ReadTextFromSpecificRegionAsync(int instanceNumber, LogService logger, Rectangle region)
        {
            try
            {
                var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
                if (screenshot == null) return string.Empty;

                using var ms = new System.IO.MemoryStream(screenshot);
                using var bitmap = new Bitmap(ms);
                
                // OCR configuration optimized for "idle" text detection
                var ocrConfig = new OcrConfiguration
                {
                    ScaleFactor = 4,
                    AdaptiveC = 6,
                    MedianBlurKernelSize = 1,
                    CharacterWhitelist = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ", // Only letters for "idle"
                    PageSegMode = PageSegMode.SingleWord
                };

                using var ocr = new OcrService(logger, ocrConfig);
                
                // Convert region to byte array for OCR
                using var regionMs = new System.IO.MemoryStream();
                using (var regionBitmap = bitmap.Clone(region, bitmap.PixelFormat))
                {
                    regionBitmap.Save(regionMs, System.Drawing.Imaging.ImageFormat.Png);
                }
                var regionBytes = regionMs.ToArray();
                
                string ocrText = ocr.ExtractTextFromScreenArea(regionBytes, new Rectangle(0, 0, region.Width, region.Height));
                
                return ocrText?.Trim() ?? string.Empty;
            }
            catch (Exception ex)
            {
                logger.LogError($"Error reading text from region: {ex.Message}");
                return string.Empty;
            }
        }

        private static AutoTechnologySettings GetTaskSettings(AccountSettings account)
        {
            if (!account.TaskSettings.TryGetValue("AutoTechnology", out var settingsJson))
            {
                return new AutoTechnologySettings();
            }

            try
            {
                return JsonSerializer.Deserialize<AutoTechnologySettings>(settingsJson) ?? new AutoTechnologySettings();
            }
            catch
            {
                return new AutoTechnologySettings();
            }
        }
    }

    public enum ResearchType
    {
        Growth,
        Economy,
        Battle
    }

    public class AutoTechnologySettings
    {
        public ResearchType ResearchType { get; set; } = ResearchType.Growth;
        public bool IsEnabled { get; set; } = true;
    }
}
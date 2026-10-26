using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;
using Bot.Core.Logging;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;
using Rectangle = System.Drawing.Rectangle;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Bot.Core.ImageDetection
{
    public sealed class ColorTemplateMatchingService : IDisposable
    {
        private readonly LogService _logger;
        // Template caches are shared by every instance (tasks each create their own matcher)
        private static readonly ConcurrentDictionary<string, (Mat BGR, Mat HSV, DateTime LastUsed, int RefCount)> _templateCache = new();
        private static readonly ConcurrentDictionary<(string, double), Lazy<ScaledColorTemplate>> _scaledTemplateCache = new();
        private bool _disposed;
        private const int MAX_CACHE_SIZE = 500;
        private const int CACHE_CLEANUP_INTERVAL_MS = 60000;
        private static readonly System.Threading.Timer _cleanupTimer = new System.Threading.Timer(CleanupCaches, null, CACHE_CLEANUP_INTERVAL_MS, CACHE_CLEANUP_INTERVAL_MS);
        private static readonly object _cacheLock = new object();
        
        public static readonly double[] StandardScales = { 0.8, 0.9, 1.0, 1.1, 1.2 };
        private const double HSV_WEIGHT = 0.7;
        private const double BGR_WEIGHT = 0.3;

        public ColorTemplateMatchingService(LogService logger)
        {
            _logger = logger;
        }

        private static void CleanupCaches(object? state)
        {
            try
            {
                lock (_cacheLock)
                {
                    while (_templateCache.Count > MAX_CACHE_SIZE)
                    {
                        var oldestKey = _templateCache
                            .Where(kvp => kvp.Value.RefCount == 0)
                            .OrderBy(kvp => kvp.Value.LastUsed)
                            .Select(kvp => kvp.Key)
                            .FirstOrDefault();
                        
                        if (!string.IsNullOrEmpty(oldestKey) && _templateCache.TryRemove(oldestKey, out var oldValue))
                        {
                            oldValue.BGR?.Dispose();
                            oldValue.HSV?.Dispose();
                        }
                        else
                        {
                            break;
                        }
                    }

                    var cutoff = DateTime.UtcNow.AddHours(-1);
                    
                    foreach (var kvp in _templateCache.Where(kvp => kvp.Value.LastUsed < cutoff && kvp.Value.RefCount == 0))
                    {
                        if (_templateCache.TryRemove(kvp.Key, out var oldValue))
                        {
                            oldValue.BGR?.Dispose();
                            oldValue.HSV?.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during color template cache cleanup: {ex.Message}");
            }
        }

        /// <summary>
        /// Cache key that changes when the template file is re-cut, so the shared cache
        /// picks up an edited template without restarting the app.
        /// </summary>
        private static string TemplateCacheKey(string templatePath)
        {
            return $"{templatePath}|{File.GetLastWriteTimeUtc(templatePath).Ticks}";
        }

        private static (Mat BGR, Mat HSV) GetOrLoadTemplate(string templatePath, string cacheKey)
        {
            var now = DateTime.UtcNow;
            
            lock (_cacheLock)
            {
                var value = _templateCache.AddOrUpdate(
                    cacheKey,
                    _ =>
                    {
                        var templateBGR = Cv2.ImRead(templatePath, ImreadModes.Color);
                        if (templateBGR.Empty())
                        {
                            throw new InvalidOperationException($"Could not load template file: {templatePath}");
                        }

                        var templateHSV = new Mat();
                        Cv2.CvtColor(templateBGR, templateHSV, ColorConversionCodes.BGR2HSV);
                        return (templateBGR, templateHSV, now, 1);
                    },
                    (_, old) =>
                    {
                        return (old.BGR, old.HSV, now, old.RefCount + 1);
                    });
                
                return (value.BGR, value.HSV);
            }
        }

        public (bool found, Rectangle matchRect, double confidence) MatchTemplate(
            byte[] screenshot,
            string templatePath,
            double threshold = 0.55,
            double[]? scales = null,
            bool verboseLogging = false,
            Rectangle? searchArea = null)
        {
            scales ??= StandardScales;

            if (!File.Exists(templatePath))
            {
                _logger.LogError($"Template not found: {templatePath}");
                return (false, Rectangle.Empty, 0);
            }

            try
            {
                using var decoded = DecodedScreenshotCache.Acquire(screenshot);
                var bgrChannels = decoded.BgrChannels;
                var hsvChannels = decoded.HsvChannels;

                if (searchArea.HasValue)
                {
                    var rect = searchArea.Value;
                    var ocvRect = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
                    if (ocvRect.Right > decoded.Bgr.Width || ocvRect.Bottom > decoded.Bgr.Height || ocvRect.X < 0 || ocvRect.Y < 0)
                    {
                        _logger.LogWarning($"Invalid search area: {rect}. Screenshot size: {decoded.Bgr.Width}x{decoded.Bgr.Height}. Using full image.");
                        return ExecuteColorMatching(bgrChannels, hsvChannels, templatePath, threshold, scales, verboseLogging);
                    }

                    var croppedBgr = bgrChannels.Select(c => new Mat(c, ocvRect)).ToArray();
                    var croppedHsv = hsvChannels.Select(c => new Mat(c, ocvRect)).ToArray();
                    try
                    {
                        var result = ExecuteColorMatching(croppedBgr, croppedHsv, templatePath, threshold, scales, verboseLogging);
                        if (result.found)
                        {
                            return (true,
                                new Rectangle(result.matchRect.X + rect.X, result.matchRect.Y + rect.Y,
                                    result.matchRect.Width, result.matchRect.Height),
                                result.confidence);
                        }
                        return (false, Rectangle.Empty, result.confidence);
                    }
                    finally
                    {
                        foreach (var mat in croppedBgr) mat.Dispose();
                        foreach (var mat in croppedHsv) mat.Dispose();
                    }
                }

                return ExecuteColorMatching(bgrChannels, hsvChannels, templatePath, threshold, scales, verboseLogging);
            }
            catch (ArgumentException ex)
            {
                _logger.LogError($"Screenshot data for {Path.GetFileName(templatePath)} is corrupted and cannot be processed. Error: {ex.Message}");
                return (false, Rectangle.Empty, 0);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Color template matching failed for {templatePath}: {ex.Message}");
                return (false, Rectangle.Empty, 0);
            }
        }

        private (bool found, Rectangle matchRect, double confidence) ExecuteColorMatching(
            Mat[] screenshotBGRChannels, Mat[] screenshotHSVChannels, string templatePath, double threshold, double[] scales, bool verboseLogging)
        {
            if (screenshotBGRChannels[0].Empty())
            {
                _logger.LogError("Screenshot image is empty");
                return (false, Rectangle.Empty, 0);
            }

            var cacheKey = TemplateCacheKey(templatePath);
            var (templateBGR, templateHSV) = GetOrLoadTemplate(templatePath, cacheKey);

            try
            {
                return FindBestColorMatch(screenshotBGRChannels, screenshotHSVChannels, cacheKey, templateBGR, templateHSV, threshold, scales, verboseLogging);
            }
            finally
            {
                lock (_cacheLock)
                {
                    if (_templateCache.TryGetValue(cacheKey, out var cached))
                    {
                        _templateCache[cacheKey] = (cached.BGR, cached.HSV, cached.LastUsed, Math.Max(0, cached.RefCount - 1));
                    }
                }
            }
        }

        /// <summary>
        /// Template channels resized to one scale. Resizing and splitting used to be redone
        /// on every match, for every scale.
        /// </summary>
        private sealed class ScaledColorTemplate
        {
            public Mat[] BGR = null!;
            public Mat Hue = null!;
            public Mat Saturation = null!;
            public double AverageSaturation;
        }

        private static ScaledColorTemplate GetOrCreateScaledTemplate(string cacheKey, Mat templateBGR, Mat templateHSV, double scale, int width, int height)
        {
            return _scaledTemplateCache.GetOrAdd((cacheKey, scale), _ => new Lazy<ScaledColorTemplate>(() =>
            {
                var size = new Size(width, height);
                var scaled = new ScaledColorTemplate { BGR = new Mat[3] };

                var bgrChannels = templateBGR.Split();
                var hsvChannels = templateHSV.Split();
                try
                {
                    for (int i = 0; i < 3; i++)
                    {
                        scaled.BGR[i] = new Mat();
                        Cv2.Resize(bgrChannels[i], scaled.BGR[i], size);
                    }
                    scaled.Hue = new Mat();
                    Cv2.Resize(hsvChannels[0], scaled.Hue, size);
                    scaled.Saturation = new Mat();
                    Cv2.Resize(hsvChannels[1], scaled.Saturation, size);
                    scaled.AverageSaturation = Cv2.Mean(scaled.Saturation).Val0 / 255.0;
                }
                finally
                {
                    foreach (var mat in bgrChannels) mat.Dispose();
                    foreach (var mat in hsvChannels) mat.Dispose();
                }

                return scaled;
            })).Value;
        }

        private (bool found, Rectangle matchRect, double confidence) FindBestColorMatch(
            Mat[] screenshotBGRChannels, Mat[] screenshotHSVChannels, string cacheKey, Mat templateBGR, Mat templateHSV,
            double threshold, double[] scales, bool verboseLogging)
        {
            double bestConfidence = -1;
            Rectangle bestRect = Rectangle.Empty;
            int screenWidth = screenshotBGRChannels[0].Width;
            int screenHeight = screenshotBGRChannels[0].Height;

            // Scales are independent, so they run in parallel; the best one is still picked
            // in scale order so ties resolve exactly as before
            var perScale = new (double confidence, Rectangle rect)?[StandardScales.Length];

            Parallel.For(0, StandardScales.Length, index =>
            {
                double scale = StandardScales[index];
                int newWidth = (int)(templateBGR.Width * scale);
                int newHeight = (int)(templateBGR.Height * scale);

                if (newWidth < 1 || newHeight < 1 || newWidth > screenWidth || newHeight > screenHeight)
                {
                    return;
                }

                try
                {
                    var template = GetOrCreateScaledTemplate(cacheKey, templateBGR, templateHSV, scale, newWidth, newHeight);

                    // Match in BGR space (per channel)
                    double bgrConfidence = 0;
                    Point bgrMaxLoc = new Point();

                    for (int i = 0; i < 3; i++)
                    {
                        using var resultBGR = new Mat();
                        Cv2.MatchTemplate(screenshotBGRChannels[i], template.BGR[i], resultBGR, TemplateMatchModes.CCoeffNormed);
                        Cv2.MinMaxLoc(resultBGR, out _, out double maxVal, out _, out Point maxLoc);

                        if (i == 0) // Use Blue channel for location
                        {
                            bgrMaxLoc = maxLoc;
                        }
                        bgrConfidence += maxVal;
                    }
                    bgrConfidence /= 3.0; // Average BGR confidence

                    // Match in HSV space with saturation filtering
                    double hsvConfidence = 0;
                    Point hsvMaxLoc = new Point();

                    // Check if template has sufficient saturation (not grey)
                    if (template.AverageSaturation >= 0.35)
                    {
                        // Hue channel matching
                        double hueConfidence;
                        using (var resultH = new Mat())
                        {
                            Cv2.MatchTemplate(screenshotHSVChannels[0], template.Hue, resultH, TemplateMatchModes.CCoeffNormed);
                            Cv2.MinMaxLoc(resultH, out _, out hueConfidence, out _, out hsvMaxLoc);
                        }

                        // Saturation channel matching
                        double saturationConfidence;
                        using (var resultS = new Mat())
                        {
                            Cv2.MatchTemplate(screenshotHSVChannels[1], template.Saturation, resultS, TemplateMatchModes.CCoeffNormed);
                            Cv2.MinMaxLoc(resultS, out _, out saturationConfidence, out _, out _);
                        }

                        // Check saturation of detected area in screenshot
                        var detectedRect = new Rect(hsvMaxLoc.X, hsvMaxLoc.Y, newWidth, newHeight);
                        if (detectedRect.X + detectedRect.Width <= screenshotHSVChannels[1].Width &&
                            detectedRect.Y + detectedRect.Height <= screenshotHSVChannels[1].Height)
                        {
                            using var detectedSaturationArea = screenshotHSVChannels[1][detectedRect];
                            var detectedSatMean = Cv2.Mean(detectedSaturationArea);
                            double detectedAvgSaturation = detectedSatMean.Val0 / 255.0;

                            if (detectedAvgSaturation < 0.3)
                            {
                                hsvConfidence = 0;
                            }
                            else
                            {
                                double preliminaryHsvConfidence = (0.9 * hueConfidence) + (0.1 * saturationConfidence);

                                // Validate confidence consistency
                                double confidenceGap = Math.Abs(bgrConfidence - preliminaryHsvConfidence);
                                if (confidenceGap > 0.3 && bgrConfidence > preliminaryHsvConfidence)
                                {
                                    hsvConfidence = preliminaryHsvConfidence * 0.5;
                                }
                                else
                                {
                                    hsvConfidence = preliminaryHsvConfidence;
                                }
                            }
                        }
                        else
                        {
                            hsvConfidence = (0.9 * hueConfidence) + (0.1 * saturationConfidence);
                        }
                    }

                    // Weighted combination of confidences
                    double combinedConfidence = (HSV_WEIGHT * hsvConfidence) + (BGR_WEIGHT * bgrConfidence);
                    Point finalMaxLoc = (hsvConfidence > bgrConfidence) ? hsvMaxLoc : bgrMaxLoc;

                    perScale[index] = (combinedConfidence, new Rectangle(finalMaxLoc.X, finalMaxLoc.Y, newWidth, newHeight));
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error in color matching scale {scale:F2}: {ex.Message}");
                }
            });

            foreach (var result in perScale)
            {
                if (result.HasValue && result.Value.confidence > bestConfidence)
                {
                    bestConfidence = result.Value.confidence;
                    bestRect = result.Value.rect;
                }
            }

            return (bestConfidence >= threshold, bestRect, bestConfidence);
        }

        public void Dispose()
        {
            // The template caches are shared with other instances, so they are not released here
            _disposed = true;
        }
    }
}

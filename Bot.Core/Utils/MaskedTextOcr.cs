using Bot.Core.Logging;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Text.RegularExpressions;
using PageSegMode = Tesseract.PageSegMode;

namespace Bot.Core.Utils
{
    /// <summary>
    /// OCR of coloured game text: the colour is masked into white-on-black first (HSV <c>InRange</c>), then one OCR pass.
    /// The Tesseract engines are cached per (scale, page mode, whitelist) and shared by every caller: building one per
    /// read costs far more than the read itself.
    /// </summary>
    public static class MaskedTextOcr
    {
        private static readonly ConcurrentDictionary<(float Scale, PageSegMode Mode, string Whitelist), OcrService> Engines = new();

        // The cached engines outlive every per-task logger, so they log to the global one
        private static readonly Lazy<LogService> EngineLogger = new(() => new LogService());

        /// <summary>The shared engine for this configuration; use it only through <see cref="Recognize"/> (an engine is single-threaded)</summary>
        public static OcrService Engine(float scale, PageSegMode mode, string? whitelist) =>
            Engines.GetOrAdd((scale, mode, whitelist ?? ""), key => new OcrService(EngineLogger.Value, new OcrConfiguration
            {
                ScaleFactor = key.Scale,
                PageSegMode = key.Mode,
                CharacterWhitelist = key.Whitelist.Length == 0 ? null : key.Whitelist
            }));

        /// <summary>OCR of a single-channel white-on-black mask</summary>
        public static string Recognize(OcrService engine, Mat mask)
        {
            using var bgr = mask.CvtColor(ColorConversionCodes.GRAY2BGR);
            using var bitmap = BitmapConverter.ToBitmap(bgr);
            lock (engine) return engine.ExtractTextFromBitmap(bitmap);
        }

        /// <summary>Text of <paramref name="area"/> in the colour range low-high (HSV)</summary>
        public static string Read(byte[] screenshot, Rectangle area, Scalar low, Scalar high, string? whitelist,
            PageSegMode mode = PageSegMode.SingleLine)
        {
            using var image = Cv2.ImDecode(screenshot, ImreadModes.Color);
            return Read(image, area, low, high, whitelist, mode);
        }

        /// <summary>
        /// Same on a decoded screenshot (decode once per screen when reading several areas). A single line gets one
        /// space between words; a block keeps its line breaks.
        /// </summary>
        public static string Read(Mat image, Rectangle area, Scalar low, Scalar high, string? whitelist,
            PageSegMode mode = PageSegMode.SingleLine)
        {
            var roi = new Rect(area.X, area.Y, area.Width, area.Height).Intersect(new Rect(0, 0, image.Width, image.Height));
            using var region = new Mat(image, roi);
            using var hsv = region.CvtColor(ColorConversionCodes.BGR2HSV);
            using var mask = hsv.InRange(low, high);
            string text = Recognize(Engine(2, mode, whitelist), mask);
            return mode == PageSegMode.SingleLine
                ? Regex.Replace(text, @"\s+", " ", RegexOptions.None, RegexTimeouts.Default).Trim()
                : text.Trim();
        }
    }
}

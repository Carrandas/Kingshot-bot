using Bot.Core.Logging;
using Bot.Core.Utils;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using TesseractPageSegMode = Tesseract.PageSegMode;

namespace Bot.Core.FindersFortune
{
    public enum FindersFortuneScreenKind
    {
        /// <summary>Anything else, including "Proceed?", chapter clear and popups. Never tap on it.</summary>
        Other,
        Stage,
        Intro,
        Rewards,
        MapReady,
    }

    public sealed record FindersFortuneLabel(int Slot, string Text, bool Struck);

    /// <summary>Reads a 720x1280 Finder's Fortune screenshot. Colours are BGR.</summary>
    public sealed class FindersFortuneScreen : IDisposable
    {
        private const int Width = 720;
        private const int Height = 1280;
        public const int SlotCount = 3;

        // Measured on recorded stages: active (193,131,75), struck-out once found (221,160,100).
        private static readonly Scalar LabelActive = new(193, 131, 75);
        private static readonly Scalar LabelStruck = new(221, 160, 100);
        private static readonly int[] SlotFillX = { 54, 284, 514 };
        private const int SlotFillY = 1145;
        private static readonly Rect[] SlotText =
        {
            new(36, 1138, 188, 60),
            new(266, 1138, 188, 60),
            new(496, 1138, 188, 60),
        };

        private static readonly Rect TimerText = new(70, 1092, 56, 26);
        private static readonly Rect BonesText = new(455, 1195, 80, 27);
        private static readonly Rect ProgressText = new(400, 1118, 170, 24);
        private const int GiveUpProbeX = 100;
        private const int GiveUpProbeY = 940;
        private static readonly Scalar GiveUpOrange = new(76, 123, 240);
        private static readonly Rect PracticeText = new(130, 1092, 65, 26);
        private const int MinPracticeWhite = 50;
        private const int PracticeSeconds = 999;
        private const int TinyTextWidth = 34;
        private const int MinRedPixels = 15;
        private const int MinProgressWhite = 400;

        private readonly OcrService _blockOcr;
        private readonly OcrService _charOcr;
        private readonly OcrService _timerOcr;

        public FindersFortuneScreen(LogService logger)
        {
            const string names = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789' ";
            _blockOcr = new OcrService(logger, new OcrConfiguration { ScaleFactor = 3, CharacterWhitelist = names, PageSegMode = TesseractPageSegMode.SingleBlock });
            _charOcr = new OcrService(logger, new OcrConfiguration { ScaleFactor = 3, CharacterWhitelist = names, PageSegMode = TesseractPageSegMode.SingleChar });
            _timerOcr = new OcrService(logger, new OcrConfiguration { ScaleFactor = 3, CharacterWhitelist = "0123456789s", PageSegMode = TesseractPageSegMode.SingleLine });
        }

        public static FindersFortuneScreenKind Classify(Mat shot)
        {
            if (shot.Width != Width || shot.Height != Height)
            {
                return FindersFortuneScreenKind.Other;
            }

            if (IsStage(shot))
            {
                return FindersFortuneScreenKind.Stage;
            }

            if (IsMapReady(shot))
            {
                return FindersFortuneScreenKind.MapReady;
            }

            // Purple Continue button, sampled either side of its dark text.
            if (IsNear(shot, 250, 920, new Scalar(244, 123, 100), 22) && IsNear(shot, 470, 920, new Scalar(242, 122, 102), 22))
            {
                return FindersFortuneScreenKind.Rewards;
            }

            // The intro dims the label bar behind it.
            if (!IsNear(shot, 60, SlotFillY + 5, new Scalar(72, 52, 33), 24))
            {
                return FindersFortuneScreenKind.Other;
            }

            return ClassifyDimmed(shot);
        }

        private static FindersFortuneScreenKind ClassifyDimmed(Mat shot)
        {
            // "Time's Up!" has the same blue cards as the intro, so rule out its orange Give Up button.
            if (IsNear(shot, GiveUpProbeX, GiveUpProbeY, GiveUpOrange, 40))
            {
                return FindersFortuneScreenKind.Other;
            }

            // The blue timer card; it dissolves once tapped.
            return IsNear(shot, 150, 590, new Scalar(224, 154, 92), 34)
                ? FindersFortuneScreenKind.Intro
                : FindersFortuneScreenKind.Other;
        }

        /// <summary>The Start Game button lights up only when the walking animation is over.</summary>
        private static bool IsMapReady(Mat shot)
        {
            if (!IsNear(shot, 486, 1160, new Scalar(230, 136, 67), 30))
            {
                return false;
            }

            using var white = WhiteMask(shot, ProgressText);
            return Cv2.CountNonZero(white) >= MinProgressWhite;
        }

        private static bool IsStage(Mat shot) => Enumerable.Range(0, SlotCount).All(s => IsActive(shot, s) || IsStruck(shot, s));

        public static bool IsStruck(Mat shot, int slot) => IsNear(shot, SlotFillX[slot], SlotFillY, LabelStruck, 12);

        private static bool IsActive(Mat shot, int slot) => IsNear(shot, SlotFillX[slot], SlotFillY, LabelActive, 12);

        /// <summary>The bones count turns red on the map when a stage is not affordable.</summary>
        public static bool IsOutOfBones(Mat shot) => CountRed(shot, BonesText) >= MinRedPixels;

        /// <summary>The timer turns red for the last seconds; a second opinion on the OCR'd number.</summary>
        public static bool IsTimerRed(Mat shot) => CountRed(shot, TimerText) >= MinRedPixels;

        /// <summary>Changed white-text pixels in a slot between two screenshots.</summary>
        public static int CountLabelChange(Mat before, Mat after, int slot)
        {
            using var a = WhiteMask(before, SlotText[slot]);
            using var b = WhiteMask(after, SlotText[slot]);
            using var difference = new Mat();
            Cv2.BitwiseXor(a, b, difference);
            return Cv2.CountNonZero(difference);
        }

        public IReadOnlyList<FindersFortuneLabel> ReadLabels(Mat shot) =>
            Enumerable.Range(0, SlotCount)
                .Select(s => new FindersFortuneLabel(s, IsStruck(shot, s) ? string.Empty : ReadSlot(shot, s), IsStruck(shot, s)))
                .ToList();

        /// <summary>Practice Mode (offered after giving up a stage) has no timer and replaces it with white text.</summary>
        public static bool IsPracticeMode(Mat shot)
        {
            using var white = WhiteMask(shot, PracticeText);
            return Cv2.CountNonZero(white) >= MinPracticeWhite;
        }

        /// <summary>Seconds left (plenty in Practice Mode), or null when the timer can't be read.</summary>
        public int? ReadTimer(Mat shot)
        {
            if (IsPracticeMode(shot))
            {
                return PracticeSeconds;
            }

            using var mask = BrightMask(shot, TimerText);
            var digits = new string(Ocr(_timerOcr, mask).Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var value) ? value : null;
        }

        private string ReadSlot(Mat shot, int slot)
        {
            using var mask = WhiteMask(shot, SlotText[slot]);
            var ocr = Cv2.BoundingRect(mask).Width < TinyTextWidth ? _charOcr : _blockOcr;
            return string.Join(' ', Ocr(ocr, mask).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string Ocr(OcrService ocr, Mat mask)
        {
            using var padded = new Mat();
            Cv2.CopyMakeBorder(mask, padded, 12, 12, 12, 12, BorderTypes.Constant, Scalar.Black);
            using var bgr = new Mat();
            Cv2.CvtColor(padded, bgr, ColorConversionCodes.GRAY2BGR);
            using var bitmap = BitmapConverter.ToBitmap(bgr);
            return ocr.ExtractTextFromBitmap(bitmap);
        }

        private static Mat WhiteMask(Mat shot, Rect area)
        {
            using var crop = new Mat(shot, area);
            var mask = new Mat();
            Cv2.InRange(crop, new Scalar(190, 190, 190), new Scalar(255, 255, 255), mask);
            return mask;
        }

        /// <summary>White or red text: the timer goes red near the end.</summary>
        private static Mat BrightMask(Mat shot, Rect area)
        {
            using var crop = new Mat(shot, area);
            using var hsv = new Mat();
            Cv2.CvtColor(crop, hsv, ColorConversionCodes.BGR2HSV);
            var mask = new Mat();
            Cv2.InRange(hsv, new Scalar(0, 0, 170), new Scalar(180, 255, 255), mask);
            return mask;
        }

        private static int CountRed(Mat shot, Rect area)
        {
            using var crop = new Mat(shot, area);
            using var mask = new Mat();
            Cv2.InRange(crop, new Scalar(0, 0, 170), new Scalar(90, 90, 255), mask);
            return Cv2.CountNonZero(mask);
        }

        private static bool IsNear(Mat shot, int x, int y, Scalar colour, int tolerance)
        {
            var pixel = shot.At<Vec3b>(y, x);
            return Math.Abs(pixel.Item0 - colour.Val0) <= tolerance
                && Math.Abs(pixel.Item1 - colour.Val1) <= tolerance
                && Math.Abs(pixel.Item2 - colour.Val2) <= tolerance;
        }

        public void Dispose()
        {
            _blockOcr.Dispose();
            _charOcr.Dispose();
            _timerOcr.Dispose();
        }
    }
}

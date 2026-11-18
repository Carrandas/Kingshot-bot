using Bot.Core.Services;
using Bot.Core.Utils;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>One platform slot of a minigame row: its centre and whether it is green (a right jar stays there)</summary>
    internal readonly record struct BoardSlot(int X, bool Green);

    /// <summary>One jar in the tray under the board</summary>
    internal readonly record struct TrayJar(int X, BrewColour Colour);

    /// <summary>
    /// Reads the Alchemy Junction minigame from a screenshot (720x1280). The current row's platforms are at y 1005, older
    /// rows above it every 195 px (810, 615) with a jar (green platform) or a splash of the wrong colour over each slot.
    /// The tray jars have white lids at y 1115-1125 and the liquid at y 1180-1235.
    /// </summary>
    internal static class AlchemyBoard
    {
        public const int CurrentRowY = 1005;
        public const int RowSpacing = 195;
        public const int TrayTapY = 1190;
        private const int HistoryRows = 3;
        private const int MinSlotWidth = 60;   // platforms are ~106 px wide, split by a dark 5 px line
        private const int MinLidWidth = 35;    // lids ~58 px
        private const double MinColourSaturation = 145; // wood and platforms 127-134, brews 160-255
        private static readonly Rect BrewCountArea = new(310, 388, 120, 48);

        public static Mat Hsv(byte[] screenshot)
        {
            using var image = Cv2.ImDecode(screenshot, ImreadModes.Color);
            return image.CvtColor(ColorConversionCodes.BGR2HSV);
        }

        /// <summary>The platforms of the row at <paramref name="y"/>: brown (empty or wrong) or green (kept)</summary>
        public static List<BoardSlot> ReadSlots(Mat hsv, int y = CurrentRowY)
        {
            int width = hsv.Width;
            var columns = new int[width];
            var green = new bool[width];
            for (int x = 0; x < width; x++)
            {
                var (h, s, v) = MedianColumn(hsv, x, y);
                bool isBrown = Math.Abs(h - 14) <= 3 && s > 110 && v > 140;
                green[x] = Math.Abs(h - 60) <= 6 && s > 110 && v > 125;
                columns[x] = isBrown || green[x] ? 1 : 0;
            }
            return Runs(columns, MinSlotWidth).Select(r => new BoardSlot((r.Start + r.End) / 2, green[(r.Start + r.End) / 2])).ToList();
        }

        private static (int h, int s, int v) MedianColumn(Mat hsv, int x, int y)
        {
            var hs = new List<int>(); var ss = new List<int>(); var vs = new List<int>();
            for (int dy = -3; dy <= 3; dy++)
            {
                var p = hsv.At<Vec3b>(y + dy, x);
                hs.Add(p.Item0); ss.Add(p.Item1); vs.Add(p.Item2);
            }
            hs.Sort(); ss.Sort(); vs.Sort();
            return (hs[3], ss[3], vs[3]);
        }

        /// <summary>The jars left in the tray, left to right</summary>
        public static List<TrayJar> ReadTray(Mat hsv)
        {
            int width = hsv.Width;
            var lid = new int[width];
            for (int x = 0; x < width; x++)
                for (int y = 1115; y <= 1125 && lid[x] == 0; y++)
                {
                    var p = hsv.At<Vec3b>(y, x);
                    if (p.Item1 < 70 && p.Item2 > 170) lid[x] = 1;
                }
            var jars = new List<TrayJar>();
            foreach (var (start, end) in Runs(lid, MinLidWidth))
            {
                int cx = (start + end) / 2;
                var colour = ColourIn(hsv, new Rect(cx - 22, 1180, 44, 55), 20);
                if (colour != null) jars.Add(new TrayJar(cx, colour.Value));
            }
            return jars;
        }

        /// <summary>The colour of a jar standing on the current row's slot, null when the slot is empty</summary>
        public static BrewColour? PlacedJar(Mat hsv, int x) => ColourIn(hsv, new Rect(x - 20, CurrentRowY - 85, 40, 55), 150);

        /// <summary>
        /// The colours ruled out per slot by the older rows still on screen: a splash over a brown platform is a jar that
        /// was wrong there. Only rows with as many slots as the current one count.
        /// </summary>
        public static Dictionary<int, HashSet<BrewColour>> ReadHistory(Mat hsv, int slotCount)
        {
            var excluded = new Dictionary<int, HashSet<BrewColour>>();
            for (int row = 1; row <= HistoryRows; row++)
            {
                int y = CurrentRowY - row * RowSpacing;
                var slots = ReadSlots(hsv, y);
                if (slots.Count != slotCount) break;
                for (int i = 0; i < slots.Count; i++)
                {
                    if (slots[i].Green) continue;
                    var splash = ColourIn(hsv, new Rect(slots[i].X - 35, y - 115, 70, 70), 300);
                    if (splash == null) continue;
                    if (!excluded.TryGetValue(i, out var set)) excluded[i] = set = new HashSet<BrewColour>();
                    set.Add(splash.Value);
                }
            }
            return excluded;
        }

        /// <summary>The Magic Brew count in the pill over the board (white digits); null when unreadable</summary>
        public static int? ReadBrews(Mat image)
        {
            string text = MaskedTextOcr.Read(image, new System.Drawing.Rectangle(BrewCountArea.X, BrewCountArea.Y, BrewCountArea.Width, BrewCountArea.Height),
                new Scalar(0, 0, 200), new Scalar(180, 60, 255), "0123456789");
            return int.TryParse(text.Replace(" ", ""), out int brews) ? brews : null;
        }

        /// <summary>Median hue of the strongly coloured pixels in <paramref name="area"/>; null below <paramref name="minPixels"/></summary>
        private static BrewColour? ColourIn(Mat hsv, Rect area, int minPixels)
        {
            var roi = area.Intersect(new Rect(0, 0, hsv.Width, hsv.Height));
            var hues = new List<int>();
            for (int y = roi.Top; y < roi.Bottom; y++)
                for (int x = roi.Left; x < roi.Right; x++)
                {
                    var p = hsv.At<Vec3b>(y, x);
                    if (p.Item1 > MinColourSaturation && p.Item2 > 90) hues.Add(p.Item0);
                }
            if (hues.Count < minPixels) return null;
            hues.Sort();
            return AlchemySolver.Classify(hues[hues.Count / 2]);
        }

        private static IEnumerable<(int Start, int End)> Runs(int[] mask, int minLength)
        {
            int start = -1;
            for (int x = 0; x <= mask.Length; x++)
            {
                bool on = x < mask.Length && mask[x] != 0;
                if (on && start < 0) start = x;
                if (on || start < 0) continue;
                if (x - start >= minLength) yield return (start, x - 1);
                start = -1;
            }
        }
    }
}

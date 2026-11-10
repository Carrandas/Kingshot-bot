using OpenCvSharp;

namespace Bot.Core.FindersFortune
{
    /// <summary>A pulsing area of the scene, in full-screen coordinates.</summary>
    public sealed record FindersFortuneFlashSpot(int X, int Y, Rect Area);

    /// <summary>
    /// Finds the game's own hint: after a few idle seconds it makes a wanted item pulse with a glow.
    /// The scene is otherwise still, so the place whose brightness swings over a few frames is the item.
    /// </summary>
    public static class FindersFortuneFlash
    {
        private static readonly Rect Scene = new(64, 84, 592, 972);
        private const double MinBrightnessSwing = 35;
        private const int MinArea = 500;
        private const int MaxArea = 120000;
        private const int MaxBannerWidth = 450; // the alliance notice scrolls across the whole scene

        public static Mat ToSceneGray(Mat shot)
        {
            using var scene = new Mat(shot, Scene);
            var gray = new Mat();
            Cv2.CvtColor(scene, gray, ColorConversionCodes.BGR2GRAY);
            return gray;
        }

        /// <summary>
        /// The biggest area that pulsed across these frames, or null. Areas in <paramref name="ignored"/>
        /// already failed a tap (a flickering torch or forge fire looks the same as a hint).
        /// </summary>
        public static FindersFortuneFlashSpot? Find(IReadOnlyList<Mat> sceneGrays, IReadOnlyCollection<Rect> ignored)
        {
            using var changed = BuildChangeMask(sceneGrays);
            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            var count = Cv2.ConnectedComponentsWithStats(changed, labels, stats, centroids);

            FindersFortuneFlashSpot? best = null;
            var bestArea = 0;
            for (var i = 1; i < count; i++)
            {
                var area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
                var box = ToScreen(stats, i);
                if (area > bestArea && IsCandidate(area, box, ignored))
                {
                    bestArea = area;
                    best = new FindersFortuneFlashSpot(box.X + (box.Width / 2), box.Y + (box.Height / 2), box);
                }
            }

            return best;
        }

        private static Mat BuildChangeMask(IReadOnlyList<Mat> sceneGrays)
        {
            using var highest = sceneGrays[0].Clone();
            using var lowest = sceneGrays[0].Clone();
            foreach (var frame in sceneGrays.Skip(1))
            {
                Cv2.Max(highest, frame, highest);
                Cv2.Min(lowest, frame, lowest);
            }

            using var swing = new Mat();
            Cv2.Subtract(highest, lowest, swing);
            using var binary = new Mat();
            Cv2.Threshold(swing, binary, MinBrightnessSwing, 255, ThresholdTypes.Binary);
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5));
            var opened = new Mat();
            Cv2.MorphologyEx(binary, opened, MorphTypes.Open, kernel);
            return opened;
        }

        private static Rect ToScreen(Mat stats, int component) => new(
            stats.At<int>(component, (int)ConnectedComponentsTypes.Left) + Scene.X,
            stats.At<int>(component, (int)ConnectedComponentsTypes.Top) + Scene.Y,
            stats.At<int>(component, (int)ConnectedComponentsTypes.Width),
            stats.At<int>(component, (int)ConnectedComponentsTypes.Height));

        private static bool IsCandidate(int area, Rect box, IEnumerable<Rect> ignored) =>
            area >= MinArea && area <= MaxArea && box.Width <= MaxBannerWidth && !ignored.Any(r => r.IntersectsWith(box));
    }
}

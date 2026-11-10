using Bot.Core.FindersFortune;
using NUnit.Framework;
using OpenCvSharp;

namespace Bot.Core.Tests
{
    [TestFixture]
    public class FindersFortuneFlashTests
    {
        private const int SceneLeft = 64;
        private const int SceneTop = 84;

        private static List<Mat> Frames(Action<Mat> drawOnSecondFrame)
        {
            var frames = new List<Mat>();
            for (var i = 0; i < 3; i++)
            {
                var frame = new Mat(972, 592, MatType.CV_8UC1, new Scalar(80));
                if (i == 1)
                {
                    drawOnSecondFrame(frame);
                }

                frames.Add(frame);
            }

            return frames;
        }

        [Test]
        public void Find_ReturnsTheCentreOfAPulseInScreenCoordinates()
        {
            var frames = Frames(f => Cv2.Circle(f, new Point(300, 500), 40, new Scalar(200), -1));

            var flash = FindersFortuneFlash.Find(frames, Array.Empty<Rect>());

            Assert.That(flash, Is.Not.Null);
            Assert.That(flash!.X, Is.EqualTo(300 + SceneLeft).Within(3));
            Assert.That(flash.Y, Is.EqualTo(500 + SceneTop).Within(3));
        }

        [Test]
        public void Find_ReturnsNothingForAStillScene()
        {
            Assert.That(FindersFortuneFlash.Find(Frames(_ => { }), Array.Empty<Rect>()), Is.Null);
        }

        [Test]
        public void Find_IgnoresTinyFlickerAndTheWideBanner()
        {
            var frames = Frames(f =>
            {
                Cv2.Circle(f, new Point(100, 100), 4, new Scalar(200), -1);
                Cv2.Rectangle(f, new Rect(0, 120, 592, 50), new Scalar(200), -1);
            });

            Assert.That(FindersFortuneFlash.Find(frames, Array.Empty<Rect>()), Is.Null);
        }

        [Test]
        public void Find_SkipsAnAreaThatAlreadyFailedATap()
        {
            var frames = Frames(f =>
            {
                Cv2.Circle(f, new Point(300, 500), 60, new Scalar(200), -1); // torch: bigger, but already tried
                Cv2.Circle(f, new Point(100, 800), 30, new Scalar(200), -1);
            });
            var torch = FindersFortuneFlash.Find(frames, Array.Empty<Rect>())!;

            var next = FindersFortuneFlash.Find(frames, new[] { torch.Area });

            Assert.That(next!.Y, Is.EqualTo(800 + SceneTop).Within(3));
        }
    }
}

using Bot.Core.Services;
using NUnit.Framework;
using OpenCvSharp;
using System.Linq;

namespace Bot.Core.IntegrationTests
{
    /// <summary>
    /// Events > Calendar, week of 2026-11-16 (read 11-18). Columns 0 = Monday ... 6 = Sunday; the days are the ones the
    /// bars covered on screen (Brew Workshop Wed-Fri matched its 2d 6h timer on Wednesday evening).
    /// </summary>
    [TestFixture]
    public class EventCalendarTests
    {
        private static (string Label, int First, int Last)? Bar(string screenshot, string name)
        {
            using var image = Cv2.ImDecode(Fixtures.Load(screenshot), ImreadModes.Color);
            var bars = EventCalendar.ReadBars(image);
            var bar = bars.FirstOrDefault(b => EventCalendar.SameEvent(b.Label, name));
            return bar.Label == null ? null : bar;
        }

        [TestCase("calendar-week-1.png", "Brew Workshop", 2, 4)]
        [TestCase("calendar-week-1.png", "Brewmaster Stall", 2, 5)]
        [TestCase("calendar-week-1.png", "Match Missions", 0, 6)]
        [TestCase("calendar-week-2.png", "Voyage of Light", 1, 5)]
        [TestCase("calendar-week-2.png", "Harbor Drill", 0, 3)]
        [TestCase("calendar-week-2.png", "Emporium of Enigma", 0, 4)]
        [TestCase("calendar-week-3.png", "Viking Vengeance", 1, 3)]
        [TestCase("calendar-week-3.png", "Sanctuary Battle", 1, 4)]
        [TestCase("calendar-week-4.png", "All Out", 4, 5)]
        public void Bar_Days(string screenshot, string name, int first, int last)
        {
            var bar = Bar(screenshot, name);
            Assert.That(bar, Is.Not.Null, name);
            Assert.That((bar!.Value.First, bar.Value.Last), Is.EqualTo((first, last)), name);
        }

        /// <summary>Group headers ("Alchemy Junction", "Deals") are not bars</summary>
        [TestCase("calendar-week-1.png", "Alchemy Junction")]
        [TestCase("calendar-week-2.png", "Deals")]
        public void Header_IsNotABar(string screenshot, string header) =>
            Assert.That(Bar(screenshot, header), Is.Null);
    }
}

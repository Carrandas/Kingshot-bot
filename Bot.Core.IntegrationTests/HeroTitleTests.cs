using Bot.Core.Models;
using Bot.Core.Services;
using NUnit.Framework;

namespace Bot.Core.IntegrationTests
{
    /// <summary>
    /// The gear swap finds a hero by the name in the Hero screen's title. The screenshots (title strip only, 720x100)
    /// were taken stepping through the ring of all 37 heroes with the > arrow; the file name is the hero's name.
    /// </summary>
    [TestFixture]
    public class HeroTitleTests
    {
        private static IEnumerable<string> Files() =>
            Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Screenshots", "heroes"), "*.png").Select(f => Path.GetFileNameWithoutExtension(f)!);

        [TestCaseSource(nameof(Files))]
        public void Title_ReadsTheHero(string file)
        {
            string wanted = HeroCatalog.Names.Single(n => HeroCatalog.Normalize(n) == file);
            string read = HeroGearSwapper.ReadTitle(Fixtures.Load(Path.Combine("heroes", file + ".png")));
            Assert.That(HeroCatalog.SameHero(read, wanted), Is.True, $"read '{read}'");
        }

        [Test]
        public void EveryHeroHasAScreenshot() =>
            Assert.That(Files().Order().ToList(), Is.EqualTo(HeroCatalog.Names.Select(HeroCatalog.Normalize).Order().ToList()));

        [Test]
        public void Title_NeverTakesAnotherHero()
        {
            // a read must match only the hero it is: no other hero is accepted for any title
            foreach (var file in Files())
            {
                string read = HeroGearSwapper.ReadTitle(Fixtures.Load(Path.Combine("heroes", file + ".png")));
                var matches = HeroCatalog.Names.Where(n => HeroCatalog.SameHero(read, n)).Select(HeroCatalog.Normalize).ToList();
                Assert.That(matches, Is.EquivalentTo(new[] { file }), $"'{file}' read as '{read}'");
            }
        }
    }
}

using Bot.Core.FindersFortune;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    [TestFixture]
    public class FindersFortuneCatalogTests
    {
        private const string Sample = "# comment\nSmithy | Key | 547,525\nSmithy | Water Jug | 122,1015 | near the edge\n\nStudy | Key | 555,357\nStudy | Clock | 558,262\nStudy | Tweezers | 215,425";

        [Test]
        public void Parse_ReadsSpotsAndSkipsComments()
        {
            var catalog = FindersFortuneCatalog.Parse(Sample);

            Assert.That(catalog.Spots, Has.Count.EqualTo(5));
            Assert.That(catalog.Spots[1], Is.EqualTo(new ItemSpot("Smithy", "Water Jug", 122, 1015, "near the edge")));
        }

        [TestCase("Water Jug")]
        [TestCase("WaterJug")]
        [TestCase("Water Jag")]
        public void Match_AcceptsSmallOcrErrorsOnLongNames(string read)
        {
            var spot = FindersFortuneCatalog.Parse(Sample).Match("Smithy", read);

            Assert.That(spot?.Item, Is.EqualTo("Water Jug"));
        }

        [TestCase("Cloth")] // one letter from Clock: a different item, never to be tapped as Clock
        [TestCase("Streciiig 9 ignt")]
        [TestCase("")]
        public void Match_RejectsWhatIsNotCloseEnough(string read)
        {
            Assert.That(FindersFortuneCatalog.Parse(Sample).Match("Study", read), Is.Null);
        }

        [Test]
        public void Match_UsesOnlyTheGivenMap()
        {
            var catalog = FindersFortuneCatalog.Parse(Sample);

            Assert.That(catalog.Match("Study", "Key")?.X, Is.EqualTo(555));
            Assert.That(catalog.Match("Smithy", "Clock"), Is.Null);
        }

        [Test]
        public void MapsWith_ListsEveryMapThatHasTheName()
        {
            var catalog = FindersFortuneCatalog.Parse(Sample);

            Assert.That(catalog.MapsWith("Key"), Is.EquivalentTo(new[] { "Smithy", "Study" }));
            Assert.That(catalog.MapsWith("Tweezers"), Is.EqualTo(new[] { "Study" }));
        }

        [Test]
        public void Upsert_MovesAnExistingItemAndKeepsComments()
        {
            var catalog = FindersFortuneCatalog.Parse(Sample);

            catalog.Upsert("Smithy", "key", 1, 2, null);

            Assert.That(catalog.Match("Smithy", "Key")?.X, Is.EqualTo(1));
            Assert.That(catalog.Spots, Has.Count.EqualTo(5));
            Assert.That(catalog.Lines.First(), Is.EqualTo("# comment"));
        }

        [Test]
        public void Upsert_AddsANewItemNextToItsMapAndANewMapAtTheEnd()
        {
            var catalog = FindersFortuneCatalog.Parse(Sample);

            catalog.Upsert("Smithy", "Anchor", 3, 4, "note");
            catalog.Upsert("Windmill", "Owl", 5, 6, null);

            var lines = catalog.Lines.ToList();
            Assert.That(lines[3], Is.EqualTo("Smithy | Anchor | 3,4 | note"));
            Assert.That(lines[^1], Is.EqualTo("Windmill | Owl | 5,6"));
        }
    }
}

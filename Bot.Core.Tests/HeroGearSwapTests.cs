using System.Text.Json;
using Bot.Core.Models;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    [TestFixture]
    public class HeroGearSwapTests
    {
        [TestCase("Amadeus", "Amadeus", true)]
        [TestCase("amadeus", "Amadeus", true)]
        [TestCase("Amadeu5", "Amadeus", true)]      // one letter off, a long name
        [TestCase("Wee & Woo", "Wee & Woo", true)]
        [TestCase("Wee Woo", "Wee & Woo", true)]
        [TestCase("Zoe", "Zoa", false)]              // short names are exact
        [TestCase("Long Fei", "Amadeus", false)]
        [TestCase("", "Amadeus", false)]
        public void SameHero(string read, string wanted, bool expected) =>
            Assert.That(HeroCatalog.SameHero(read, wanted), Is.EqualTo(expected));

        [Test]
        public void Catalog_NoHeroPassesForAnother()
        {
            foreach (var a in HeroCatalog.Names)
                foreach (var b in HeroCatalog.Names.Where(n => n != a))
                    Assert.That(HeroCatalog.SameHero(a, b), Is.False, $"{a} / {b}");
            Assert.That(HeroCatalog.Names, Has.Length.EqualTo(37));
        }

        [Test]
        public void GearSwapsFor_OnlyTickedCompleteSwapsOfTheCharacter()
        {
            var settings = new BearHuntSettings();
            settings.GearSwaps.Add(new HeroGearSwap { Character = "[KFC]Asli", NormalHero = "Long Fei", BearHero = "Amadeus" });
            settings.GearSwaps.Add(new HeroGearSwap { Character = "[KFC]Asli", NormalHero = "Ava", BearHero = "Ava" });          // same hero
            settings.GearSwaps.Add(new HeroGearSwap { Character = "[KFC]Asli", NormalHero = "Liz", BearHero = "Luna", Enabled = false });
            settings.GearSwaps.Add(new HeroGearSwap { Character = "[KFC]llsa", NormalHero = "Liz", BearHero = "Luna" });

            var asli = settings.GearSwapsFor("[KFC]Asli");
            Assert.That(asli, Has.Count.EqualTo(1));
            Assert.That(asli[0].BearHero, Is.EqualTo("Amadeus"));
            Assert.That(settings.GearSwapsFor("nobody"), Is.Empty);
        }

        [Test]
        public void GearSwaps_SaveAndRaiseChanged()
        {
            var settings = new BearHuntSettings();
            var changes = new List<string?>();
            settings.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
            var swap = new HeroGearSwap { Character = "a", NormalHero = "Long Fei", BearHero = "Amadeus" };
            settings.GearSwaps.Add(swap);
            swap.BearHero = "Ava";
            Assert.That(changes.Count(c => c == nameof(BearHuntSettings.GearSwaps)), Is.EqualTo(2));

            var back = JsonSerializer.Deserialize<BearHuntSettings>(JsonSerializer.Serialize(settings))!;
            Assert.That(back.GearSwaps, Has.Count.EqualTo(1));
            Assert.That(back.GearSwaps[0].BearHero, Is.EqualTo("Ava"));
            // a loaded swap is watched too
            back.PropertyChanged += (_, e) => changes.Add("loaded:" + e.PropertyName);
            back.GearSwaps[0].NormalHero = "Liz";
            Assert.That(changes, Contains.Item("loaded:" + nameof(BearHuntSettings.GearSwaps)));
        }
    }
}

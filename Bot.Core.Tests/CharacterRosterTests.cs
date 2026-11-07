using Bot.Core.Models;
using Bot.Core.Services;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    [TestFixture]
    public class CharacterRosterTests
    {
        [TestCase("[KFC]Asli", "asll")]
        [TestCase("(KFC) Ilsa", "llsa")]
        [TestCase("llsa", "llsa")]
        [TestCase("1lsa", "llsa")]
        [TestCase("B0b", "bob")]
        [TestCase("  [AB]  x-y z ", "xyz")]
        public void NormalizeKey_DropsTagAndFoldsLookalikes(string name, string expected)
        {
            Assert.That(CharacterRoster.NormalizeKey(name), Is.EqualTo(expected));
        }

        [TestCase("[KFC]Ilsa", "[KFC]llsa")]   // OCR reads a capital I as l
        [TestCase("[KFC]Asli", "[XYZ]Asli")]   // tag changed
        [TestCase("Larryboy", "Larryb0y")]
        [TestCase("Larryboy", "Larrybey")]     // one character off, 5+ characters
        [TestCase("[KFC]Asli", "[SKY]Asi")]    // castle battle Characters screen: a letter dropped next to the tag
        [TestCase("[KFC]Ilsa", "[SKY]lsa")]
        public void SameName_True(string a, string b)
        {
            Assert.That(CharacterRoster.SameName(a, b), Is.True);
        }

        [TestCase("Asli", "Asla")]             // one off but shorter than 5
        [TestCase("Larryboy", "Larrbeyy")]     // two off
        [TestCase("[KFC]Asli", "[KFC]llsa")]
        [TestCase("", "")]
        [TestCase("[KFC]", "[KFC]")]           // tag only
        public void SameName_False(string a, string b)
        {
            Assert.That(CharacterRoster.SameName(a, b), Is.False);
        }

        [TestCase("[KFClAsli", "[KFC]Asli")]   // ] read as l
        [TestCase("[KFCJAsli", "[KFC]Asli")]   // ] read as J
        [TestCase("[KFCAsli", "[KFC]Asli")]    // ] dropped
        [TestCase("[KFC]Asli", "[KFC]Asli")]
        [TestCase("Asli", "Asli")]
        public void ChangeAccount_FixAllianceTag(string ocr, string expected)
        {
            Assert.That(Bot.Core.Tasks.Modules.ChangeAccountTask.FixAllianceTag(ocr), Is.EqualTo(expected));
        }

        [Test]
        public void Merge_AddsNewCharactersEnabled_AndKeepsTheUsersChoice()
        {
            var account = new AccountSettings();
            account.Characters.Add(new CharacterEntry { Name = "[KFC]Asli", Kingdom = 1, Enabled = false });

            CharacterRoster.Merge(account, new (string, int?)[] { ("[KFC]Asli", 1), ("[KFC]llsa", 1) });

            Assert.That(account.Characters, Has.Count.EqualTo(2));
            Assert.That(CharacterRoster.IsEnabled(account, "[KFC]Asli", 1), Is.False, "unticked stays unticked");
            Assert.That(CharacterRoster.IsEnabled(account, "[KFC]Ilsa", 1), Is.True, "new character is ticked");
        }

        [Test]
        public void Merge_UpdatesTheTag_ButNotForAnOcrVariant()
        {
            var account = new AccountSettings();
            account.Characters.Add(new CharacterEntry { Name = "[KFC]Ilsa", Kingdom = 1 });

            CharacterRoster.Merge(account, new (string, int?)[] { ("[KFC]llsa", 1) });
            Assert.That(account.Characters[0].Name, Is.EqualTo("[KFC]Ilsa"), "l for I is an OCR variant");

            CharacterRoster.Merge(account, new (string, int?)[] { ("[NEW]Ilsa", 1) });
            Assert.That(account.Characters[0].Name, Is.EqualTo("[NEW]Ilsa"), "new alliance tag");
        }

        [Test]
        public void Merge_RemovesOcrDuplicates_AndKeepsTheirTick()
        {
            // the roster after the castle battle: misreads "[SKY]Asi"/"[SKY]lsa" were added next to the real entries
            var account = new AccountSettings();
            account.Characters.Add(new CharacterEntry { Name = "Asli Farm", Kingdom = 754, Enabled = false });
            account.Characters.Add(new CharacterEntry { Name = "[SKY]Asli", Kingdom = 196, Enabled = false, AccountId = "34431520" });
            account.Characters.Add(new CharacterEntry { Name = "[SKY]llsa", Kingdom = 196, Enabled = false, AccountId = "1104083889" });
            account.Characters.Add(new CharacterEntry { Name = "[SKY]Asi", Kingdom = 196, Enabled = true });
            account.Characters.Add(new CharacterEntry { Name = "[SKY]lsa", Kingdom = 196, Enabled = false });

            CharacterRoster.Merge(account, new (string, int?)[] { ("Asli Farm", 754), ("[KFC]Asli", 196), ("[KFC]llsa", 196) });

            Assert.That(account.Characters.Select(c => c.Name), Is.EqualTo(new[] { "Asli Farm", "[KFC]Asli", "[KFC]llsa" }));
            Assert.That(account.Characters[1].AccountId, Is.EqualTo("34431520"));
            Assert.That(account.Characters[1].Enabled, Is.True, "the duplicate's tick carries over");
            Assert.That(account.Characters[2].Enabled, Is.False);
        }

        [Test]
        public void Merge_KeepsCharactersTheScanMissed()
        {
            var account = new AccountSettings();
            account.Characters.Add(new CharacterEntry { Name = "[KFC]Asli", Kingdom = 1 });
            account.Characters.Add(new CharacterEntry { Name = "[KFC]Ilsa", Kingdom = 1 });

            CharacterRoster.Merge(account, new (string, int?)[] { ("[KFC]Asli", 1) });

            Assert.That(account.Characters, Has.Count.EqualTo(2), "a row that failed to read is no reason to drop a character");
        }

        [Test]
        public void Find_IgnoresTheKingdomWhenUnknown_ButNotWhenItDiffers()
        {
            var account = new AccountSettings();
            account.Characters.Add(new CharacterEntry { Name = "Asli", Kingdom = 12 });

            Assert.That(CharacterRoster.Find(account, "Asli", null), Is.Not.Null);
            Assert.That(CharacterRoster.Find(account, "Asli", 12), Is.Not.Null);
            Assert.That(CharacterRoster.Find(account, "Asli", 13), Is.Null);
        }

        [Test]
        public void LinkAccountId_StoresTheIdOnTheActiveCharacter()
        {
            var account = new AccountSettings { InstanceNumber = 901 };
            account.Characters.Add(new CharacterEntry { Name = "[KFC]Asli", Kingdom = 1 });
            account.Characters.Add(new CharacterEntry { Name = "[KFC]Ilsa", Kingdom = 1 });

            CharacterRoster.SetActive(901, "[KFC]llsa", 1);
            CharacterRoster.LinkAccountId(account, "1104083889");

            Assert.That(account.Characters[1].AccountId, Is.EqualTo("1104083889"));
            Assert.That(account.Characters[0].AccountId, Is.Null.Or.Empty);
        }
    }
}

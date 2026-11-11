using Bot.Core.Services;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    [TestFixture]
    public class ArenaGuildieTests
    {
        [TestCase("[KFC]llsa", "KFC")]
        [TestCase(" [KFC]Asli", "KFC")]
        [TestCase("llsa", null)]
        [TestCase(null, null)]
        public void AllianceTag_FromTheCharacterName(string? name, string? tag) =>
            Assert.That(CharacterRoster.AllianceTagOf(name), Is.EqualTo(tag));

        // OCR garbles the brackets: "KFCIMAS SCHUIL24", "[KFC|Darth Vader" (War list reads, 2026-11-08)
        [TestCase("[KFC]Someone", true)]
        [TestCase("[kfc]someone", true)]
        [TestCase("KFCIMAS SCHUIL24", true)]
        [TestCase("[KFC|Darth Vader", true)]
        [TestCase("(KFC)Someone", true)]
        [TestCase("[CKP]Someone", false)]
        [TestCase("Common Guardsman", false)]
        [TestCase("", false)]
        public void IsAllianceMember(string name, bool member) =>
            Assert.That(CharacterRoster.HasAllianceTag(name, "KFC"), Is.EqualTo(member));
    }
}

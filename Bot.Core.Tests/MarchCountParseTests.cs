using Bot.Core.Tasks.Modules.AutoHunt;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    /// <summary>OCR text of the march counter to the number of free marches (AutoHunt).</summary>
    [TestFixture]
    public class MarchCountParseTests
    {
        [TestCase("3/5", 3)]
        [TestCase("0/5", 0)]
        [TestCase(" 2 / 6 \n", 2)]
        [TestCase("4", 4)]
        [TestCase("x1/5y", 1)]
        [TestCase("77", 6)]      // noise from the VIP badge: capped at the queue limit
        [TestCase("12/5", 6)]
        public void Parse_ReadsFirstNumber(string text, int expected) =>
            Assert.That(AutoHuntCoordinator.ParseMarchCount(text), Is.EqualTo(expected));

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("abc")]
        [TestCase("/5")]
        [TestCase("//")]
        [TestCase(null)]
        public void Parse_UnreadableIsNull(string? text) =>
            Assert.That(AutoHuntCoordinator.ParseMarchCount(text), Is.Null);
    }
}

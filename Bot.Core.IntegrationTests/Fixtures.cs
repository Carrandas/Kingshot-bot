namespace Bot.Core.IntegrationTests
{
    /// <summary>
    /// Saved 720x1280 game screenshots in Screenshots/. They are PNG bytes, the same format the bot's
    /// TakeScreenshotAsync hands the matchers, so a test goes through the same decode as a live run.
    /// </summary>
    internal static class Fixtures
    {
        public static byte[] Load(string name) =>
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Screenshots", name));
    }
}

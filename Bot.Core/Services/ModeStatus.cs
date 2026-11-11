namespace Bot.Core.Services
{
    /// <summary>The exclusive modes (Castle Battle, Bear Hunt): at most one runs, and neither runs next to a single task run</summary>
    public static class ModeStatus
    {
        public static bool AnyRunning => CastleBattleStatus.IsRunning || BearHuntStatus.IsRunning;
    }
}

namespace Gunbound
{
    /// <summary>
    /// Current game format: Free-for-all. Values are set from GameBootstrap's inspector on start.
    /// </summary>
    public static class GameRules
    {
        public static string ModeName = "FREE FOR ALL";
        /// <summary>Points awarded for killing another player.</summary>
        public static int PointsPerKill = 1;
        /// <summary>A spawn is only used if every living enemy is at least this far away (metres)...</summary>
        public static float MinSpawnDistance = 15f;
        /// <summary>...and none of them can see it.</summary>
        public static bool RequireNoLineOfSight = true;
        /// <summary>Damage dealt when a grapple hook hits a player.</summary>
        public static float HookDamage = 20f;
    }
}

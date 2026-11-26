using System;
using UnityEngine;

namespace LSBS
{
    /// <summary>Up to eight sides, each with a colour. The person playing commands PlayerTeam.</summary>
    public static class Team
    {
        public const int Max = 8;
        public const byte None = 255;
        public static readonly string[] Names = { "Blue", "Red", "Green", "Gold", "Violet", "Orange", "Cyan", "White" };
        public static readonly UnityEngine.Color[] Colours =
        {
            new(0.25f, 0.55f, 1f), new(0.95f, 0.25f, 0.2f), new(0.3f, 0.85f, 0.35f), new(1f, 0.8f, 0.2f),
            new(0.7f, 0.4f, 1f), new(1f, 0.5f, 0.1f), new(0.2f, 0.9f, 0.9f), new(0.92f, 0.92f, 0.95f),
        };
        public static UnityEngine.Color Colour(int t) => t >= 0 && t < Max ? Colours[t] : new UnityEngine.Color(0.5f, 0.5f, 0.5f);
    }

    public static class Clip
    {
        public const byte Idle = 0, Run = 1, CrouchRun = 2, StandAim = 3, StandFire = 4, StandReload = 5,
            KneelIdle = 6, KneelAim = 7, KneelFire = 8, KneelReload = 9, DieBack = 10, DieForward = 11,
            StandPump = 12, KneelPump = 13, Throw = 14, Call = 15,
            PistolBase = 16, // the first ten again, with a pistol
            MaximStand = 26, MaximKneel = 27, StandStepL = 28, StandStepR = 29, KneelStepL = 30, KneelStepR = 31, MaximStandReload = 32, MaximKneelReload = 33, Setup = 34, Count = 35;
        /// <summary>The machine gun clips run from one end of its user's reach to the other, not over time.</summary>
        public const int TwistFrames = 25;
        public static bool IsRun(byte c) => c == Run || c == CrouchRun || c == PistolBase + Run || c == PistolBase + CrouchRun;
        public static bool IsFire(byte c) => c == PistolBase + StandFire || c == PistolBase + KneelFire || c == StandFire || c == KneelFire || c == StandPump || c == KneelPump;
    }

    /// <summary>Every number worth adjusting. Edit them on the Battle object in the LSBS scene.</summary>
    [Serializable]
    public struct Tuning
    {
        /// <summary>Bumped when the settings change shape: a scene saved before then takes the new ones.</summary>
        public const int Latest = 15;
        /// <summary>A scene saved with older settings: keep what it has, and fill in what's new.</summary>
        public static void Upgrade(ref Tuning t)
        {
            if (t.version == Latest) return;
            var d = Default;
            int had = t.version;
            if (had < 6) { t = d; return; }
            t.version = Latest;
            // launchers reload rather than run out; missiles home only on aircraft, and go astray a little at anything else
            t.missiles = d.missiles; t.missileCooldown = d.missileCooldown; t.missileError = d.missileError;
            if (had == 14) return;
            // the practice game: one objective held ten minutes to win, gold to buy with, reinforcements every five minutes
            t.holdToWin = d.holdToWin; t.startGold = d.startGold; t.waveGold = d.waveGold;
            t.squadCost = d.squadCost; t.tankCost = d.tankCost; t.destroyerCost = d.destroyerCost;
            t.waveInterval = d.waveInterval; t.captureRadius = d.captureRadius; t.captureSeconds = d.captureSeconds;
            if (had == 13) return;
            // one of each squad carries a launcher, with homing missiles for vehicles
            t.missileRange = d.missileRange; t.missileSpeed = d.missileSpeed; t.missileTurn = d.missileTurn;
            if (had == 12) return;
            // the rifle goes automatic: 30% less a round, 30% as likely to hit (magazine and rate live in Gun);
            // squads are eight riflemen and two sweepers, nobody else
            t.damage *= 0.7f;
            t.closeHit *= 0.3f; t.maxRangeHit *= 0.3f;
            t.infantryWeight = d.infantryWeight; t.sweeperWeight = d.sweeperWeight; t.communicatorWeight = 0f; t.gunnerWeight = 0f;
            if (had == 11) return;
            // blasts do half the harm they did, and a tank's shell wanders by angle rather than by a set distance
            t.grenadeFullDamage *= 0.5f; t.grenadeEdgeDamage *= 0.5f; t.shellFullDamage *= 0.5f; t.shellEdgeDamage *= 0.5f;
            t.tankScatter = d.tankScatter;
            if (had == 10) return;
            // everything less accurate, and all but shotguns shorter ranged, than whatever they were set to
            const float aim = 0.3f, range = 0.7f;
            t.pistolMaxRange = t.shotgunMaxRange * range; t.pistolCloseRange = t.shotgunCloseRange * range;
            t.pistolMaxRangeHit = t.shotgunMaxRangeHit * aim; t.pistolCloseHit = t.shotgunCloseHit * aim;
            t.maxRange *= range; t.closeRange *= range;
            t.maxRangeHit *= aim; t.closeHit *= aim;
            t.shotgunMaxRangeHit *= aim; t.shotgunCloseHit *= aim;
            t.tankRange = d.tankRange; t.tankScatter = d.tankScatter;
            t.maximHit = d.maximHit; t.tankGunRange = d.tankGunRange;
            if (had == 9) return;
            t.soldiersPerGun = d.soldiersPerGun;
            if (had < 8) { t.soldiersPerTank = d.soldiersPerTank; t.maxTanks = d.maxTanks; }
            if (had > 6) return;
            t.infantryWeight = d.infantryWeight; t.sweeperWeight = d.sweeperWeight; t.communicatorWeight = d.communicatorWeight; t.gunnerWeight = d.gunnerWeight;
            t.pistolDamage = d.pistolDamage;
        }
        [HideInInspector] public int version;

        [Header("The game")]
        [Tooltip("Seconds a side must hold the objective, all told, to win.")] public float holdToWin;
        [Tooltip("Gold each side starts with, and gets again each wave.")] public int startGold, waveGold;
        [Tooltip("What each thing costs.")] public int squadCost, tankCost, destroyerCost;

        [Header("Forces")]
        [Tooltip("Soldiers each side starts with.")] public int startPerSide;
        [Tooltip("Reinforcements never take a side above this.")] public int capPerSide;
        public int waveSize;
        [Tooltip("Seconds between reinforcement waves.")] public float waveInterval;
        [Tooltip("How likely each is to be what a recruit turns out as, against the others.")] public float infantryWeight, sweeperWeight, communicatorWeight, gunnerWeight;
        [Tooltip("How much taller or shorter than the model people are.")] public float heightSpread;

        [Header("Sebastion")]
        public float maxRange;
        public float maxRangeHit;
        [Tooltip("At this range or closer, closeHit applies.")] public float closeRange;
        public float closeHit;
        public float damage;

        [Header("Rachmaninoff (each pellet)")]
        public float shotgunMaxRange;
        public float shotgunMaxRangeHit, shotgunCloseRange, shotgunCloseHit, pelletDamage;
        public int pellets;

        [Header("Maxim (against the Sebastion's numbers)")]
        [Tooltip("Its hit chances, as a share of the Sebastion's.")] public float maximHit;
        [Tooltip("The range of those on tanks, as a share of one on its tripod.")] public float tankGunRange;

        [Header("Wolfgang")]
        public float pistolMaxRange;
        public float pistolMaxRangeHit, pistolCloseRange, pistolCloseHit;
        public float pistolDamage;

        [Header("Where shots land")]
        public float headChance;
        public float limbChance, headMultiplier, limbMultiplier;
        [Tooltip("Off: exactly the hit chances above. On: cover, kneeling, movement and suppression reduce them.")]
        public bool battlefieldModifiers;
        public float coverHit, kneelHit, movingHit;
        [Tooltip("Seconds a soldier takes to line up each shot.")] public float aimMin, aimMax;

        [Header("Grenades")]
        public int grenades;
        public float grenadeFullRadius, grenadeFullDamage, grenadeEdgeRadius, grenadeEdgeDamage;
        public float grenadeMinThrow, grenadeMaxThrow, grenadeFuse;

        [Header("Missile launcher")]
        [Tooltip("Missiles each launcher man carries (they reload in between, so this is only a cap).")] public int missiles;
        [Tooltip("Seconds between one missile and the next.")] public float missileCooldown;
        [Tooltip("Degrees a missile fired at something on the ground may go astray (aircraft are tracked).")] public float missileError;
        [Tooltip("How far off they'll fire at a vehicle.")] public float missileRange;
        [Tooltip("Metres a second.")] public float missileSpeed;
        [Tooltip("Degrees a second it can turn after its target.")] public float missileTurn;

        [Header("Artillery")]
        public float callRange;
        [Tooltip("A side has one gun for every so many soldiers in its army.")] public int soldiersPerGun;
        public float strikeSeconds, strikeRadius, strikeCooldown;
        [Tooltip("Seconds a gun is out of it after a strike, before it can fire another.")] public float gunRest;
        public float shellFullRadius, shellFullDamage, shellEdgeRadius, shellEdgeDamage;
        [Tooltip("How much of a shell somebody down on one knee takes, unless it's right on top of them.")] public float lowBlast;

        [Header("Blasts")]
        [Tooltip("Explosions hurt the thrower's own side too.")] public bool friendlyBlasts;
        [Tooltip("Chance that somebody killed well inside a blast is blown apart.")] public float dismember;

        [Header("Movement")]
        public float runSpeed;
        public float cautiousSpeed;

        [Header("Commanders")]
        [Tooltip("Seconds of recent kills and losses a commander remembers.")] public float memory;
        [Tooltip("Push when kills minus losses is above this share of the cap.")] public float attackEdge;
        [Tooltip("Dig in when losses minus kills is above this share of the cap.")] public float defendEdge;
        [Tooltip("A side that has held this long pushes anyway.")] public float maxDefend;
        [Tooltip("Share of captains who attack while their side pushes, and while it digs in.")] public float pushShare, holdShare;

        [Header("Tanks")]
        [Tooltip("One tank comes up for every so many soldiers. 0 for none.")] public int soldiersPerTank;
        [Tooltip("The most a side may have at once.")] public int maxTanks;
        [Tooltip("How far off a tank's crew will fire its main gun.")] public float tankRange;
        [Tooltip("Degrees off where it's aimed a tank's shell may go, seen from the tank.")] public float tankScatter;

        [Header("Bunkers")]
        [Tooltip("The most soldiers who'll stay to hold one.")] public int bunkerCap;
        [Tooltip("Chance a second that a soldier of a squad holding nearby stays in one with room.")] public float bunkerStay;

        [Header("Objectives")]
        public float captureRadius;
        public float captureSeconds;

        [Header("Bodies")]
        public int maxRagdolls;
        public int maxCorpses;
        public float ragdollDistance;

        public static Tuning Default => new Tuning
        {
            version = Latest,
            holdToWin = 600f, startGold = 1000, waveGold = 500, squadCost = 10, tankCost = 100, destroyerCost = 100,
            startPerSide = 500, capPerSide = 500, waveSize = 100, waveInterval = 300f, infantryWeight = 8f, sweeperWeight = 2f, communicatorWeight = 0f, gunnerWeight = 0f, pistolDamage = 80f, heightSpread = 0.08f,
            maxRange = 52.5f, maxRangeHit = 0.009f, closeRange = 17.5f, closeHit = 0.081f, damage = 70f,
            shotgunMaxRange = 40f, shotgunMaxRangeHit = 0.03f, shotgunCloseRange = 20f, shotgunCloseHit = 0.27f,
            pistolMaxRange = 28f, pistolMaxRangeHit = 0.03f, pistolCloseRange = 14f, pistolCloseHit = 0.27f, tankRange = 100f, tankScatter = 10f, maximHit = 0.5f, tankGunRange = 0.75f, pelletDamage = 20f, pellets = 9,
            headChance = 0.10f, limbChance = 0.20f, headMultiplier = 2f, limbMultiplier = 0.5f,
            battlefieldModifiers = true, coverHit = 0.35f, kneelHit = 0.8f, movingHit = 0.6f, aimMin = 0.8f, aimMax = 2.4f,
            grenades = 2, grenadeFullRadius = 8f, grenadeFullDamage = 50f, grenadeEdgeRadius = 15f, grenadeEdgeDamage = 15f,
            grenadeMinThrow = 17f, grenadeMaxThrow = 34f, grenadeFuse = 2.4f,
            missiles = 999, missileCooldown = 30f, missileError = 2.5f, missileRange = 140f, missileSpeed = 55f, missileTurn = 120f,
            callRange = 200f, soldiersPerGun = 50, strikeSeconds = 20f, strikeRadius = 20f, strikeCooldown = 90f, gunRest = 240f,
            shellFullRadius = 8.5f, shellFullDamage = 60f, shellEdgeRadius = 20f, shellEdgeDamage = 15f, lowBlast = 0.5f,
            friendlyBlasts = true, dismember = 0.55f,
            runSpeed = 4.6f, cautiousSpeed = 3.2f,
            memory = 20f, attackEdge = 0.01f, defendEdge = 0.03f, maxDefend = 30f, pushShare = 0.75f, holdShare = 0.3f,
            captureRadius = 24f, captureSeconds = 20f,
            maxRagdolls = 300, maxCorpses = 1500, ragdollDistance = 190f, soldiersPerTank = 100, maxTanks = 30, bunkerCap = 6, bunkerStay = 0.35f,
        };
    }
}

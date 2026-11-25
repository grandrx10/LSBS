using System;
using UnityEngine;

namespace LSBS
{
    public static class Team { public const byte Green = 0, Grey = 1; }

    public static class Clip
    {
        public const byte Idle = 0, Run = 1, CrouchRun = 2, StandAim = 3, StandFire = 4, StandReload = 5,
            KneelIdle = 6, KneelAim = 7, KneelFire = 8, KneelReload = 9, DieBack = 10, DieForward = 11,
            StandPump = 12, KneelPump = 13, Throw = 14, Call = 15, Count = 16;
        public static bool IsRun(byte c) => c == Run || c == CrouchRun;
        public static bool IsFire(byte c) => c == StandFire || c == KneelFire || c == StandPump || c == KneelPump;
    }

    /// <summary>Every number worth adjusting. Edit them on the Battle object in the LSBS scene.</summary>
    [Serializable]
    public struct Tuning
    {
        /// <summary>Bumped when the settings change shape: a scene saved before then takes the new ones.</summary>
        public const int Latest = 5;
        [HideInInspector] public int version;

        [Header("Forces")]
        [Tooltip("Soldiers each side starts with.")] public int startPerSide;
        [Tooltip("Reinforcements never take a side above this.")] public int capPerSide;
        public int waveSize;
        [Tooltip("Seconds between reinforcement waves.")] public float waveInterval;
        [Tooltip("Share of recruits. The rest are infantry.")] public float sweepers, communicators;
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

        [Header("Artillery")]
        public float callRange;
        public int gunsPerTeam;
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
            startPerSide = 500, capPerSide = 500, waveSize = 100, waveInterval = 30f, sweepers = 0.2f, communicators = 0.2f, heightSpread = 0.08f,
            maxRange = 75f, maxRangeHit = 0.10f, closeRange = 25f, closeHit = 0.90f, damage = 100f,
            shotgunMaxRange = 40f, shotgunMaxRangeHit = 0.10f, shotgunCloseRange = 20f, shotgunCloseHit = 0.90f, pelletDamage = 20f, pellets = 9,
            headChance = 0.10f, limbChance = 0.20f, headMultiplier = 2f, limbMultiplier = 0.5f,
            battlefieldModifiers = true, coverHit = 0.35f, kneelHit = 0.8f, movingHit = 0.6f, aimMin = 0.8f, aimMax = 2.4f,
            grenades = 2, grenadeFullRadius = 8f, grenadeFullDamage = 100f, grenadeEdgeRadius = 15f, grenadeEdgeDamage = 30f,
            grenadeMinThrow = 17f, grenadeMaxThrow = 34f, grenadeFuse = 2.4f,
            callRange = 200f, gunsPerTeam = 20, strikeSeconds = 20f, strikeRadius = 20f, strikeCooldown = 90f, gunRest = 240f,
            shellFullRadius = 8.5f, shellFullDamage = 120f, shellEdgeRadius = 20f, shellEdgeDamage = 30f, lowBlast = 0.5f,
            friendlyBlasts = true, dismember = 0.55f,
            runSpeed = 4.6f, cautiousSpeed = 3.2f,
            memory = 20f, attackEdge = 0.01f, defendEdge = 0.03f, maxDefend = 30f, pushShare = 0.75f, holdShare = 0.3f,
            captureRadius = 18f, captureSeconds = 18f,
            maxRagdolls = 64, maxCorpses = 1500, ragdollDistance = 110f,
        };
    }
}

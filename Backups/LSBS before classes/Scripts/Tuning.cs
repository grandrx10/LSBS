using System;
using UnityEngine;

namespace LSBS
{
    public static class Team { public const byte Green = 0, Grey = 1; }
    public static class Obj { public const int GreenBase = 0, Middle = 1, GreyBase = 2; }

    public static class Clip
    {
        public const byte Idle = 0, Run = 1, CrouchRun = 2, StandAim = 3, StandFire = 4, StandReload = 5,
            KneelIdle = 6, KneelAim = 7, KneelFire = 8, KneelReload = 9, DieBack = 10, DieForward = 11, Count = 12;
    }

    /// <summary>Every number worth adjusting. Edit them on the Battle object in the LSBS scene.</summary>
    [Serializable]
    public struct Tuning
    {
        [Header("Forces")]
        [Tooltip("Soldiers each side starts with.")] public int startPerSide;
        [Tooltip("Reinforcements never take a side above this.")] public int capPerSide;
        public int waveSize;
        [Tooltip("Seconds between reinforcement waves.")] public float waveInterval;

        [Header("Shooting")]
        public float maxRange;
        public float maxRangeHit;
        [Tooltip("At this range or closer, closeHit applies.")] public float closeRange;
        public float closeHit;
        public float damage;
        public float headChance;
        public float limbChance;
        public float limbMultiplier;
        [Tooltip("Off: exactly the hit chances above. On: cover, kneeling, movement and suppression reduce them.")]
        public bool battlefieldModifiers;
        public float coverHit, kneelHit, movingHit;
        [Tooltip("Seconds a soldier takes to line up each shot.")] public float aimMin, aimMax;

        [Header("Movement")]
        public float runSpeed;
        public float cautiousSpeed;

        [Header("Commanders")]
        [Tooltip("Seconds of recent kills and losses a commander remembers.")] public float memory;
        [Tooltip("Push when kills minus losses is above this share of the cap.")] public float attackEdge;
        [Tooltip("Dig in when losses minus kills is above this share of the cap.")] public float defendEdge;
        [Tooltip("A side that has held this long pushes anyway.")] public float maxDefend;

        [Header("Objectives")]
        public float captureRadius;
        public float captureSeconds;

        [Header("Bodies")]
        public int maxRagdolls;
        public int maxCorpses;
        public float ragdollDistance;

        public static Tuning Default => new Tuning
        {
            startPerSide = 500, capPerSide = 500, waveSize = 100, waveInterval = 30f,
            maxRange = 75f, maxRangeHit = 0.10f, closeRange = 25f, closeHit = 0.90f,
            damage = 100f, headChance = 0.10f, limbChance = 0.20f, limbMultiplier = 0.5f,
            battlefieldModifiers = true, coverHit = 0.35f, kneelHit = 0.8f, movingHit = 0.6f, aimMin = 0.8f, aimMax = 2.4f,
            runSpeed = 4.6f, cautiousSpeed = 3.2f,
            memory = 20f, attackEdge = 0.01f, defendEdge = 0.03f, maxDefend = 30f,
            captureRadius = 24f, captureSeconds = 20f,
            maxRagdolls = 64, maxCorpses = 1500, ragdollDistance = 110f,
        };
    }
}

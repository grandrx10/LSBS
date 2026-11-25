using Unity.Entities;
using Unity.Mathematics;

namespace LSBS
{
    /// <summary>Who a soldier is. Slot is their stable row in the shared arrays; Gen tells one occupant of a slot from the next.</summary>
    public struct Soldier : IComponentData
    {
        public int Slot;
        public int Gen;
        public int Squad;
        public byte Team;
        public byte Member;
        /// <summary>Driven by the person playing instead of the AI.</summary>
        public bool Player;
    }

    public struct Body : IComponentData
    {
        public float3 Pos;
        public float2 Vel;
        public float Yaw;
        public float Pitch;
    }

    public struct Health : IComponentData
    {
        public float Value;
    }

    public struct Brain : IComponentData
    {
        public Random Rng;
        public int Target;       // slot, or -1
        public int TargetGen;
        public float TargetDist;
        public float SenseTimer;
        public float Suppression;
        public float2 HoldPos;
        public bool Holding, HasHold;
        public bool Kneel;
        public float KneelTimer;
        public float Stuck;
    }

    public enum GunPhase : byte { Ready, Firing, Reloading }

    public struct Gun : IComponentData
    {
        public const float FireTime = 0.6f, ReloadTime = 2f; // Sebastian: 0.2 s, then 0.4 s of lever; 0.5 s a round
        public const int Magazine = 4;
        public GunPhase Phase;
        public float Timer;
        public int Ammo;
    }

    public struct Anim : IComponentData
    {
        public byte Clip, Prev;
        public float Time, PrevTime;
        public float Blend; // 0 = all Prev, 1 = all Clip
    }

    /// <summary>The public view of a soldier that other soldiers read (one row a slot, refreshed every frame).</summary>
    public struct SlotState
    {
        public float3 Pos;
        public float2 Vel;
        public float Yaw;
        public float Suppression;
        public int Gen;
        public int Squad;
        public byte Team;
        public byte Member;
        public bool Alive, Kneel, HasTarget, Player;
    }

    public enum SquadMode : byte { Advance, Hold }

    public unsafe struct Squad
    {
        public const int Size = 5;
        public bool Active;
        public byte Team;
        public fixed int Members[Size]; // slots, or -1
        public int Captain;             // index into Members
        public int Alive;
        public int Contacts;
        public bool Attacking;
        public SquadMode Mode;
        public float ModeTimer;
        public float2 Pos;              // the captain's
        public float2 Facing;
        public float2 Goal;
        public int Field;               // objective being walked to
        public float Lane;              // z this squad keeps to on the way
        public float Nerve;             // 0 careful .. 1 reckless
        public bool PlayerLed;
        public bool PlayerHold;
        public Random Rng;
    }

    public struct TeamState
    {
        public bool Attacking;
        public int Target;       // objective index it's attacking or holding
        public int Alive;
        public float Kills, Losses; // recent, fading
        public int TotalKills, TotalLosses;
        public float StanceTime;
    }

    public struct ShotEvent
    {
        public int Shooter, Target, ShooterGen, TargetGen;
        public float3 From, To;
        public float Damage;
        public byte Region; // 0 body, 1 head, 2 limb
        public byte Team;
        public bool Hit;
    }

    public struct DeathEvent
    {
        public int Slot, Gen;
        public float3 Pos, Dir;
        public float2 Vel;
        public float Yaw;
        public Anim Anim;
        public byte Team, Region;
        public bool Captain, Player;
        public float Seed;
    }
}

using Unity.Entities;
using Unity.Mathematics;

namespace LSBS
{
    public enum SoldierClass : byte { Infantry, Sweeper, Communicator }

    /// <summary>The nine objectives: a base at each end, and rows of two, three and two between.</summary>
    public static class Front
    {
        public const int Count = 9, GreenBase = 0, GreyBase = 8, Centre = 4;
        public static readonly int[] Row = { 0, 1, 1, 2, 2, 2, 3, 3, 4 };
        /// <summary>What leads on to what. A side can only take an objective next to one it already holds.</summary>
        public static readonly int[][] Next =
        {
            new[] { 1, 2 }, new[] { 0, 3, 4 }, new[] { 0, 4, 5 }, new[] { 1, 6 }, new[] { 1, 2, 6, 7 }, new[] { 2, 7 },
            new[] { 3, 4, 8 }, new[] { 4, 5, 8 }, new[] { 6, 7 },
        };
        public static int Home(int team) => team == Team.Green ? GreenBase : GreyBase;
    }

    /// <summary>Who a soldier is. Slot is their stable row in the shared arrays; Gen tells one occupant of a slot from the next.</summary>
    public struct Soldier : IComponentData
    {
        public int Slot;
        public int Gen;
        public int Squad;
        public byte Team;
        public byte Member;
        public SoldierClass Class;
        /// <summary>Driven by the person playing instead of the AI.</summary>
        public bool Player;
        /// <summary>1 is the model's own height.</summary>
        public float Height;
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
        public float2 Heading;   // the way they've been going, smoothed
        public bool Holding;
        public bool Walking;
        public float HoldLeft;   // seconds they'll go on holding regardless
        public bool Kneel;
        public float KneelTimer;
        public float Dive;       // seconds left flat out of the way of something coming in
        public float Still;      // seconds since they last walked
        public float Stuck;
    }

    public enum Act : byte { None, Throw, Call }

    /// <summary>A class's own business: grenades, and calling the guns.</summary>
    public struct Ability : IComponentData
    {
        public const float ThrowTime = 0.9f, ReleaseAt = 0.45f, CallTime = 2.6f;
        public int Grenades;
        public float Cooldown;
        public Act Act;
        public float Timer;      // counts up
        public bool Done;        // thrown, or called
        public float3 At;
    }

    public enum GunPhase : byte { Ready, Firing, Reloading }

    public struct Gun : IComponentData
    {
        public GunPhase Phase;
        public float Timer;
        public int Ammo;

        // Sebastion: 0.2 s, then 0.4 s of lever. Rachmaninoff: 0.1 s, then 0.3 s of pump. Half a second a round to reload.
        public static float FireTime(SoldierClass c) => c == SoldierClass.Sweeper ? 0.4f : 0.6f;
        public static int Magazine(SoldierClass c) => c == SoldierClass.Sweeper ? 5 : 4;
        public static float ReloadTime(SoldierClass c) => 0.5f * Magazine(c);
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
        public float Height;
        public int Gen;
        public int Squad;
        public byte Team;
        public byte Member;
        public SoldierClass Class;
        public bool Alive, Kneel, HasTarget, Player;
    }

    public enum SquadMode : byte { Advance, Hold }

    public unsafe struct Squad
    {
        public const int Size = 10;
        public bool Active;
        public byte Team;
        public fixed int Members[Size]; // slots, or -1
        public int Captain;             // index into Members
        public int Alive;
        public int Contacts;
        public bool Attacking;          // the captain's choice
        public int Objective;           // what they're taking or holding
        public SquadMode Mode;
        public float ModeTimer;
        public float2 Pos;              // the captain's
        public float2 Vel;
        public float2 Facing;
        public float2 Goal;
        public float2 Spot;             // where in the objective they like to be, -1..1
        public int Crowd;               // squads of their side after the same one
        public float Nerve;             // 0 careful .. 1 reckless
        public bool PlayerLed;
        public bool PlayerHold;
        public Random Rng;
    }

    public struct TeamState
    {
        public bool Attacking;
        public int Alive;
        public float Kills, Losses; // recent, fading
        public int TotalKills, TotalLosses;
        public float StanceTime;
        public int Held;            // objectives
        public int GunsBusy;
    }

    public enum ShotKind : byte { Rifle, Shotgun, Blast }

    public struct ShotEvent
    {
        public int Shooter, Target, ShooterGen, TargetGen;
        public float3 From, To;     // a blast's From is where it went off
        public float Damage;
        public float Power;         // blasts: 1 at the middle, 0 at the edge
        public byte Region;         // 0 body, 1 head, 2 limb
        public byte Team;
        public byte Pellets;        // that struck
        public ShotKind Kind;
        public bool Hit;
        public bool Big;            // artillery
    }

    public struct DeathEvent
    {
        public int Slot, Gen;
        public float3 Pos, Dir, From;
        public float2 Vel;
        public float Yaw, Height;
        public Anim Anim;
        public byte Team, Region;
        public SoldierClass Class;
        public ShotKind Kind;
        public float Power;
        public bool Captain, Player, Big;
        public float Seed;
    }

    public struct Throw
    {
        public float3 From, At;
        public byte Team;
        public int Shooter, ShooterGen;
    }

    /// <summary>A live grenade.</summary>
    public struct Grenade
    {
        public float3 Pos, Vel;
        public float Fuse, Spin;
        public byte Team;
        public int Shooter, ShooterGen;
    }

    /// <summary>Five shells in twenty seconds, about one spot.</summary>
    public unsafe struct Strike
    {
        public const int Shells = 5;
        public float3 At;
        public float Age;
        public byte Team;
        public int Caller, CallerGen;
        public fixed float When[Shells];
        public fixed float X[Shells];
        public fixed float Z[Shells];
        public fixed int Gun[Shells];
        public const float Flight = 4.2f; // seconds in the air
        public int Fired, Landed, Warned; // bits
        public bool Done;         // its gun's resting
    }

    /// <summary>Something for the eyes and ears.</summary>
    public struct BlastEvent
    {
        public float3 At;
        public float Radius;
        public bool Big;
        public bool Whistle; // not landed yet: on its way
        public bool Report;  // a gun going off at home
        public byte Team;
    }
}

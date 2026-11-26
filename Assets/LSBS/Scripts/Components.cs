using Unity.Entities;
using Unity.Mathematics;

namespace LSBS
{
    public enum SoldierClass : byte { Infantry, Sweeper, Communicator, Gunner, Launcher }

    /// <summary>The places that matter: the objective in the middle, then each side's landing ground.</summary>
    public static class Front
    {
        public const int Centre = 0;
        /// <summary>One more than the number of sides; set when the field is made.</summary>
        public static int Count = 3;
        public static int Home(int team) => 1 + team;
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
        public float2 Way;       // the next corner to make for, when the straight way's blocked
        public float WayLeft;
        public bool Mounted;     // on a machine gun, which looks this way
        public float Arc;
        public bool OnPost;      // staying to man a bunker
        public float2 Post;
        public float Dive;       // seconds left flat out of the way of something coming in
        public float Still;      // seconds since they last walked
        public float Stuck;
    }

    public enum Act : byte { None, Throw, Call, Setup, Pack, Launch }

    /// <summary>A class's own business: grenades, and calling the guns.</summary>
    public struct Ability : IComponentData
    {
        public const float ThrowTime = 0.9f, ReleaseAt = 0.45f, CallTime = 2.6f, LaunchTime = 1.6f, LaunchAt = 1.0f;
        public int Grenades;
        /// <summary>Missile launchers: how many they've left, and which vehicle (index into the tanks this frame) they're putting one into.</summary>
        public int Missiles;
        public int Target;
        public float Cooldown;
        public Act Act;
        public float Timer;      // counts up
        public bool Done;        // thrown, or called
        public float3 At;
        // gunners, and whoever's on a machine gun
        public bool Carrying;    // it's on their back
        public int MaximAmmo;
        public int Maxim;        // the one they're on, or -1
        public int Tank;         // the one they're inside, or -1
        public int Want;         // one they're going to, or -1
        public float2 WantAt;
        public float Yaw;        // which way it's to face
        public bool Tall;

        public static float Length(Act a) => a == Act.Throw ? ThrowTime : a == Act.Call ? CallTime : a == Act.Launch ? LaunchTime : a == Act.Setup ? LSBS.Maxim.SetUp : LSBS.Maxim.PackUp;
    }

    public enum GunPhase : byte { Ready, Firing, Reloading }

    public struct Gun : IComponentData
    {
        public GunPhase Phase;
        public float Timer;
        public int Ammo;
        /// <summary>Who the last round went at (slot, or -1): staying on them needs no fresh aim.</summary>
        public int AimedAt;

        // LR-15: automatic, 500 rounds a minute, twenty to a magazine. Rachmaninoff: 0.1 s, then 0.3 s of pump, half a second a shell to reload.
        public const float RifleRpm = 500f;
        public static float FireTime(SoldierClass c) => c == SoldierClass.Sweeper ? 0.4f : c == SoldierClass.Gunner ? 0.2f : 60f / RifleRpm;
        public static int Magazine(SoldierClass c) => c == SoldierClass.Sweeper ? 5 : c == SoldierClass.Gunner ? 8 : 20;
        public static float ReloadTime(SoldierClass c) => c == SoldierClass.Gunner ? 1.5f : Automatic(c) ? 2.2f : 0.5f * Magazine(c);
        /// <summary>Fires for as long as the trigger's held, rather than a round a click.</summary>
        public static bool Automatic(SoldierClass c) => c == SoldierClass.Infantry || c == SoldierClass.Launcher;
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
        public bool Alive, Kneel, HasTarget, Player, Mounted, Carrying, Inside;
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
        public bool Crew;               // of a tank
        /// <summary>Aboard a transport, out of the fight till it lands.</summary>
        public bool Inside;
        public Random Rng;
        /// <summary>The commander has told them where to go; they find their own way there and fight what they meet.</summary>
        public bool Ordered;
        public float2 OrderAt;
        /// <summary>Where the group they were sent with is going; their flow field leads there.</summary>
        public float2 OrderVia;
        /// <summary>Which of the order flow fields leads there.</summary>
        public int Field;

        /// <summary>Seven riflemen, a missile launcher and two sweepers, always in the same places.</summary>
        public static SoldierClass ClassOf(int member) => member >= Size - 2 ? SoldierClass.Sweeper : member == Size - 3 ? SoldierClass.Launcher : SoldierClass.Infantry;
    }

    /// <summary>An order from a commander, applied at the start of a step (so it can one day travel over the network).</summary>
    public struct OrderCommand
    {
        public int Squad;
        public float2 At, Via;
        public byte Team;
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

    public enum ShotKind : byte { Rifle, Shotgun, Blast, Pistol, Maxim, Tank }

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
        public bool Captain, Player, Big, Carrying;
        public float Seed;
        public int Killer, KillerGen;
        public byte KillerTeam;
    }

    /// <summary>A missile leaving its launcher, after a vehicle.</summary>
    public struct Launch
    {
        public float3 From;
        /// <summary>The vehicle (a tank below the tank count, else a transport), or -1 to fly straight along Dir.</summary>
        public int Tank;
        public float3 Dir;
        /// <summary>Fired by the person playing, looking along it: steadier than the AI's reckoning.</summary>
        public bool Aimed;
        public byte Team;
        public int Shooter, ShooterGen;
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
        public bool Wreck;   // a machine gun broken up
        public bool Cannon;  // a tank's gun going off
        public float3 Dir;
        public byte Team;
    }
}

using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace LSBS
{
    public enum TankKind : byte { Tank, Destroyer }

    /// <summary>What soldiers need to know of a tank (or anything else they might shoot a missile at).</summary>
    public struct TankState
    {
        public float3 Pos;
        public float Yaw, Speed;
        public byte Team;
        public bool Wreck;
        public float HalfWide, HalfLong;
        /// <summary>Up in the air: no use throwing grenades at it or stepping out of its way.</summary>
        public bool Flying;

        /// <summary>A place in the tank's own frame (x right, y ahead).</summary>
        public float2 Local(float2 p)
        {
            float2 d = p - Pos.xz;
            math.sincos(Yaw, out float s, out float c);
            return new float2(d.x * c - d.y * s, d.x * s + d.y * c);
        }

        public float2 World(float2 l)
        {
            math.sincos(Yaw, out float s, out float c);
            return Pos.xz + new float2(l.x * c + l.y * s, -l.x * s + l.y * c);
        }
    }

    /// <summary>
    /// The two kinds' shapes, from the blockouts: the tank, a turret on a hull between two tracks, with a machine gun port in
    /// its nose; the destroyer, longer and lower, its gun fixed in the front of a casemate.
    /// </summary>
    public static class TankShape
    {
        public static float HalfWide(TankKind k) => k == TankKind.Tank ? 3.0f : 3.4f;
        public static float HalfLong(TankKind k) => k == TankKind.Tank ? 3.75f : 5.6f;
        public static float Top(TankKind k) => k == TankKind.Tank ? 2.5f : 4.1f;
        /// <summary>Where the track centres are, either side.</summary>
        public static float Gauge(TankKind k) => k == TankKind.Tank ? 2.4f : 2.9f;
        /// <summary>What the turret turns about (the destroyer's gun turns only a little, in its own mount).</summary>
        public static readonly Vector3 TurretPivot = new(0f, 2.45f, -0.1f);
        /// <summary>Where the gun's trunnion is, the turret straight ahead.</summary>
        public static Vector3 Cannon(TankKind k) => k == TankKind.Tank ? new Vector3(0f, 3.3f, 1.35f) : new Vector3(0f, 2.65f, 4.75f);
        public static float CannonLength(TankKind k) => k == TankKind.Tank ? 3.9f : 3.6f;
        /// <summary>How far round the gun goes either way.</summary>
        public static float Arc(TankKind k) => k == TankKind.Tank ? math.PI : 0.26f;
        public static int Guns(TankKind k) => k == TankKind.Tank ? 1 : 0;
        /// <summary>The machine gun in the tank's nose: where, and facing straight ahead.</summary>
        public static readonly Vector3 GunPort = new(0.6f, 1.85f, 2.85f);
        public static readonly Vector3[] Exhausts = { new(-1.6f, 2.3f, -3.6f), new(1.6f, 2.3f, -3.6f) };
    }

    public class TankGun
    {
        public float Yaw, Pitch, WantYaw, WantPitch; // from straight out
        public int Ammo = Maxim.Clip, Target = -1, TargetGen;
        public float Reloading, Timer, Think;
    }

    public class Tank
    {
        /// <summary>Hit points: a tank's shell takes a quarter, a destroyer's half; a grenade an eighth.</summary>
        public const float Health0 = 1000f, ShellHurt = 250f, GrenadeHurt = 125f;
        public const float TopSpeed = 5.2f, Reverse = 2.6f, TurnRate = 0.6f, ReloadTime = 5f, ShellSpeed = 90f;
        public const float SideArc = 1.0f;
        public const float WreckStay = 45f, SinkTime = 14f;

        public TankKind Kind;
        public byte Team;
        public float3 Pos;
        public float Yaw, Pitch, Roll;
        public float Fall, PitchV, RollV;
        public float Speed, Turning, Throttle, Steer;
        public float CannonYaw, CannonPitch, WantCannonYaw, WantCannonPitch, Reload, Recoil;
        public bool Trigger;
        public float Health = Health0;
        public float TrackL, TrackR;
        public readonly TankGun[] Guns = { new() };
        public readonly List<(int slot, int gen)> Crew = new();
        public int Objective = -1;
        public float Think, AimThink, Stuck, Backing, BackSteer;
        public Tank Enemy;
        public bool HasAim;
        public float3 Aim;
        public bool Wreck;
        public float WreckTime;
        /// <summary>Where its gun's laid, worked out once a frame for whoever's driving it.</summary>
        public float3 Laid;
        public int LaidFrame = -1;
        /// <summary>The person's slot and seat (0 driving and the main gun, 1 the machine gun), or -1.</summary>
        public int Player = -1, Seat;
        public float Rumble;
        /// <summary>Sent somewhere by its commander.</summary>
        public bool Ordered;
        public float2 OrderAt;
        public int Field = -1;
        /// <summary>Slung under a transport, or null.</summary>
        public Aircraft Carrier;

        public float HalfWide => TankShape.HalfWide(Kind);
        public float HalfLong => TankShape.HalfLong(Kind);
        public float Arc => TankShape.Arc(Kind);
        public int GunCount => TankShape.Guns(Kind);
        /// <summary>A destroyer's shell does as much as two of a tank's.</summary>
        public float Hurt => Kind == TankKind.Destroyer ? ShellHurt * 2f : ShellHurt;
        public bool Carried => Carrier != null;

        public Quaternion Rotation => Quaternion.Euler(Pitch * Mathf.Rad2Deg, Yaw * Mathf.Rad2Deg, Roll * Mathf.Rad2Deg);
        public Vector3 At(Vector3 local) => (Vector3)Pos + Rotation * local;
        public float3 Forward => new(math.sin(Yaw), 0, math.cos(Yaw));
        /// <summary>The turret, turned with the gun (the destroyer has none: its gun turns in its mount).</summary>
        public Quaternion TurretRotation => Kind == TankKind.Tank ? Rotation * Quaternion.Euler(0, CannonYaw * Mathf.Rad2Deg, 0) : Rotation;
        public Vector3 CannonBase => Kind == TankKind.Tank
            ? At(TankShape.TurretPivot) + TurretRotation * (TankShape.Cannon(Kind) - TankShape.TurretPivot)
            : At(TankShape.Cannon(Kind));
        public Quaternion CannonRotation => Rotation * Quaternion.Euler(CannonPitch * Mathf.Rad2Deg, CannonYaw * Mathf.Rad2Deg, 0);
        public Vector3 Muzzle => CannonBase + CannonRotation * Vector3.forward * (TankShape.CannonLength(Kind) + 0.1f);
        public Quaternion GunRotation(int g) => Rotation * Quaternion.Euler(Guns[g].Pitch * Mathf.Rad2Deg, Guns[g].Yaw * Mathf.Rad2Deg, 0);
        public Vector3 GunAt(int g) => At(TankShape.GunPort);
        public TankState State => new() { Pos = Pos, Yaw = Yaw, Speed = Speed, Team = Team, Wreck = Wreck || Carried, HalfWide = HalfWide, HalfLong = HalfLong };

        /// <summary>How far a place is from its sides (0 inside).</summary>
        public float Distance(float3 p)
        {
            var l = Quaternion.Inverse(Rotation) * (Vector3)(p - Pos);
            var d = new Vector3(Mathf.Max(0f, Mathf.Abs(l.x) - HalfWide + 0.2f), Mathf.Max(0f, Mathf.Max(-l.y, l.y - TankShape.Top(Kind) - (Kind == TankKind.Tank ? 1.5f : 0f))), Mathf.Max(0f, Mathf.Abs(l.z) - HalfLong));
            return d.magnitude;
        }
    }

    public struct TankShell
    {
        public float3 Pos, Vel;
        public float Life, Hurt;
        public byte Team;
        public int Shooter, ShooterGen;
    }

    public unsafe partial class BattleSystem
    {
        public readonly List<Tank> Tanks = new();
        public readonly List<TankShell> Shells = new();
        NativeList<TankState> _tankStates;
        /// <summary>Where a tank can't go, and the way for one to each objective.</summary>
        public NativeArray<byte> TankBlocked;
        public NativeArray<sbyte> TankFlow;
        public int TanksLost;

        NativeList<TankRest> _rest;
        NativeList<GunSearch> _search;

        void TankCreate()
        {
            _tankStates = new NativeList<TankState>(256, Allocator.Persistent);
            _rest = new NativeList<TankRest>(256, Allocator.Persistent);
            _search = new NativeList<GunSearch>(256, Allocator.Persistent);
        }

        void TankDestroy() { _tankStates.Dispose(); _rest.Dispose(); _search.Dispose(); }

        public int TanksOf(int team)
        {
            int n = 0;
            foreach (var t in Tanks) if (!t.Wreck && t.Team == team) n++;
            return n;
        }

        /// <summary>Looking about for all the machine guns whose turn it is, together.</summary>
        void SearchGuns(float dt)
        {
            _search.Clear();
            for (int i = 0; i < Tanks.Count; i++)
            {
                var t = Tanks[i];
                if (t.Wreck || t.Carried || t.Crew.Count == 0) continue;
                for (int g = 0; g < t.GunCount; g++)
                {
                    var gun = t.Guns[g];
                    gun.Think -= dt;
                    if (gun.Think > 0f || t.Player >= 0 && t.Seat == g + 1) continue;
                    gun.Think = _rng.NextFloat(0.35f, 0.6f);
                    _search.Add(new GunSearch { From = t.GunAt(g), Inverse = Quaternion.Inverse(t.Rotation), Team = t.Team, Tank = i, Side = g });
                }
            }
            if (_search.Length == 0) return;
            new GunSearchJob { Guns = _search.AsArray(), Slots = Slots, All = _all, Map = Map, Range = T.maxRange * T.tankGunRange, Arc = Tank.SideArc }.Schedule(_search.Length, 1).Complete();
            for (int i = 0; i < _search.Length; i++)
            {
                var s = _search[i];
                var g = Tanks[s.Tank].Guns[s.Side];
                g.Target = s.Found;
                g.TargetGen = s.Gen;
            }
        }

        /// <summary>Where the ground holds each of them.</summary>
        void SettleAll(float dt)
        {
            _rest.Clear();
            foreach (var t in Tanks) _rest.Add(new TankRest { Pos = t.Pos.xz, Yaw = t.Yaw, Gauge = TankShape.Gauge(t.Kind), Reach = t.HalfLong - 0.4f });
            if (_rest.Length == 0) return;
            new TankRestJob { Tanks = _rest.AsArray(), Map = Map }.Schedule(_rest.Length, 4).Complete();
            for (int i = 0; i < Tanks.Count; i++)
            {
                if (Tanks[i].Carried) continue;
                Settle(Tanks[i], _rest[i], dt);
                if (Tanks[i].Wreck && Tanks[i].WreckTime > Tank.WreckStay) Tanks[i].Fall = 0f;
            }
        }

        void TankClear()
        {
            Tanks.Clear();
            Shells.Clear();
            TanksLost = 0;
        }

        // ------------------------------------------------------------------ arriving

        bool TankCanStand(float2 p, float r = 9.5f)
        {
            if (math.abs(p.x) > MapView.HalfW - 8f || math.abs(p.y) > MapView.HalfH - 8f || TankBlocked[MapView.Index(p)] != 0) return false;
            foreach (var t in Tanks) if (!t.Carried && math.distancesq(t.Pos.xz, p) < r * r) return false;
            return true;
        }

        /// <summary>A tank and its crew of four, at a place. The person playing's own wait there for orders.</summary>
        public Tank SpawnTank(byte team, TankKind kind, float3 at, float yaw)
        {
            int squad = SpawnSquad(team, at.xz, 4, true);
            if (squad < 0) return null;
            var t = new Tank { Kind = kind, Team = team, Pos = at, Yaw = yaw };
            if (team == PlayerTeam) { t.Ordered = true; t.OrderAt = at.xz; t.Field = -1; }
            int index = Tanks.Count;
            Tanks.Add(t);
            var q = Squads[squad];
            for (int m = 0; m < Squad.Size; m++)
            {
                int slot = q.Members[m];
                if (slot < 0) continue;
                t.Crew.Add((slot, Slots[slot].Gen));
                Board(slot, index);
            }
            return t;
        }

        void Board(int slot, int tank)
        {
            var e = SlotEntities[slot];
            var a = EntityManager.GetComponentData<Ability>(e);
            a.Tank = tank; a.Want = -1; a.Act = Act.None;
            EntityManager.SetComponentData(e, a);
            var brain = EntityManager.GetComponentData<Brain>(e);
            brain.Target = -1; brain.OnPost = false; brain.Mounted = false;
            EntityManager.SetComponentData(e, brain);
            var st = Slots[slot];
            st.Inside = true;
            Slots[slot] = st;
        }

        public void PlayerBoards(int slot, int tank, int seat)
        {
            var t = Tanks[tank];
            if (t.Wreck || t.Carried) return;
            if (!t.Crew.Contains((slot, Slots[slot].Gen))) t.Crew.Add((slot, Slots[slot].Gen));
            Board(slot, tank);
            t.Player = slot;
            t.Seat = seat;
        }

        /// <summary>Out, and down beside it.</summary>
        public void Alight(int slot)
        {
            var e = SlotEntities[slot];
            var a = EntityManager.GetComponentData<Ability>(e);
            int tank = a.Tank;
            a.Tank = -1;
            EntityManager.SetComponentData(e, a);
            var st = Slots[slot];
            st.Inside = false;
            if (tank >= 0 && tank < Tanks.Count)
            {
                var t = Tanks[tank];
                if (t.Player == slot) t.Player = -1;
                t.Crew.Remove((slot, st.Gen));
                var ts = t.State;
                float2 p = ts.Pos.xz;
                foreach (var l in new[] { new float2(0, -t.HalfLong - 1.4f), new float2(-t.HalfWide - 1.2f, 0), new float2(t.HalfWide + 1.2f, 0), new float2(0, t.HalfLong + 1.4f) })
                {
                    p = MapView.Clamp(ts.World(l));
                    if (!Map.IsBlocked(p)) break;
                }
                var body = EntityManager.GetComponentData<Body>(e);
                body.Pos = new float3(p.x, Map.Height(p), p.y);
                body.Vel = 0;
                EntityManager.SetComponentData(e, body);
                st.Pos = body.Pos;
            }
            Slots[slot] = st;
        }

        /// <summary>The nearest of a side's tanks to somebody standing by it.</summary>
        public int TankBeside(float2 p, int team, float reach)
        {
            int best = -1;
            float d = reach;
            for (int i = 0; i < Tanks.Count; i++)
            {
                var t = Tanks[i];
                if (t.Wreck || t.Carried || t.Team != team) continue;
                float k = t.Distance(new float3(p.x, t.Pos.y + 1f, p.y));
                if (k < d) { d = k; best = i; }
            }
            return best;
        }

        // ------------------------------------------------------------------ each frame

        void TankTick(float dt)
        {
            ApplyTankOrders();
            SearchGuns(dt);
            for (int i = 0; i < Tanks.Count; i++)
            {
                var t = Tanks[i];
                if (t.Wreck) { Rot(t, dt); continue; }
                t.Crew.RemoveAll(c => !Is(c.slot, c.gen));
                if (t.Carried) { t.Speed = 0f; t.Throttle = t.Steer = 0f; t.Trigger = false; continue; }
                if (t.Player >= 0 && (!Is(t.Player, Slots[t.Player].Gen) || !Slots[t.Player].Player)) t.Player = -1;
                bool manned = t.Crew.Count > 0;
                bool driven = t.Player >= 0 && t.Seat == 0;
                if (!driven) { t.Trigger = false; if (manned) Drive(t, dt); else { t.Throttle = t.Steer = 0f; } }
                Move(t, dt);
                MainGun(t, dt);
                for (int g = 0; g < t.GunCount; g++) SideGun(t, g, dt, manned);
                Crush(t);
            }
            SettleAll(dt);
            // wrecks that have sunk right down
            for (int i = Tanks.Count - 1; i >= 0; i--)
            {
                var t = Tanks[i];
                if (t.Wreck && t.WreckTime > Tank.WreckStay + Tank.SinkTime) Remove(i);
            }
            FlyShells(dt);
            _tankStates.Clear();
            foreach (var t in Tanks) _tankStates.Add(t.State);
        }

        /// <summary>Taken out of the list: those in later ones are told their tank's new number.</summary>
        void Remove(int i)
        {
            Tanks.RemoveAt(i);
            for (int k = i; k < Tanks.Count; k++)
                foreach (var (slot, gen) in Tanks[k].Crew)
                {
                    if (!Is(slot, gen)) continue;
                    var e = SlotEntities[slot];
                    var a = EntityManager.GetComponentData<Ability>(e);
                    a.Tank = k;
                    EntityManager.SetComponentData(e, a);
                }
        }

        void Rot(Tank t, float dt)
        {
            t.WreckTime += dt;
            t.Speed = 0f;
            t.CannonPitch = math.lerp(t.CannonPitch, 0.22f, 1f - math.exp(-2f * dt));
        }

        /// <summary>How far down a wreck's gone.</summary>
        public static float Sunk(Tank t) => t.Wreck ? math.saturate((t.WreckTime - Tank.WreckStay) / Tank.SinkTime) * 3.6f : 0f;

        // ------------------------------------------------------------------ orders

        /// <summary>The commander's tanks: sent somewhere, they drive there by their own way.</summary>
        public readonly List<(Tank tank, float2 at)> TankCommands = new();

        public void OrderTank(Tank t, float2 at) => TankCommands.Add((t, at));

        void ApplyTankOrders()
        {
            foreach (var (t, at) in TankCommands)
            {
                if (t == null || t.Wreck || t.Carried) continue;
                t.Ordered = true;
                t.OrderAt = MapView.Clamp(at);
                t.Field = TankFieldTo(t.OrderAt);
                t.Backing = 0f;
            }
            TankCommands.Clear();
        }

        // ------------------------------------------------------------------ driving

        static float Wrap(float a) => a - math.PI * 2f * math.round(a / (math.PI * 2f));

        void Drive(Tank t, float dt)
        {
            t.Think -= dt;
            if (t.Think <= 0f || t.Objective < 0) { t.Think = 4f; t.Objective = Front.Centre; }
            t.AimThink -= dt;
            if (t.AimThink <= 0f) { t.AimThink = _rng.NextFloat(0.7f, 1.1f); PickTarget(t); }

            if (t.Backing > 0f)
            {
                t.Backing -= dt;
                t.Throttle = -1f;
                t.Steer = t.BackSteer;
                return;
            }

            // something to shoot: a turret just turns to it; a destroyer brings the whole of itself round
            if (t.HasAim)
            {
                float2 d = t.Aim.xz - t.Pos.xz;
                float off = Wrap(math.atan2(d.x, d.y) - t.Yaw);
                Elevate(t, t.Aim);
                bool ready = t.Reload < 1.2f;
                bool laid = math.abs(Wrap(t.WantCannonYaw - t.CannonYaw)) < 0.02f && math.abs(t.WantCannonPitch - t.CannonPitch) < 0.03f;
                if (t.Kind == TankKind.Destroyer && (t.Enemy != null || ready))
                {
                    t.Throttle = 0f;
                    t.Steer = math.abs(off) > t.Arc * 0.8f ? math.clamp(off * 2.2f, -1f, 1f) : 0f;
                    t.Trigger = math.abs(off) < t.Arc && laid;
                    return;
                }
                // a tank fires on the move, slowing for the shot
                t.Trigger = ready && laid;
            }
            else { t.WantCannonYaw = math.lerp(t.WantCannonYaw, 0f, 1f - math.exp(-dt)); t.WantCannonPitch = -0.03f; }

            float2 goal = t.Ordered ? t.OrderAt : Map.Objectives[t.Objective];
            float toGoal = math.distance(goal, t.Pos.xz);
            float2 dir = t.Ordered ? (t.Field >= 0 ? TankWay(TankOrderFlow, t.Field, t.Pos.xz, goal) : math.normalizesafe(goal - t.Pos.xz)) : TankWay(TankFlow, t.Objective, t.Pos.xz, goal);
            float stop = t.Ordered ? 5f : 24f;
            if (toGoal < stop || !math.any(dir)) { t.Throttle = 0f; t.Steer = 0f; return; }
            float turn = Wrap(math.atan2(dir.x, dir.y) - t.Yaw);
            t.Steer = math.clamp(turn * 1.8f, -1f, 1f);
            t.Throttle = math.abs(turn) > 0.9f ? 0f : math.saturate(1.15f - math.abs(turn) * 1.1f) * math.saturate(toGoal / 12f + 0.3f);
            if (t.HasAim && t.Kind == TankKind.Tank) t.Throttle *= 0.6f;
            if (t.Throttle > 0.2f && math.abs(t.Speed) < 0.25f) t.Stuck += dt; else t.Stuck = math.max(0f, t.Stuck - dt * 2f);
            if (t.Stuck > 1.6f) { t.Stuck = 0f; t.Backing = _rng.NextFloat(1.6f, 2.8f); t.BackSteer = _rng.NextBool() ? 0.8f : -0.8f; }
        }

        float2 TankWay(NativeArray<sbyte> flow, int field, float2 p, float2 goal)
        {
            int2 c = MapView.Cell(p);
            float2 sum = 0;
            for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++)
            {
                int2 q = math.clamp(c + new int2(x, y), 0, new int2(MapView.W - 1, MapView.H - 1));
                int f = flow[field * MapView.W * MapView.H + MapView.Index(q)];
                if (f >= 0) sum += math.normalize((float2)MapView.Dir(f));
            }
            if (math.any(sum)) return math.normalizesafe(sum);
            // inside something's margin: straight for it
            return math.normalizesafe(goal - p);
        }

        bool TankHits(Tank t, float2 p, float yaw)
        {
            var s = new TankState { Pos = new float3(p.x, 0, p.y), Yaw = yaw };
            for (int z = -2; z <= 2; z++)
            for (int x = -1; x <= 1; x++)
            {
                if (x == 0 && math.abs(z) < 2) continue;
                float2 q = s.World(new float2(x * (t.HalfWide - 0.4f), z * t.HalfLong * 0.5f));
                if (math.abs(q.x) > MapView.HalfW - 2f || math.abs(q.y) > MapView.HalfH - 2f) return true;
                int c = MapView.Index(q);
                if (Map.Blocked[c] != 0 && Map.PropTop[c] - Map.Height(q) > 0.55f) return true;
            }
            foreach (var o in Tanks)
            {
                if (o == t || o.Carried) continue;
                float now = math.distancesq(o.Pos.xz, t.Pos.xz), then = math.distancesq(o.Pos.xz, p);
                float r = (o.HalfLong + t.HalfLong) * 0.85f;
                if (then < r * r && then < now) return true;
            }
            return false;
        }

        void Move(Tank t, float dt)
        {
            // uphill it labours, downhill it runs on a little
            if (t.Throttle == 0f && t.Steer == 0f && math.abs(t.Speed) < 0.01f && math.abs(t.Turning) < 0.002f)
            {
                t.Speed = 0f; t.Turning = 0f;
                t.Rumble = math.lerp(t.Rumble, 0f, 1f - math.exp(-3f * dt));
                return;
            }
            float grade = math.sin(-t.Pitch); // nose up positive
            float most = t.Throttle >= 0f ? Tank.TopSpeed : Tank.Reverse;
            float want = t.Throttle * most * math.clamp(1f - grade * math.sign(t.Throttle) * 1.5f, 0.15f, 1.2f);
            float pace = math.abs(want) > math.abs(t.Speed) && want * t.Speed >= 0f ? 1.7f : 3.4f;
            t.Speed += math.clamp(want - t.Speed, -pace * dt, pace * dt);
            float steer = t.Steer * Tank.TurnRate * (1f - 0.35f * math.abs(t.Speed) / Tank.TopSpeed);
            t.Turning += math.clamp(steer - t.Turning, -1.6f * dt, 1.6f * dt);

            float yaw = t.Yaw + t.Turning * dt;
            float2 f = new float2(math.sin(yaw), math.cos(yaw));
            float2 p = t.Pos.xz + f * t.Speed * dt * math.cos(t.Pitch);
            bool inIt = TankHits(t, t.Pos.xz, t.Yaw);
            if (inIt || !TankHits(t, p, yaw)) { t.Pos.x = p.x; t.Pos.z = p.y; t.Yaw = yaw; }
            else if (!TankHits(t, t.Pos.xz, yaw)) { t.Yaw = yaw; t.Speed *= math.exp(-8f * dt); }
            else { t.Speed *= math.exp(-8f * dt); t.Turning = 0f; }
            t.TrackL += (t.Speed + t.Turning * 2f) * dt;
            t.TrackR += (t.Speed - t.Turning * 2f) * dt;
            t.Rumble = math.lerp(t.Rumble, math.saturate(math.abs(t.Speed) / 3f + math.abs(t.Turning)), 1f - math.exp(-3f * dt));
        }

        /// <summary>How it sits: each track rests across the high points under it.</summary>
        void Settle(Tank t, in TankRest rest, float dt)
        {
            float y = rest.Y, pitch = rest.Pitch, roll = rest.Roll;
            float gap = y - t.Pos.y;
            if (gap > 0f) { t.Pos.y += gap * (1f - math.exp(-14f * dt)); t.Fall = 0f; }
            else
            {
                t.Fall = math.min(t.Fall + 9.8f * dt, 8f);
                float drop = math.min(t.Fall * dt, -gap);
                t.Pos.y -= drop;
                if (drop >= -gap) t.Fall = 0f;
            }
            Spring(ref t.Pitch, ref t.PitchV, pitch, dt);
            Spring(ref t.Roll, ref t.RollV, roll, dt);
        }

        static void Spring(ref float x, ref float v, float to, float dt)
        {
            const float stiff = 42f, damp = 9.5f;
            v += ((to - x) * stiff - v * damp) * dt;
            x += v * dt;
        }

        // ------------------------------------------------------------------ the main gun

        /// <summary>Another tank before anything else; failing that, the thickest of the enemy's infantry in sight.</summary>
        void PickTarget(Tank t)
        {
            t.HasAim = false;
            t.Enemy = null;
            float3 eye = (float3)t.CannonBase + new float3(0, 0.3f, 0);
            float best = T.tankRange;
            foreach (var o in Tanks)
            {
                if (o.Wreck || o.Carried || o.Team == t.Team) continue;
                float d = math.distance(o.Pos, t.Pos);
                if (d >= best || !Map.Sees(eye, o.Pos + new float3(0, 2f, 0))) continue;
                best = d;
                t.Enemy = o;
            }
            if (t.Enemy != null) { t.HasAim = true; t.Aim = t.Enemy.Pos + new float3(0, 1.4f, 0); return; }
            // a destroyer's for tanks: it won't waste itself on men
            if (t.Kind == TankKind.Destroyer) return;

            var all = _all;
            int2 lo = all.At(t.Pos.xz - T.tankRange), hi = all.At(t.Pos.xz + T.tankRange);
            float worthBest = 3f;
            for (int y = lo.y; y <= hi.y; y++)
            for (int x = lo.x; x <= hi.x; x++)
            {
                int k = y * all.W + x;
                int count = 0, first = -1;
                for (int n = all.Start[k]; n < all.Start[k + 1]; n++)
                {
                    int slot = all.Items[n];
                    if (Slots[slot].Team == t.Team) continue;
                    count++;
                    if (first < 0) first = slot;
                }
                if (count < 2) continue;
                var s = Slots[first];
                float d = math.distance(s.Pos, t.Pos);
                if (d < 16f || d > T.tankRange) continue;
                float2 to = s.Pos.xz - t.Pos.xz;
                float off = math.abs(Wrap(math.atan2(to.x, to.y) - t.Yaw));
                float worth = count * (1.4f - d / T.tankRange) - (t.Kind == TankKind.Tank ? 0f : off * 1.5f);
                if (worth <= worthBest || all.CountNear(Slots, s.Pos.xz, T.shellEdgeRadius * 0.8f, 1, t.Team, true) > 0) continue;
                if (!Map.Sees(eye, s.Pos + new float3(0, 1.4f, 0))) continue;
                worthBest = worth;
                t.HasAim = true;
                t.Aim = s.Pos + new float3(0, 0.4f, 0);
            }
        }

        /// <summary>Lays the gun on a place, as far as it'll go.</summary>
        public void Elevate(Tank t, float3 at)
        {
            float3 from = t.CannonBase;
            Lay(t, Arc(at - from));
        }

        public void Lay(Tank t, float3 dir)
        {
            float3 l = Quaternion.Inverse(t.Rotation) * (Vector3)dir;
            t.WantCannonYaw = math.clamp(math.atan2(l.x, l.z), -t.Arc, t.Arc);
            t.WantCannonPitch = math.clamp(-math.atan2(l.y, math.length(l.xz)), -0.5f, 0.2f);
        }

        /// <summary>How far from its mark a shell may fall: so many degrees off, seen from the tank, so nearer marks get closer shots.</summary>
        public float Scatter(Tank t, float3 mark) => math.distance(mark.xz, t.Pos.xz) * math.tan(math.radians(math.clamp(T.tankScatter, 0f, 45f)));

        /// <summary>The way a shell goes to come down somewhere about where the gun's laid.</summary>
        float3 Scattered(Tank t, float3 dir)
        {
            float3 mark = CannonPointNow(t);
            float r = Scatter(t, mark);
            if (r < 0.01f) return dir;
            float a = _rng.NextFloat(0f, math.PI * 2f);
            float2 at = mark.xz + new float2(math.sin(a), math.cos(a)) * (math.sqrt(_rng.NextFloat()) * r);
            return Arc(new float3(at.x, Map.Height(at), at.y) - (float3)t.Muzzle);
        }

        /// <summary>The low way of lobbing a shell that far.</summary>
        static float3 Arc(float3 d)
        {
            float flat = math.max(1f, math.length(d.xz));
            const float v = Tank.ShellSpeed, g = 9.81f;
            float root = v * v * v * v - g * (g * flat * flat + 2f * d.y * v * v);
            float up = root > 0f ? math.atan((v * v - math.sqrt(root)) / (g * flat)) : 0.5f;
            return new float3(d.x / flat * math.cos(up), math.sin(up), d.z / flat * math.cos(up));
        }

        void MainGun(Tank t, float dt)
        {
            t.Reload = math.max(0f, t.Reload - dt);
            t.Recoil = math.max(0f, t.Recoil - dt * 1.6f);
            // a turret turns faster than a destroyer's mount, which hardly needs to
            float swing = t.Kind == TankKind.Tank ? 0.9f : 0.5f;
            float dy = t.Kind == TankKind.Tank ? Wrap(t.WantCannonYaw - t.CannonYaw) : t.WantCannonYaw - t.CannonYaw;
            t.CannonYaw = Wrap(t.CannonYaw + math.clamp(dy, -swing * dt, swing * dt));
            t.CannonPitch += math.clamp(t.WantCannonPitch - t.CannonPitch, -0.5f * dt, 0.5f * dt);
            if (!t.Trigger || t.Reload > 0f || t.Crew.Count == 0) return;
            t.Trigger = false;
            t.Reload = Tank.ReloadTime;
            t.Recoil = 1f;
            t.PitchV -= 0.5f;
            t.Speed -= 0.4f * math.cos(t.CannonYaw);
            float3 dir = t.CannonRotation * Vector3.forward;
            dir = Scattered(t, dir);
            var gunner = t.Player >= 0 && t.Seat == 0 ? (t.Player, Slots[t.Player].Gen) : t.Crew[0];
            Shells.Add(new TankShell { Pos = t.Muzzle, Vel = dir * Tank.ShellSpeed, Life = 8f, Hurt = t.Hurt, Team = t.Team, Shooter = gunner.Item1, ShooterGen = gunner.Item2 });
            Blasts.Add(new BlastEvent { At = t.Muzzle, Dir = dir, Cannon = true, Team = t.Team });
        }

        void FlyShells(float dt)
        {
            for (int i = Shells.Count - 1; i >= 0; i--)
            {
                var s = Shells[i];
                bool hit = false;
                Tank struck = null;
                for (int k = 0; k < 4 && !hit; k++)
                {
                    float h = dt / 4f;
                    s.Vel.y -= 9.81f * h;
                    s.Pos += s.Vel * h;
                    hit = s.Pos.y < Map.Height(s.Pos.xz) || s.Pos.y < Map.PropTop[MapView.Index(s.Pos.xz)];
                    foreach (var t in Tanks)
                        if (!t.Wreck && !t.Carried && t.Team != s.Team && math.distancesq(t.Pos, s.Pos) < 64f && t.Distance(s.Pos) < 0.25f) { hit = true; struck = t; }
                    // and transports, low over the field
                    if (!hit && HitAircraft(s.Pos, s.Team, s.Hurt)) hit = true;
                }
                s.Life -= dt;
                if (hit)
                {
                    Shells.RemoveAt(i);
                    if (struck != null) HurtTank(struck, s.Hurt);
                    Explode(s.Pos, T.shellFullRadius, T.shellFullDamage, T.shellEdgeRadius, T.shellEdgeDamage, s.Team, true, false, s.Shooter, s.ShooterGen, struck == null);
                }
                else if (s.Life <= 0f || math.abs(s.Pos.x) > MapView.HalfW + 20f || math.abs(s.Pos.z) > MapView.HalfH + 20f) Shells.RemoveAt(i);
                else Shells[i] = s;
            }
        }

        /// <summary>Where the gun's laid, for whoever's looking along it.</summary>
        public float3 CannonPoint(Tank t)
        {
            if (t.LaidFrame == UnityEngine.Time.frameCount) return t.Laid;
            t.LaidFrame = UnityEngine.Time.frameCount;
            return t.Laid = CannonPointNow(t);
        }

        float3 CannonPointNow(Tank t)
        {
            float3 p = t.Muzzle, v = (float3)(Vector3)(t.CannonRotation * Vector3.forward) * Tank.ShellSpeed;
            for (int i = 0; i < 180; i++)
            {
                v.y -= 9.81f * 0.03f;
                p += v * 0.03f;
                if (math.abs(p.x) > MapView.HalfW || math.abs(p.z) > MapView.HalfH) break;
                if (p.y < Map.Height(p.xz) || p.y < Map.PropTop[MapView.Index(p.xz)]) break;
                foreach (var o in Tanks) if (o != t && !o.Wreck && !o.Carried && math.distancesq(o.Pos, p) < 64f && o.Distance(p) < 0.2f) return p;
            }
            return p;
        }

        // ------------------------------------------------------------------ the machine gun

        void SideGun(Tank t, int side, float dt, bool manned)
        {
            var g = t.Guns[side];
            g.Timer -= dt;
            bool mine = t.Player >= 0 && t.Seat == side + 1;
            float swing = mine ? 2.6f : 1.5f;
            g.Yaw += math.clamp(math.clamp(g.WantYaw, -Tank.SideArc, Tank.SideArc) - g.Yaw, -swing * dt, swing * dt);
            g.Pitch += math.clamp(math.clamp(g.WantPitch, -0.35f, 0.3f) - g.Pitch, -swing * dt, swing * dt);
            if (g.Reloading > 0f)
            {
                g.Reloading -= dt;
                if (g.Reloading <= 0f) { g.Reloading = 0f; g.Ammo = Maxim.Clip; }
                return;
            }
            if (g.Ammo <= 0) { g.Reloading = Maxim.Reload; return; }
            if (mine || !manned) return;

            float3 from = t.GunAt(side);
            if (!Is(g.Target, g.TargetGen)) { g.Target = -1; g.WantPitch = 0f; return; }
            var foe = Slots[g.Target];
            float3 at = foe.Pos + new float3(0, foe.Kneel ? 0.9f : 1.25f, 0);
            if (!Bears(t, side, at, out float yaw, out float pitch)) { g.Target = -1; return; }
            g.WantYaw = yaw;
            g.WantPitch = pitch;
            if (math.distance(foe.Pos, from) > T.maxRange * T.tankGunRange) { g.Target = -1; return; }
            if (math.abs(g.Yaw - yaw) > 0.07f) { g.Timer = math.max(g.Timer, 0.1f); return; }
            while (g.Timer <= 0f && g.Ammo > 0)
            {
                g.Timer += 60f / Maxim.Rpm;
                g.Ammo--;
                float dist = math.distance(foe.Pos, from);
                float near = T.closeRange * T.tankGunRange, far = T.maxRange * T.tankGunRange;
                if (dist > far) break;
                float chance = math.lerp(T.closeHit, T.maxRangeHit, math.saturate((dist - near) / math.max(0.01f, far - near))) * T.maximHit;
                if (T.battlefieldModifiers)
                {
                    if (foe.Kneel) chance *= T.kneelHit;
                    if (math.lengthsq(foe.Vel) > 1f) chance *= T.movingHit;
                    if (math.abs(t.Speed) > 0.5f) chance *= 0.7f;
                }
                float roll = _rng.NextFloat();
                byte part = (byte)(roll < T.headChance ? 1 : roll < T.headChance + T.limbChance ? 2 : 0);
                float3 spot = foe.Pos + new float3(0, (part == 1 ? (foe.Kneel ? 1.45f : 1.87f) : part == 2 ? 0.45f : foe.Kneel ? 0.92f : 1.3f) * foe.Height, 0);
                bool hit = _rng.NextFloat() < chance && Map.Sees(from, spot);
                float3 muzzle = from + (float3)(Vector3)(t.GunRotation(side) * Vector3.forward) * 0.6f;
                if (!hit) spot = Map.Trace(muzzle, spot + _rng.NextFloat3(-0.9f, 0.9f) + math.normalizesafe(spot - from) * 12f);
                var who = t.Crew[math.min(t.Crew.Count - 1, 1 + side)];
                Shots.Enqueue(new ShotEvent
                {
                    Shooter = who.slot, ShooterGen = who.gen, Target = g.Target, TargetGen = g.TargetGen, From = muzzle, To = spot, Team = t.Team, Kind = ShotKind.Maxim, Hit = hit, Region = part,
                    Pellets = (byte)(hit ? 1 : 0), Damage = hit ? T.damage * (part == 1 ? T.headMultiplier : part == 2 ? T.limbMultiplier : 1f) : 0f,
                });
            }
        }

        /// <summary>Whether the machine gun can be brought to bear on a place.</summary>
        static bool Bears(Tank t, int side, float3 at, out float yaw, out float pitch)
        {
            Vector3 l = Quaternion.Inverse(t.Rotation) * ((Vector3)at - t.GunAt(side));
            yaw = Mathf.Atan2(l.x, l.z);
            pitch = -Mathf.Atan2(l.y, new Vector2(l.x, l.z).magnitude);
            return Mathf.Abs(yaw) < Tank.SideArc && Mathf.Abs(pitch) < 0.4f;
        }

        // ------------------------------------------------------------------ hurting, and being hurt

        /// <summary>Anybody of anyone else's it runs onto.</summary>
        void Crush(Tank t)
        {
            if (math.abs(t.Speed) < 1.1f) return;
            var all = _all;
            var s = t.State;
            float r = t.HalfLong + 1f;
            int2 lo = all.At(t.Pos.xz - r), hi = all.At(t.Pos.xz + r);
            for (int y = lo.y; y <= hi.y; y++)
            for (int x = lo.x; x <= hi.x; x++)
            {
                int k = y * all.W + x;
                for (int n = all.Start[k]; n < all.Start[k + 1]; n++)
                {
                    int slot = all.Items[n];
                    var st = Slots[slot];
                    if (!st.Alive || st.Team == t.Team) continue;
                    float2 l = s.Local(st.Pos.xz);
                    if (math.abs(l.x) > t.HalfWide - 0.1f || math.abs(l.y) > t.HalfLong - 0.2f) continue;
                    var who = t.Crew.Count > 0 ? t.Crew[0] : (slot: -1, gen: 0);
                    Shots.Enqueue(new ShotEvent { Shooter = who.slot, ShooterGen = who.gen, Target = slot, TargetGen = st.Gen, From = t.Pos, To = st.Pos + new float3(0, 0.6f, 0), Damage = 400f, Hit = true, Team = t.Team, Kind = ShotKind.Tank });
                }
            }
        }

        /// <summary>A blast close by: a shell's near miss does half what a hit would, a grenade a little.</summary>
        void HurtTanks(float3 at, bool big)
        {
            for (int i = 0; i < Tanks.Count; i++)
            {
                var t = Tanks[i];
                if (t.Wreck || t.Carried || t.Distance(at) > (big ? 4f : 3.2f)) continue;
                HurtTank(t, big ? Tank.ShellHurt * 0.5f : Tank.GrenadeHurt);
            }
        }

        public void HurtTank(Tank t, float hurt)
        {
            if (t.Wreck) return;
            t.Health -= hurt;
            t.PitchV += _rng.NextFloat(-0.5f, 0.5f);
            t.RollV += _rng.NextFloat(-0.5f, 0.5f);
            if (t.Health <= 0.01f) Destroy(t);
        }

        void Destroy(Tank t)
        {
            t.Wreck = true;
            t.WreckTime = 0f;
            TanksLost++;
            t.PitchV += 1.2f;
            Blasts.Add(new BlastEvent { At = t.Pos + new float3(0, 1.4f, 0), Radius = 12f, Big = true, Team = t.Team });
            foreach (var (slot, gen) in t.Crew)
            {
                if (!Is(slot, gen)) continue;
                var e = SlotEntities[slot];
                var a = EntityManager.GetComponentData<Ability>(e);
                a.Tank = -1;
                EntityManager.SetComponentData(e, a);
                var st = Slots[slot];
                st.Inside = false;
                st.Pos = t.Pos + new float3(_rng.NextFloat(-1.5f, 1.5f), 1.6f, _rng.NextFloat(-1.5f, 1.5f));
                Slots[slot] = st;
                Shots.Enqueue(new ShotEvent { Shooter = -1, Target = slot, TargetGen = gen, From = t.Pos, To = st.Pos, Damage = 9999f, Power = 0.75f, Hit = true, Team = Team.None, Kind = ShotKind.Blast, Big = true });
            }
            t.Crew.Clear();
            t.Player = -1;
        }
    }
}

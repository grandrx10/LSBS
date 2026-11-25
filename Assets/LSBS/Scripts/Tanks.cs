using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace LSBS
{
    /// <summary>What soldiers need to know of a tank.</summary>
    public struct TankState
    {
        public float3 Pos;
        public float Yaw, Speed;
        public byte Team;
        public bool Wreck;

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

    public class TankGun
    {
        public float Yaw, Pitch, WantYaw, WantPitch; // from straight out to its side
        public int Ammo = Maxim.Clip, Target = -1, TargetGen;
        public float Reloading, Timer, Think;
    }

    public class Tank
    {
        public const float Health0 = 24f, ShellHurt = 8f, GrenadeHurt = 3f; // three shells, or eight grenades
        public const float TopSpeed = 4.4f, Reverse = 2.2f, TurnRate = 0.55f, CannonArc = 0.262f, ReloadTime = 5f, ShellSpeed = 78f;
        public const float SideArc = 1.35f;
        public const float WreckStay = 45f, SinkTime = 14f;
        public const float HalfWide = 2.5f, HalfLong = 3.95f;

        public byte Team;
        public float3 Pos;
        public float Yaw, Pitch, Roll;
        public float Fall, PitchV, RollV;
        public float Speed, Turning, Throttle, Steer;
        public float CannonYaw, CannonPitch, WantCannonYaw, WantCannonPitch, Reload, Recoil;
        public bool Trigger;
        public float Health = Health0;
        public float TrackL, TrackR;
        public readonly TankGun[] Guns = { new(), new() };
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
        /// <summary>The person's slot and seat (0 driving and the main gun, 1 the left gun, 2 the right), or -1.</summary>
        public int Player = -1, Seat;
        public float Rumble;

        public Quaternion Rotation => Quaternion.Euler(Pitch * Mathf.Rad2Deg, Yaw * Mathf.Rad2Deg, Roll * Mathf.Rad2Deg);
        public Vector3 At(Vector3 local) => (Vector3)Pos + Rotation * local;
        public float3 Forward => new(math.sin(Yaw), 0, math.cos(Yaw));
        public Quaternion CannonRotation => Rotation * Quaternion.Euler(CannonPitch * Mathf.Rad2Deg, CannonYaw * Mathf.Rad2Deg, 0);
        public Vector3 Muzzle => At(TankModel.Cannon) + CannonRotation * Vector3.forward * (TankModel.CannonLength + 0.1f);
        public Quaternion GunRotation(int side) => Rotation * Quaternion.Euler(0, side == 0 ? -90f : 90f, 0) * Quaternion.Euler(Guns[side].Pitch * Mathf.Rad2Deg, Guns[side].Yaw * Mathf.Rad2Deg, 0);
        public Vector3 GunAt(int side) => At(TankModel.SideGun(side));
        public TankState State => new() { Pos = Pos, Yaw = Yaw, Speed = Speed, Team = Team, Wreck = Wreck };

        /// <summary>How far a place is from its sides (0 inside).</summary>
        public float Distance(float3 p)
        {
            var l = Quaternion.Inverse(Rotation) * (Vector3)(p - Pos);
            var d = new Vector3(Mathf.Max(0f, Mathf.Abs(l.x) - TankModel.SponsonOut + 0.4f), Mathf.Max(0f, Mathf.Max(-l.y, l.y - TankModel.HullY1)), Mathf.Max(0f, Mathf.Abs(l.z) - HalfLong));
            return d.magnitude;
        }
    }

    public struct TankShell
    {
        public float3 Pos, Vel;
        public float Life;
        public byte Team;
        public int Shooter, ShooterGen;
    }

    public partial class BattleSystem
    {
        public readonly List<Tank> Tanks = new();
        public readonly List<TankShell> Shells = new();
        NativeList<TankState> _tankStates;
        /// <summary>Where a tank can't go, and the way for one to each objective.</summary>
        public NativeArray<byte> TankBlocked;
        public NativeArray<sbyte> TankFlow;
        public int TanksLost;
        readonly float[] _tankDue = new float[2];

        NativeList<TankRest> _rest;
        NativeList<GunSearch> _search;

        void TankCreate()
        {
            _tankStates = new NativeList<TankState>(256, Allocator.Persistent);
            _rest = new NativeList<TankRest>(256, Allocator.Persistent);
            _search = new NativeList<GunSearch>(256, Allocator.Persistent);
        }

        void TankDestroy() { _tankStates.Dispose(); _rest.Dispose(); _search.Dispose(); }

        /// <summary>Looking about for all the side guns whose turn it is, together.</summary>
        void SearchGuns(float dt)
        {
            _search.Clear();
            for (int i = 0; i < Tanks.Count; i++)
            {
                var t = Tanks[i];
                if (t.Wreck || t.Crew.Count == 0) continue;
                for (int side = 0; side < 2; side++)
                {
                    var g = t.Guns[side];
                    g.Think -= dt;
                    if (g.Think > 0f || t.Player >= 0 && t.Seat == side + 1) continue;
                    g.Think = _rng.NextFloat(0.35f, 0.6f);
                    _search.Add(new GunSearch { From = t.GunAt(side), Inverse = Quaternion.Inverse(t.Rotation * Quaternion.Euler(0, side == 0 ? -90f : 90f, 0)), Team = t.Team, Tank = i, Side = side });
                }
            }
            if (_search.Length == 0) return;
            new GunSearchJob { Guns = _search.AsArray(), Slots = Slots, Green = _green, Grey = _grey, Map = Map, Range = T.maxRange * T.tankGunRange, Arc = Tank.SideArc }.Schedule(_search.Length, 1).Complete();
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
            foreach (var t in Tanks) _rest.Add(new TankRest { Pos = t.Pos.xz, Yaw = t.Yaw });
            if (_rest.Length == 0) return;
            new TankRestJob { Tanks = _rest.AsArray(), Map = Map }.Schedule(_rest.Length, 4).Complete();
            for (int i = 0; i < Tanks.Count; i++)
            {
                Settle(Tanks[i], _rest[i], dt);
                if (Tanks[i].Wreck && Tanks[i].WreckTime > Tank.WreckStay) Tanks[i].Fall = 0f;
            }
        }

        void TankClear()
        {
            Tanks.Clear();
            Shells.Clear();
            _tankDue[0] = _tankDue[1] = 0f;
            TanksLost = 0;
        }

        // ------------------------------------------------------------------ arriving

        /// <summary>One for every so many soldiers who've come up.</summary>
        void TanksFor(byte team, int soldiers)
        {
            if (T.soldiersPerTank <= 0 || soldiers <= 0) return;
            _tankDue[team] += soldiers;
            int alive = 0;
            foreach (var t in Tanks) if (!t.Wreck && t.Team == team) alive++;
            while (_tankDue[team] >= T.soldiersPerTank)
            {
                _tankDue[team] -= T.soldiersPerTank;
                if (alive++ < T.maxTanks) SpawnTank(team);
            }
        }

        bool TankCanStand(float2 p)
        {
            if (math.abs(p.x) > MapView.HalfW - 8f || math.abs(p.y) > MapView.HalfH - 8f || TankBlocked[MapView.Index(p)] != 0) return false;
            foreach (var t in Tanks) if (math.distancesq(t.Pos.xz, p) < 9.5f * 9.5f) return false;
            return true;
        }

        void SpawnTank(byte team)
        {
            float side = team == Team.Green ? -1f : 1f;
            float2 home = Map.Objectives[Front.Home(team)];
            for (int tries = 0; tries < 60; tries++)
            {
                float2 p = home + new float2(side * _rng.NextFloat(-24f, 2f), _rng.NextFloat(-95f, 95f));
                if (!TankCanStand(p)) continue;
                int from = HighSlot;
                int squad = SpawnCrew(team, p);
                if (squad < 0) return;
                var t = new Tank { Team = team, Pos = new float3(p.x, Map.Height(p), p.y), Yaw = -side * math.PI * 0.5f };
                int index = Tanks.Count;
                Tanks.Add(t);
                unsafe
                {
                    var q = Squads[squad];
                    for (int m = 0; m < Squad.Size; m++)
                    {
                        int slot = q.Members[m];
                        if (slot < 0) continue;
                        t.Crew.Add((slot, Slots[slot].Gen));
                        Board(slot, index);
                    }
                }
                return;
            }
        }

        /// <summary>Four soldiers, a squad to themselves.</summary>
        int SpawnCrew(byte team, float2 at)
        {
            _crewAt = at;
            _crewSquad = -1;
            _spawningCrew = true;
            Spawn(team, 4);
            _spawningCrew = false;
            return _crewSquad;
        }

        bool _spawningCrew;
        float2 _crewAt;
        int _crewSquad;

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
            if (t.Wreck) return;
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
                foreach (var l in new[] { new float2(0, -5.2f), new float2(-4.4f, 0), new float2(4.4f, 0), new float2(0, 5.2f) })
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
                if (t.Wreck || t.Team != team) continue;
                float k = t.Distance(new float3(p.x, t.Pos.y + 1f, p.y));
                if (k < d) { d = k; best = i; }
            }
            return best;
        }

        // ------------------------------------------------------------------ each frame

        void TankTick(float dt)
        {
            SearchGuns(dt);
            for (int i = 0; i < Tanks.Count; i++)
            {
                var t = Tanks[i];
                if (t.Wreck) { Rot(t, dt); continue; }
                t.Crew.RemoveAll(c => !Is(c.slot, c.gen));
                if (t.Player >= 0 && (!Is(t.Player, Slots[t.Player].Gen) || !Slots[t.Player].Player)) t.Player = -1;
                bool manned = t.Crew.Count > 0;
                bool driven = t.Player >= 0 && t.Seat == 0;
                if (!driven) { t.Trigger = false; if (manned) Drive(t, dt); else { t.Throttle = t.Steer = 0f; } }
                Move(t, dt);
                MainGun(t, dt);
                for (int g = 0; g < 2; g++) SideGun(t, g, dt, manned);
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

        // ------------------------------------------------------------------ driving

        static float Wrap(float a) => a - math.PI * 2f * math.round(a / (math.PI * 2f));

        void Drive(Tank t, float dt)
        {
            t.Think -= dt;
            if (t.Think <= 0f || t.Objective < 0) { t.Think = 4f; t.Objective = TankObjective(t); }
            t.AimThink -= dt;
            if (t.AimThink <= 0f) { t.AimThink = _rng.NextFloat(0.7f, 1.1f); PickTarget(t); }

            if (t.Backing > 0f)
            {
                t.Backing -= dt;
                t.Throttle = -1f;
                t.Steer = t.BackSteer;
                return;
            }

            // something to shoot that the gun can't be brought round to: bring the tank round
            if (t.HasAim)
            {
                float2 d = t.Aim.xz - t.Pos.xz;
                float off = Wrap(math.atan2(d.x, d.y) - t.Yaw);
                Elevate(t, t.Aim);
                bool ready = t.Reload < 1.2f;
                if (t.Enemy != null || ready)
                {
                    t.Throttle = 0f;
                    t.Steer = math.abs(off) > Tank.CannonArc * 0.8f ? math.clamp(off * 2.2f, -1f, 1f) : 0f;
                    t.Trigger = math.abs(off) < Tank.CannonArc && math.abs(Wrap(t.WantCannonYaw - t.CannonYaw)) < 0.02f && math.abs(t.WantCannonPitch - t.CannonPitch) < 0.03f;
                    return;
                }
            }
            else { t.WantCannonYaw = 0f; t.WantCannonPitch = -0.03f; }

            float2 goal = Map.Objectives[t.Objective];
            float toGoal = math.distance(goal, t.Pos.xz);
            float2 dir = TankWay(t.Objective, t.Pos.xz);
            if (toGoal < 24f || !math.any(dir)) { t.Throttle = 0f; t.Steer = 0f; return; }
            float turn = Wrap(math.atan2(dir.x, dir.y) - t.Yaw);
            t.Steer = math.clamp(turn * 1.8f, -1f, 1f);
            t.Throttle = math.abs(turn) > 0.9f ? 0f : math.saturate(1.15f - math.abs(turn) * 1.1f);
            if (t.Throttle > 0.2f && math.abs(t.Speed) < 0.25f) t.Stuck += dt; else t.Stuck = math.max(0f, t.Stuck - dt * 2f);
            if (t.Stuck > 1.6f) { t.Stuck = 0f; t.Backing = _rng.NextFloat(1.6f, 2.8f); t.BackSteer = _rng.NextBool() ? 0.8f : -0.8f; }
        }

        float2 TankWay(int objective, float2 p)
        {
            int2 c = MapView.Cell(p);
            float2 sum = 0;
            for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++)
            {
                int2 q = math.clamp(c + new int2(x, y), 0, new int2(MapView.W - 1, MapView.H - 1));
                int f = TankFlow[objective * MapView.W * MapView.H + MapView.Index(q)];
                if (f >= 0) sum += math.normalize((float2)MapView.Dir(f));
            }
            if (math.any(sum)) return math.normalizesafe(sum);
            // inside something's margin: straight for it
            return math.normalizesafe(Map.Objectives[objective] - p);
        }

        int TankObjective(Tank t)
        {
            bool attack = Orders[t.Team] == OrderAttack || Orders[t.Team] == OrderAuto && (Teams[t.Team].Attacking || (t.Crew.Count & 1) == 0);
            int best = -1;
            float bestScore = float.MaxValue;
            for (int pass = 0; pass < 2 && best < 0; pass++)
                for (int o = 0; o < Front.Count; o++)
                {
                    if (pass == 0 == attack ? !CanTake(o, t.Team) : !OnTheLine(o, t.Team)) continue;
                    float score = math.distance(Map.Objectives[o], t.Pos.xz) - (o == t.Objective ? 40f : 0f);
                    foreach (var k in Tanks) if (k != t && !k.Wreck && k.Team == t.Team && k.Objective == o) score += 45f;
                    if (score < bestScore) { bestScore = score; best = o; }
                }
            return best >= 0 ? best : Front.Home(t.Team);
        }

        bool TankHits(Tank t, float2 p, float yaw)
        {
            var s = new TankState { Pos = new float3(p.x, 0, p.y), Yaw = yaw };
            for (int z = -2; z <= 2; z++)
            for (int x = -1; x <= 1; x++)
            {
                if (x == 0 && math.abs(z) < 2) continue;
                float2 q = s.World(new float2(x * Tank.HalfWide, z * Tank.HalfLong * 0.5f));
                if (math.abs(q.x) > MapView.HalfW - 2f || math.abs(q.y) > MapView.HalfH - 2f) return true;
                int c = MapView.Index(q);
                if (Map.Blocked[c] != 0 && Map.PropTop[c] - Map.Height(q) > 0.55f) return true;
            }
            foreach (var o in Tanks)
            {
                if (o == t) continue;
                float now = math.distancesq(o.Pos.xz, t.Pos.xz), then = math.distancesq(o.Pos.xz, p);
                if (then < 6.6f * 6.6f && then < now) return true;
            }
            return false;
        }

        void Move(Tank t, float dt)
        {
            // uphill it labours, downhill it runs on a little
            if (t.Throttle == 0f && t.Steer == 0f && math.abs(t.Speed) < 0.01f && math.abs(t.Turning) < 0.002f)
            {
                // standing
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

        /// <summary>
        /// How it sits: each track rests across the high points under it, so it bridges trenches, noses over crests and drops
        /// its weight down the far side.
        /// </summary>
        void Settle(Tank t, in TankRest rest, float dt)
        {
            float y = rest.Y, pitch = rest.Pitch, roll = rest.Roll;
            // sprung: pushed up smartly, falling under its own weight
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

        /// <summary>Another tank before anything else; failing that, the thickest of their infantry in sight.</summary>
        void PickTarget(Tank t)
        {
            t.HasAim = false;
            t.Enemy = null;
            float3 eye = (float3)t.At(TankModel.Cannon) + new float3(0, 0.3f, 0);
            float best = T.tankRange;
            foreach (var o in Tanks)
            {
                if (o.Wreck || o.Team == t.Team) continue;
                float d = math.distance(o.Pos, t.Pos);
                if (d >= best || !Map.Sees(eye, o.Pos + new float3(0, 2f, 0))) continue;
                best = d;
                t.Enemy = o;
            }
            if (t.Enemy != null) { t.HasAim = true; t.Aim = t.Enemy.Pos + new float3(0, 1.4f, 0); return; }

            var theirs = t.Team == Team.Green ? _grey : _green;
            var mine = t.Team == Team.Green ? _green : _grey;
            int2 lo = theirs.At(t.Pos.xz - T.tankRange), hi = theirs.At(t.Pos.xz + T.tankRange);
            float worthBest = 3f;
            for (int y = lo.y; y <= hi.y; y++)
            for (int x = lo.x; x <= hi.x; x++)
            {
                int k = y * theirs.W + x;
                int count = theirs.Start[k + 1] - theirs.Start[k];
                if (count < 2) continue;
                var s = Slots[theirs.Items[theirs.Start[k]]];
                float d = math.distance(s.Pos, t.Pos);
                if (d < 16f || d > T.tankRange) continue;
                float2 to = s.Pos.xz - t.Pos.xz;
                float off = math.abs(Wrap(math.atan2(to.x, to.y) - t.Yaw));
                float worth = count * (1.4f - d / T.tankRange) - off * 1.5f;
                if (worth <= worthBest || mine.CountNear(Slots, s.Pos.xz, T.shellEdgeRadius * 0.8f, 1) > 0) continue;
                if (!Map.Sees(eye, s.Pos + new float3(0, 1.4f, 0))) continue;
                worthBest = worth;
                t.HasAim = true;
                t.Aim = s.Pos + new float3(0, 0.4f, 0);
            }
        }

        /// <summary>Lays the gun on a place, as far as it'll go.</summary>
        public void Elevate(Tank t, float3 at)
        {
            float3 from = t.At(TankModel.Cannon);
            float3 d = at - from;
            // into the tank's own frame
            float3 dir = Arc(d);
            Lay(t, dir);
        }

        public void Lay(Tank t, float3 dir)
        {
            float3 l = Quaternion.Inverse(t.Rotation) * (Vector3)dir;
            t.WantCannonYaw = math.clamp(math.atan2(l.x, l.z), -Tank.CannonArc, Tank.CannonArc);
            t.WantCannonPitch = math.clamp(-math.atan2(l.y, math.length(l.xz)), -0.5f, 0.2f);
        }

        /// <summary>How far from its mark a shell may fall: less when that's nearly on top of the tank itself.</summary>
        public float Scatter(Tank t, float3 mark) => math.min(T.tankScatter, math.distance(mark.xz, t.Pos.xz) * 0.5f);

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
            const float swing = 0.5f;
            t.CannonYaw += math.clamp(t.WantCannonYaw - t.CannonYaw, -swing * dt, swing * dt);
            t.CannonPitch += math.clamp(t.WantCannonPitch - t.CannonPitch, -swing * dt, swing * dt);
            if (!t.Trigger || t.Reload > 0f || t.Crew.Count == 0) return;
            t.Trigger = false;
            t.Reload = Tank.ReloadTime;
            t.Recoil = 1f;
            // it rocks back on its tracks
            t.PitchV -= 0.5f;
            t.Speed -= 0.4f * math.cos(t.CannonYaw);
            float3 dir = t.CannonRotation * Vector3.forward;
            dir = Scattered(t, dir);
            var gunner = t.Player >= 0 && t.Seat == 0 ? (t.Player, Slots[t.Player].Gen) : t.Crew[0];
            Shells.Add(new TankShell { Pos = t.Muzzle, Vel = dir * Tank.ShellSpeed, Life = 8f, Team = t.Team, Shooter = gunner.Item1, ShooterGen = gunner.Item2 });
            Blasts.Add(new BlastEvent { At = t.Muzzle, Dir = dir, Cannon = true, Team = t.Team });
        }

        void FlyShells(float dt)
        {
            for (int i = Shells.Count - 1; i >= 0; i--)
            {
                var s = Shells[i];
                bool hit = false;
                for (int k = 0; k < 4 && !hit; k++)
                {
                    float h = dt / 4f;
                    s.Vel.y -= 9.81f * h;
                    s.Pos += s.Vel * h;
                    hit = s.Pos.y < Map.Height(s.Pos.xz) || s.Pos.y < Map.PropTop[MapView.Index(s.Pos.xz)];
                    foreach (var t in Tanks) if (!t.Wreck && t.Team != s.Team && math.distancesq(t.Pos, s.Pos) < 36f && t.Distance(s.Pos) < 0.25f) hit = true;
                }
                s.Life -= dt;
                if (hit)
                {
                    Shells.RemoveAt(i);
                    Explode(s.Pos, T.shellFullRadius, T.shellFullDamage, T.shellEdgeRadius, T.shellEdgeDamage, s.Team, true, false, s.Shooter, s.ShooterGen);
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
                foreach (var o in Tanks) if (o != t && !o.Wreck && math.distancesq(o.Pos, p) < 36f && o.Distance(p) < 0.2f) return p;
            }
            return p;
        }

        // ------------------------------------------------------------------ the side guns

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
            // got away out of range
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

        /// <summary>Whether a side gun can be brought to bear on a place.</summary>
        static bool Bears(Tank t, int side, float3 at, out float yaw, out float pitch)
        {
            Vector3 l = Quaternion.Inverse(t.Rotation * Quaternion.Euler(0, side == 0 ? -90f : 90f, 0)) * ((Vector3)at - t.GunAt(side));
            yaw = Mathf.Atan2(l.x, l.z);
            pitch = -Mathf.Atan2(l.y, new Vector2(l.x, l.z).magnitude);
            return Mathf.Abs(yaw) < Tank.SideArc && Mathf.Abs(pitch) < 0.4f;
        }

        // ------------------------------------------------------------------ hurting, and being hurt

        /// <summary>Anybody of theirs it runs onto.</summary>
        void Crush(Tank t)
        {
            if (math.abs(t.Speed) < 1.1f) return;
            var theirs = t.Team == Team.Green ? _grey : _green;
            var s = t.State;
            int2 lo = theirs.At(t.Pos.xz - 5f), hi = theirs.At(t.Pos.xz + 5f);
            for (int y = lo.y; y <= hi.y; y++)
            for (int x = lo.x; x <= hi.x; x++)
            {
                int k = y * theirs.W + x;
                for (int n = theirs.Start[k]; n < theirs.Start[k + 1]; n++)
                {
                    int slot = theirs.Items[n];
                    var st = Slots[slot];
                    if (!st.Alive || st.Team == t.Team) continue;
                    float2 l = s.Local(st.Pos.xz);
                    if (math.abs(l.x) > TankModel.TrackOut - 0.1f || math.abs(l.y) > TankModel.TrackZ - 0.5f) continue;
                    var who = t.Crew.Count > 0 ? t.Crew[0] : (slot: -1, gen: 0);
                    Shots.Enqueue(new ShotEvent { Shooter = who.slot, ShooterGen = who.gen, Target = slot, TargetGen = st.Gen, From = t.Pos, To = st.Pos + new float3(0, 0.6f, 0), Damage = 400f, Hit = true, Team = t.Team, Kind = ShotKind.Tank });
                }
            }
        }

        void HurtTanks(float3 at, bool big)
        {
            for (int i = 0; i < Tanks.Count; i++)
            {
                var t = Tanks[i];
                if (t.Wreck || t.Distance(at) > (big ? 4.5f : 3.2f)) continue;
                t.Health -= big ? Tank.ShellHurt : Tank.GrenadeHurt;
                t.PitchV += _rng.NextFloat(-0.5f, 0.5f);
                t.RollV += _rng.NextFloat(-0.5f, 0.5f);
                if (t.Health <= 0.01f) Destroy(t);
            }
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
                Shots.Enqueue(new ShotEvent { Shooter = -1, Target = slot, TargetGen = gen, From = t.Pos, To = st.Pos, Damage = 9999f, Power = 0.75f, Hit = true, Team = (byte)(1 - t.Team), Kind = ShotKind.Blast, Big = true });
            }
            t.Crew.Clear();
            t.Player = -1;
        }
    }
}

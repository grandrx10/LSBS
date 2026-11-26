using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace LSBS
{
    public enum AirKind : byte { Troop, Vehicle }
    public enum AirStage : byte { Waiting, ToPickup, Loading, ToDrop, Unloading, Leaving }

    /// <summary>Somebody aboard: all that's needed to put them back on the ground as they were.</summary>
    public struct Passenger
    {
        public SoldierClass Class;
        public float Health, Height;
        public int Grenades, Missiles, Ammo;
    }

    /// <summary>
    /// A transport: four ducted fans and a hold. A troop transport carries one squad, a vehicle transport one tank slung
    /// beneath it. Each is free with what it carries: it brings it in, and comes back whenever it's wanted to move it.
    /// </summary>
    public class Aircraft
    {
        public const float Cruise = 24f, Speed = 34f, Climb = 9f, HalfWide = 5.4f, HalfLong = 5.7f, LoadTime = 3f;
        public const float TroopHealth = 500f, VehicleHealth = 500f; // two missiles

        public AirKind Kind;
        public byte Team;
        public float3 Pos, Vel;
        public float Yaw, Bank, Pitch;
        public float Health;
        public bool Wreck;
        public float WreckTime;
        public AirStage Stage;
        public float Timer, Delay;
        public float2 Pickup, Drop, Exit;
        /// <summary>Aboard, or coming for: a squad's soldiers, a tank.</summary>
        public List<Passenger> Troops;
        public Tank Cargo;
        public int FetchSquad = -1;
        public Tank FetchTank;
        /// <summary>Where the commander wants what it's carrying to go once it's down.</summary>
        public bool Hold;
        public float Fans;

        public bool Flying => !Wreck && Delay <= 0f;
        /// <summary>How high its middle rides above the ground once down: higher with a tank hung under it.</summary>
        public float Clearance => Kind == AirKind.Vehicle && Cargo != null ? 5.2f : 1.9f;
        public Quaternion Rotation => Quaternion.Euler(Pitch * Mathf.Rad2Deg, Yaw * Mathf.Rad2Deg, Bank * Mathf.Rad2Deg);
        public TankState State => new() { Pos = Pos, Yaw = Yaw, Speed = math.length(Vel.xz), Team = Team, Wreck = Wreck || Delay > 0f, HalfWide = HalfWide, HalfLong = HalfLong, Flying = true };
    }

    public unsafe partial class BattleSystem
    {
        public readonly List<Aircraft> Flights = new();
        /// <summary>Tanks then transports, for whoever fires missiles: an index into this is a tank below Tanks.Count, else a transport.</summary>
        NativeList<TankState> _vehicleStates;
        public int AircraftLost;
        readonly int[] _dropCount = new int[Team.Max];

        void AirCreate() => _vehicleStates = new NativeList<TankState>(512, Allocator.Persistent);
        void AirDestroy() => _vehicleStates.Dispose();
        void AirClear()
        {
            Flights.Clear();
            AircraftLost = 0;
            System.Array.Clear(_dropCount, 0, _dropCount.Length);
        }

        /// <summary>Off the edge of the field behind a side's landing ground.</summary>
        public float2 EdgeBehind(int team, float along)
        {
            float2 home = Map.Objectives[Front.Home(team)];
            float2 out_ = math.normalizesafe(home, new float2(1, 0));
            float2 side = new float2(out_.y, -out_.x);
            return out_ * (MapView.HalfW + 70f) + side * along;
        }

        /// <summary>The k-th place on a side's landing ground: round and round it, a transport's width apart.</summary>
        float2 DropSpot(int team, int k, bool tank)
        {
            float2 home = Map.Objectives[Front.Home(team)];
            for (int tries = 0; tries < 40; tries++, k += 7)
            {
                // a sunflower: evenly spread, out from the middle
                float r = 15f * math.sqrt(k + 0.5f), a = k * 2.39996f;
                float2 p = MapView.Clamp(home + new float2(math.cos(a), math.sin(a)) * r);
                if (tank ? TankCanStand(p, 8f) : !Map.IsBlocked(p)) return p;
            }
            return home;
        }

        /// <summary>A squad's worth of new soldiers.</summary>
        List<Passenger> Recruits()
        {
            var l = new List<Passenger>(Squad.Size);
            for (int m = 0; m < Squad.Size; m++)
            {
                var c = Squad.ClassOf(m);
                float height = 1f + (_rng.NextFloat(-1f, 1f) + _rng.NextFloat(-1f, 1f)) * 0.5f * T.heightSpread * 1.4f;
                l.Add(new Passenger { Class = c, Health = 100f, Height = math.clamp(height, 1f - T.heightSpread, 1f + T.heightSpread), Grenades = c == SoldierClass.Sweeper ? T.grenades : 0, Missiles = c == SoldierClass.Launcher ? T.missiles : 0, Ammo = Gun.Magazine(c) });
            }
            return l;
        }

        /// <summary>Brings in a squad, or a tank, from off the field to its side's landing ground.</summary>
        void Bring(byte team, bool tank, TankKind kind, float delay)
        {
            var a = new Aircraft
            {
                Kind = tank ? AirKind.Vehicle : AirKind.Troop, Team = team, Delay = delay, Stage = AirStage.ToDrop,
                Health = tank ? Aircraft.VehicleHealth : Aircraft.TroopHealth, Hold = team == PlayerTeam,
            };
            a.Exit = EdgeBehind(team, _rng.NextFloat(-90f, 90f));
            a.Drop = DropSpot(team, _dropCount[team]++, tank);
            float2 from = a.Exit;
            a.Pos = new float3(from.x, Map.Height(MapView.Clamp(from)) + Aircraft.Cruise + 10f, from.y);
            float2 to = a.Drop - from;
            a.Yaw = math.atan2(to.x, to.y);
            if (tank)
            {
                var t = SpawnTank(team, kind, a.Pos - new float3(0, 3.4f, 0), a.Yaw);
                if (t == null) return;
                t.Carrier = a;
                a.Cargo = t;
            }
            else a.Troops = Recruits();
            Flights.Add(a);
        }

        /// <summary>The commander wants a squad moved by air: its transport comes for it, and takes it where it's sent.</summary>
        public void Airlift(int squad, float2 to, byte team)
        {
            var q = Squads[squad];
            if (!q.Active || q.Team != team || q.Crew || q.Inside) return;
            foreach (var x in Flights) if (x.FetchSquad == squad && !x.Wreck) return;
            var a = new Aircraft { Kind = AirKind.Troop, Team = team, Stage = AirStage.ToPickup, Health = Aircraft.TroopHealth, FetchSquad = squad, Hold = true };
            a.Exit = EdgeBehind(team, _rng.NextFloat(-60f, 60f));
            a.Pickup = Landing(q.Pos, false);
            a.Drop = Landing(to, false);
            a.Pos = new float3(a.Exit.x, Map.Height(MapView.Clamp(a.Exit)) + Aircraft.Cruise + 10f, a.Exit.y);
            Flights.Add(a);
            // they wait where they are for it
            Order(squad, a.Pickup, a.Pickup, team);
        }

        public void Airlift(Tank t, float2 to)
        {
            if (t == null || t.Wreck || t.Carried) return;
            foreach (var x in Flights) if (x.FetchTank == t && !x.Wreck) return;
            var a = new Aircraft { Kind = AirKind.Vehicle, Team = t.Team, Stage = AirStage.ToPickup, Health = Aircraft.VehicleHealth, FetchTank = t, Hold = true };
            a.Exit = EdgeBehind(t.Team, _rng.NextFloat(-60f, 60f));
            a.Pickup = t.Pos.xz;
            a.Drop = Landing(to, true);
            a.Pos = new float3(a.Exit.x, Map.Height(MapView.Clamp(a.Exit)) + Aircraft.Cruise + 10f, a.Exit.y);
            Flights.Add(a);
            OrderTank(t, t.Pos.xz);
        }

        /// <summary>Somewhere near here clear enough to come down.</summary>
        float2 Landing(float2 p, bool tank)
        {
            p = MapView.Clamp(p);
            for (int r = 0; r < 40; r += 2)
            for (int k = 0; k < math.max(1, r * 3); k++)
            {
                float a = k * math.PI * 2f / math.max(1, r * 3);
                float2 q = MapView.Clamp(p + new float2(math.cos(a), math.sin(a)) * r);
                if (tank ? TankCanStand(q, 7f) : Clear(q, 3f)) return q;
            }
            return p;
        }

        bool Clear(float2 p, float r)
        {
            for (float x = -r; x <= r; x += 1f)
            for (float z = -r; z <= r; z += 1f)
                if (Map.IsBlocked(MapView.Clamp(p + new float2(x, z)))) return false;
            return true;
        }

        // ------------------------------------------------------------------ flying

        void AirTick(float dt)
        {
            for (int i = Flights.Count - 1; i >= 0; i--)
            {
                var a = Flights[i];
                if (a.Delay > 0f) { a.Delay -= dt; continue; }
                if (a.Wreck) { if (Fall(a, dt)) Flights.RemoveAt(i); continue; }
                a.Fans = math.min(1f, a.Fans + dt);
                switch (a.Stage)
                {
                    case AirStage.ToPickup:
                        if (a.FetchSquad >= 0 && !Squads[a.FetchSquad].Active || a.FetchTank != null && (a.FetchTank.Wreck || a.FetchTank.Carried)) { a.Stage = AirStage.Leaving; break; }
                        if (a.FetchTank != null) a.Pickup = a.FetchTank.Pos.xz;
                        if (Fly(a, a.Pickup, true, dt)) { a.Stage = AirStage.Loading; a.Timer = 0f; }
                        break;
                    case AirStage.Loading:
                        Hover(a, a.Pickup, dt);
                        a.Timer += dt;
                        if (a.Timer < Aircraft.LoadTime) break;
                        if (!Load(a)) { a.Stage = AirStage.Leaving; break; }
                        a.Stage = AirStage.ToDrop;
                        break;
                    case AirStage.ToDrop:
                        if (Fly(a, a.Drop, true, dt)) { a.Stage = AirStage.Unloading; a.Timer = 0f; }
                        break;
                    case AirStage.Unloading:
                        Hover(a, a.Drop, dt);
                        a.Timer += dt;
                        if (a.Timer < Aircraft.LoadTime * 0.6f) break;
                        Unload(a);
                        a.Stage = AirStage.Leaving;
                        break;
                    case AirStage.Leaving:
                        if (Fly(a, a.Exit, false, dt) || math.abs(a.Pos.x) > MapView.HalfW + 60f || math.abs(a.Pos.z) > MapView.HalfH + 60f) { Flights.RemoveAt(i); continue; }
                        break;
                }
                if (a.Cargo != null)
                {
                    a.Cargo.Pos = a.Pos - new float3(0, 3.4f, 0);
                    a.Cargo.Yaw = a.Yaw;
                    a.Cargo.Pitch = a.Pitch; a.Cargo.Roll = a.Bank;
                }
            }
            _vehicleStates.Clear();
            for (int i = 0; i < _tankStates.Length; i++) _vehicleStates.Add(_tankStates[i]);
            foreach (var a in Flights) _vehicleStates.Add(a.State);
        }

        /// <summary>Towards a place, over whatever's between, and down onto it at the end if it's to land. True once there.</summary>
        bool Fly(Aircraft a, float2 to, bool land, float dt)
        {
            float2 d = to - a.Pos.xz;
            float dist = math.length(d);
            // slowing for the landing, then straight down
            float speed = land ? math.min(Aircraft.Speed, math.max(2f, dist * 0.55f)) : Aircraft.Speed;
            float2 want = dist > 0.5f ? d / dist * speed : 0;
            a.Vel.xz = math.lerp(a.Vel.xz, want, 1f - math.exp(-1.2f * dt));
            float2 at = MapView.Clamp(a.Pos.xz);
            // high enough over the ground ahead
            float ground = Map.Height(at);
            for (float k = 10f; k <= 40f; k += 10f) ground = math.max(ground, Map.Height(MapView.Clamp(at + math.normalizesafe(a.Vel.xz) * k)));
            float floor = Map.Height(MapView.Clamp(to)) + a.Clearance;
            float height = land && dist < 40f ? math.lerp(floor, ground + Aircraft.Cruise, math.saturate((dist - 3f) / 37f)) : ground + Aircraft.Cruise;
            a.Vel.y = math.clamp((height - a.Pos.y) * 1.5f, -Aircraft.Climb, Aircraft.Climb);
            a.Pos += a.Vel * dt;
            Turn(a, dist > 3f ? math.atan2(d.x, d.y) : a.Yaw, dt);
            return land ? dist < 1.5f && a.Pos.y - floor < 0.6f : dist < 20f;
        }

        void Hover(Aircraft a, float2 at, float dt)
        {
            a.Vel = math.lerp(a.Vel, 0, 1f - math.exp(-3f * dt));
            float floor = Map.Height(MapView.Clamp(at)) + a.Clearance;
            a.Pos.y = math.lerp(a.Pos.y, floor, 1f - math.exp(-3f * dt));
            Turn(a, a.Yaw, dt);
        }

        static void Turn(Aircraft a, float yaw, float dt)
        {
            float off = Wrap(yaw - a.Yaw);
            float step = math.clamp(off, -1.1f * dt, 1.1f * dt);
            a.Yaw = Wrap(a.Yaw + step);
            // leaning into the turn, nose down to go
            float ahead = math.dot(a.Vel.xz, new float2(math.sin(a.Yaw), math.cos(a.Yaw)));
            a.Bank = math.lerp(a.Bank, -step / math.max(dt, 1e-4f) * 0.25f, 1f - math.exp(-3f * dt));
            a.Pitch = math.lerp(a.Pitch, math.clamp(ahead * 0.008f, -0.2f, 0.2f), 1f - math.exp(-2f * dt));
        }

        /// <summary>Takes aboard what it came for, if it's there. False if not.</summary>
        bool Load(Aircraft a)
        {
            if (a.FetchTank != null)
            {
                var t = a.FetchTank;
                if (t.Wreck || t.Carried || math.distance(t.Pos.xz, a.Pos.xz) > 12f) return false;
                t.Carrier = a;
                t.Ordered = false;
                a.Cargo = t;
                a.FetchTank = null;
                return true;
            }
            if (a.FetchSquad < 0) return false;
            var q = Squads[a.FetchSquad];
            if (!q.Active || math.distance(q.Pos, a.Pos.xz) > 30f) return false;
            a.Troops = new List<Passenger>();
            for (int m = 0; m < Squad.Size; m++)
            {
                int slot = q.Members[m];
                if (slot < 0 || Slots[slot].Player || Slots[slot].Inside) continue;
                if (math.distance(Slots[slot].Pos.xz, a.Pos.xz) > 30f) continue;
                var e = SlotEntities[slot];
                var s = EntityManager.GetComponentData<Soldier>(e);
                var ab = EntityManager.GetComponentData<Ability>(e);
                a.Troops.Add(new Passenger
                {
                    Class = s.Class, Height = s.Height, Health = EntityManager.GetComponentData<Health>(e).Value,
                    Grenades = ab.Grenades, Missiles = ab.Missiles, Ammo = EntityManager.GetComponentData<Gun>(e).Ammo,
                });
                Vanish(slot);
            }
            a.FetchSquad = -1;
            return a.Troops.Count > 0;
        }

        /// <summary>Puts down what it's carrying where it's come down.</summary>
        void Unload(Aircraft a)
        {
            if (a.Cargo != null)
            {
                var t = a.Cargo;
                a.Cargo = null;
                t.Carrier = null;
                t.Pos = new float3(a.Pos.x, Map.Height(a.Pos.xz), a.Pos.z);
                t.Pitch = t.Roll = 0f;
                t.Ordered = a.Hold;
                t.OrderAt = t.Pos.xz;
                t.Field = -1;
                return;
            }
            if (a.Troops == null || a.Troops.Count == 0) return;
            int qi = SpawnSquad(a.Team, a.Pos.xz, a.Troops.Count, false);
            if (qi < 0) return;
            var q = Squads[qi];
            if (!a.Hold && q.Ordered) { q.Ordered = false; Assign(ref q, q.Objective); }
            Squads[qi] = q;
            // as they were when they got on
            for (int m = 0; m < a.Troops.Count; m++)
            {
                int slot = q.Members[m];
                if (slot < 0) continue;
                var p = a.Troops[m];
                var e = SlotEntities[slot];
                var s = EntityManager.GetComponentData<Soldier>(e);
                s.Class = p.Class; s.Height = p.Height;
                EntityManager.SetComponentData(e, s);
                EntityManager.SetComponentData(e, new Health { Value = p.Health });
                var ab = EntityManager.GetComponentData<Ability>(e);
                ab.Grenades = p.Grenades; ab.Missiles = p.Missiles;
                EntityManager.SetComponentData(e, ab);
                var g = EntityManager.GetComponentData<Gun>(e);
                g.Ammo = p.Ammo;
                EntityManager.SetComponentData(e, g);
                var st = Slots[slot];
                st.Class = p.Class; st.Height = p.Height;
                Slots[slot] = st;
            }
            a.Troops = null;
            Arrived?.Invoke(qi);
        }

        /// <summary>Told when a squad's put down by a transport (its squad number).</summary>
        public System.Action<int> Arrived;

        /// <summary>Gone from the field without dying: aboard a transport.</summary>
        void Vanish(int slot)
        {
            var st = Slots[slot];
            EntityManager.DestroyEntity(SlotEntities[slot]);
            var q = Squads[st.Squad];
            q.Members[st.Member] = -1;
            q.Alive = math.max(0, q.Alive - 1);
            bool any = false;
            for (int m = 0; m < Squad.Size; m++) any |= q.Members[m] >= 0;
            if (!any)
            {
                Unassign(q);
                q.Active = false;
                _freeSquads.Add(st.Squad);
                if (PlayerSquad == st.Squad) PlayerSquad = -1;
            }
            Squads[st.Squad] = q;
            st.Alive = false;
            Slots[slot] = st;
            _freeSlots.Add(slot);
        }

        // ------------------------------------------------------------------ coming down the hard way

        /// <summary>A shell or missile at this place hits a transport it's in. True if it did.</summary>
        bool HitAircraft(float3 p, byte team, float hurt)
        {
            foreach (var a in Flights)
            {
                if (!a.Flying || a.Team == team) continue;
                if (math.distancesq(a.Pos, p) > 6.5f * 6.5f) continue;
                HurtAircraft(a, hurt);
                return true;
            }
            return false;
        }

        public void HurtAircraft(Aircraft a, float hurt)
        {
            if (a.Wreck) return;
            a.Health -= hurt;
            if (a.Health > 0.01f) return;
            a.Wreck = true;
            a.WreckTime = 0f;
            AircraftLost++;
            Blasts.Add(new BlastEvent { At = a.Pos, Radius = 10f, Big = true, Team = a.Team });
            // whoever's aboard goes down with it
            if (a.Troops != null)
            {
                var ts = Teams[a.Team];
                ts.Losses += a.Troops.Count; ts.TotalLosses += a.Troops.Count;
                Teams[a.Team] = ts;
                a.Troops = null;
            }
            if (a.Cargo != null) { var t = a.Cargo; a.Cargo = null; t.Carrier = null; HurtTank(t, 999f); t.Pos.y = Map.Height(t.Pos.xz); }
        }

        /// <summary>Down out of the sky; true once it's hit the ground and gone.</summary>
        bool Fall(Aircraft a, float dt)
        {
            a.WreckTime += dt;
            a.Vel.y -= 9.81f * dt;
            a.Pos += a.Vel * dt;
            a.Bank += dt * 1.6f;
            a.Pitch += dt * 0.5f;
            float ground = Map.Height(MapView.Clamp(a.Pos.xz));
            if (a.Pos.y > ground + 0.5f && a.WreckTime < 12f) return false;
            Explode(new float3(a.Pos.x, ground, a.Pos.z), 4f, 80f, 10f, 20f, a.Team, true, false, -1, 0);
            return true;
        }
    }
}

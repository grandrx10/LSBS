using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace LSBS
{
    /// <summary>Soldiers bucketed by square cell, so anyone can look up who's near without checking everybody.</summary>
    public struct Grid
    {
        public float Cell;
        public int W, H;
        [NativeDisableParallelForRestriction] public NativeArray<int> Start; // W*H+1
        [NativeDisableParallelForRestriction] public NativeArray<int> Items;

        public static Grid Create(float cell, int max)
        {
            int w = (int)math.ceil(MapView.W / cell), h = (int)math.ceil(MapView.H / cell);
            return new Grid
            {
                Cell = cell, W = w, H = h,
                Start = new NativeArray<int>(w * h + 1, Allocator.Persistent),
                Items = new NativeArray<int>(max, Allocator.Persistent),
            };
        }

        public void Dispose() { Start.Dispose(); Items.Dispose(); }
        public int2 At(float2 p) => math.clamp((int2)((p + new float2(MapView.HalfW, MapView.HalfH)) / Cell), 0, new int2(W - 1, H - 1));

        /// <summary>How many of one side (same) or of everyone else (!same) are within r of a point.</summary>
        public int CountNear(in NativeArray<SlotState> slots, float2 p, float r, int enough, byte team, bool same)
        {
            int n = 0;
            int2 a = At(p - r), b = At(p + r);
            for (int y = a.y; y <= b.y; y++)
            for (int x = a.x; x <= b.x; x++)
            {
                int k = y * W + x;
                for (int i = Start[k]; i < Start[k + 1]; i++)
                {
                    var s = slots[Items[i]];
                    if ((s.Team == team) != same) continue;
                    if (math.distancesq(s.Pos.xz, p) < r * r && ++n >= enough) return n;
                }
            }
            return n;
        }
    }

    [BurstCompile]
    public partial struct SnapshotJob : IJobEntity
    {
        [NativeDisableParallelForRestriction] public NativeArray<SlotState> Slots;

        void Execute(in Soldier s, in Body b, in Brain brain, in Ability ability)
        {
            Slots[s.Slot] = new SlotState
            {
                Pos = b.Pos, Vel = b.Vel, Yaw = b.Yaw, Suppression = brain.Suppression, Height = s.Height, Gen = s.Gen, Squad = s.Squad,
                Team = s.Team, Member = s.Member, Class = s.Class, Alive = true, Kneel = brain.Kneel, HasTarget = brain.Target >= 0, Player = s.Player,
                Mounted = brain.Mounted, Carrying = ability.Carrying, Inside = ability.Tank >= 0,
            };
        }
    }

    /// <summary>Counting sort of one team (or everyone, team 255) into a grid.</summary>
    [BurstCompile]
    public struct GridJob : IJob
    {
        [ReadOnly] public NativeArray<SlotState> Slots;
        public int Count;
        public byte Team;
        public Grid Grid;

        public void Execute()
        {
            var start = Grid.Start;
            for (int i = 0; i < start.Length; i++) start[i] = 0;
            for (int i = 0; i < Count; i++)
            {
                var s = Slots[i];
                if (!s.Alive || s.Inside || (Team != 255 && s.Team != Team)) continue;
                int2 c = Grid.At(s.Pos.xz);
                start[c.y * Grid.W + c.x + 1]++;
            }
            for (int i = 1; i < start.Length; i++) start[i] += start[i - 1];
            var next = new NativeArray<int>(start.Length, Allocator.Temp);
            next.CopyFrom(start);
            var items = Grid.Items;
            for (int i = 0; i < Count; i++)
            {
                var s = Slots[i];
                if (!s.Alive || s.Inside || (Team != 255 && s.Team != Team)) continue;
                int2 c = Grid.At(s.Pos.xz);
                int k = c.y * Grid.W + c.x;
                items[next[k]] = i;
                next[k]++;
            }
        }
    }

    /// <summary>Captains: where exactly the squad is going, and whether it's on the move or holding.</summary>
    [BurstCompile]
    public unsafe struct SquadJob : IJobParallelFor
    {
        public NativeArray<Squad> Squads;
        [ReadOnly] public NativeArray<SlotState> Slots;
        [ReadOnly] public MapView Map;
        public float Dt, CaptureRadius;

        public void Execute(int i)
        {
            var q = Squads[i];
            if (!q.Active) return;

            int alive = 0, contacts = 0, captain = -1;
            for (int m = 0; m < Squad.Size; m++)
            {
                int slot = q.Members[m];
                if (slot < 0) continue;
                alive++;
                if (Slots[slot].HasTarget) contacts++;
                if (m == q.Captain) captain = m;
                // the captain has fallen: the first soldier left takes over
                else if (captain < 0 && q.Captain >= 0 && q.Members[q.Captain] < 0) captain = m;
            }
            q.Alive = alive;
            q.Contacts = contacts;
            if (alive == 0 || captain < 0) { Squads[i] = q; return; }
            q.Captain = captain;
            var cap = Slots[q.Members[captain]];
            q.PlayerLed = cap.Player;
            q.Pos = cap.Pos.xz;
            q.Vel = math.lerp(q.Vel, cap.Vel, math.min(1f, Dt * 3f));
            float2 obj = Map.Objectives[q.Objective];
            // looking out from the objective, or in at it
            float2 outward = math.normalizesafe(q.Pos - obj, new float2(1, 0));

            if (q.PlayerLed)
            {
                q.Facing = math.normalizesafe(math.lerp(q.Facing, new float2(math.sin(cap.Yaw), math.cos(cap.Yaw)), math.min(1f, Dt * 2f)), outward);
                q.Mode = q.PlayerHold ? SquadMode.Hold : SquadMode.Advance;
                q.Goal = q.Pos;
                q.Attacking = true;
                Squads[i] = q;
                return;
            }

            if (q.Ordered)
            {
                // where the commander said, by their own way, ready for whatever's there
                q.Attacking = true;
                // everyone sent to the same place: each squad a little to its own side of it, rather than all on one spot
                q.Goal = q.OrderAt + q.Spot * 7f;
            }
            else
            {
                // the more there are going for it, the wider they come at it
                if (q.Attacking) q.Goal = obj + q.Spot * (CaptureRadius * 0.8f + q.Crowd * 1.1f);
                // holding it: a ring about it, a little out from the middle
                else q.Goal = obj + math.normalizesafe(q.Spot, outward) * (CaptureRadius * (0.55f + 0.35f * math.length(q.Spot)) + q.Crowd * 0.5f);
            }
            q.Goal = MapView.Clamp(q.Goal);

            // the squad faces the way it's headed (or the enemy's side), and only comes round slowly
            float2 toGoal = q.Goal - q.Pos;
            float dist = math.length(toGoal);
            float2 want = dist > 12f ? toGoal / dist : q.Ordered ? math.normalizesafe(q.Facing, outward) : q.Attacking ? -outward : outward;
            q.Facing = math.normalizesafe(math.lerp(q.Facing, want, math.min(1f, Dt * 0.7f)), want);

            q.ModeTimer -= Dt;
            if (!q.Attacking)
            {
                bool caught = contacts >= 2 && dist < 40f;
                // once settled, stay settled unless there's a real way to go
                float settle = q.Mode == SquadMode.Hold ? 14f : 5f;
                var mode = dist < settle || caught ? SquadMode.Hold : SquadMode.Advance;
                if (mode != q.Mode && q.ModeTimer <= 0f) { q.Mode = mode; q.ModeTimer = 3f; }
            }
            else if (contacts == 0)
            {
                var mode = dist < (q.Mode == SquadMode.Hold ? 9f : 4f) ? SquadMode.Hold : SquadMode.Advance;
                if (mode != q.Mode && q.ModeTimer <= 0f) { q.Mode = mode; q.ModeTimer = 1.5f; }
            }
            else if (q.ModeTimer <= 0f)
            {
                // under fire: rush, go to ground, shoot back, rush again
                if (q.Mode == SquadMode.Advance)
                {
                    q.Mode = SquadMode.Hold;
                    q.ModeTimer = q.Rng.NextFloat(4f, 9f) * (1.3f - q.Nerve * 0.6f);
                }
                else
                {
                    q.Mode = SquadMode.Advance;
                    q.ModeTimer = q.Rng.NextFloat(3f, 5f) * (0.7f + q.Nerve * 0.7f);
                }
            }
            Squads[i] = q;
        }
    }

    /// <summary>Looking for someone to shoot: the nearest enemies in range, then whether they can be seen.</summary>
    [BurstCompile]
    public partial struct SenseJob : IJobEntity
    {
        [ReadOnly] public NativeArray<SlotState> Slots;
        /// <summary>Everybody, of every side.</summary>
        [ReadOnly] public Grid All;
        [ReadOnly] public MapView Map;
        public float Dt, Range;

        public const float EyeStand = 1.82f, EyeKneel = 1.40f;

        void Execute(in Soldier s, in Body b, in Ability ability, ref Brain brain)
        {
            if (ability.Tank >= 0) { brain.Target = -1; brain.Suppression = 0f; return; }
            brain.Suppression = math.max(0f, brain.Suppression - Dt * 0.3f);
            if (brain.Target >= 0)
            {
                var t = Slots[brain.Target];
                if (!t.Alive || t.Gen != brain.TargetGen) brain.Target = -1;
                else brain.TargetDist = math.distance(t.Pos, b.Pos);
            }
            if (s.Player) { brain.Target = -1; return; }
            brain.SenseTimer -= Dt;
            if (brain.SenseTimer > 0f) return;
            brain.SenseTimer = brain.Rng.NextFloat(0.25f, 0.45f);

            var grid = All;
            const int K = 5;
            var bestD = new FixedList64Bytes<float>();
            var bestI = new FixedList64Bytes<int>();
            float2 p = b.Pos.xz;
            int2 c0 = grid.At(p);
            int rings = (int)math.ceil(Range / grid.Cell);
            for (int ring = 0; ring <= rings; ring++)
            {
                // everything further out is further than the candidates already in hand
                if (bestD.Length == K && (ring - 1) * grid.Cell > math.sqrt(bestD[K - 1])) break;
                for (int y = -ring; y <= ring; y++)
                {
                    int cy = c0.y + y;
                    if (cy < 0 || cy >= grid.H) continue;
                    int step = math.abs(y) == ring ? 1 : math.max(1, ring * 2);
                    for (int x = -ring; x <= ring; x += step)
                    {
                        int cx = c0.x + x;
                        if (cx < 0 || cx >= grid.W) continue;
                        int k = cy * grid.W + cx;
                        int end = grid.Start[k + 1];
                        for (int n = grid.Start[k]; n < end; n++)
                        {
                            int slot = grid.Items[n];
                            if (Slots[slot].Team == s.Team) continue;
                            float d = math.distancesq(Slots[slot].Pos.xz, p);
                            if (d > Range * Range) continue;
                            if (brain.Mounted)
                            {
                                // only what the gun can be brought round to
                                float2 to = Slots[slot].Pos.xz - p;
                                float off = math.atan2(to.x, to.y) - brain.Arc;
                                off -= math.PI * 2f * math.round(off / (math.PI * 2f));
                                if (math.abs(off) > Maxim.Arc) continue;
                            }
                            if (slot == brain.Target) d *= 0.6f; // stick with who they're on
                            int at = bestD.Length;
                            while (at > 0 && bestD[at - 1] > d) at--;
                            if (at >= K) continue;
                            if (bestD.Length == K) { bestD.RemoveAt(K - 1); bestI.RemoveAt(K - 1); }
                            bestD.InsertRange(at, 1); bestD[at] = d;
                            bestI.InsertRange(at, 1); bestI[at] = slot;
                        }
                    }
                }
            }
            brain.Target = -1;
            float3 eye = b.Pos + new float3(0, (brain.Mounted && brain.Kneel ? 1.25f : EyeStand) * s.Height, 0);
            for (int i = 0; i < bestI.Length; i++)
            {
                var t = Slots[bestI[i]];
                if (!Map.Sees(eye, t.Pos + new float3(0, (t.Kneel ? EyeKneel : EyeStand) * t.Height, 0))) continue;
                brain.Target = bestI[i];
                brain.TargetGen = t.Gen;
                brain.TargetDist = math.distance(t.Pos, b.Pos);
                break;
            }
        }
    }

    /// <summary>
    /// Legs: where each soldier goes, keeping to the squad, out of each other's way, and into cover. Everything here that
    /// starts or stops them has a gap between the two, or a minimum time, so nobody dithers back and forth.
    /// </summary>
    [BurstCompile]
    public partial struct MoveJob : IJobEntity
    {
        [ReadOnly] public NativeArray<SlotState> Slots;
        [ReadOnly] public NativeArray<Squad> Squads;
        [ReadOnly] public Grid Near;
        [ReadOnly] public MapView Map;
        [ReadOnly] public NativeArray<TankState> Tanks;
        [NativeDisableParallelForRestriction] public NativeArray<Maxim> Maxims;
        /// <summary>The way to wherever commanders have sent squads (BattleSystem.OrderFields of them).</summary>
        [ReadOnly] public NativeArray<sbyte> OrderFlow;
        public float MaximBack;
        public Tuning T;
        public float Dt;

        /// <summary>On a machine gun: it swings, and they go round with it, their feet catching up a step at a time.</summary>
        void Man(in Soldier s, in Ability ability, ref Body b, ref Brain brain)
        {
            var m = Maxims[ability.Maxim];
            if (!s.Player)
            {
                if (brain.Target >= 0)
                {
                    var t = Slots[brain.Target];
                    float2 f = t.Pos.xz - m.Pos.xz;
                    m.WantYaw = math.atan2(f.x, f.y);
                    m.Pitch = math.lerp(m.Pitch, -math.atan2(t.Pos.y + 1.1f - m.Pivot.y, math.max(1f, math.length(f))), 1f - math.exp(-5f * Dt));
                }
                else m.Pitch = math.lerp(m.Pitch, 0f, 1f - math.exp(-2f * Dt));
            }
            float want = m.WantYaw - m.BaseYaw;
            want = m.BaseYaw + math.clamp(want - math.PI * 2f * math.round(want / (math.PI * 2f)), -Maxim.Arc, Maxim.Arc);
            float turn = want - m.Yaw;
            turn -= math.PI * 2f * math.round(turn / (math.PI * 2f));
            float most = (s.Player ? 2.2f : 1.0f) * Dt;
            m.Yaw += math.clamp(turn * (1f - math.exp(-9f * Dt)), -most, most);
            if (ability.Act == Act.Pack) { }
            else if (m.Step > 0f)
            {
                m.Step += Dt / Maxim.StepTime;
                if (m.Step >= 1f) { m.Stance += m.StepTurn; m.Step = 0f; }
            }
            else
            {
                float off = m.Yaw - m.Stance;
                off -= math.PI * 2f * math.round(off / (math.PI * 2f));
                if (math.abs(off) > Maxim.Planted) { m.Step = 0.001f; m.StepTurn = math.sign(off) * Maxim.Planted; }
            }
            Maxims[ability.Maxim] = m;
            float3 p = m.Pos - m.Forward * MaximBack;
            b.Pos = new float3(p.x, m.Pos.y, p.z); // level with it, so the hands meet the grips
            b.Vel = 0;
            b.Yaw = m.Yaw;
            b.Pitch = 0f;
            brain.Kneel = !m.Tall;
            brain.Still += Dt;
            brain.Walking = false;
        }

        static float2 Formation(int member, int captain, float2 facing, bool player)
        {
            // places either side of the captain, two loose ranks
            int k = member > captain ? member - 1 : member; // 0..8
            float sx = (k & 1) == 0 ? -1f : 1f;
            int file = k / 2;                               // 0..4
            float2 side = new float2(facing.y, -facing.x);
            if (player) return side * sx * (2.8f + file * 2.2f) + facing * (3.4f - file * 0.9f);
            return side * sx * (3.2f + (file % 3) * 3.4f) + facing * (file >= 3 ? -6f : -1.4f - file * 1.1f);
        }

        unsafe void Execute(in Soldier s, in Ability ability, ref Body b, ref Brain brain)
        {
            if (ability.Tank >= 0 && ability.Tank < Tanks.Length)
            {
                // inside: wherever it goes
                var k = Tanks[ability.Tank];
                b.Pos = k.Pos;
                b.Vel = new float2(math.sin(k.Yaw), math.cos(k.Yaw)) * k.Speed;
                b.Yaw = k.Yaw;
                brain.Kneel = false;
                return;
            }
            if (ability.Maxim >= 0 && brain.Mounted) { Man(s, ability, ref b, ref brain); return; }
            if (s.Player)
            {
                b.Pos.y = Map.Height(b.Pos.xz);
                return;
            }
            var q = Squads[s.Squad];
            float2 p = b.Pos.xz;
            bool captain = q.Captain == s.Member;
            float2 dest;
            bool flow = false;

            if (captain) { dest = q.Goal; flow = math.distance(p, dest) > 16f; }
            else
            {
                // where their place will be in a moment, not where it was
                dest = q.Pos + q.Vel * 0.7f + Formation(s.Member, q.Captain, q.Facing, q.PlayerLed);
                if (math.distance(p, dest) > 26f && !q.PlayerLed) flow = true;
            }

            // close enough to shoot point blank: stop and fight, whatever the captain says
            float stopAt = s.Class == SoldierClass.Sweeper ? 23f : 20f;
            brain.HoldLeft -= Dt;
            bool told = q.Mode == SquadMode.Hold && math.distance(p, q.Pos) < (brain.Holding ? 30f : 20f);
            if (brain.Target >= 0 && brain.TargetDist < stopAt) brain.HoldLeft = math.max(brain.HoldLeft, 2.5f);
            // a shotgun's no use from back here: sweepers keep closing while the others hold
            bool closing = s.Class == SoldierClass.Sweeper && q.Attacking && brain.Target >= 0 && brain.TargetDist > 25f && brain.TargetDist < 46f && brain.Suppression < 0.6f && brain.Dive <= 0f;
            // something's coming in: get down
            brain.Dive -= Dt;
            bool dive = brain.Dive > 0f;
            if (dive) brain.HoldLeft = math.max(brain.HoldLeft, 0.3f);
            // a tank in reach of a grenade, or nearly: go for it
            float2 tankAt = 0;
            bool hunt = false;
            if (s.Class == SoldierClass.Sweeper && ability.Grenades > 0 && brain.Dive <= 0f && brain.Suppression < 0.85f)
            {
                float nearest = 60f * 60f;
                for (int i = 0; i < Tanks.Length; i++)
                {
                    var k = Tanks[i];
                    if (k.Wreck || k.Team == s.Team) continue;
                    float d2 = math.distancesq(k.Pos.xz, p);
                    if (d2 < nearest) { nearest = d2; tankAt = k.Pos.xz; hunt = true; }
                }
                if (hunt && nearest < 22f * 22f) { hunt = false; brain.HoldLeft = math.max(brain.HoldLeft, 0.5f); }
                if (hunt) { closing = true; brain.HoldLeft = 0f; }
            }
            bool hold = (told && !closing) || brain.HoldLeft > 0f || ability.Act != Act.None;
            if (hold)
            {
                if (!brain.Holding || dive && brain.Walking)
                {
                    brain.Holding = true;
                    if (dive) brain.Walking = false;
                    brain.HoldPos = ability.Act != Act.None || dive ? p : FindCover(p, brain.Target >= 0 ? Slots[brain.Target].Pos.xz - p : q.Facing, q.Attacking, ref brain.Rng, brain.HoldLeft > 0f ? 3 : 6);
                    if (told) brain.HoldLeft = math.max(brain.HoldLeft, 1.5f);
                }
                dest = brain.HoldPos;
                flow = false;
            }
            else if (brain.OnPost) brain.Holding = true;
            else
            {
                brain.Holding = false;
                if (hunt) { dest = tankAt; flow = false; }
                else if (closing) { dest = Slots[brain.Target].Pos.xz; flow = false; }
            }

            // a machine gun wants somebody on it
            if (ability.Want >= 0 && ability.Act == Act.None) { dest = ability.WantAt; flow = false; hold = false; captain = false; }
            // holding a bunker: its slit, and nowhere else
            if (brain.OnPost && ability.Act == Act.None && ability.Want < 0) { dest = brain.Post; flow = false; hold = true; captain = false; }

            // the group's shared way runs to the middle of where they're going: off it once their own place is the nearer
            if (flow && q.Ordered && math.distance(p, q.OrderVia) < math.distance(q.OrderAt, q.OrderVia) + 12f) flow = false;

            float2 to = dest - p;
            float dist = math.length(to);
            float2 dir = math.normalizesafe(to);
            brain.WayLeft -= Dt;
            if (!flow && dist > 0.9f && !Map.LineClear(p, dest))
            {
                // round it
                if (brain.WayLeft <= 0f || math.distance(p, brain.Way) < 0.5f)
                {
                    brain.Way = Map.Detour(p, dest);
                    brain.WayLeft = brain.Rng.NextFloat(0.7f, 1.1f);
                }
                float2 w = brain.Way - p;
                dir = math.lengthsq(w) > 0.04f ? math.normalize(w) : float2.zero;
            }
            if (flow)
            {
                bool own = q.Ordered && q.Field >= 0;
                float2 f = own ? MapView.FlowDir(OrderFlow, q.Field, p) : Map.FlowDir(q.Objective, p);
                if (math.lengthsq(f) > 0.1f)
                {
                    // squads keep abreast of each other rather than filing down one path
                    float lane = q.Ordered ? 0f : math.clamp((Map.Objectives[q.Objective].y + q.Spot.y * 42f - p.y) * 0.03f, -0.55f, 0.55f) * math.saturate((dist - 34f) / 40f);
                    dir = math.normalizesafe(f + new float2(0, lane));
                    if (brain.Stuck > 0.15f || Map.IsBlocked(p + dir * 1.1f))
                    {
                        float2 sure = own ? MapView.FlowHere(OrderFlow, q.Field, p) : Map.FlowHere(q.Objective, p);
                        if (math.any(sure)) dir = sure;
                    }
                }
            }

            // set off when there's somewhere to go, stop when there: never in between
            float start = brain.OnPost || ability.Want >= 0 ? 0.6f : hold ? 1.4f : captain ? 3f : 2.6f, stop = hold ? 0.4f : captain ? 1.2f : 0.9f;
            if (!brain.Walking && dist > start) brain.Walking = true;
            else if (brain.Walking && dist < stop) brain.Walking = false;

            float speed = !closing && (brain.Target >= 0 || brain.Suppression > 0.3f) ? T.cautiousSpeed : T.runSpeed;
            if (!captain && !hold && dist > 8f) speed *= 1.15f; // catch up
            float2 want = brain.Walking ? dir * speed * math.saturate(dist / 1.5f + 0.3f) : float2.zero;
            // ahead of their place: wait for the rest to come up instead of walking back to it
            if (!captain && !hold && !flow && !brain.OnPost && ability.Want < 0 && dist < 9f && math.lengthsq(q.Vel) > 0.5f && math.dot(to, q.Facing) < 0f)
                want = q.PlayerLed ? want * 0.5f : float2.zero;

            // keep apart (a nudge, not somewhere to go)
            float2 push = 0;
            bool moving = math.any(want);
            float room = moving ? 1.9f : 1.0f;
            int2 c0 = Near.At(p);
            for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
            {
                int2 c = c0 + new int2(x, y);
                if (c.x < 0 || c.y < 0 || c.x >= Near.W || c.y >= Near.H) continue;
                int k = c.y * Near.W + c.x;
                int end = math.min(Near.Start[k + 1], Near.Start[k] + 12);
                for (int n = Near.Start[k]; n < end; n++)
                {
                    int slot = Near.Items[n];
                    if (slot == s.Slot) continue;
                    float2 d = p - Slots[slot].Pos.xz;
                    float l = math.length(d);
                    if (l >= room) continue;
                    push += (l > 0.01f ? d / l : math.float2(math.sin(s.Slot), math.cos(s.Slot))) * (room - l);
                }
            }
            float pushed = math.length(push);
            if (pushed > 1f) push /= pushed;
            // somebody standing only shuffles clear of whoever they're on top of, slowly enough to go on shooting
            want += moving ? push * 1.8f : push * 0.45f;
            // out of the way of tanks
            for (int i = 0; i < Tanks.Length; i++)
            {
                var k = Tanks[i];
                if (k.Flying) continue;
                float wide = k.HalfWide + 0.6f, along = k.HalfLong + 0.8f;
                if (math.distancesq(k.Pos.xz, p) > (along + 4f) * (along + 4f)) continue;
                float2 l = k.Local(p);
                // further out ahead of one that's moving
                float ahead = along + math.max(0f, k.Speed) * 0.8f;
                if (math.abs(l.x) > wide || l.y > ahead || l.y < -along) continue;
                float2 outward = math.abs(l.x) / wide > math.abs(l.y) / along || l.y > along - 1.7f ? new float2(l.x >= 0f ? 1f : -1f, 0f) : new float2(0f, l.y >= 0f ? 1f : -1f);
                want += (k.World(outward) - k.Pos.xz) * 4.2f;
            }

            // slopes and wire slow you down
            float2 wd = math.normalizesafe(want);
            float slope = (Map.Height(p + wd * 0.6f) - b.Pos.y) / 0.6f;
            float slow = 1f / (1f + math.max(0f, slope) * 0.9f + Map.Rough[MapView.Index(p)] * 0.35f);

            b.Vel = math.lerp(b.Vel, want * slow, 1f - math.exp(-6f * Dt));
            if (math.lengthsq(b.Vel) < 0.02f) b.Vel = 0;
            float2 next = p + b.Vel * Dt;

            if (Map.IsBlocked(next))
            {
                // slide along whatever it is
                float2 nx = new float2(next.x, p.y), ny = new float2(p.x, next.y);
                if (!Map.IsBlocked(nx)) next = nx;
                else if (!Map.IsBlocked(ny)) next = ny;
                else next = Map.IsBlocked(p) ? next : p;
                brain.Stuck += Dt;
                float2 around = new float2(-dir.y, dir.x) * ((s.Slot & 1) == 0 ? 1f : -1f);
                float2 side = next + around * T.cautiousSpeed * Dt;
                if (!Map.IsBlocked(side)) next = side;
                if (hold && brain.Stuck > 1.2f) { brain.HoldPos = p; brain.Stuck = 0; }
            }
            else brain.Stuck = math.max(0f, brain.Stuck - Dt);
            next = MapView.Clamp(next);
            b.Pos = new float3(next.x, Map.Height(next), next.y);

            // ---- which way they look ----
            float going = math.length(b.Vel);
            bool stopped = going < 0.7f;
            brain.Still = stopped ? brain.Still + Dt : 0f;
            brain.Heading = math.lerp(brain.Heading, b.Vel, 1f - math.exp(-5f * Dt));
            float yaw = b.Yaw, wantPitch = 0f;
            if (ability.Act != Act.None) { float2 f = ability.At.xz - next; yaw = math.atan2(f.x, f.y); }
            else if (brain.Target >= 0 && (stopped || going < 2f))
            {
                // an enemy in sight holds their eye, even while they shuffle about
                var t = Slots[brain.Target];
                float2 f = t.Pos.xz - next;
                yaw = math.atan2(f.x, f.y);
                if (stopped) wantPitch = -math.atan2(t.Pos.y - b.Pos.y, math.max(1f, math.length(f)));
            }
            else if (math.any(want) && math.lengthsq(brain.Heading) > 1.2f) yaw = math.atan2(brain.Heading.x, brain.Heading.y);
            else if (brain.Still > 2.5f) yaw = math.atan2(q.Facing.x, q.Facing.y) + math.sin(s.Slot * 1.7f) * 0.5f;
            float diff = yaw - b.Yaw;
            diff -= math.PI * 2f * math.round(diff / (math.PI * 2f));
            b.Yaw += math.clamp(diff * (1f - math.exp(-6f * Dt)), -4.5f * Dt, 4.5f * Dt);
            b.Pitch = math.lerp(b.Pitch, wantPitch, 1f - math.exp(-8f * Dt));

            // kneel when stopped to fight, unless the ground in front is in the way
            brain.KneelTimer -= Dt;
            if (!stopped) { brain.Kneel = false; brain.KneelTimer = 0f; }
            else if (ability.Act == Act.Call || ability.Act == Act.Setup || ability.Act == Act.Pack || dive) brain.Kneel = true;
            else if (ability.Act == Act.Throw) brain.Kneel = false;
            else if (brain.OnPost && dist < 2f) brain.Kneel = false; // the slit's at standing height
            else if (brain.KneelTimer <= 0f)
            {
                brain.KneelTimer = brain.Rng.NextFloat(1.2f, 2.2f);
                if (brain.Target >= 0)
                {
                    var t = Slots[brain.Target];
                    brain.Kneel = Map.Sees(b.Pos + new float3(0, 1.28f, 0), t.Pos + new float3(0, 1.45f, 0));
                }
                else brain.Kneel = hold && (brain.Suppression > 0.15f || s.Slot % 3 != 0);
            }
        }

        /// <summary>The nearest spot sheltered from the enemy's side, within a few metres; where they stand if there's none.</summary>
        float2 FindCover(float2 p, float2 threat, bool attacking, ref Random rng, int radius)
        {
            byte bit = MapView.CoverFrom(threat);
            float2 ahead = math.normalizesafe(threat, new float2(1, 0));
            int2 c0 = MapView.Cell(p);
            float best = 1e9f;
            float2 at = p;
            for (int y = -radius; y <= radius; y++)
            for (int x = -radius; x <= radius; x++)
            {
                // not back the way they've come, while attacking
                if (attacking && math.dot(new float2(x, y), ahead) < -2f) continue;
                int2 c = c0 + new int2(x, y);
                if (c.x < 1 || c.y < 1 || c.x >= MapView.W - 1 || c.y >= MapView.H - 1) continue;
                int i = MapView.Index(c);
                // bunkers are for those who man them
                if ((Map.Cover[i] & bit) == 0 || (Map.Cover[i] & MapView.Roofed) != 0 || Map.Blocked[i] != 0) continue;
                float d = math.lengthsq(new float2(x, y)) * rng.NextFloat(0.6f, 1.6f);
                if (d >= best) continue;
                best = d;
                at = MapView.Centre(c) + rng.NextFloat2(-0.3f, 0.3f);
            }
            return at;
        }
    }

    /// <summary>Sweepers throwing grenades and communicators calling the guns: deciding to, and doing it.</summary>
    [BurstCompile]
    public partial struct AbilityJob : IJobEntity
    {
        [ReadOnly] public NativeArray<SlotState> Slots;
        [ReadOnly] public Grid All;
        [ReadOnly] public MapView Map;
        [ReadOnly] public NativeArray<byte> Calling; // slots with a strike under way
        [ReadOnly] public NativeArray<Strike> Strikes;
        [ReadOnly] public NativeArray<TankState> Tanks;
        public Tuning T;
        public float Dt;
        public NativeQueue<Throw>.ParallelWriter Throws;
        public NativeQueue<Throw>.ParallelWriter Calls;
        public NativeQueue<Deed>.ParallelWriter Deeds;
        public NativeQueue<Launch>.ParallelWriter Launches;

        void Execute(in Soldier s, in Body b, ref Brain brain, ref Ability a, ref Gun gun)
        {
            if (a.Tank >= 0) { a.Act = Act.None; return; }
            a.Cooldown -= Dt;
            if (a.Act != Act.None)
            {
                a.Timer += Dt;
                float at = a.Act == Act.Throw ? Ability.ReleaseAt : a.Act == Act.Call ? Ability.CallTime * 0.8f : a.Act == Act.Launch ? Ability.LaunchAt : Ability.Length(a.Act);
                if (!a.Done && a.Timer >= at)
                {
                    a.Done = true;
                    var e = new Throw { From = b.Pos + new float3(0, 1.9f * s.Height, 0), At = a.At, Team = s.Team, Shooter = s.Slot, ShooterGen = s.Gen };
                    if (a.Act == Act.Throw) { a.Grenades--; Throws.Enqueue(e); }
                    else if (a.Act == Act.Launch)
                    {
                        a.Missiles--;
                        float3 back = new float3(math.sin(b.Yaw), 0, math.cos(b.Yaw));
                        float3 from = b.Pos + new float3(0, 1.75f * s.Height, 0) + back * 0.5f;
                        Launches.Enqueue(new Launch { From = from, Tank = a.Target, Dir = a.Target < 0 ? math.normalizesafe(a.At - from) : float3.zero, Aimed = s.Player, Team = s.Team, Shooter = s.Slot, ShooterGen = s.Gen });
                        a.Cooldown = T.missileCooldown;
                    }
                    else if (a.Act == Act.Call) Calls.Enqueue(e);
                    else Deeds.Enqueue(new Deed { Slot = s.Slot, Gen = s.Gen, Act = a.Act, At = a.At, Yaw = a.Yaw, Tall = a.Tall });
                }
                if (a.Timer >= Ability.Length(a.Act))
                {
                    a.Act = Act.None;
                    gun.Phase = GunPhase.Ready;
                    gun.Timer = 0.4f;
                }
                return;
            }
            if (s.Player || a.Cooldown > 0f || a.Maxim >= 0) return;
            var all = All;

            if (s.Class == SoldierClass.Launcher)
            {
                if (a.Missiles <= 0 || gun.Phase == GunPhase.Firing || math.lengthsq(b.Vel) > 4f) { a.Cooldown = 0.3f; return; }
                a.Cooldown = brain.Rng.NextFloat(0.4f, 0.8f);
                // the nearest enemy vehicle in range that can be seen
                int best = -1;
                float bestD = T.missileRange;
                float3 eye = b.Pos + new float3(0, 1.75f * s.Height, 0);
                for (int i = 0; i < Tanks.Length; i++)
                {
                    var k = Tanks[i];
                    if (k.Wreck || k.Team == s.Team) continue;
                    float away = math.distance(k.Pos, b.Pos);
                    if (away > bestD || away < 12f) continue;
                    if (!Map.Sees(eye, k.Pos + new float3(0, 2.2f, 0))) continue;
                    bestD = away; best = i;
                }
                if (best < 0) return;
                Begin(ref a, Act.Launch, Tanks[best].Pos);
                a.Target = best;
                a.Cooldown = T.missileCooldown;
                return;
            }
            if (s.Class == SoldierClass.Sweeper)
            {
                if (a.Grenades <= 0 || gun.Phase == GunPhase.Firing) return;
                // tanks before anybody
                for (int i = 0; i < Tanks.Length; i++)
                {
                    var k = Tanks[i];
                    if (k.Wreck || k.Flying || k.Team == s.Team) continue;
                    float away = math.distance(k.Pos, b.Pos);
                    if (away > T.grenadeMaxThrow - 4f || away < 6f) continue;
                    if (!Map.Sees(b.Pos + new float3(0, 1.8f, 0), k.Pos + new float3(0, 2f, 0))) continue;
                    if (T.friendlyBlasts && all.CountNear(Slots, k.Pos.xz, 6f, 1, s.Team, true) > 0) continue;
                    float2 on = k.Pos.xz + new float2(math.sin(k.Yaw), math.cos(k.Yaw)) * k.Speed * 1.4f;
                    Begin(ref a, Act.Throw, new float3(on.x, k.Pos.y + 0.4f, on.y));
                    a.Cooldown = brain.Rng.NextFloat(1.4f, 2.2f);
                    return;
                }
                if (brain.Target < 0) return;
                a.Cooldown = brain.Rng.NextFloat(0.6f, 1.2f);
                var t = Slots[brain.Target];
                float d = math.distance(t.Pos, b.Pos);
                if (!t.Alive || t.Gen != brain.TargetGen || d < T.grenadeMinThrow || d > T.grenadeMaxThrow) return;
                // worth it for a few together, or somebody dug in
                bool dugIn = !Map.Sees(b.Pos + new float3(0, 1.72f, 0), t.Pos + new float3(0, t.Kneel ? 0.85f : 1.3f, 0));
                if (all.CountNear(Slots, t.Pos.xz, 8f, 2, s.Team, false) < (dugIn ? 1 : 2)) return;
                if (T.friendlyBlasts && all.CountNear(Slots, t.Pos.xz, T.grenadeEdgeRadius * 0.75f, 1, s.Team, true) > 0) return;
                Begin(ref a, Act.Throw, t.Pos);
                a.Cooldown = brain.Rng.NextFloat(5f, 9f);
            }
        }

        static void Begin(ref Ability a, Act act, float3 at)
        {
            a.Act = act;
            a.Timer = 0f;
            a.Done = false;
            a.At = at;
        }
    }

    /// <summary>Firearms: line up, fire, work the action, reload. Whether a shot lands is the dice described in the tuning.</summary>
    [BurstCompile]
    public partial struct FireJob : IJobEntity
    {
        [ReadOnly] public NativeArray<SlotState> Slots;
        [ReadOnly] public MapView Map;
        [NativeDisableParallelForRestriction] public NativeArray<Maxim> Maxims;
        public float MaximMuzzle;
        public Tuning T;
        public float Dt;
        public NativeQueue<ShotEvent>.ParallelWriter Shots;

        /// <summary>Six hundred a minute, a hundred to the belt, ten seconds to put in another.</summary>
        void Work(in Soldier s, in Body b, in Ability ability, ref Brain brain, ref Anim anim)
        {
            var m = Maxims[ability.Maxim];
            m.Timer -= Dt;
            if (m.Reloading > 0f)
            {
                m.Reloading -= Dt;
                if (m.Reloading <= 0f) { m.Reloading = 0f; m.Ammo = Maxim.Clip; }
            }
            else if (m.Ammo <= 0) m.Reloading = Maxim.Reload;
            else if (!s.Player && ability.Act == Act.None)
            {
                bool on = false;
                if (brain.Target >= 0 && brain.TargetDist <= T.maxRange)
                {
                    float2 f = Slots[brain.Target].Pos.xz - m.Pos.xz;
                    float off = math.atan2(f.x, f.y) - m.Yaw;
                    off -= math.PI * 2f * math.round(off / (math.PI * 2f));
                    on = math.abs(off) < 0.06f;
                }
                if (!on) m.Timer = math.max(m.Timer, 0.25f);
                while (on && m.Timer <= 0f && m.Ammo > 0 && brain.Target >= 0)
                {
                    m.Timer += 60f / Maxim.Rpm;
                    if (!Shoot(s, b, ref brain, ShotKind.Maxim, m.Pivot + new float3(0, 0.05f, 0), MaximMuzzle)) { m.Timer = 0.2f; break; }
                    m.Ammo--;
                }
            }
            Maxims[ability.Maxim] = m;

            bool tall = m.Tall;
            anim.Blend = math.min(1f, anim.Blend + Dt / 0.22f);
            byte clip;
            float time;
            if (ability.Act == Act.Pack) { clip = Clip.Setup; time = anim.Clip == clip ? anim.Time + Dt : 0f; }
            else if (m.Step > 0f)
            {
                clip = m.StepTurn > 0f ? (tall ? Clip.StandStepR : Clip.KneelStepR) : tall ? Clip.StandStepL : Clip.KneelStepL;
                time = m.Step * Maxim.StepTime;
            }
            else if (m.Reloading > 0f) { clip = tall ? Clip.MaximStandReload : Clip.MaximKneelReload; time = anim.Clip == clip ? anim.Time + Dt : 0f; }
            else
            {
                clip = tall ? Clip.MaximStand : Clip.MaximKneel;
                float off = m.Yaw - m.Stance;
                off -= math.PI * 2f * math.round(off / (math.PI * 2f));
                time = math.saturate(off / (Maxim.Planted * 2f) + 0.5f) * (Clip.TwistFrames - 1) / SoldierData.Fps;
            }
            if (clip != anim.Clip)
            {
                bool same = anim.Clip >= Clip.MaximStand && anim.Clip <= Clip.KneelStepR && clip >= Clip.MaximStand && clip <= Clip.KneelStepR;
                anim.Prev = anim.Clip; anim.PrevTime = anim.Time;
                anim.Clip = clip;
                anim.Blend = same ? 1f : 0f; // a step begins and ends just as they were standing
            }
            anim.Time = time;
        }

        void Execute(in Soldier s, in Body b, in Ability ability, ref Brain brain, ref Gun gun, ref Anim anim)
        {
            if (ability.Tank >= 0) return;
            if (ability.Maxim >= 0 && brain.Mounted) { Work(s, b, ability, ref brain, ref anim); return; }
            gun.Timer -= Dt;
            bool stopped = math.lengthsq(b.Vel) < 0.5f;
            bool busy = ability.Act != Act.None;
            float reach = s.Class == SoldierClass.Sweeper ? T.shotgunMaxRange : s.Class == SoldierClass.Gunner ? T.pistolMaxRange : T.maxRange;
            bool aiming = brain.Target >= 0 && stopped && !busy && brain.TargetDist <= reach;

            if (!s.Player)
            {
                switch (gun.Phase)
                {
                    case GunPhase.Firing:
                        if (gun.Timer > 0f) break;
                        if (gun.Ammo <= 0) { gun.Phase = GunPhase.Reloading; gun.Timer = Gun.ReloadTime(s.Class); }
                        // an automatic stays on whoever it's on without stopping to aim again
                        else { gun.Phase = GunPhase.Ready; gun.Timer = Gun.Automatic(s.Class) && brain.Target >= 0 && brain.Target == gun.AimedAt ? gun.Timer : Aim(ref brain); }
                        break;
                    case GunPhase.Reloading:
                        if (gun.Timer > 0f || busy) break;
                        gun.Ammo = Gun.Magazine(s.Class);
                        gun.Phase = GunPhase.Ready;
                        gun.Timer = Aim(ref brain) * (Gun.Automatic(s.Class) && brain.Target >= 0 && brain.Target == gun.AimedAt ? 0.3f : 1f);
                        break;
                    default:
                        if (!aiming) { gun.Timer = math.max(gun.Timer, 0.45f); break; }
                        if (gun.Timer > 0f) break;
                        if (!Shoot(s, b, ref brain, s.Class == SoldierClass.Sweeper ? ShotKind.Shotgun : s.Class == SoldierClass.Gunner ? ShotKind.Pistol : ShotKind.Rifle,
                            b.Pos + new float3(0, (brain.Kneel ? 1.30f : 1.72f) * s.Height, 0), s.Class == SoldierClass.Gunner ? 0.95f : 1.25f)) { gun.Timer = 0.3f; gun.AimedAt = -1; break; }
                        gun.Ammo--;
                        gun.AimedAt = brain.Target;
                        gun.Phase = GunPhase.Firing;
                        // carry the remainder over, so a frame longer than a round apart doesn't slow the rate
                        gun.Timer = Gun.FireTime(s.Class) + math.min(0f, gun.Timer);
                        break;
                }
            }
            else if (gun.Timer <= 0f && gun.Phase == GunPhase.Firing) gun.Phase = GunPhase.Ready;
            else if (gun.Timer <= 0f && gun.Phase == GunPhase.Reloading) { gun.Phase = GunPhase.Ready; gun.Ammo = Gun.Magazine(s.Class); }

            // ---- which pose that looks like ----
            bool pump = s.Class == SoldierClass.Sweeper;
            byte clip;
            if (ability.Act == Act.Throw) clip = Clip.Throw;
            else if (ability.Act == Act.Setup || ability.Act == Act.Pack) clip = Clip.Setup;
            else if (ability.Act == Act.Call) clip = Clip.Call;
            else if (s.Player)
            {
                if (gun.Phase == GunPhase.Firing) clip = brain.Kneel ? (pump ? Clip.KneelPump : Clip.KneelFire) : pump ? Clip.StandPump : Clip.StandFire;
                else if (gun.Phase == GunPhase.Reloading) clip = brain.Kneel ? Clip.KneelReload : Clip.StandReload;
                // the person looking along it keeps it up; only a run brings it down
                else if (math.lengthsq(b.Vel) > 25f) clip = Clip.Run;
                else clip = brain.Kneel ? Clip.KneelAim : Clip.StandAim;
            }
            else if (!stopped) clip = brain.Suppression > 0.25f ? Clip.CrouchRun : Clip.Run;
            else if (gun.Phase == GunPhase.Firing) clip = brain.Kneel ? (pump ? Clip.KneelPump : Clip.KneelFire) : pump ? Clip.StandPump : Clip.StandFire;
            else if (gun.Phase == GunPhase.Reloading) clip = brain.Kneel ? Clip.KneelReload : Clip.StandReload;
            else if (aiming || brain.Target >= 0) clip = brain.Kneel ? Clip.KneelAim : Clip.StandAim;
            else clip = brain.Kneel ? Clip.KneelIdle : Clip.Idle;
            if (s.Class == SoldierClass.Gunner && clip < 10) clip += Clip.PistolBase;
            // one kind of run isn't swapped for the other until it's been properly begun
            if (Clip.IsRun(clip) && Clip.IsRun(anim.Clip) && clip != anim.Clip && anim.Blend < 1f) clip = anim.Clip;

            anim.PrevTime += Dt;
            if (Clip.IsRun(clip))
            {
                // backing away runs it backwards
                float2 face = new float2(math.sin(b.Yaw), math.cos(b.Yaw));
                float way = math.dot(b.Vel, face) < -0.3f ? -1f : 1f;
                anim.Time += Dt * way * math.max(0.35f, math.length(b.Vel) / T.runSpeed);
            }
            else anim.Time += Dt;
            if (clip != anim.Clip)
            {
                anim.Prev = anim.Clip;
                anim.PrevTime = anim.Time;
                bool runs = Clip.IsRun(clip) && Clip.IsRun(anim.Clip);
                anim.Clip = clip;
                if (!runs) anim.Time = Clip.IsRun(clip) || clip == Clip.Idle || clip == Clip.PistolBase ? brain.Rng.NextFloat() : 0f;
                anim.Blend = 0f;
            }
            anim.Blend = math.min(1f, anim.Blend + Dt / (Clip.IsFire(clip) ? 0.05f : 0.22f));
        }

        float Aim(ref Brain brain) => brain.Rng.NextFloat(T.aimMin, T.aimMax) * (1f + brain.Suppression);

        bool Shoot(in Soldier s, in Body b, ref Brain brain, ShotKind kind, float3 from, float ahead)
        {
            var t = Slots[brain.Target];
            if (!t.Alive || t.Gen != brain.TargetGen) return false;
            bool shotgun = kind == ShotKind.Shotgun;
            float dist = math.distance(t.Pos, b.Pos);
            float3 aimDir = math.normalizesafe(t.Pos - b.Pos);
            float3 muzzle = from + aimDir * ahead;

            float chance = kind == ShotKind.Pistol ? Chance(dist, T.pistolCloseRange, T.pistolCloseHit, T.pistolMaxRange, T.pistolMaxRangeHit)
                : shotgun ? Chance(dist, T.shotgunCloseRange, T.shotgunCloseHit, T.shotgunMaxRange, T.shotgunMaxRangeHit)
                : Chance(dist, T.closeRange, T.closeHit, T.maxRange, T.maxRangeHit) * (kind == ShotKind.Maxim ? T.maximHit : 1f);
            // which parts of them are in view from here
            float headAt = (t.Kneel ? 1.45f : 1.87f) * t.Height, bodyAt = (t.Kneel ? 0.92f : 1.3f) * t.Height, legsAt = (t.Kneel ? 0.3f : 0.5f) * t.Height;
            bool seesHead = Map.Sees(from, t.Pos + new float3(0, headAt, 0)), seesBody = Map.Sees(from, t.Pos + new float3(0, bodyAt, 0)), seesLegs = Map.Sees(from, t.Pos + new float3(0, legsAt, 0));
            if (!seesHead && !seesBody && !seesLegs) { brain.Target = -1; return false; }
            if (T.battlefieldModifiers)
            {
                if (t.Kneel) chance *= T.kneelHit;
                if (math.lengthsq(t.Vel) > 1f) chance *= T.movingHit;
                chance *= 1f - 0.5f * brain.Suppression;
            }

            int rounds = shotgun ? T.pellets : 1, struck = 0, limbs = 0;
            float damage = 0f;
            bool head = false;
            byte wanted = 0; // the part a miss was meant for
            for (int i = 0; i < rounds; i++)
            {
                bool lucky = brain.Rng.NextFloat() < chance;
                float roll = brain.Rng.NextFloat();
                byte part = (byte)(roll < T.headChance ? 1 : roll < T.headChance + T.limbChance ? 2 : 0);
                wanted = part;
                // a part that's behind something stops whatever was going to hit it (an arm shows wherever the body does)
                bool open = part == 1 ? seesHead : part == 2 ? seesLegs || seesBody && brain.Rng.NextBool() : seesBody;
                if (!lucky || !open) continue;
                struck++;
                float one = shotgun ? T.pelletDamage : kind == ShotKind.Pistol ? T.pistolDamage : T.damage;
                if (part == 1) { one *= T.headMultiplier; head = true; }
                else if (part == 2) { one *= T.limbMultiplier; limbs++; }
                damage += one;
            }
            byte region = (byte)(head ? 1 : struck > 0 && limbs == struck ? 2 : 0);
            float3 to = t.Pos + new float3(0, struck > 0 ? (region == 1 ? headAt : region == 2 ? legsAt : bodyAt) : wanted == 1 ? headAt : wanted == 2 ? legsAt : bodyAt, 0);
            if (struck == 0)
            {
                // wide, or into whatever they're behind
                to += new float3(brain.Rng.NextFloat(-0.9f, 0.9f), brain.Rng.NextFloat(-0.6f, 0.8f), brain.Rng.NextFloat(-0.9f, 0.9f));
                to = Map.Trace(from, to + aimDir * 12f);
            }
            Shots.Enqueue(new ShotEvent
            {
                Shooter = s.Slot, ShooterGen = s.Gen, Target = brain.Target, TargetGen = brain.TargetGen, From = muzzle, To = to,
                Damage = damage, Region = region, Team = s.Team, Hit = struck > 0, Pellets = (byte)struck, Kind = kind,
            });
            return true;
        }

        static float Chance(float dist, float near, float nearHit, float far, float farHit) =>
            dist > far ? 0f : math.lerp(nearHit, farHit, math.saturate((dist - near) / math.max(0.01f, far - near)));
    }

    /// <summary>The frame's shots, landed one after another.</summary>
    [BurstCompile]
    public struct ApplyShotsJob : IJob
    {
        public NativeQueue<ShotEvent> Shots;
        public NativeList<ShotEvent> Seen; // for tracers and sound
        public NativeList<DeathEvent> Deaths;
        [ReadOnly] public NativeArray<Entity> Entities;
        [ReadOnly] public NativeArray<SlotState> Slots;
        [ReadOnly] public NativeArray<Squad> Squads;
        public ComponentLookup<Health> Healths;
        public ComponentLookup<Brain> Brains;
        [ReadOnly] public ComponentLookup<Anim> Anims;
        public uint Seed;

        public void Execute()
        {
            var rng = new Random(Seed | 1u);
            while (Shots.TryDequeue(out var shot))
            {
                if (shot.Kind != ShotKind.Blast && shot.Kind != ShotKind.Tank) Seen.Add(shot);
                if (shot.Target < 0) continue;
                var st = Slots[shot.Target];
                if (!st.Alive || st.Gen != shot.TargetGen) continue;
                var e = Entities[shot.Target];
                var brain = Brains[e];
                brain.Suppression = math.min(1f, brain.Suppression + (shot.Kind == ShotKind.Blast ? 0.9f : shot.Hit ? 0.5f : 0.22f));
                // a shell on its way: most hear it in time
                if (shot.Kind == ShotKind.Blast && !shot.Hit && rng.NextFloat() < 0.85f) brain.Dive = rng.NextFloat(2.1f, 2.9f);
                Brains[e] = brain;
                if (!shot.Hit) continue;
                var health = Healths[e];
                if (health.Value <= 0f) continue; // somebody else got there first
                health.Value -= shot.Damage;
                Healths[e] = health;
                if (health.Value > 0f) continue;
                Deaths.Add(new DeathEvent
                {
                    Slot = shot.Target, Gen = st.Gen, Pos = st.Pos, Vel = st.Vel, Yaw = st.Yaw, Height = st.Height, Class = st.Class,
                    Dir = math.normalizesafe(shot.To - shot.From, new float3(0, 1, 0)), From = shot.From, Kind = shot.Kind, Power = shot.Power, Big = shot.Big,
                    Anim = Anims[e], Team = st.Team, Region = shot.Region, Captain = Squads[st.Squad].Captain == st.Member,
                    Player = st.Player, Seed = rng.NextFloat(), Carrying = st.Carrying, Killer = shot.Shooter, KillerGen = shot.ShooterGen, KillerTeam = shot.Team,
                });
            }
        }
    }

    /// <summary>What one explosion does to everyone about it.</summary>
    [BurstCompile]
    public struct BlastJob : IJob
    {
        [ReadOnly] public NativeArray<SlotState> Slots;
        [ReadOnly] public Grid Near;
        [ReadOnly] public MapView Map;
        public NativeQueue<ShotEvent> Shots;
        public float3 At;
        public float FullRadius, FullDamage, EdgeRadius, EdgeDamage;
        public byte Team;
        public int Shooter, ShooterGen;
        public bool Friendly, Big, Warning;
        public float Low;

        public void Execute()
        {
            int2 a = Near.At(At.xz - EdgeRadius), b = Near.At(At.xz + EdgeRadius);
            for (int y = a.y; y <= b.y; y++)
            for (int x = a.x; x <= b.x; x++)
            {
                int k = y * Near.W + x;
                for (int i = Near.Start[k]; i < Near.Start[k + 1]; i++)
                {
                    int slot = Near.Items[i];
                    var s = Slots[slot];
                    if (!s.Alive || (!Friendly && s.Team == Team)) continue;
                    float3 chest = s.Pos + new float3(0, s.Kneel ? 0.7f : 1.1f, 0);
                    float d = math.distance(chest, At);
                    if (d > EdgeRadius) continue;
                    float damage = d <= FullRadius ? FullDamage : math.lerp(FullDamage, EdgeDamage, (d - FullRadius) / math.max(0.01f, EdgeRadius - FullRadius));
                    // a trench or a wall between takes most of it
                    if (d > 2.5f && !Map.Sees(At + new float3(0, 0.7f, 0), chest)) damage *= 0.35f;
                    // a bunker's roof keeps most of it out, unless it went off inside
                    bool roofed = (Map.Cover[MapView.Index(s.Pos.xz)] & MapView.Roofed) != 0;
                    if (roofed && (Map.Cover[MapView.Index(At.xz)] & MapView.Roofed) == 0) damage *= 0.25f;
                    else if (Big && d < 3.5f) damage *= 3f;
                    else if (Big && s.Kneel) damage *= Low;
                    Shots.Enqueue(new ShotEvent
                    {
                        Shooter = Shooter, ShooterGen = ShooterGen, Target = slot, TargetGen = s.Gen, From = At, To = chest, Damage = damage, Power = 1f - d / EdgeRadius,
                        Team = Team, Hit = !Warning, Kind = ShotKind.Blast, Big = Big,
                    });
                }
            }
        }
    }
}

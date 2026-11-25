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
    }

    [BurstCompile]
    public partial struct SnapshotJob : IJobEntity
    {
        [NativeDisableParallelForRestriction] public NativeArray<SlotState> Slots;

        void Execute(in Soldier s, in Body b, in Brain brain)
        {
            Slots[s.Slot] = new SlotState
            {
                Pos = b.Pos, Vel = b.Vel, Yaw = b.Yaw, Suppression = brain.Suppression, Gen = s.Gen, Squad = s.Squad,
                Team = s.Team, Member = s.Member, Alive = true, Kneel = brain.Kneel, HasTarget = brain.Target >= 0, Player = s.Player,
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
                if (!s.Alive || (Team != 255 && s.Team != Team)) continue;
                int2 c = Grid.At(s.Pos.xz);
                start[c.y * Grid.W + c.x + 1]++;
            }
            for (int i = 1; i < start.Length; i++) start[i] += start[i - 1];
            var next = new NativeArray<int>(start.Length, Allocator.Temp);
            next.CopyFrom(start);
            for (int i = 0; i < Count; i++)
            {
                var s = Slots[i];
                if (!s.Alive || (Team != 255 && s.Team != Team)) continue;
                int2 c = Grid.At(s.Pos.xz);
                int k = c.y * Grid.W + c.x;
                var items = Grid.Items;
                items[next[k]] = i;
                next[k]++;
            }
        }
    }

    /// <summary>Captains: where the squad is going, and whether it's on the move or holding.</summary>
    [BurstCompile]
    public unsafe struct SquadJob : IJobParallelFor
    {
        public NativeArray<Squad> Squads;
        [ReadOnly] public NativeArray<SlotState> Slots;
        [ReadOnly] public NativeArray<TeamState> Teams;
        [ReadOnly] public MapView Map;
        public float Dt;

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
                var st = Slots[slot];
                if (st.HasTarget) contacts++;
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
            if (math.lengthsq(cap.Vel) > 1f) q.Facing = math.normalize(math.lerp(q.Facing, math.normalize(cap.Vel), math.min(1f, Dt * 3f)) + 1e-4f);

            if (q.PlayerLed)
            {
                q.Mode = q.PlayerHold || (math.lengthsq(cap.Vel) < 0.2f && contacts > 0) ? SquadMode.Hold : SquadMode.Advance;
                q.Goal = q.Pos;
                q.Attacking = true;
                Squads[i] = q;
                return;
            }

            var team = Teams[q.Team];
            float side = q.Team == Team.Green ? 1f : -1f;
            q.Attacking = team.Attacking && alive > 1;
            q.Field = team.Target;
            float2 obj = Map.Objectives[team.Target];
            var r = new Random((uint)(i * 7919 + 17) | 1u);

            if (q.Attacking)
            {
                float a = r.NextFloat(0f, math.PI * 2f);
                q.Goal = obj + new float2(math.cos(a), math.sin(a)) * r.NextFloat(4f, 21f);
            }
            else
            {
                // a line across the approach, a little ahead of what's being held
                bool atMiddle = team.Target == Obj.Middle;
                float ahead = r.NextFloat(-4f, 22f);
                float spread = atMiddle ? 46f : 62f;
                q.Goal = obj + new float2(side * ahead, r.NextFloat(-spread, spread));
            }
            q.Goal = MapView.Clamp(q.Goal);

            q.ModeTimer -= Dt;
            float toGoal = math.distance(q.Pos, q.Goal);
            if (!q.Attacking)
            {
                bool caught = contacts > 0 && toGoal < 45f;
                q.Mode = toGoal < 5f || caught || alive == 1 && contacts > 0 ? SquadMode.Hold : SquadMode.Advance;
            }
            else if (contacts == 0)
            {
                q.Mode = toGoal < 4f ? SquadMode.Hold : SquadMode.Advance;
                q.ModeTimer = math.min(q.ModeTimer, 0.5f);
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
                    q.ModeTimer = q.Rng.NextFloat(2f, 4f) * (0.7f + q.Nerve * 0.7f);
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
        [ReadOnly] public Grid Green, Grey;
        [ReadOnly] public MapView Map;
        public float Dt, Range;

        public const float EyeStand = 1.82f, EyeKneel = 1.40f;

        void Execute(in Soldier s, in Body b, ref Brain brain)
        {
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

            var grid = s.Team == Team.Green ? Grey : Green;
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
                            float d = math.distancesq(Slots[slot].Pos.xz, p);
                            if (d > Range * Range) continue;
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
            float3 eye = b.Pos + new float3(0, EyeStand, 0);
            for (int i = 0; i < bestI.Length; i++)
            {
                var t = Slots[bestI[i]];
                if (!Map.Sees(eye, t.Pos + new float3(0, t.Kneel ? EyeKneel : EyeStand, 0))) continue;
                brain.Target = bestI[i];
                brain.TargetGen = t.Gen;
                brain.TargetDist = math.distance(t.Pos, b.Pos);
                break;
            }
        }
    }

    /// <summary>Legs: where each soldier goes, keeping to the squad, out of each other's way, and into cover.</summary>
    [BurstCompile]
    public partial struct MoveJob : IJobEntity
    {
        [ReadOnly] public NativeArray<SlotState> Slots;
        [ReadOnly] public NativeArray<Squad> Squads;
        [ReadOnly] public Grid Near;
        [ReadOnly] public MapView Map;
        public Tuning T;
        public float Dt;

        static float2 Formation(int member, float2 facing, bool player)
        {
            float2 side = new float2(facing.y, -facing.x);
            // a squad led by the player fans out beside and ahead of them, where they can be seen
            if (player) return side * ((member & 1) == 0 ? -1f : 1f) * (2.6f + member * 0.8f) + facing * (3.4f - member * 0.5f);
            // a loose wedge behind the captain
            float2 o;
            switch (member)
            {
                case 0: o = new float2(-2.2f, -2.0f); break;
                case 1: o = new float2(2.2f, -2.0f); break;
                case 2: o = new float2(-4.4f, -3.6f); break;
                case 3: o = new float2(4.4f, -3.6f); break;
                default: o = new float2(0f, -4.2f); break;
            }
            float2 right = new float2(facing.y, -facing.x);
            return right * o.x + facing * o.y;
        }

        unsafe void Execute(in Soldier s, ref Body b, ref Brain brain)
        {
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
                dest = q.Pos + Formation(s.Member, q.Facing, q.PlayerLed);
                if (math.distance(p, dest) > 24f && !q.PlayerLed) flow = true;
            }

            // close enough to shoot point blank: stop and fight, whatever the captain says
            bool pointBlank = brain.Target >= 0 && brain.TargetDist < (q.Mode == SquadMode.Hold ? T.maxRange : 18f);
            bool hold = q.Mode == SquadMode.Hold && math.distance(p, q.Pos) < 22f || pointBlank;
            if (hold)
            {
                if (!brain.Holding)
                {
                    brain.Holding = true;
                    brain.HoldPos = FindCover(p, s.Team, ref brain.Rng, pointBlank ? 5 : 9);
                }
                dest = brain.HoldPos;
                flow = false;
            }
            else brain.Holding = false;

            float2 to = dest - p;
            float dist = math.length(to);
            float2 dir = math.normalizesafe(to);
            if (flow)
            {
                float2 f = Map.FlowDir(q.Field, p);
                if (math.lengthsq(f) > 0.1f)
                {
                    // keep to the squad's own lane, so an army doesn't funnel down one path
                    float lane = math.clamp((q.Lane - p.y) * 0.05f, -0.9f, 0.9f) * math.saturate((dist - 34f) / 40f);
                    dir = math.normalizesafe(f + new float2(0, lane));
                }
            }

            float speed = brain.Target >= 0 || brain.Suppression > 0.3f ? T.cautiousSpeed : T.runSpeed;
            if (!captain && !hold && dist > 8f) speed *= 1.15f; // catch up
            float arrive = hold ? 0.35f : captain ? 1.2f : 0.8f;
            float2 want = dist > arrive ? dir * speed * math.saturate(dist / 1.5f + 0.25f) : float2.zero;

            // keep apart
            float2 push = 0;
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
                    if (l >= 1.3f) continue;
                    push += (l > 0.01f ? d / l : math.float2(math.sin(s.Slot), math.cos(s.Slot))) * (1.3f - l);
                }
            }
            want += push * 2.2f;
            if (math.lengthsq(want) < 0.12f) want = 0;

            // slopes and wire slow you down
            float h = b.Pos.y;
            float2 wd = math.normalizesafe(want);
            float slope = (Map.Height(p + wd * 0.6f) - h) / 0.6f;
            float slow = 1f / (1f + math.max(0f, slope) * 0.9f + Map.Rough[MapView.Index(p)] * 0.35f);

            b.Vel = math.lerp(b.Vel, want * slow, 1f - math.exp(-9f * Dt));
            if (math.lengthsq(b.Vel) < 0.01f) b.Vel = 0;
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
                if (hold && brain.Stuck > 0.6f) { brain.HoldPos = p; brain.Stuck = 0; }
            }
            else brain.Stuck = math.max(0f, brain.Stuck - Dt);
            next = MapView.Clamp(next);
            b.Pos = new float3(next.x, Map.Height(next), next.y);

            // face the enemy when standing to fight, else the way they're going
            float2 face = math.lengthsq(b.Vel) > 0.4f ? b.Vel : new float2(s.Team == Team.Green ? 1 : -1, 0);
            float wantPitch = 0f;
            bool stopped = math.lengthsq(b.Vel) < 0.5f;
            if (brain.Target >= 0 && stopped)
            {
                var t = Slots[brain.Target];
                face = t.Pos.xz - next;
                wantPitch = -math.atan2(t.Pos.y - b.Pos.y, math.max(1f, math.length(face)));
            }
            float yaw = math.atan2(face.x, face.y);
            float diff = yaw - b.Yaw;
            diff -= math.PI * 2f * math.round(diff / (math.PI * 2f));
            b.Yaw += diff * (1f - math.exp(-8f * Dt));
            b.Pitch = math.lerp(b.Pitch, wantPitch, 1f - math.exp(-8f * Dt));

            // kneel when stopped to fight, unless the ground in front is in the way
            brain.KneelTimer -= Dt;
            if (!stopped) { brain.Kneel = false; brain.KneelTimer = 0f; }
            else if (brain.KneelTimer <= 0f)
            {
                brain.KneelTimer = brain.Rng.NextFloat(0.6f, 1.2f);
                if (brain.Target >= 0)
                {
                    var t = Slots[brain.Target];
                    brain.Kneel = Map.Sees(b.Pos + new float3(0, 1.28f, 0), t.Pos + new float3(0, 1.45f, 0));
                }
                else brain.Kneel = hold && (brain.Suppression > 0.15f || (s.Slot % 3) != 0);
            }
        }

        /// <summary>The nearest spot sheltered from the enemy's side, within a few metres; where they stand if there's none.</summary>
        float2 FindCover(float2 p, byte team, ref Random rng, int radius)
        {
            byte bit = team == Team.Green ? MapView.FromPlusX : MapView.FromMinusX;
            int2 c0 = MapView.Cell(p);
            float best = 1e9f;
            float2 at = p + rng.NextFloat2(-0.8f, 0.8f);
            for (int y = -radius; y <= radius; y++)
            for (int x = -radius; x <= radius; x++)
            {
                int2 c = c0 + new int2(x, y);
                if (c.x < 1 || c.y < 1 || c.x >= MapView.W - 1 || c.y >= MapView.H - 1) continue;
                int i = MapView.Index(c);
                if ((Map.Cover[i] & bit) == 0 || Map.Blocked[i] != 0) continue;
                float d = math.lengthsq(new float2(x, y)) * rng.NextFloat(0.6f, 1.6f);
                if (d >= best) continue;
                best = d;
                at = MapView.Centre(c) + rng.NextFloat2(-0.3f, 0.3f);
            }
            return at;
        }
    }

    /// <summary>Rifles: line up, fire, work the lever, reload. Whether a shot lands is the dice described in the tuning.</summary>
    [BurstCompile]
    public partial struct FireJob : IJobEntity
    {
        [ReadOnly] public NativeArray<SlotState> Slots;
        [ReadOnly] public MapView Map;
        public Tuning T;
        public float Dt;
        public NativeQueue<ShotEvent>.ParallelWriter Shots;

        void Execute(in Soldier s, in Body b, ref Brain brain, ref Gun gun, ref Anim anim)
        {
            gun.Timer -= Dt;
            bool stopped = math.lengthsq(b.Vel) < 0.5f;
            bool aiming = brain.Target >= 0 && stopped;

            if (!s.Player)
            {
                switch (gun.Phase)
                {
                    case GunPhase.Firing:
                        if (gun.Timer > 0f) break;
                        if (gun.Ammo <= 0) { gun.Phase = GunPhase.Reloading; gun.Timer = Gun.ReloadTime; }
                        else { gun.Phase = GunPhase.Ready; gun.Timer = Aim(ref brain); }
                        break;
                    case GunPhase.Reloading:
                        if (gun.Timer > 0f) break;
                        gun.Ammo = Gun.Magazine;
                        gun.Phase = GunPhase.Ready;
                        gun.Timer = Aim(ref brain);
                        break;
                    default:
                        if (!aiming) { gun.Timer = math.max(gun.Timer, 0.45f); break; }
                        if (gun.Timer > 0f) break;
                        Shoot(s, b, ref brain);
                        gun.Ammo--;
                        gun.Phase = GunPhase.Firing;
                        gun.Timer = Gun.FireTime;
                        break;
                }
            }
            else if (gun.Timer <= 0f && gun.Phase == GunPhase.Firing) gun.Phase = GunPhase.Ready;
            else if (gun.Timer <= 0f && gun.Phase == GunPhase.Reloading) { gun.Phase = GunPhase.Ready; gun.Ammo = Gun.Magazine; }

            // ---- which pose that looks like ----
            byte clip;
            if (!stopped && !(s.Player && gun.Phase != GunPhase.Ready))
                clip = brain.Suppression > 0.25f || brain.Kneel ? Clip.CrouchRun : Clip.Run;
            else if (gun.Phase == GunPhase.Firing) clip = brain.Kneel ? Clip.KneelFire : Clip.StandFire;
            else if (gun.Phase == GunPhase.Reloading) clip = brain.Kneel ? Clip.KneelReload : Clip.StandReload;
            else if (aiming || s.Player) clip = brain.Kneel ? Clip.KneelAim : Clip.StandAim;
            else clip = brain.Kneel ? Clip.KneelIdle : Clip.Idle;

            anim.PrevTime += Dt;
            if (clip == Clip.Run || clip == Clip.CrouchRun) anim.Time += Dt * math.length(b.Vel) / T.runSpeed;
            else anim.Time += Dt;
            if (clip != anim.Clip)
            {
                anim.Prev = anim.Clip;
                anim.PrevTime = anim.Time;
                anim.Clip = clip;
                anim.Time = clip == Clip.Run || clip == Clip.CrouchRun || clip == Clip.Idle ? brain.Rng.NextFloat() : 0f;
                anim.Blend = 0f;
            }
            float blendTime = clip == Clip.StandFire || clip == Clip.KneelFire ? 0.05f : 0.2f;
            anim.Blend = math.min(1f, anim.Blend + Dt / blendTime);
        }

        float Aim(ref Brain brain) => brain.Rng.NextFloat(T.aimMin, T.aimMax) * (1f + brain.Suppression);

        void Shoot(in Soldier s, in Body b, ref Brain brain)
        {
            var t = Slots[brain.Target];
            if (!t.Alive || t.Gen != brain.TargetGen) return;
            float dist = math.distance(t.Pos, b.Pos);
            float3 from = b.Pos + new float3(0, brain.Kneel ? 1.30f : 1.72f, 0);
            float3 aimDir = math.normalizesafe(t.Pos - b.Pos);
            float3 muzzle = from + aimDir * 0.95f;

            float chance = dist > T.maxRange ? 0f
                : math.lerp(T.closeHit, T.maxRangeHit, math.saturate((dist - T.closeRange) / math.max(0.01f, T.maxRange - T.closeRange)));
            bool chestHidden = !Map.Sees(from, t.Pos + new float3(0, t.Kneel ? 0.85f : 1.3f, 0));
            if (T.battlefieldModifiers)
            {
                if (chestHidden) chance *= T.coverHit;
                if (t.Kneel) chance *= T.kneelHit;
                if (math.lengthsq(t.Vel) > 1f) chance *= T.movingHit;
                chance *= 1f - 0.5f * brain.Suppression;
            }
            bool hit = brain.Rng.NextFloat() < chance;
            byte region = 0;
            float damage = T.damage;
            float roll = brain.Rng.NextFloat();
            if (roll < T.headChance) region = 1;
            else if (roll < T.headChance + T.limbChance) { region = 2; damage *= T.limbMultiplier; }

            float top = t.Kneel ? 1.45f : 1.87f;
            float3 to = t.Pos + new float3(0, region == 1 ? top : region == 2 ? top * 0.3f : top * 0.72f, 0);
            if (!hit)
            {
                // wide: a miss goes past, or into the dirt by them
                to += new float3(brain.Rng.NextFloat(-1.6f, 1.6f), brain.Rng.NextFloat(-1.1f, 1.3f), brain.Rng.NextFloat(-1.6f, 1.6f));
                to += aimDir * brain.Rng.NextFloat(0f, 14f);
            }
            Shots.Enqueue(new ShotEvent
            {
                Shooter = s.Slot, ShooterGen = s.Gen, Target = brain.Target, TargetGen = brain.TargetGen, From = muzzle, To = to,
                Damage = hit ? damage : 0f, Region = region, Team = s.Team, Hit = hit,
            });
        }
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
                Seen.Add(shot);
                if (shot.Target < 0) continue;
                var st = Slots[shot.Target];
                if (!st.Alive || st.Gen != shot.TargetGen) continue;
                var e = Entities[shot.Target];
                var brain = Brains[e];
                brain.Suppression = math.min(1f, brain.Suppression + (shot.Hit ? 0.5f : 0.22f));
                Brains[e] = brain;
                if (!shot.Hit) continue;
                var health = Healths[e];
                if (health.Value <= 0f) continue; // somebody else got there first
                health.Value -= shot.Damage;
                Healths[e] = health;
                if (health.Value > 0f) continue;
                Deaths.Add(new DeathEvent
                {
                    Slot = shot.Target, Gen = st.Gen, Pos = st.Pos, Vel = st.Vel, Yaw = st.Yaw, Dir = math.normalizesafe(shot.To - shot.From),
                    Anim = Anims[e], Team = st.Team, Region = shot.Region, Captain = Squads[st.Squad].Captain == st.Member,
                    Player = st.Player, Seed = rng.NextFloat(),
                });
            }
        }
    }
}

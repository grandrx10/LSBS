using System.Diagnostics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using Random = Unity.Mathematics.Random;

namespace LSBS
{
    /// <summary>
    /// The battle. Each frame: everyone's public state is copied into slot arrays, captains decide, soldiers look, move and
    /// shoot in parallel jobs that only read those arrays, and the shots are then landed in order.
    /// </summary>
    [DisableAutoCreation]
    public unsafe partial class BattleSystem : SystemBase
    {
        public const int MaxSoldiers = 32768;

        public Tuning T = Tuning.Default;
        public MapView Map;
        public NativeArray<SlotState> Slots;
        public NativeArray<Entity> SlotEntities;
        public NativeArray<Squad> Squads;
        public NativeArray<TeamState> Teams;
        public NativeList<ShotEvent> ShotsSeen;
        public NativeList<DeathEvent> Deaths;
        public NativeList<BlastEvent> Blasts;
        public NativeQueue<ShotEvent> Shots;
        public NativeQueue<Throw> Throws, Calls;
        public NativeList<Grenade> Grenades;
        public NativeList<Strike> Strikes;
        /// <summary>Each side's guns, where they stand (none, these days).</summary>
        public float3[][] Guns = NoGuns();
        static float3[][] NoGuns() { var g = new float3[Team.Max][]; for (int i = 0; i < g.Length; i++) g[i] = new float3[0]; return g; }

        /// <summary>How many sides there are, and which of them the person playing commands.</summary>
        public int TeamCount = 2;
        public byte PlayerTeam;
        /// <summary>Who holds each objective (255 nobody), who's taking it, and how far they've got (0..1).</summary>
        public byte[] Owner = new byte[1];
        public float[] Capture = new float[1];
        public byte[] Capturing = new byte[1];
        /// <summary>Seconds each side has held the objective, all told.</summary>
        public readonly float[] Held = new float[Team.Max];
        /// <summary>How many of each side are on the objective.</summary>
        public readonly int[] OnPoint = new int[Team.Max];
        public byte Winner = 255;
        public float WaveTimer;
        public int WaveNumber;
        public float Clock;
        public float SimMs;
        public bool Running;
        /// <summary>One past the highest slot in use.</summary>
        public int HighSlot;
        public int PlayerSquad = -1;
        /// <summary>No artillery now; kept so what once called it still stands.</summary>
        public int GunsFor(int team) => 0;
        // for the log
        public int GrenadesThrown, StrikesCalled, ShellsLanded, BlastKills, ShotgunKills;

        NativeArray<int> _presence;
        NativeList<int> _freeSlots, _freeSquads;
        NativeArray<int> _gen;
        NativeArray<byte> _calling;
        Grid _all, _near;
        EntityArchetype _archetype;
        EntityQuery _soldiers;
        Random _rng;
        float _think, _merge;
        int _thinkTurn;
        int _squadHigh = MaxSoldiers;
        int[] _assigned = new int[Team.Max];
        static readonly ProfilerMarker Marker = new("LSBS.Battle");

        protected override void OnCreate()
        {
            Slots = new NativeArray<SlotState>(MaxSoldiers, Allocator.Persistent);
            SlotEntities = new NativeArray<Entity>(MaxSoldiers, Allocator.Persistent);
            _gen = new NativeArray<int>(MaxSoldiers, Allocator.Persistent);
            _calling = new NativeArray<byte>(MaxSoldiers, Allocator.Persistent);
            Squads = new NativeArray<Squad>(MaxSoldiers, Allocator.Persistent);
            Teams = new NativeArray<TeamState>(Team.Max, Allocator.Persistent);
            ShotsSeen = new NativeList<ShotEvent>(4096, Allocator.Persistent);
            Deaths = new NativeList<DeathEvent>(1024, Allocator.Persistent);
            Blasts = new NativeList<BlastEvent>(256, Allocator.Persistent);
            Shots = new NativeQueue<ShotEvent>(Allocator.Persistent);
            Throws = new NativeQueue<Throw>(Allocator.Persistent);
            Calls = new NativeQueue<Throw>(Allocator.Persistent);
            Grenades = new NativeList<Grenade>(256, Allocator.Persistent);
            Strikes = new NativeList<Strike>(64, Allocator.Persistent);
            MaximCreate();
            TankCreate();
            OrdersCreate();
            MissilesCreate();
            AirCreate();
            _presence = new NativeArray<int>(Team.Max * 2, Allocator.Persistent);
            _freeSlots = new NativeList<int>(MaxSoldiers, Allocator.Persistent);
            _freeSquads = new NativeList<int>(MaxSoldiers, Allocator.Persistent);
            _all = Grid.Create(8f, MaxSoldiers);
            _near = Grid.Create(2f, MaxSoldiers);
            _archetype = EntityManager.CreateArchetype(typeof(Soldier), typeof(Body), typeof(Health), typeof(Brain), typeof(Ability), typeof(Gun), typeof(Anim));
            _soldiers = GetEntityQuery(typeof(Soldier));
            _rng = new Random(12345);
            ResetFront();
        }

        protected override void OnDestroy()
        {
            CompleteDependency();
            MaximDestroy();
            TankDestroy();
            OrdersDestroy();
            MissilesDestroy();
            AirDestroy();
            _presence.Dispose();
            Slots.Dispose(); SlotEntities.Dispose(); _gen.Dispose(); _calling.Dispose(); Squads.Dispose(); Teams.Dispose();
            ShotsSeen.Dispose(); Deaths.Dispose(); Blasts.Dispose(); Shots.Dispose(); Throws.Dispose(); Calls.Dispose(); Grenades.Dispose(); Strikes.Dispose();
            _freeSlots.Dispose(); _freeSquads.Dispose();
            _all.Dispose(); _near.Dispose();
        }

        // ------------------------------------------------------------------ starting and stopping

        void ResetFront()
        {
            int n = Front.Count;
            Owner = new byte[n]; Capture = new float[n]; Capturing = new byte[n];
            for (int i = 0; i < n; i++) { Owner[i] = 255; Capturing[i] = 255; }
            System.Array.Clear(Held, 0, Held.Length);
            System.Array.Clear(OnPoint, 0, OnPoint.Length);
            _assigned = new int[n * Team.Max];
        }

        void ClearAll()
        {
            CompleteDependency();
            EntityManager.DestroyEntity(_soldiers);
            for (int i = 0; i < MaxSoldiers; i++) { Slots[i] = default; Squads[i] = default; _calling[i] = 0; }
            _freeSlots.Clear(); _freeSquads.Clear();
            for (int i = MaxSoldiers - 1; i >= 0; i--) { _freeSlots.Add(i); _freeSquads.Add(i); }
            Shots.Clear(); Throws.Clear(); Calls.Clear(); Grenades.Clear(); Strikes.Clear(); Blasts.Clear(); Deaths.Clear(); ShotsSeen.Clear();
            Maxims.Clear(); Deeds.Clear(); TankClear(); OrdersClear(); MissilesClear(); AirClear();
            HighSlot = 0;
            _squadHigh = MaxSoldiers;
            PlayerSquad = -1;
            for (int t = 0; t < Team.Max; t++) Teams[t] = new TeamState { Attacking = true };
            ResetFront();
            Winner = 255;
            Clock = 0f;
        }

        public void Stop()
        {
            ClearAll();
            Running = false;
        }

        /// <summary>A new battle: so many sides, each bringing what it's bought, more of the same every wave.</summary>
        public void StartBattle(int teams, byte player, Buy[] first, Buy[] wave)
        {
            TeamCount = math.clamp(teams, 2, Team.Max);
            PlayerTeam = player;
            ClearAll();
            for (int t = 0; t < Team.Max; t++) { StartBuy[t] = t < first.Length ? first[t] : default; WaveBuy[t] = t < wave.Length ? wave[t] : default; }
            WaveTimer = T.waveInterval;
            WaveNumber = 0;
            GrenadesThrown = StrikesCalled = ShellsLanded = BlastKills = ShotgunKills = 0;
            CommanderReset();
            for (byte t = 0; t < TeamCount; t++) Deliver(t, StartBuy[t], true);
            Running = true;
        }

        /// <summary>
        /// A squad of soldiers stood about a place; its index, or -1. A squad of the person playing's side waits there for
        /// orders; anybody else's goes where its captain thinks best.
        /// </summary>
        public int SpawnSquad(byte team, float2 at, int size, bool crew)
        {
            size = math.min(size, math.min(Squad.Size, _freeSlots.Length));
            if (size <= 0 || _freeSquads.Length == 0) return -1;
            var entities = EntityManager.CreateEntity(_archetype, size, Allocator.Temp);
            int qi = _freeSquads[_freeSquads.Length - 1];
            _freeSquads.Length--;
            float2 face = math.normalizesafe(Map.Objectives[Front.Centre] - at, new float2(1, 0));
            float yaw = math.atan2(face.x, face.y);
            var q = new Squad
            {
                Active = true, Team = team, Captain = 0, Facing = face, Nerve = _rng.NextFloat(),
                Rng = new Random(_rng.NextUInt() | 1u), Mode = SquadMode.Advance, Spot = math.clamp(_rng.NextFloat2Direction() * math.sqrt(_rng.NextFloat()), -1f, 1f),
                Alive = size, Crew = crew, Pos = at, Field = -1,
            };
            Assign(ref q, -1);
            if (team == PlayerTeam && !crew)
            {
                // the commander's own: they hold where they're put until told otherwise
                q.Ordered = true; q.OrderAt = at; q.OrderVia = at; q.Field = -1; q.Objective = Front.Home(team);
            }
            for (int m = 0; m < Squad.Size; m++)
            {
                q.Members[m] = -1;
                if (m >= size) continue;
                int slot = _freeSlots[_freeSlots.Length - 1];
                _freeSlots.Length--;
                HighSlot = math.max(HighSlot, slot + 1);
                _gen[slot]++;
                _calling[slot] = 0;
                q.Members[m] = slot;
                float2 p = at;
                for (int tries = 0; tries < 12; tries++)
                {
                    p = MapView.Clamp(at + _rng.NextFloat2Direction() * _rng.NextFloat(0.5f, 5f + tries));
                    if (!Map.IsBlocked(p)) break;
                }
                var cls = crew ? SoldierClass.Infantry : Squad.ClassOf(m);
                // mostly near the middle, a few tall or short
                float height = 1f + (_rng.NextFloat(-1f, 1f) + _rng.NextFloat(-1f, 1f)) * 0.5f * T.heightSpread * 1.4f;
                height = math.clamp(height, 1f - T.heightSpread, 1f + T.heightSpread);
                var e = entities[m];
                SlotEntities[slot] = e;
                EntityManager.SetComponentData(e, new Soldier { Slot = slot, Gen = _gen[slot], Squad = qi, Team = team, Member = (byte)m, Class = cls, Height = height });
                var body = new Body { Pos = new float3(p.x, Map.Height(p), p.y), Yaw = yaw };
                EntityManager.SetComponentData(e, body);
                EntityManager.SetComponentData(e, new Health { Value = 100f });
                EntityManager.SetComponentData(e, new Brain { Rng = new Random(_rng.NextUInt() | 1u), Target = -1, SenseTimer = _rng.NextFloat(0f, 0.4f) });
                EntityManager.SetComponentData(e, new Ability { Maxim = -1, Want = -1, Tank = -1, Target = -1, MaximAmmo = Maxim.Clip, Grenades = cls == SoldierClass.Sweeper ? T.grenades : 0, Missiles = cls == SoldierClass.Launcher ? T.missiles : 0, Cooldown = _rng.NextFloat(1f, 4f) });
                EntityManager.SetComponentData(e, new Gun { Ammo = Gun.Magazine(cls), Timer = _rng.NextFloat(0.2f, 1.2f), AimedAt = -1 });
                EntityManager.SetComponentData(e, new Anim { Clip = Clip.Idle, Time = _rng.NextFloat(), Blend = 1f });
                Slots[slot] = new SlotState { Pos = body.Pos, Yaw = body.Yaw, Gen = _gen[slot], Squad = qi, Team = team, Member = (byte)m, Class = cls, Height = height, Alive = true };
            }
            Squads[qi] = q;
            entities.Dispose();
            return qi;
        }

        void Wave()
        {
            WaveTimer -= SystemAPI.Time.DeltaTime;
            if (WaveTimer > 0f) return;
            WaveTimer = T.waveInterval;
            WaveNumber++;
            for (byte t = 0; t < TeamCount; t++) if (Teams[t].Alive > 0 || TanksOf(t) > 0 || WaveNumber > 0) Deliver(t, WaveBuy[t], false);
        }

        // ------------------------------------------------------------------ the frame

        protected override void OnUpdate()
        {
            if (!Running || !Map.Ground.IsCreated) return;
            var clock = Stopwatch.StartNew();
            using (Marker.Auto()) Step(math.min(SystemAPI.Time.DeltaTime, 0.05f));
            SimMs = math.lerp(SimMs, (float)clock.Elapsed.TotalMilliseconds, 0.1f);
        }

        void Step(float dt)
        {
            Clock += dt;
            Wave();
            Dependency.Complete();
            Deaths.Clear();
            ShotsSeen.Clear();
            Blasts.Clear();
            ApplyOrders();
            Commanders(dt);
            using (Prof.Command.Auto()) Command(dt);
            using (Prof.Maxims.Auto()) MaximTick(dt);
            using (Prof.Tanks.Auto()) TankTick(dt);
            AirTick(dt);

            var snap = new SnapshotJob { Slots = Slots }.ScheduleParallel(Dependency);
            var g1 = new GridJob { Slots = Slots, Count = HighSlot, Team = 255, Grid = _all }.Schedule(snap);
            var g3 = new GridJob { Slots = Slots, Count = HighSlot, Team = 255, Grid = _near }.Schedule(snap);
            var squads = new SquadJob { Squads = Squads, Slots = Slots, Map = Map, Dt = dt, CaptureRadius = T.captureRadius }.Schedule(math.min(MaxSoldiers, _squadHigh), 32, snap);
            var sense = new SenseJob { Slots = Slots, All = _all, Map = Map, Dt = dt, Range = T.maxRange }
                .ScheduleParallel(JobHandle.CombineDependencies(g1, squads));
            var ability = new AbilityJob { Slots = Slots, All = _all, Map = Map, Tanks = _vehicleStates.AsArray(), Calling = _calling, Strikes = Strikes.AsArray(), T = T, Dt = dt, Throws = Throws.AsParallelWriter(), Calls = Calls.AsParallelWriter(), Deeds = Deeds.AsParallelWriter(), Launches = Launches.AsParallelWriter() }
                .ScheduleParallel(sense);
            var move = new MoveJob { OrderFlow = OrderFlow, Tanks = _tankStates.AsArray(), Maxims = Maxims.AsArray(), MaximBack = MaximBack, Slots = Slots, Squads = Squads, Near = _near, Map = Map, T = T, Dt = dt }
                .ScheduleParallel(JobHandle.CombineDependencies(ability, g3));
            var fire = new FireJob { Maxims = Maxims.AsArray(), MaximMuzzle = MaximMuzzle, Slots = Slots, Map = Map, T = T, Dt = dt, Shots = Shots.AsParallelWriter() }.ScheduleParallel(move);
            using (Prof.Jobs.Auto()) fire.Complete();
            DoDeeds();
            Explosives(dt);
            MissileTick(dt);
            Dependency = new ApplyShotsJob
            {
                Shots = Shots, Seen = ShotsSeen, Deaths = Deaths, Entities = SlotEntities, Slots = Slots, Squads = Squads,
                Healths = GetComponentLookup<Health>(), Brains = GetComponentLookup<Brain>(), Anims = GetComponentLookup<Anim>(true),
                Seed = _rng.NextUInt(),
            }.Schedule(fire);
            Dependency.Complete();
            Bury();
            _merge -= dt;
            if (_merge <= 0f) { _merge = 1f; using (Prof.Second.Auto()) MergeSquads(); }
        }

        /// <summary>For the renderer: fill its lists of who to draw.</summary>
        public void Cull(CullJob job)
        {
            Dependency = job.ScheduleParallel(Dependency);
            Dependency.Complete();
        }

        // ------------------------------------------------------------------ grenades and guns

        public static float3 Lob(float3 from, float3 at, float seconds)
        {
            float3 v = (at - from) / seconds;
            v.y += 0.5f * 9.81f * seconds;
            return v;
        }

        void Explosives(float dt)
        {
            while (Throws.TryDequeue(out var t))
            {
                GrenadesThrown++;
                float flight = math.clamp(math.distance(t.From, t.At) / 17f, 0.7f, 2.2f);
                float3 wild = new float3(_rng.NextFloat(-1.6f, 1.6f), 0, _rng.NextFloat(-1.6f, 1.6f));
                Grenades.Add(new Grenade { Pos = t.From, Vel = Lob(t.From, t.At + wild, flight), Fuse = T.grenadeFuse, Team = t.Team, Shooter = t.Shooter, ShooterGen = t.ShooterGen, Spin = _rng.NextFloat(4f, 11f) });
            }
            for (int i = Grenades.Length - 1; i >= 0; i--)
            {
                var g = Grenades[i];
                g.Vel.y -= 9.81f * dt;
                g.Pos += g.Vel * dt;
                float h = Map.Height(g.Pos.xz) + 0.07f;
                if (g.Pos.y < h)
                {
                    g.Pos.y = h;
                    g.Vel = new float3(g.Vel.x * 0.45f, math.abs(g.Vel.y) * 0.3f, g.Vel.z * 0.45f);
                    g.Spin *= 0.5f;
                }
                else if (Map.IsBlocked(g.Pos.xz) && g.Pos.y < Map.PropTop[MapView.Index(g.Pos.xz)]) { g.Pos -= g.Vel * dt; g.Vel.xz *= -0.3f; }
                g.Fuse -= dt;
                if (g.Fuse > 0f) { Grenades[i] = g; continue; }
                Grenades.RemoveAtSwapBack(i);
                Explode(g.Pos, T.grenadeFullRadius, T.grenadeFullDamage, T.grenadeEdgeRadius, T.grenadeEdgeDamage, g.Team, false, false, g.Shooter, g.ShooterGen);
            }

            while (Calls.TryDequeue(out var c))
            {
                // every gun that side has is already firing for somebody
                var ts = Teams[c.Team];
                if (ts.GunsBusy >= GunsFor(c.Team) || _calling[c.Shooter] != 0) continue;
                ts.GunsBusy++;
                Teams[c.Team] = ts;
                _calling[c.Shooter] = 1;
                StrikesCalled++;
                var s = new Strike { At = c.At, Team = c.Team, Caller = c.Shooter, CallerGen = c.ShooterGen };
                for (int i = 0; i < Strike.Shells; i++)
                {
                    s.When[i] = _rng.NextFloat(Strike.Flight + 0.3f, math.max(Strike.Flight + 1f, T.strikeSeconds));
                    float2 o = _rng.NextFloat2Direction() * math.sqrt(_rng.NextFloat()) * T.strikeRadius;
                    s.X[i] = o.x; s.Z[i] = o.y;
                    s.Gun[i] = _rng.NextInt(1000);
                }
                Strikes.Add(s);
            }
            for (int i = Strikes.Length - 1; i >= 0; i--)
            {
                var s = Strikes[i];
                s.Age += dt;
                for (int k = 0; k < Strike.Shells; k++)
                {
                    float2 p = MapView.Clamp(s.At.xz + new float2(s.X[k], s.Z[k]));
                    var at = new float3(p.x, Map.Height(p), p.y);
                    if ((s.Fired & (1 << k)) == 0 && s.Age >= s.When[k] - Strike.Flight)
                    {
                        s.Fired |= 1 << k;
                        var guns = Guns[s.Team];
                        if (guns.Length > 0) Blasts.Add(new BlastEvent { At = guns[s.Gun[k] % guns.Length], Report = true, Team = s.Team });
                    }
                    if ((s.Warned & (1 << k)) == 0 && s.Age >= s.When[k] - 2.2f)
                    {
                        s.Warned |= 1 << k;
                        Blasts.Add(new BlastEvent { At = at, Whistle = true, Team = s.Team });
                        Explode(at, 0, 0, T.shellEdgeRadius, 0, s.Team, true, true);
                    }
                    if ((s.Landed & (1 << k)) == 0 && s.Age >= s.When[k])
                    {
                        s.Landed |= 1 << k;
                        ShellsLanded++;
                        Explode(at, T.shellFullRadius, T.shellFullDamage, T.shellEdgeRadius, T.shellEdgeDamage, s.Team, true, false, s.Caller, s.CallerGen);
                    }
                }
                if (!s.Done && s.Age >= T.strikeSeconds + 0.1f && (s.Landed & ((1 << Strike.Shells) - 1)) == (1 << Strike.Shells) - 1)
                {
                    s.Done = true;
                    if (Slots[s.Caller].Gen == s.CallerGen) _calling[s.Caller] = 0;
                }
                if (s.Done && s.Age >= T.strikeSeconds + T.gunRest)
                {
                    Strikes.RemoveAtSwapBack(i);
                    var ts = Teams[s.Team];
                    ts.GunsBusy--;
                    Teams[s.Team] = ts;
                }
                else Strikes[i] = s;
            }
        }

        public bool IsCalling(int slot) => _calling[slot] != 0;

        void Explode(float3 at, float fullR, float fullD, float edgeR, float edgeD, byte team, bool big, bool warning = false, int shooter = -1, int shooterGen = 0, bool hurtTanks = true)
        {
            if (!warning) { BreakMaxims(at, fullR); if (hurtTanks) HurtTanks(at, big); }
            if (!warning) Blasts.Add(new BlastEvent { At = at, Radius = edgeR, Big = big, Team = team });
            new BlastJob
            {
                Slots = Slots, Near = _near, Map = Map, Shots = Shots, At = at, FullRadius = fullR, FullDamage = fullD, EdgeRadius = edgeR, EdgeDamage = edgeD,
                Team = team, Friendly = T.friendlyBlasts || warning, Big = big, Warning = warning, Low = T.lowBlast, Shooter = shooter, ShooterGen = shooterGen,
            }.Run();
        }

        // ------------------------------------------------------------------ the dead, and what's left of their squads

        void Bury()
        {
            for (int i = 0; i < Deaths.Length; i++)
            {
                var d = Deaths[i];
                var st = Slots[d.Slot];
                EntityManager.DestroyEntity(SlotEntities[d.Slot]);
                var q = Squads[st.Squad];
                q.Members[st.Member] = -1;
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
                Slots[d.Slot] = st;
                _freeSlots.Add(d.Slot);
                if (d.Kind == ShotKind.Blast) BlastKills++;
                if (d.Kind == ShotKind.Shotgun) ShotgunKills++;

                var mine = Teams[st.Team];
                mine.Losses++; mine.TotalLosses++;
                Teams[st.Team] = mine;
                if (d.KillerTeam < Team.Max && d.KillerTeam != st.Team)
                {
                    var theirs = Teams[d.KillerTeam];
                    theirs.Kills++; theirs.TotalKills++;
                    Teams[d.KillerTeam] = theirs;
                }
            }
        }

        /// <summary>What's left of a broken squad falls in with another one nearby that has room.</summary>
        void MergeSquads()
        {
            int high = 0;
            for (int i = 0; i < MaxSoldiers; i++)
            {
                var q = Squads[i];
                if (!q.Active) continue;
                high = i + 1;
                if (q.Alive > 3 || q.Alive == 0 || q.Crew || i == PlayerSquad || q.PlayerLed || q.Inside) continue;
                int best = -1;
                float bestD = 40f * 40f;
                for (int j = 0; j < _squadHigh; j++)
                {
                    if (j == i) continue;
                    var o = Squads[j];
                    if (!o.Active || o.Crew || o.Inside || o.Team != q.Team || o.Alive == 0 || o.Alive + q.Alive > Squad.Size || j == PlayerSquad) continue;
                    // the commander's squads only join up with others sent the same way
                    if (q.Ordered != o.Ordered || q.Ordered && math.distancesq(q.OrderAt, o.OrderAt) > 30f * 30f) continue;
                    float d = math.distancesq(o.Pos, q.Pos);
                    if (d < bestD) { bestD = d; best = j; }
                }
                if (best < 0) continue;
                var into = Squads[best];
                for (int m = 0; m < Squad.Size; m++)
                {
                    int slot = q.Members[m];
                    if (slot < 0) continue;
                    for (int k = 0; k < Squad.Size; k++)
                    {
                        if (into.Members[k] >= 0) continue;
                        into.Members[k] = slot;
                        q.Members[m] = -1;
                        var e = SlotEntities[slot];
                        var s = EntityManager.GetComponentData<Soldier>(e);
                        s.Squad = best; s.Member = (byte)k;
                        EntityManager.SetComponentData(e, s);
                        var st = Slots[slot];
                        st.Squad = best; st.Member = (byte)k;
                        Slots[slot] = st;
                        break;
                    }
                }
                into.Alive += q.Alive;
                Squads[best] = into;
                Unassign(q);
                q.Active = false;
                Squads[i] = q;
                _freeSquads.Add(i);
            }
            _squadHigh = math.min(MaxSoldiers, math.max(high + 2048, 2048));
        }

        // ------------------------------------------------------------------ captains and the objective

        void Unassign(in Squad q)
        {
            int k = q.Objective * Team.Max + q.Team;
            if (k >= 0 && k < _assigned.Length && _assigned[k] > 0) _assigned[k]--;
        }

        /// <summary>A captain left to himself makes for the objective: to take it, or to hold it if his side has it.</summary>
        void Assign(ref Squad q, int current)
        {
            if (current >= 0) Unassign(q);
            q.Objective = Front.Centre;
            bool ours = Owner.Length > 0 && Owner[Front.Centre] == q.Team;
            // even holding it, the bolder go out to meet whoever's coming
            q.Attacking = !ours || q.Nerve > 0.8f;
            int k = q.Objective * Team.Max + q.Team;
            _assigned[k]++;
            q.Crowd = _assigned[k];
        }

        void Command(float dt)
        {
            float fade = math.exp(-dt / math.max(1f, T.memory));
            new PresenceJob { Slots = Slots, Objectives = Map.Objectives, Out = _presence, Count = HighSlot, Radius2 = T.captureRadius * T.captureRadius }.Run();
            for (int t = 0; t < Team.Max; t++)
            {
                var ts = Teams[t];
                OnPoint[t] = _presence[t];
                ts.Alive = _presence[Team.Max + t];
                ts.Kills *= fade;
                ts.Losses *= fade;
                ts.StanceTime += dt;
                ts.Held = Owner[Front.Centre] == t ? 1 : 0;
                Teams[t] = ts;
            }
            bool changed = Objective(dt);

            _think -= dt;
            if (_think > 0f && !changed) return;
            _think = 0.5f;
            // a few captains think again each time, all of them when the objective's changed hands
            _thinkTurn++;
            int high = math.min(_squadHigh, MaxSoldiers);
            for (int i = 0; i < high; i++)
            {
                var q = Squads[i];
                if (!q.Active || q.PlayerLed || q.Ordered || q.Inside) continue;
                if (!changed && (i + _thinkTurn) % 8 != 0) continue;
                Assign(ref q, q.Objective);
                Squads[i] = q;
            }
        }

        /// <summary>
        /// The objective: one side alone on it takes it in time; whoever holds it, alone, adds to their time. Two sides on it
        /// and nothing moves. True when it's changed hands.
        /// </summary>
        bool Objective(float dt)
        {
            if (Winner != 255) return false;
            const int o = Front.Centre;
            int sides = 0, lead = -1, most = 0;
            for (int t = 0; t < TeamCount; t++)
            {
                if (OnPoint[t] == 0) continue;
                sides++;
                if (OnPoint[t] > most) { most = OnPoint[t]; lead = t; }
            }
            bool changed = false;
            if (sides == 1 && lead != Owner[o])
            {
                if (Capturing[o] != lead) { Capturing[o] = (byte)lead; Capture[o] = 0f; }
                float strength = math.saturate(most / 15f);
                Capture[o] += dt / math.max(0.5f, T.captureSeconds) * (0.35f + 0.65f * strength);
                if (Capture[o] >= 1f)
                {
                    Owner[o] = (byte)lead;
                    Capture[o] = 0f; Capturing[o] = 255;
                    changed = true;
                }
            }
            else if (sides == 0 && Capture[o] > 0f)
            {
                Capture[o] = math.max(0f, Capture[o] - dt / T.captureSeconds * 0.5f);
                if (Capture[o] <= 0f) Capturing[o] = 255;
            }
            // holding it: the clock runs while nobody else is on it
            byte owner = Owner[o];
            if (owner < Team.Max && (sides == 0 || sides == 1 && lead == owner))
            {
                Held[owner] += dt;
                if (Held[owner] >= T.holdToWin) Winner = owner;
            }
            return changed;
        }
    }
}

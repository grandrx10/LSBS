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
        /// <summary>Each side's guns, where they stand.</summary>
        public float3[][] Guns = { new float3[0], new float3[0] };
        /// <summary>Who holds each objective (255 nobody), and how far the other side has got with it (0..1).</summary>
        public readonly byte[] Owner = new byte[Front.Count];
        public readonly float[] Capture = new float[Front.Count];
        public readonly byte[] Capturing = new byte[Front.Count];
        public byte Winner = 255;
        public float WaveTimer;
        public int WaveNumber;
        public float SimMs;
        public bool Running;
        /// <summary>One past the highest slot in use.</summary>
        public int HighSlot;
        /// <summary>The most each side may have on the field.</summary>
        public readonly int[] Caps = { 500, 500 };
        public int PlayerSquad = -1;
        /// <summary>Set while the player waits to join the next wave; the side they'll fight for.</summary>
        public int PlayerDeploy = -1;
        public int PlayerDeployedSlot = -1;
        // for the log
        public int GrenadesThrown, StrikesCalled, ShellsLanded, BlastKills, ShotgunKills;

        NativeList<int> _freeSlots, _freeSquads;
        NativeArray<int> _gen;
        NativeArray<byte> _calling;
        Grid _green, _grey, _near;
        EntityArchetype _archetype;
        EntityQuery _soldiers;
        Random _rng;
        float _think, _merge;
        int _thinkTurn;
        int _squadHigh = MaxSoldiers;
        readonly int[] _present = new int[Front.Count * 2]; // objective * 2 + team
        readonly int[] _assigned = new int[Front.Count * 2];
        static readonly ProfilerMarker Marker = new("LSBS.Battle");

        protected override void OnCreate()
        {
            Slots = new NativeArray<SlotState>(MaxSoldiers, Allocator.Persistent);
            SlotEntities = new NativeArray<Entity>(MaxSoldiers, Allocator.Persistent);
            _gen = new NativeArray<int>(MaxSoldiers, Allocator.Persistent);
            _calling = new NativeArray<byte>(MaxSoldiers, Allocator.Persistent);
            Squads = new NativeArray<Squad>(MaxSoldiers, Allocator.Persistent);
            Teams = new NativeArray<TeamState>(2, Allocator.Persistent);
            ShotsSeen = new NativeList<ShotEvent>(4096, Allocator.Persistent);
            Deaths = new NativeList<DeathEvent>(1024, Allocator.Persistent);
            Blasts = new NativeList<BlastEvent>(256, Allocator.Persistent);
            Shots = new NativeQueue<ShotEvent>(Allocator.Persistent);
            Throws = new NativeQueue<Throw>(Allocator.Persistent);
            Calls = new NativeQueue<Throw>(Allocator.Persistent);
            Grenades = new NativeList<Grenade>(256, Allocator.Persistent);
            Strikes = new NativeList<Strike>(64, Allocator.Persistent);
            _freeSlots = new NativeList<int>(MaxSoldiers, Allocator.Persistent);
            _freeSquads = new NativeList<int>(MaxSoldiers, Allocator.Persistent);
            _green = Grid.Create(8f, MaxSoldiers);
            _grey = Grid.Create(8f, MaxSoldiers);
            _near = Grid.Create(2f, MaxSoldiers);
            _archetype = EntityManager.CreateArchetype(typeof(Soldier), typeof(Body), typeof(Health), typeof(Brain), typeof(Ability), typeof(Gun), typeof(Anim));
            _soldiers = GetEntityQuery(typeof(Soldier));
            _rng = new Random(12345);
            ResetFront();
        }

        protected override void OnDestroy()
        {
            CompleteDependency();
            Slots.Dispose(); SlotEntities.Dispose(); _gen.Dispose(); _calling.Dispose(); Squads.Dispose(); Teams.Dispose();
            ShotsSeen.Dispose(); Deaths.Dispose(); Blasts.Dispose(); Shots.Dispose(); Throws.Dispose(); Calls.Dispose(); Grenades.Dispose(); Strikes.Dispose();
            _freeSlots.Dispose(); _freeSquads.Dispose();
            _green.Dispose(); _grey.Dispose(); _near.Dispose();
        }

        // ------------------------------------------------------------------ starting and reinforcing

        void ResetFront()
        {
            for (int i = 0; i < Front.Count; i++) { Owner[i] = 255; Capture[i] = 0f; Capturing[i] = 255; }
            Owner[Front.GreenBase] = Team.Green;
            Owner[Front.GreyBase] = Team.Grey;
        }

        public void Stop()
        {
            CompleteDependency();
            EntityManager.DestroyEntity(_soldiers);
            for (int i = 0; i < MaxSoldiers; i++) { Slots[i] = default; Squads[i] = default; }
            Shots.Clear(); Throws.Clear(); Calls.Clear(); Grenades.Clear(); Strikes.Clear(); Blasts.Clear(); Deaths.Clear(); ShotsSeen.Clear();
            HighSlot = 0;
            PlayerSquad = -1; PlayerDeploy = -1; PlayerDeployedSlot = -1;
            ResetFront();
            Winner = 255;
            Running = false;
        }

        public void StartBattle(int green, int grey)
        {
            Caps[0] = green; Caps[1] = grey;
            CompleteDependency();
            EntityManager.DestroyEntity(_soldiers);
            for (int i = 0; i < MaxSoldiers; i++)
            {
                Slots[i] = default;
                Squads[i] = default;
                _calling[i] = 0;
            }
            _freeSlots.Clear(); _freeSquads.Clear();
            for (int i = MaxSoldiers - 1; i >= 0; i--) { _freeSlots.Add(i); _freeSquads.Add(i); }
            Shots.Clear(); Throws.Clear(); Calls.Clear(); Grenades.Clear(); Strikes.Clear(); Blasts.Clear();
            HighSlot = 0;
            Teams[0] = new TeamState { Attacking = true };
            Teams[1] = new TeamState { Attacking = true };
            ResetFront();
            Winner = 255;
            WaveTimer = T.waveInterval;
            WaveNumber = 0;
            _squadHigh = MaxSoldiers;
            PlayerSquad = -1; PlayerDeployedSlot = -1;
            GrenadesThrown = StrikesCalled = ShellsLanded = BlastKills = ShotgunKills = 0;
            System.Array.Clear(_assigned, 0, _assigned.Length);
            // the guns come ready one by one, not all at once
            for (byte t = 0; t < 2; t++)
            {
                for (int i = 0; i < T.gunsPerTeam; i++)
                    Strikes.Add(new Strike { Team = t, Done = true, CallerGen = -1, Fired = ~0, Landed = ~0, Warned = ~0, Age = T.strikeSeconds + T.gunRest - math.lerp(35f, T.gunRest + T.strikeSeconds, (i + 0.5f) / T.gunsPerTeam) });
                var ts = Teams[t];
                ts.GunsBusy = T.gunsPerTeam;
                Teams[t] = ts;
            }
            Spawn(Team.Green, green);
            Spawn(Team.Grey, grey);
            Running = true;
        }

        void Spawn(byte team, int count)
        {
            count = math.min(count, _freeSlots.Length);
            if (count <= 0) return;
            var entities = EntityManager.CreateEntity(_archetype, count, Allocator.Temp);
            float side = team == Team.Green ? -1f : 1f;
            float2 home = Map.Objectives[Front.Home(team)];
            int squads = (count + Squad.Size - 1) / Squad.Size;
            for (int sq = 0; sq < squads; sq++)
            {
                int qi = _freeSquads[_freeSquads.Length - 1];
                _freeSquads.Length--;
                float across = math.lerp(-1f, 1f, (sq + 0.5f) / squads);
                var q = new Squad
                {
                    Active = true, Team = team, Captain = 0, Facing = new float2(-side, 0), Nerve = _rng.NextFloat(),
                    Rng = new Random(_rng.NextUInt() | 1u), Mode = SquadMode.Advance, Spot = math.clamp(_rng.NextFloat2Direction() * math.sqrt(_rng.NextFloat()), -1f, 1f),
                    Alive = math.min(Squad.Size, count - sq * Squad.Size),
                };
                float2 at = home + new float2(side * _rng.NextFloat(-6f, 20f), across * (MapView.HalfH - 20f) * 0.85f + _rng.NextFloat(-3f, 3f));
                q.Pos = at;
                Assign(ref q, -1);
                for (int m = 0; m < Squad.Size; m++)
                {
                    q.Members[m] = -1;
                    int n = sq * Squad.Size + m;
                    if (n >= count) continue;
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
                    float roll = _rng.NextFloat();
                    var cls = roll < T.sweepers ? SoldierClass.Sweeper : roll < T.sweepers + T.communicators ? SoldierClass.Communicator : SoldierClass.Infantry;
                    // mostly near the middle, a few tall or short
                    float height = 1f + (_rng.NextFloat(-1f, 1f) + _rng.NextFloat(-1f, 1f)) * 0.5f * T.heightSpread * 1.4f;
                    height = math.clamp(height, 1f - T.heightSpread, 1f + T.heightSpread);
                    var e = entities[n];
                    SlotEntities[slot] = e;
                    EntityManager.SetComponentData(e, new Soldier { Slot = slot, Gen = _gen[slot], Squad = qi, Team = team, Member = (byte)m, Class = cls, Height = height });
                    var body = new Body { Pos = new float3(p.x, Map.Height(p), p.y), Yaw = -side * math.PI * 0.5f };
                    EntityManager.SetComponentData(e, body);
                    EntityManager.SetComponentData(e, new Health { Value = 100f });
                    EntityManager.SetComponentData(e, new Brain { Rng = new Random(_rng.NextUInt() | 1u), Target = -1, SenseTimer = _rng.NextFloat(0f, 0.4f) });
                    EntityManager.SetComponentData(e, new Ability { Grenades = cls == SoldierClass.Sweeper ? T.grenades : 0, Cooldown = cls == SoldierClass.Communicator ? _rng.NextFloat(5f, T.strikeCooldown) : _rng.NextFloat(1f, 4f) });
                    EntityManager.SetComponentData(e, new Gun { Ammo = Gun.Magazine(cls), Timer = _rng.NextFloat(0.2f, 1.2f) });
                    EntityManager.SetComponentData(e, new Anim { Clip = Clip.Idle, Time = _rng.NextFloat(), Blend = 1f });
                    Slots[slot] = new SlotState { Pos = body.Pos, Yaw = body.Yaw, Gen = _gen[slot], Squad = qi, Team = team, Member = (byte)m, Class = cls, Height = height, Alive = true };
                }
                // the player came in with this wave: the first squad is theirs
                if (PlayerDeploy == team && sq == 0 && count >= Squad.Size)
                {
                    PlayerDeploy = -1;
                    PlayerDeployedSlot = q.Members[0];
                }
                Squads[qi] = q;
            }
            entities.Dispose();
        }

        void Wave()
        {
            WaveTimer -= SystemAPI.Time.DeltaTime;
            if (WaveTimer > 0f) return;
            WaveTimer = T.waveInterval;
            WaveNumber++;
            for (byte t = 0; t < 2; t++)
            {
                int n = math.min(T.waveSize, Caps[t] - Teams[t].Alive);
                n -= n % Squad.Size;
                // always room for a squad led by the player
                if (PlayerDeploy == t) n = math.max(n, Squad.Size);
                Spawn(t, n);
            }
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
            Wave();
            Dependency.Complete();
            Deaths.Clear();
            ShotsSeen.Clear();
            Blasts.Clear();
            Command(dt);

            var snap = new SnapshotJob { Slots = Slots }.ScheduleParallel(Dependency);
            var g1 = new GridJob { Slots = Slots, Count = HighSlot, Team = Team.Green, Grid = _green }.Schedule(snap);
            var g2 = new GridJob { Slots = Slots, Count = HighSlot, Team = Team.Grey, Grid = _grey }.Schedule(snap);
            var g3 = new GridJob { Slots = Slots, Count = HighSlot, Team = 255, Grid = _near }.Schedule(snap);
            var squads = new SquadJob { Squads = Squads, Slots = Slots, Map = Map, Dt = dt, CaptureRadius = T.captureRadius }.Schedule(math.min(MaxSoldiers, _squadHigh), 32, snap);
            var sense = new SenseJob { Slots = Slots, Green = _green, Grey = _grey, Map = Map, Dt = dt, Range = T.maxRange }
                .ScheduleParallel(JobHandle.CombineDependencies(g1, g2, squads));
            var ability = new AbilityJob { Slots = Slots, Green = _green, Grey = _grey, Map = Map, Calling = _calling, Strikes = Strikes.AsArray(), T = T, Dt = dt, Throws = Throws.AsParallelWriter(), Calls = Calls.AsParallelWriter() }
                .ScheduleParallel(sense);
            var move = new MoveJob { Slots = Slots, Squads = Squads, Near = _near, Map = Map, T = T, Dt = dt }
                .ScheduleParallel(JobHandle.CombineDependencies(ability, g3));
            var fire = new FireJob { Slots = Slots, Map = Map, T = T, Dt = dt, Shots = Shots.AsParallelWriter() }.ScheduleParallel(move);
            fire.Complete();
            Explosives(dt);
            Dependency = new ApplyShotsJob
            {
                Shots = Shots, Seen = ShotsSeen, Deaths = Deaths, Entities = SlotEntities, Slots = Slots, Squads = Squads,
                Healths = GetComponentLookup<Health>(), Brains = GetComponentLookup<Brain>(), Anims = GetComponentLookup<Anim>(true),
                Seed = _rng.NextUInt(),
            }.Schedule(fire);
            Dependency.Complete();
            Bury();
            _merge -= dt;
            if (_merge <= 0f) { _merge = 1f; MergeSquads(); Posts(); }
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
                if (ts.GunsBusy >= T.gunsPerTeam || _calling[c.Shooter] != 0) continue;
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

        void Explode(float3 at, float fullR, float fullD, float edgeR, float edgeD, byte team, bool big, bool warning = false, int shooter = -1, int shooterGen = 0)
        {
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
                var theirs = Teams[1 - st.Team];
                theirs.Kills++; theirs.TotalKills++;
                Teams[1 - st.Team] = theirs;
            }
        }

        // ------------------------------------------------------------------ bunkers

        public MapGenerator.BunkerInfo[] Bunkers = new MapGenerator.BunkerInfo[0];
        (int slot, int gen)[] _posts = new (int, int)[0];
        const int MostPosts = 12;

        /// <summary>Who's staying to man what. Whoever holds one keeps it till they fall, or the war's moved well on.</summary>
        void Posts()
        {
            if (_posts.Length != Bunkers.Length * MostPosts) _posts = new (int, int)[Bunkers.Length * MostPosts];
            int cap = math.clamp(T.bunkerCap, 0, MostPosts);
            for (int b = 0; b < Bunkers.Length; b++)
            {
                var k = Bunkers[b];
                byte side = k.Face > 0f ? Team.Green : Team.Grey; // it looks one way
                bool enemies = (side == Team.Green ? _grey : _green).CountNear(Slots, k.At, 150f, 1) > 0;
                for (int i = 0; i < MostPosts; i++)
                {
                    var (slot, gen) = _posts[b * MostPosts + i];
                    if (gen == 0) continue;
                    bool here = Slots[slot].Alive && Slots[slot].Gen == gen && Slots[slot].Team == side && !Slots[slot].Player;
                    if (here && (enemies || _rng.NextFloat() < 0.97f)) continue;
                    _posts[b * MostPosts + i] = default;
                    if (!here) continue;
                    var brain = EntityManager.GetComponentData<Brain>(SlotEntities[slot]);
                    brain.OnPost = false;
                    brain.Holding = false;
                    EntityManager.SetComponentData(SlotEntities[slot], brain);
                }
            }
            for (int slot = 0; slot < HighSlot; slot++)
            {
                var s = Slots[slot];
                if (!s.Alive || s.Player || s.Squad == PlayerSquad) continue;
                var q = Squads[s.Squad];
                if (q.Captain == s.Member || q.PlayerLed) continue;
                // squads holding leave men in them; squads pushing on, now and then one
                float chance = q.Attacking ? T.bunkerStay * 0.06f : T.bunkerStay;
                float reach = q.Attacking ? 12f : 32f;
                if (_rng.NextFloat() >= chance) continue;
                for (int b = 0; b < Bunkers.Length; b++)
                {
                    var k = Bunkers[b];
                    if ((k.Face > 0f ? Team.Green : Team.Grey) != s.Team || math.distancesq(k.At, s.Pos.xz) > reach * reach) continue;
                    int free = -1;
                    for (int i = 0; i < cap && free < 0; i++) if (_posts[b * MostPosts + i].gen == 0) free = i;
                    if (free < 0) continue;
                    var e = SlotEntities[slot];
                    var brain = EntityManager.GetComponentData<Brain>(e);
                    if (brain.OnPost) break;
                    _posts[b * MostPosts + free] = (slot, s.Gen);
                    brain.OnPost = true;
                    // along the slit first, then behind
                    int across = free % 5;
                    float z = across == 0 ? 0f : ((across & 1) == 1 ? -1f : 1f) * ((across + 1) / 2) * 1.1f;
                    brain.Post = k.At + new float2((free < 5 ? 1.0f : -0.6f) * k.Face, z);
                    EntityManager.SetComponentData(e, brain);
                    break;
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
                if (q.Alive > 3 || q.Alive == 0 || i == PlayerSquad || q.PlayerLed) continue;
                int best = -1;
                float bestD = 40f * 40f;
                for (int j = 0; j < _squadHigh; j++)
                {
                    if (j == i) continue;
                    var o = Squads[j];
                    if (!o.Active || o.Team != q.Team || o.Alive == 0 || o.Alive + q.Alive > Squad.Size || j == PlayerSquad) continue;
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

        // ------------------------------------------------------------------ commanders and objectives

        /// <summary>Whether a side may take this one: it's next to one they hold.</summary>
        public bool CanTake(int objective, int team)
        {
            if (Owner[objective] == team) return false;
            foreach (int n in Front.Next[objective]) if (Owner[n] == team) return true;
            return false;
        }

        bool OnTheLine(int objective, int team)
        {
            if (Owner[objective] != team) return false;
            foreach (int n in Front.Next[objective]) if (Owner[n] != team) return true;
            return false;
        }

        void Unassign(in Squad q)
        {
            int k = q.Objective * 2 + q.Team;
            if (_assigned[k] > 0) _assigned[k]--;
        }

        /// <summary>A captain picks something to attack or to hold: near, and not already swarming with their own.</summary>
        void Assign(ref Squad q, int current)
        {
            if (current >= 0) Unassign(q);
            var ts = Teams[q.Team];
            int total = 0;
            for (int o = 0; o < Front.Count; o++) total += _assigned[o * 2 + q.Team];
            float share = ts.Attacking ? T.pushShare : T.holdShare;
            bool attack = 1f - q.Nerve < share;
            int best = -1;
            float bestScore = float.MaxValue;
            for (int pass = 0; pass < 2 && best < 0; pass++)
            {
                bool want = pass == 0 ? attack : !attack;
                for (int o = 0; o < Front.Count; o++)
                {
                    if (want ? !CanTake(o, q.Team) : !OnTheLine(o, q.Team)) continue;
                    float walk = math.min(600f, Map.Dist[o * MapView.W * MapView.H + MapView.Index(q.Pos)]);
                    float crowd = _assigned[o * 2 + q.Team] / math.max(1f, total);
                    float score = walk + crowd * 260f - (o == current ? 45f : 0f);
                    // somebody's taking it off us
                    if (!want && Capturing[o] == 1 - q.Team && Capture[o] > 0.1f) score -= 70f;
                    // the last objective before the war's won draws people on
                    if (want && o == Front.Home(1 - q.Team)) score -= 50f;
                    if (score < bestScore) { bestScore = score; best = o; }
                }
                if (best >= 0) attack = want;
            }
            q.Attacking = attack;
            q.Objective = best >= 0 ? best : Front.Home(q.Team);
            _assigned[q.Objective * 2 + q.Team]++;
            q.Crowd = _assigned[q.Objective * 2 + q.Team];
        }

        void Command(float dt)
        {
            float fade = math.exp(-dt / math.max(1f, T.memory));
            System.Array.Clear(_present, 0, _present.Length);
            int a0 = 0, a1 = 0;
            float r2 = T.captureRadius * T.captureRadius;
            for (int i = 0; i < HighSlot; i++)
            {
                var s = Slots[i];
                if (!s.Alive) continue;
                if (s.Team == 0) a0++; else a1++;
                for (int o = 0; o < Front.Count; o++)
                    if (math.distancesq(s.Pos.xz, Map.Objectives[o]) < r2) { _present[o * 2 + s.Team]++; break; }
            }
            for (byte t = 0; t < 2; t++)
            {
                var ts = Teams[t];
                ts.Alive = t == 0 ? a0 : a1;
                ts.Kills *= fade;
                ts.Losses *= fade;
                ts.StanceTime += dt;
                ts.Held = 0;
                for (int o = 0; o < Front.Count; o++) if (Owner[o] == t) ts.Held++;
                Teams[t] = ts;
            }
            bool changed = Objectives(dt);

            _think -= dt;
            if (_think > 0f && !changed) return;
            _think = 0.5f;
            for (byte t = 0; t < 2; t++)
            {
                var ts = Teams[t];
                float cap = math.max(20f, Caps[t]);
                float edge = (ts.Kills - ts.Losses) / cap;
                int enemy = Teams[1 - t].Alive;
                bool was = ts.Attacking;
                if (ts.StanceTime > 6f)
                {
                    if (ts.Attacking)
                    {
                        // too many lost: dig in
                        if (edge < -T.defendEdge || ts.Alive < enemy * 0.6f) ts.Attacking = false;
                    }
                    else if (edge > T.attackEdge || ts.Alive > enemy * 1.25f || ts.StanceTime > T.maxDefend && ts.Alive > cap * 0.4f)
                        ts.Attacking = true;
                }
                if (was != ts.Attacking) ts.StanceTime = 0f;
                Teams[t] = ts;
            }
            // a few captains think again each time, all of them when the map's changed
            _thinkTurn++;
            int high = math.min(_squadHigh, MaxSoldiers);
            for (int i = 0; i < high; i++)
            {
                var q = Squads[i];
                if (!q.Active || q.PlayerLed) continue;
                if (!changed && (i + _thinkTurn) % 8 != 0) continue;
                Assign(ref q, q.Objective);
                Squads[i] = q;
            }
        }

        /// <summary>True when one has changed hands.</summary>
        bool Objectives(float dt)
        {
            if (Winner != 255) return false;
            bool changed = false;
            for (int o = 0; o < Front.Count; o++)
            {
                int green = _present[o * 2], grey = _present[o * 2 + 1];
                byte lead = green > grey ? Team.Green : grey > green ? Team.Grey : (byte)255;
                if (lead != 255 && lead != Owner[o] && !CanTake(o, lead)) lead = 255;
                bool contested = lead != 255 && math.min(green, grey) * 2 > math.max(green, grey);

                if (lead != 255 && lead != Owner[o] && !contested)
                {
                    if (Capturing[o] != lead) { Capturing[o] = lead; Capture[o] = 0f; }
                    float strength = math.saturate(math.abs(green - grey) / 15f);
                    Capture[o] += dt / math.max(0.5f, T.captureSeconds) * (0.35f + 0.65f * strength);
                    if (Capture[o] >= 1f)
                    {
                        Owner[o] = lead;
                        Capture[o] = 0f; Capturing[o] = 255;
                        changed = true;
                        if (o == Front.Home(1 - lead)) Winner = lead;
                    }
                }
                else if (!contested && Capture[o] > 0f)
                {
                    Capture[o] = math.max(0f, Capture[o] - dt / T.captureSeconds * 0.5f);
                    if (Capture[o] <= 0f) Capturing[o] = 255;
                }
            }
            return changed;
        }
    }
}

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
        public NativeQueue<ShotEvent> Shots;
        /// <summary>Who holds each objective (255 nobody), and how far the other side has got with it (0..1).</summary>
        public readonly byte[] Owner = { Team.Green, 255, Team.Grey };
        public readonly float[] Capture = new float[3];
        public readonly byte[] Capturing = { 255, 255, 255 };
        public byte Winner = 255;
        public float WaveTimer;
        public int WaveNumber;
        public float SimMs;
        public bool Running;
        /// <summary>One past the highest slot in use.</summary>
        public int HighSlot;
        public int PlayerSquad = -1;
        /// <summary>Set while the player waits to join the next wave; the side they'll fight for.</summary>
        public int PlayerDeploy = -1;
        public int PlayerDeployedSlot = -1;

        NativeList<int> _freeSlots, _freeSquads;
        NativeArray<int> _gen;
        Grid _green, _grey, _near;
        EntityArchetype _archetype;
        EntityQuery _soldiers;
        Random _rng;
        float _think, _merge;
        static readonly ProfilerMarker Marker = new("LSBS.Battle");

        protected override void OnCreate()
        {
            Slots = new NativeArray<SlotState>(MaxSoldiers, Allocator.Persistent);
            SlotEntities = new NativeArray<Entity>(MaxSoldiers, Allocator.Persistent);
            _gen = new NativeArray<int>(MaxSoldiers, Allocator.Persistent);
            Squads = new NativeArray<Squad>(MaxSoldiers, Allocator.Persistent);
            Teams = new NativeArray<TeamState>(2, Allocator.Persistent);
            ShotsSeen = new NativeList<ShotEvent>(4096, Allocator.Persistent);
            Deaths = new NativeList<DeathEvent>(1024, Allocator.Persistent);
            Shots = new NativeQueue<ShotEvent>(Allocator.Persistent);
            _freeSlots = new NativeList<int>(MaxSoldiers, Allocator.Persistent);
            _freeSquads = new NativeList<int>(MaxSoldiers, Allocator.Persistent);
            for (int i = MaxSoldiers - 1; i >= 0; i--) { _freeSlots.Add(i); _freeSquads.Add(i); }
            _green = Grid.Create(8f, MaxSoldiers);
            _grey = Grid.Create(8f, MaxSoldiers);
            _near = Grid.Create(2f, MaxSoldiers);
            _archetype = EntityManager.CreateArchetype(typeof(Soldier), typeof(Body), typeof(Health), typeof(Brain), typeof(Gun), typeof(Anim));
            _soldiers = GetEntityQuery(typeof(Soldier));
            _rng = new Random(12345);
        }

        protected override void OnDestroy()
        {
            CompleteDependency();
            Slots.Dispose(); SlotEntities.Dispose(); _gen.Dispose(); Squads.Dispose(); Teams.Dispose();
            ShotsSeen.Dispose(); Deaths.Dispose(); Shots.Dispose(); _freeSlots.Dispose(); _freeSquads.Dispose();
            _green.Dispose(); _grey.Dispose(); _near.Dispose();
        }

        // ------------------------------------------------------------------ starting and reinforcing

        public void StartBattle(int perSide)
        {
            CompleteDependency();
            EntityManager.DestroyEntity(_soldiers);
            for (int i = 0; i < MaxSoldiers; i++)
            {
                Slots[i] = default;
                Squads[i] = default;
            }
            _freeSlots.Clear(); _freeSquads.Clear();
            for (int i = MaxSoldiers - 1; i >= 0; i--) { _freeSlots.Add(i); _freeSquads.Add(i); }
            HighSlot = 0;
            Teams[0] = new TeamState { Attacking = true, Target = Obj.Middle };
            Teams[1] = new TeamState { Attacking = true, Target = Obj.Middle };
            Owner[0] = Team.Green; Owner[1] = 255; Owner[2] = Team.Grey;
            for (int i = 0; i < 3; i++) { Capture[i] = 0f; Capturing[i] = 255; }
            Winner = 255;
            WaveTimer = T.waveInterval;
            WaveNumber = 0;
            _squadHigh = MaxSoldiers;
            PlayerSquad = -1; PlayerDeployedSlot = -1;
            Spawn(Team.Green, perSide);
            Spawn(Team.Grey, perSide);
            Running = true;
        }

        void Spawn(byte team, int count)
        {
            count = math.min(count, _freeSlots.Length);
            if (count <= 0) return;
            var entities = EntityManager.CreateEntity(_archetype, count, Allocator.Temp);
            float side = team == Team.Green ? -1f : 1f;
            float2 home = Map.Objectives[team == Team.Green ? Obj.GreenBase : Obj.GreyBase];
            int squads = (count + Squad.Size - 1) / Squad.Size;
            for (int sq = 0; sq < squads; sq++)
            {
                int qi = _freeSquads[_freeSquads.Length - 1];
                _freeSquads.Length--;
                var q = new Squad
                {
                    Active = true, Team = team, Captain = 0, Facing = new float2(-side, 0), Field = Obj.Middle,
                    Lane = math.lerp(-MapView.HalfH + 16f, MapView.HalfH - 16f, (sq + 0.5f) / squads), Nerve = _rng.NextFloat(),
                    Rng = new Random(_rng.NextUInt() | 1u), Mode = SquadMode.Advance,
                };
                float2 at = home + new float2(side * _rng.NextFloat(0f, 24f), 0f);
                at.y = q.Lane * 0.85f + _rng.NextFloat(-3f, 3f);
                for (int m = 0; m < Squad.Size; m++)
                {
                    q.Members[m] = -1;
                    int n = sq * Squad.Size + m;
                    if (n >= count) continue;
                    int slot = _freeSlots[_freeSlots.Length - 1];
                    _freeSlots.Length--;
                    HighSlot = math.max(HighSlot, slot + 1);
                    _gen[slot]++;
                    q.Members[m] = slot;
                    float2 p = at;
                    for (int tries = 0; tries < 12; tries++)
                    {
                        p = MapView.Clamp(at + _rng.NextFloat2Direction() * _rng.NextFloat(0.5f, 4f + tries));
                        if (!Map.IsBlocked(p)) break;
                    }
                    var e = entities[n];
                    SlotEntities[slot] = e;
                    EntityManager.SetComponentData(e, new Soldier { Slot = slot, Gen = _gen[slot], Squad = qi, Team = team, Member = (byte)m });
                    var body = new Body { Pos = new float3(p.x, Map.Height(p), p.y), Yaw = -side * math.PI * 0.5f };
                    EntityManager.SetComponentData(e, body);
                    EntityManager.SetComponentData(e, new Health { Value = 100f });
                    EntityManager.SetComponentData(e, new Brain { Rng = new Random(_rng.NextUInt() | 1u), Target = -1, SenseTimer = _rng.NextFloat(0f, 0.4f) });
                    EntityManager.SetComponentData(e, new Gun { Ammo = Gun.Magazine, Timer = _rng.NextFloat(0.2f, 1.2f) });
                    EntityManager.SetComponentData(e, new Anim { Clip = Clip.Idle, Time = _rng.NextFloat(), Blend = 1f });
                    Slots[slot] = new SlotState { Pos = body.Pos, Yaw = body.Yaw, Gen = _gen[slot], Squad = qi, Team = team, Member = (byte)m, Alive = true };
                }
                q.Pos = at;
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
                int room = T.capPerSide - Teams[t].Alive;
                int n = math.min(T.waveSize, room);
                // always room for a squad led by the player
                if (PlayerDeploy == t) n = math.max(n, Squad.Size);
                n -= n % Squad.Size;
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
            Command(dt);

            var snap = new SnapshotJob { Slots = Slots }.ScheduleParallel(Dependency);
            var g1 = new GridJob { Slots = Slots, Count = HighSlot, Team = Team.Green, Grid = _green }.Schedule(snap);
            var g2 = new GridJob { Slots = Slots, Count = HighSlot, Team = Team.Grey, Grid = _grey }.Schedule(snap);
            var g3 = new GridJob { Slots = Slots, Count = HighSlot, Team = 255, Grid = _near }.Schedule(snap);
            var squads = new SquadJob { Squads = Squads, Slots = Slots, Teams = Teams, Map = Map, Dt = dt }.Schedule(SquadCount, 32, snap);
            var sense = new SenseJob { Slots = Slots, Green = _green, Grey = _grey, Map = Map, Dt = dt, Range = T.maxRange }
                .ScheduleParallel(JobHandle.CombineDependencies(g1, g2, squads));
            var move = new MoveJob { Slots = Slots, Squads = Squads, Near = _near, Map = Map, T = T, Dt = dt }
                .ScheduleParallel(JobHandle.CombineDependencies(sense, g3));
            var fire = new FireJob { Slots = Slots, Map = Map, T = T, Dt = dt, Shots = Shots.AsParallelWriter() }.ScheduleParallel(move);
            Deaths.Clear();
            ShotsSeen.Clear();
            Dependency = new ApplyShotsJob
            {
                Shots = Shots, Seen = ShotsSeen, Deaths = Deaths, Entities = SlotEntities, Slots = Slots, Squads = Squads,
                Healths = GetComponentLookup<Health>(), Brains = GetComponentLookup<Brain>(), Anims = GetComponentLookup<Anim>(true),
                Seed = _rng.NextUInt(),
            }.Schedule(fire);
            Dependency.Complete();
            Bury();
            _merge -= dt;
            if (_merge <= 0f) { _merge = 1f; MergeSquads(); }
        }

        /// <summary>Squads are handed out lowest first, so this covers every one in use.</summary>
        int SquadCount => math.min(MaxSoldiers, _squadHigh);
        int _squadHigh = MaxSoldiers;

        /// <summary>For the renderer: fill its lists of who to draw.</summary>
        public void Cull(CullJob job)
        {
            Dependency = job.ScheduleParallel(Dependency);
            Dependency.Complete();
        }

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
                    q.Active = false;
                    _freeSquads.Add(st.Squad);
                }
                Squads[st.Squad] = q;
                if (PlayerSquad == st.Squad && !any) PlayerSquad = -1;
                st.Alive = false;
                Slots[d.Slot] = st;
                _freeSlots.Add(d.Slot);

                var mine = Teams[st.Team];
                mine.Losses++; mine.TotalLosses++;
                Teams[st.Team] = mine;
                var theirs = Teams[1 - st.Team];
                theirs.Kills++; theirs.TotalKills++;
                Teams[1 - st.Team] = theirs;
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
                if (q.Alive > 2 || q.Alive == 0 || i == PlayerSquad || q.PlayerLed) continue;
                int best = -1;
                float bestD = 30f * 30f;
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
                q.Active = false;
                Squads[i] = q;
                _freeSquads.Add(i);
            }
            _squadHigh = math.min(MaxSoldiers, math.max(high + 1024, 1024));
        }

        // ------------------------------------------------------------------ commanders and objectives

        void Command(float dt)
        {
            float fade = math.exp(-dt / math.max(1f, T.memory));
            System.Array.Clear(_present, 0, 6);
            int a0 = 0, a1 = 0;
            float r2 = T.captureRadius * T.captureRadius;
            for (int i = 0; i < HighSlot; i++)
            {
                var s = Slots[i];
                if (!s.Alive) continue;
                if (s.Team == 0) a0++; else a1++;
                for (int o = 0; o < 3; o++)
                    if (math.distancesq(s.Pos.xz, Map.Objectives[o]) < r2) _present[o * 2 + s.Team]++;
            }
            for (byte t = 0; t < 2; t++)
            {
                var ts = Teams[t];
                ts.Alive = t == 0 ? a0 : a1;
                ts.Kills *= fade;
                ts.Losses *= fade;
                ts.StanceTime += dt;
                Teams[t] = ts;
            }
            Objectives(dt);

            _think -= dt;
            if (_think > 0f) return;
            _think = 0.5f;
            for (byte t = 0; t < 2; t++)
            {
                var ts = Teams[t];
                float cap = math.max(20f, T.capPerSide);
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

                int home = t == 0 ? Obj.GreenBase : Obj.GreyBase, theirs = t == 0 ? Obj.GreyBase : Obj.GreenBase;
                bool middle = Owner[Obj.Middle] == t;
                if (ts.Attacking) ts.Target = middle ? theirs : Obj.Middle;
                else ts.Target = middle ? Obj.Middle : home;
                // everyone back if home is being taken
                if (Capturing[home] == 1 - t && Capture[home] > 0.25f) { ts.Target = home; ts.Attacking = false; }
                Teams[t] = ts;
            }
        }

        readonly int[] _present = new int[6]; // objective * 2 + team

        void Objectives(float dt)
        {
            if (Winner != 255) return;
            for (int o = 0; o < 3; o++)
            {
                int green = _present[o * 2], grey = _present[o * 2 + 1];
                byte lead = green > grey ? Team.Green : grey > green ? Team.Grey : (byte)255;
                // the middle comes before the enemy's base
                if (lead == Team.Green && o == Obj.GreyBase && Owner[Obj.Middle] != Team.Green) lead = 255;
                if (lead == Team.Grey && o == Obj.GreenBase && Owner[Obj.Middle] != Team.Grey) lead = 255;
                bool contested = lead != 255 && math.min(green, grey) * 2 > math.max(green, grey);

                if (lead != 255 && lead != Owner[o] && !contested)
                {
                    if (Capturing[o] != lead) { Capturing[o] = lead; Capture[o] = 0f; }
                    float strength = math.saturate(math.abs(green - grey) / 25f);
                    Capture[o] += dt / math.max(0.5f, T.captureSeconds) * (0.4f + 0.6f * strength);
                    if (Capture[o] >= 1f)
                    {
                        Owner[o] = lead;
                        Capture[o] = 0f; Capturing[o] = 255;
                        if (o != Obj.Middle) Winner = lead;
                    }
                }
                else if (!contested && Capture[o] > 0f)
                {
                    Capture[o] = math.max(0f, Capture[o] - dt / T.captureSeconds * 0.5f);
                    if (Capture[o] <= 0f) Capturing[o] = 255;
                }
            }
        }
    }
}

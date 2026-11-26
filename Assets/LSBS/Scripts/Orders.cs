using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace LSBS
{
    /// <summary>
    /// Ways across the field to wherever things have been sent: each a flow field to one place, made when first wanted and
    /// kept till the oldest nobody's following is needed for somewhere else.
    /// </summary>
    public class FlowPool
    {
        const int Cells = MapView.W * MapView.H;
        public readonly int Count;
        public NativeArray<sbyte> Flow;
        NativeArray<float> _dist;
        NativeArray<float2> _at;
        readonly int[] _cell, _used;
        int _clock;

        public FlowPool(int count)
        {
            Count = count;
            Flow = new NativeArray<sbyte>(count * Cells, Allocator.Persistent);
            _dist = new NativeArray<float>(Cells, Allocator.Persistent);
            _at = new NativeArray<float2>(1, Allocator.Persistent);
            _cell = new int[count];
            _used = new int[count];
            Clear();
        }

        public void Dispose() { Flow.Dispose(); _dist.Dispose(); _at.Dispose(); }

        public void Clear()
        {
            for (int i = 0; i < Count; i++) { _cell[i] = -1; _used[i] = 0; }
        }

        /// <summary>A field to here, over ground where blocked says nothing stands. busy: fields still being followed.</summary>
        public int To(float2 at, in MapView map, NativeArray<byte> blocked, bool[] busy)
        {
            int cell = MapView.Index(at);
            _clock++;
            for (int i = 0; i < Count; i++)
                if (_cell[i] == cell) { _used[i] = _clock; return i; }
            int pick = -1;
            for (int pass = 0; pass < 2 && pick < 0; pass++)
                for (int i = 0; i < Count; i++)
                    if ((pass == 1 || !busy[i]) && (pick < 0 || _used[i] < _used[pick])) pick = i;
            _at[0] = at;
            var job = new FlowFieldJob
            {
                Ground = map.Ground, Blocked = blocked, Rough = map.Rough, Objectives = _at,
                Flow = Flow.GetSubArray(pick * Cells, Cells), Dist = _dist,
            };
            Unity.Jobs.IJobParallelForExtensions.Run(job, 1);
            _cell[pick] = cell;
            _used[pick] = _clock;
            return pick;
        }
    }

    /// <summary>
    /// Commanders' orders: a squad is sent somewhere, and from then on finds its own way there and fights whatever it meets.
    /// Orders are queued and only take effect at the start of a step, so the same list could be shared by every machine in a game.
    /// </summary>
    public unsafe partial class BattleSystem
    {
        /// <summary>How many different places squads, and tanks, can be on their way to at once.</summary>
        public const int OrderFields = 32, TankFields = 16;

        FlowPool _squadWays, _tankWays;
        /// <summary>The way to each place squads have been sent: OrderFields fields of W*H.</summary>
        public NativeArray<sbyte> OrderFlow => _squadWays.Flow;
        public NativeArray<sbyte> TankOrderFlow => _tankWays.Flow;
        public NativeList<OrderCommand> Commands;

        void OrdersCreate()
        {
            _squadWays = new FlowPool(OrderFields);
            _tankWays = new FlowPool(TankFields);
            Commands = new NativeList<OrderCommand>(64, Allocator.Persistent);
        }

        void OrdersDestroy()
        {
            _squadWays.Dispose(); _tankWays.Dispose(); Commands.Dispose();
        }

        void OrdersClear()
        {
            Commands.Clear();
            TankCommands.Clear();
            _squadWays.Clear();
            _tankWays.Clear();
        }

        /// <param name="via">Where the group as a whole is going: the squads share one way there, then each makes for its own place.</param>
        public void Order(int squad, float2 at, float2 via, byte team) => Commands.Add(new OrderCommand { Squad = squad, At = at, Via = via, Team = team });

        /// <summary>Let a squad's own captain decide again.</summary>
        public void Release(int squad, byte team) => Commands.Add(new OrderCommand { Squad = squad, At = new float2(float.NaN), Team = team });

        void ApplyOrders()
        {
            for (int i = 0; i < Commands.Length; i++)
            {
                var c = Commands[i];
                if (c.Squad < 0 || c.Squad >= MaxSoldiers) continue;
                var q = Squads[c.Squad];
                if (!q.Active || q.Team != c.Team || q.Crew || q.Inside) continue;
                if (math.any(math.isnan(c.At)))
                {
                    q.Ordered = false;
                    Squads[c.Squad] = q;
                    continue;
                }
                float2 at = Walkable(MapView.Clamp(c.At)), via = Walkable(MapView.Clamp(c.Via));
                q.Ordered = true;
                q.OrderAt = at;
                q.OrderVia = via;
                q.Field = SquadFieldTo(via);
                q.Attacking = true;
                q.Mode = SquadMode.Advance;
                q.ModeTimer = 0f;
                Squads[c.Squad] = q;
                // nobody stays behind in a bunker, or holds where they were
                for (int m = 0; m < Squad.Size; m++)
                {
                    int slot = q.Members[m];
                    if (slot < 0 || Slots[slot].Player) continue;
                    var e = SlotEntities[slot];
                    var brain = EntityManager.GetComponentData<Brain>(e);
                    brain.OnPost = false;
                    brain.Holding = false;
                    brain.HoldLeft = 0f;
                    EntityManager.SetComponentData(e, brain);
                }
            }
            Commands.Clear();
        }

        /// <summary>The nearest place that can be stood on.</summary>
        float2 Walkable(float2 p)
        {
            if (!Map.IsBlocked(p)) return p;
            for (int r = 1; r < 30; r++)
            for (int k = 0; k < 8 * r; k++)
            {
                float a = k * math.PI * 2f / (8 * r);
                float2 q = MapView.Clamp(p + new float2(math.cos(a), math.sin(a)) * r);
                if (!Map.IsBlocked(q)) return q;
            }
            return p;
        }

        int SquadFieldTo(float2 at)
        {
            var busy = new bool[OrderFields];
            int high = math.min(_squadHigh, MaxSoldiers);
            for (int i = 0; i < high; i++)
            {
                var q = Squads[i];
                if (q.Active && q.Ordered && q.Field >= 0 && q.Field < OrderFields) busy[q.Field] = true;
            }
            return _squadWays.To(at, Map, Map.Blocked, busy);
        }

        int TankFieldTo(float2 at)
        {
            var busy = new bool[TankFields];
            foreach (var t in Tanks) if (!t.Wreck && t.Ordered && t.Field >= 0 && t.Field < TankFields) busy[t.Field] = true;
            // as near as a tank can get
            int2 c0 = MapView.Cell(at);
            float2 end = at;
            float best = 1e9f;
            for (int dz = -30; dz <= 30; dz++)
            for (int dx = -30; dx <= 30; dx++)
            {
                int2 c = c0 + new int2(dx, dz);
                if (c.x < 0 || c.y < 0 || c.x >= MapView.W || c.y >= MapView.H || TankBlocked[MapView.Index(c)] != 0) continue;
                float d = dx * dx + dz * dz;
                if (d < best) { best = d; end = MapView.Centre(c); }
            }
            return _tankWays.To(end, Map, TankBlocked, busy);
        }
    }
}

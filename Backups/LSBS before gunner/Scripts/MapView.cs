using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace LSBS
{
    /// <summary>What soldiers know about the ground. One-metre cells; x runs from the greens (-) to the greys (+).</summary>
    public struct MapView
    {
        public const int W = 480, H = 256;
        public const float HalfW = W * 0.5f, HalfH = H * 0.5f;
        /// <summary>Cover bits: shelters from enemies further along +x (useful to greens), or -x (useful to greys).</summary>
        public const byte FromPlusX = 1, FromMinusX = 2, Roofed = 4;

        [ReadOnly] public NativeArray<float> Ground;   // (W+1)*(H+1)
        [ReadOnly] public NativeArray<float> PropTop;  // W*H, very low where empty
        [ReadOnly] public NativeArray<byte> Blocked;   // W*H
        [ReadOnly] public NativeArray<byte> Rough;     // W*H, extra cost (wire)
        [ReadOnly] public NativeArray<byte> Cover;     // W*H
        [ReadOnly] public NativeArray<sbyte> Flow;     // objectives * W*H, neighbour index or -1
        [ReadOnly] public NativeArray<float> Dist;     // objectives * W*H, walking distance to each
        [ReadOnly] public NativeArray<float2> Objectives;

        public static int2 Cell(float2 p) => math.clamp((int2)math.floor(p + new float2(HalfW, HalfH)), 0, new int2(W - 1, H - 1));
        public static int Index(int2 c) => c.y * W + c.x;
        public static int Index(float2 p) => Index(Cell(p));
        public static float2 Centre(int2 c) => new float2(c.x + 0.5f - HalfW, c.y + 0.5f - HalfH);
        public static float2 Clamp(float2 p) => math.clamp(p, new float2(-HalfW + 3, -HalfH + 3), new float2(HalfW - 3, HalfH - 3));

        public float Height(float2 p) => Height(Ground, p);

        public static float Height(NativeArray<float> ground, float2 p)
        {
            float2 g = math.clamp(p + new float2(HalfW, HalfH), 0f, new float2(W - 0.01f, H - 0.01f));
            int2 c = (int2)g;
            float2 f = g - c;
            int i = c.y * (W + 1) + c.x;
            return math.lerp(math.lerp(ground[i], ground[i + 1], f.x), math.lerp(ground[i + W + 1], ground[i + W + 2], f.x), f.y);
        }

        public bool IsBlocked(float2 p) => Blocked[Index(p)] != 0;

        /// <summary>Nothing solid between two points.</summary>
        public bool Sees(float3 a, float3 b)
        {
            float3 d = b - a;
            float len = math.length(d.xz);
            if (len < 1.5f) return true;
            int n = (int)(len / 0.75f);
            float3 step = d / n;
            float3 p = a + step;
            for (int i = 1; i < n; i++, p += step)
                if (p.y < PropTop[Index(p.xz)] || p.y < Height(p.xz)) return false;
            return true;
        }

        /// <summary>Where a shot from a to b is stopped by the ground or something on it; b itself if it isn't.</summary>
        public float3 Trace(float3 a, float3 b)
        {
            float3 d = b - a;
            float len = math.length(d);
            if (len < 1f) return b;
            int n = (int)(len / 0.5f);
            float3 step = d / n, p = a + step * 2f;
            for (int i = 2; i < n; i++, p += step)
                if (p.y < PropTop[Index(p.xz)] || p.y < Height(p.xz)) return p - step * 0.5f;
            return b;
        }

        /// <summary>Nothing to walk into on the straight way there. The cell being stood in doesn't count.</summary>
        public bool LineClear(float2 a, float2 b)
        {
            float len = math.distance(a, b);
            int n = (int)(len / 0.4f) + 1;
            int here = Index(a);
            for (int i = 1; i <= n; i++)
            {
                int c = Index(math.lerp(a, b, (float)i / n));
                if (c != here && Blocked[c] != 0) return false;
            }
            return true;
        }

        /// <summary>The first corner to make for on the way round what's between here and there, looking a little way about.</summary>
        public float2 Detour(float2 p, float2 dest)
        {
            const int R = 20, N = R * 2 + 1;
            int2 c0 = Cell(p), goal = Cell(dest) - c0;
            var came = new NativeArray<sbyte>(N * N, Allocator.Temp);
            for (int i = 0; i < came.Length; i++) came[i] = -1;
            var queue = new NativeArray<short>(N * N, Allocator.Temp);
            int head = 0, tail = 0, start = R * N + R, best = start;
            float bestD = math.lengthsq((float2)goal);
            came[start] = 8;
            queue[tail++] = (short)start;
            while (head < tail)
            {
                int k = queue[head++];
                int2 o = new int2(k % N - R, k / N - R);
                float d = math.lengthsq((float2)(goal - o));
                if (d < bestD) { bestD = d; best = k; if (d < 0.5f) break; }
                for (int n = 0; n < 8; n++)
                {
                    int2 dir = Dir(n), q = o + dir;
                    if (math.abs(q.x) > R || math.abs(q.y) > R) continue;
                    int2 c = c0 + q;
                    if (c.x < 1 || c.y < 1 || c.x >= W - 1 || c.y >= H - 1 || Blocked[Index(c)] != 0) continue;
                    if (dir.x != 0 && dir.y != 0 && (Blocked[Index(c0 + new int2(q.x, o.y))] != 0 || Blocked[Index(c0 + new int2(o.x, q.y))] != 0)) continue;
                    int kq = (q.y + R) * N + q.x + R;
                    if (came[kq] >= 0) continue;
                    came[kq] = (sbyte)n;
                    queue[tail++] = (short)kq;
                }
            }
            // back along it to the furthest place that can be walked straight to
            int at = best;
            float2 first = p;
            while (at != start)
            {
                int2 o = new int2(at % N - R, at / N - R);
                float2 w = Centre(c0 + o);
                first = w;
                if (LineClear(p, w)) break;
                o -= Dir(came[at]);
                at = (o.y + R) * N + o.x + R;
            }
            came.Dispose();
            queue.Dispose();
            return first;
        }

        public static int2 Dir(int i)
        {
            switch (i)
            {
                case 0: return new int2(1, 0);
                case 1: return new int2(1, 1);
                case 2: return new int2(0, 1);
                case 3: return new int2(-1, 1);
                case 4: return new int2(-1, 0);
                case 5: return new int2(-1, -1);
                case 6: return new int2(0, -1);
                default: return new int2(1, -1);
            }
        }

        /// <summary>The same for just this cell: never into anything.</summary>
        public float2 FlowHere(int objective, float2 p)
        {
            int f = Flow[objective * W * H + Index(p)];
            return f >= 0 ? math.normalize((float2)Dir(f)) : float2.zero;
        }

        /// <summary>The way to walk toward an objective from here.</summary>
        public float2 FlowDir(int objective, float2 p)
        {
            int2 c = Cell(p);
            float2 sum = 0;
            for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
            {
                int2 q = math.clamp(c + new int2(x, y), 0, new int2(W - 1, H - 1));
                int f = Flow[objective * W * H + Index(q)];
                if (f >= 0) sum += math.normalize((float2)Dir(f));
            }
            return math.normalizesafe(sum);
        }
    }

    /// <summary>Walking directions to each objective from everywhere (Dijkstra outward from the objective).</summary>
    [BurstCompile]
    public struct FlowFieldJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> Ground;
        [ReadOnly] public NativeArray<byte> Blocked, Rough;
        [ReadOnly] public NativeArray<float2> Objectives;
        [NativeDisableParallelForRestriction] public NativeArray<sbyte> Flow;
        [NativeDisableParallelForRestriction] public NativeArray<float> Dist;

        float CellHeight(int2 c) => Ground[c.y * (MapView.W + 1) + c.x];

        static void Push(ref NativeList<float2> heap, float2 v)
        {
            heap.Add(v);
            int i = heap.Length - 1;
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (heap[p].x <= v.x) break;
                heap[i] = heap[p];
                i = p;
            }
            heap[i] = v;
        }

        static float2 Pop(ref NativeList<float2> heap)
        {
            float2 top = heap[0], last = heap[heap.Length - 1];
            heap.RemoveAt(heap.Length - 1);
            int n = heap.Length, i = 0;
            if (n == 0) return top;
            while (true)
            {
                int c = i * 2 + 1;
                if (c >= n) break;
                if (c + 1 < n && heap[c + 1].x < heap[c].x) c++;
                if (heap[c].x >= last.x) break;
                heap[i] = heap[c];
                i = c;
            }
            heap[i] = last;
            return top;
        }

        public void Execute(int field)
        {
            const int W = MapView.W, H = MapView.H;
            int off = field * W * H;
            for (int i = 0; i < W * H; i++) { Flow[off + i] = -1; Dist[off + i] = 1e9f; }
            var heap = new NativeList<float2>(4096, Allocator.Temp);
            int start = MapView.Index(Objectives[field]);
            Dist[off + start] = 0;
            Push(ref heap, new float2(0, start));
            while (heap.Length > 0)
            {
                float2 top = Pop(ref heap);
                int idx = (int)top.y;
                if (top.x > Dist[off + idx] + 0.001f) continue; // reached cheaper since
                int2 c = new int2(idx % W, idx / W);
                for (int n = 0; n < 8; n++)
                {
                    int2 dir = MapView.Dir(n);
                    int2 q = c + dir;
                    if (q.x < 1 || q.y < 1 || q.x >= W - 1 || q.y >= H - 1) continue;
                    int qi = MapView.Index(q);
                    if (Blocked[qi] != 0) continue;
                    bool diagonal = dir.x != 0 && dir.y != 0;
                    if (diagonal && (Blocked[MapView.Index(new int2(q.x, c.y))] != 0 || Blocked[MapView.Index(new int2(c.x, q.y))] != 0)) continue;
                    float slope = math.abs(CellHeight(q) - CellHeight(c));
                    float nd = top.x + (diagonal ? 1.414f : 1f) * (1f + math.min(4f, slope * 1.2f) + Rough[qi]);
                    if (nd >= Dist[off + qi] - 0.001f) continue;
                    Dist[off + qi] = nd;
                    Flow[off + qi] = (sbyte)((n + 4) % 8);
                    Push(ref heap, new float2(nd, qi));
                }
            }
        }
    }
}

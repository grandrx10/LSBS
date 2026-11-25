using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace LSBS
{
    /// <summary>A machine gun set up on the field.</summary>
    public struct Maxim
    {
        public const int Clip = 100;
        public const float SetUp = 3f, PackUp = 5f, Reload = 10f, Rpm = 600f;
        /// <summary>How far round it swings either way, and how far its user's feet stay put before they step round after it (radians).</summary>
        public const float Arc = 1.05f, Planted = 0.35f, StepTime = 0.5f;

        public bool Active;
        public byte Team;
        public float3 Pos;          // the ground under its pivot
        public float BaseYaw, Yaw, WantYaw, Pitch;
        public bool Tall;           // on a stand, to fire over something
        public int User, UserGen;   // slot, or -1
        public int Coming, ComingGen; // somebody on their way to it
        public int Ammo;
        public float Timer, Reloading;
        public float Stance;        // which way its user's feet are set
        public float Step;          // 0 planted, else how far through stepping round (1 done)
        public float StepTurn;      // how far round that step goes
        public float Idle;

        public float Height => Tall ? MaximShape.TallPivot : MaximShape.LowPivot;
        public float3 Pivot => Pos + new float3(0, Height, 0);
        public float3 Forward => new float3(math.sin(Yaw), 0, math.cos(Yaw));
    }

    public static class MaximShape
    {
        /// <summary>How high the gun's pivot stands, for somebody kneeling at it and somebody standing.</summary>
        public const float LowPivot = 0.95f, TallPivot = 1.46f;
    }

    public struct Deed
    {
        public int Slot, Gen;
        public Act Act;
        public float3 At;
        public float Yaw;
        public bool Tall;
    }

    public unsafe partial class BattleSystem
    {
        public NativeList<Maxim> Maxims;
        public NativeQueue<Deed> Deeds;
        /// <summary>From the model: how far behind the pivot its user stands, and how far ahead of it the muzzle is.</summary>
        public float MaximBack = 0.9f, MaximMuzzle = 0.8f;
        public int MaximsSet, MaximsLost;
        float _gunners, _crew;

        public const int OrderAuto = 0, OrderAttack = 1, OrderDefend = 2;
        public readonly int[] Orders = new int[2];
        public readonly int[] WaveSize = { 100, 100 };

        void MaximCreate()
        {
            Maxims = new NativeList<Maxim>(512, Allocator.Persistent);
            Deeds = new NativeQueue<Deed>(Allocator.Persistent);
        }

        void MaximDestroy()
        {
            Maxims.Dispose();
            Deeds.Dispose();
        }

        // ------------------------------------------------------------------ orders from the panel

        public void Order(int team, int order)
        {
            Orders[team] = order;
            for (int i = 0; i < math.min(_squadHigh, MaxSoldiers); i++)
            {
                var q = Squads[i];
                if (!q.Active || q.PlayerLed || q.Team != team) continue;
                Assign(ref q, q.Objective);
                q.ModeTimer = 0f;
                Squads[i] = q;
            }
        }

        /// <summary>Reinforcements now, whatever the clock and the cap say.</summary>
        public void SpawnNow(byte team, int count)
        {
            CompleteDependency();
            Spawn(team, count);
            TanksFor(team, count);
        }

        // ------------------------------------------------------------------ setting up, taking down

        bool Is(int slot, int gen) => slot >= 0 && Slots[slot].Alive && Slots[slot].Gen == gen;

        void DoDeeds()
        {
            while (Deeds.TryDequeue(out var d))
            {
                if (!Is(d.Slot, d.Gen)) continue;
                var e = SlotEntities[d.Slot];
                var a = EntityManager.GetComponentData<Ability>(e);
                if (d.Act == Act.Setup && a.Carrying)
                {
                    var m = new Maxim
                    {
                        Active = true, Team = Slots[d.Slot].Team, Pos = new float3(d.At.x, Map.Height(d.At.xz), d.At.z), BaseYaw = d.Yaw, Yaw = d.Yaw, WantYaw = d.Yaw, Stance = d.Yaw,
                        Tall = d.Tall, Ammo = a.MaximAmmo, User = -1, Coming = -1,
                    };
                    int at = -1;
                    for (int i = 0; i < Maxims.Length && at < 0; i++) if (!Maxims[i].Active) at = i;
                    if (at < 0) { at = Maxims.Length; Maxims.Add(m); } else Maxims[at] = m;
                    MaximsSet++;
                    a.Carrying = false;
                    EntityManager.SetComponentData(e, a);
                    Mount(d.Slot, at);
                }
                else if (d.Act == Act.Pack && !a.Carrying && a.Maxim >= 0 && a.Maxim < Maxims.Length && Maxims[a.Maxim].Active)
                {
                    int at = a.Maxim;
                    var m = Maxims[at];
                    a.Carrying = true;
                    a.MaximAmmo = m.Ammo;
                    EntityManager.SetComponentData(e, a);
                    Dismount(d.Slot);
                    m.Active = false;
                    Maxims[at] = m;
                }
            }
        }

        public void Mount(int slot, int at)
        {
            var m = Maxims[at];
            if (!m.Active || Is(m.User, m.UserGen)) return;
            m.User = slot; m.UserGen = Slots[slot].Gen;
            m.Coming = -1;
            m.Stance = m.Yaw;
            m.Step = 0f;
            Maxims[at] = m;
            var e = SlotEntities[slot];
            var a = EntityManager.GetComponentData<Ability>(e);
            a.Maxim = at; a.Want = -1;
            EntityManager.SetComponentData(e, a);
            var brain = EntityManager.GetComponentData<Brain>(e);
            brain.Mounted = true; brain.Arc = m.BaseYaw; brain.Walking = false;
            brain.Kneel = !m.Tall;
            EntityManager.SetComponentData(e, brain);
        }

        public void Dismount(int slot)
        {
            var e = SlotEntities[slot];
            var a = EntityManager.GetComponentData<Ability>(e);
            if (a.Maxim >= 0 && a.Maxim < Maxims.Length)
            {
                var m = Maxims[a.Maxim];
                if (m.User == slot) { m.User = -1; Maxims[a.Maxim] = m; }
            }
            a.Maxim = -1; a.Want = -1;
            EntityManager.SetComponentData(e, a);
            var brain = EntityManager.GetComponentData<Brain>(e);
            brain.Mounted = false;
            brain.HoldPos = Slots[slot].Pos.xz;
            EntityManager.SetComponentData(e, brain);
        }

        /// <summary>The nearest of a side's guns to a place with nobody on it.</summary>
        public int FreeMaxim(float2 p, int team, float reach)
        {
            int best = -1;
            float d2 = reach * reach;
            for (int i = 0; i < Maxims.Length; i++)
            {
                var m = Maxims[i];
                if (!m.Active || m.Team != team || Is(m.User, m.UserGen)) continue;
                float d = math.distancesq(m.Pos.xz - m.Forward.xz * MaximBack, p);
                if (d < d2) { d2 = d; best = i; }
            }
            return best;
        }

        /// <summary>Whether a gun put here can see out, and how high it has to stand for that.</summary>
        public bool Site(float2 stand, float yaw, out float3 at, out bool tall)
        {
            float2 f = new float2(math.sin(yaw), math.cos(yaw));
            float2 p = stand + f * MaximBack;
            at = new float3(p.x, Map.Height(p), p.y);
            tall = false;
            bool roofed = (Map.Cover[MapView.Index(stand)] & MapView.Roofed) != 0;
            if (math.abs(at.y - Map.Height(stand)) > 0.45f || !roofed && Map.IsBlocked(p)) return false;
            for (int k = 0; k < 2; k++)
            {
                tall = k == 1;
                if (roofed && !tall) continue;
                float3 from = at + new float3(0, tall ? MaximShape.TallPivot : MaximShape.LowPivot, 0) + new float3(f.x, 0, f.y) * MaximMuzzle;
                int open = 0;
                for (int i = -1; i <= 1; i++)
                {
                    float2 g = new float2(math.sin(yaw + i * 0.35f), math.cos(yaw + i * 0.35f));
                    float2 far = MapView.Clamp(p + g * 34f);
                    if (Map.Sees(from, new float3(far.x, Map.Height(far) + 1.5f, far.y))) open++;
                }
                if (open >= 2) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ crews

        void MaximTick(float dt)
        {
            for (int i = 0; i < Maxims.Length; i++)
            {
                var m = Maxims[i];
                if (!m.Active) continue;
                bool manned = Is(m.User, m.UserGen);
                if (!manned && m.User >= 0) { m.User = -1; Maxims[i] = m; }
                if (!manned && m.Coming >= 0)
                {
                    bool near = false, lost = !Is(m.Coming, m.ComingGen);
                    if (!lost)
                    {
                        float2 spot = m.Pos.xz - m.Forward.xz * MaximBack;
                        float d = math.distance(Slots[m.Coming].Pos.xz, spot);
                        near = d < 0.9f;
                        lost = d > 25f || Slots[m.Coming].Player;
                    }
                    if (near) { Mount(m.Coming, i); continue; }
                    if (lost)
                    {
                        if (Is(m.Coming, m.ComingGen)) Unwant(m.Coming);
                        m.Coming = -1;
                        Maxims[i] = m;
                    }
                }
            }
            _crew -= dt;
            if (_crew <= 0f) { _crew = 0.6f; Crew(); }
            _gunners -= dt;
            if (_gunners <= 0f) { _gunners = 0.3f; GunnerOrders(0.3f); }
        }

        void Unwant(int slot)
        {
            var e = SlotEntities[slot];
            var a = EntityManager.GetComponentData<Ability>(e);
            a.Want = -1;
            EntityManager.SetComponentData(e, a);
        }

        /// <summary>A gun standing idle gets whoever's nearest.</summary>
        void Crew()
        {
            for (int i = 0; i < Maxims.Length; i++)
            {
                var m = Maxims[i];
                if (!m.Active || m.User >= 0 || m.Coming >= 0) continue;
                float2 spot = m.Pos.xz - m.Forward.xz * MaximBack;
                int best = -1;
                float d2 = 9f * 9f;
                int2 a = _near.At(spot - 9f), b = _near.At(spot + 9f);
                for (int y = a.y; y <= b.y; y++)
                for (int x = a.x; x <= b.x; x++)
                {
                    int k = y * _near.W + x;
                    for (int n = _near.Start[k]; n < _near.Start[k + 1]; n++)
                    {
                        int slot = _near.Items[n];
                        var s = Slots[slot];
                        if (!s.Alive || s.Team != m.Team || s.Player || s.Mounted || s.Squad == PlayerSquad || s.Class == SoldierClass.Communicator) continue;
                        var q = Squads[s.Squad];
                        if (q.Captain == s.Member || q.Mode != SquadMode.Hold) continue;
                        float d = math.distancesq(s.Pos.xz, spot) * (s.Class == SoldierClass.Gunner ? 0.2f : 1f);
                        if (d < d2) { d2 = d; best = slot; }
                    }
                }
                if (best < 0) continue;
                var e = SlotEntities[best];
                var ab = EntityManager.GetComponentData<Ability>(e);
                if (ab.Act != Act.None || ab.Maxim >= 0 || ab.Want >= 0) continue;
                ab.Want = i;
                ab.WantAt = spot;
                EntityManager.SetComponentData(e, ab);
                m.Coming = best; m.ComingGen = Slots[best].Gen;
                Maxims[i] = m;
            }
        }

        /// <summary>Gunners: set it up when the squad stops to fight, pack it when the squad moves off.</summary>
        void GunnerOrders(float dt)
        {
            for (int slot = 0; slot < HighSlot; slot++)
            {
                var s = Slots[slot];
                if (!s.Alive || s.Player || s.Inside || s.Class != SoldierClass.Gunner && !s.Mounted) continue;
                var e = SlotEntities[slot];
                var a = EntityManager.GetComponentData<Ability>(e);
                if (a.Act != Act.None) continue;
                var q = Squads[s.Squad];
                if (a.Maxim >= 0)
                {
                    var m = Maxims[a.Maxim];
                    // off with the squad: when it's moving on, or they've nothing to shoot at and everyone's gone
                    bool posted = EntityManager.GetComponentData<Brain>(e).OnPost;
                    float away = math.distance(q.Pos, s.Pos.xz);
                    bool leaving = !posted && (q.PlayerLed ? !q.PlayerHold && away > 14f : q.Mode == SquadMode.Advance && (q.Attacking || away > 20f) || away > 45f);
                    m.Idle = leaving && !s.HasTarget ? m.Idle + dt : 0f;
                    Maxims[a.Maxim] = m;
                    if (m.Idle < 2.5f) continue;
                    if (s.Class == SoldierClass.Gunner && !a.Carrying)
                    {
                        a.Act = Act.Pack; a.Timer = 0f; a.Done = false; a.At = m.Pos;
                        EntityManager.SetComponentData(e, a);
                    }
                    else Dismount(slot);
                    continue;
                }
                if (!a.Carrying || a.Want >= 0 || a.Cooldown > 0f) continue;
                var brain = EntityManager.GetComponentData<Brain>(e);
                bool settled = brain.OnPost ? math.distance(brain.Post, s.Pos.xz) < 0.8f : q.Mode == SquadMode.Hold && brain.Still > 0.8f && (q.Contacts > 0 || !q.Attacking || q.PlayerHold);
                if (!settled || brain.Dive > 0f) continue;
                // facing who they can see, else the way the squad does
                float yaw = math.atan2(q.Facing.x, q.Facing.y);
                if (brain.OnPost) yaw = s.Team == Team.Green ? math.PI * 0.5f : -math.PI * 0.5f;
                else if (brain.Target >= 0) { float2 f = Slots[brain.Target].Pos.xz - s.Pos.xz; yaw = math.atan2(f.x, f.y); }
                if (!Site(s.Pos.xz, yaw, out var at, out bool tall))
                {
                    a.Cooldown = 1.5f;
                    EntityManager.SetComponentData(e, a);
                    continue;
                }
                a.Act = Act.Setup; a.Timer = 0f; a.Done = false; a.At = at; a.Yaw = yaw; a.Tall = tall;
                EntityManager.SetComponentData(e, a);
            }
        }

        /// <summary>An explosion breaks any gun in reach of it.</summary>
        void BreakMaxims(float3 at, float reach)
        {
            bool inside = (Map.Cover[MapView.Index(at.xz)] & MapView.Roofed) != 0;
            for (int i = 0; i < Maxims.Length; i++)
            {
                var m = Maxims[i];
                if (!m.Active || math.distancesq(m.Pos, at) > reach * reach) continue;
                bool roofed = (Map.Cover[MapView.Index(m.Pos.xz - m.Forward.xz * MaximBack)] & MapView.Roofed) != 0;
                if (roofed && !inside) continue;
                if (Is(m.User, m.UserGen)) Dismount(m.User);
                if (Is(m.Coming, m.ComingGen)) Unwant(m.Coming);
                m = Maxims[i];
                m.Active = false;
                Maxims[i] = m;
                MaximsLost++;
                Blasts.Add(new BlastEvent { At = m.Pivot, Wreck = true, Team = m.Team });
            }
        }
    }
}

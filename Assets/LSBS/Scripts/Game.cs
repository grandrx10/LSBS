using Unity.Mathematics;

namespace LSBS
{
    /// <summary>What a side buys: squads of ten, tanks, tank destroyers. Each comes with its own transport.</summary>
    [System.Serializable]
    public struct Buy
    {
        public int Squads, Tanks, Destroyers;

        public int Cost(in Tuning t) => Squads * t.squadCost + Tanks * t.tankCost + Destroyers * t.destroyerCost;

        /// <summary>A computer commander's choice: mostly infantry, a few tanks and destroyers.</summary>
        public static Buy Pick(in Tuning t, int gold, ref Random rng, bool squads = true, bool tanks = true, bool destroyers = true)
        {
            var b = new Buy();
            int armour = !tanks && !destroyers ? 0 : !squads ? gold : (int)(gold * rng.NextFloat(0.2f, 0.45f));
            while (armour >= math.min(t.tankCost, t.destroyerCost) && b.Cost(t) + math.min(t.tankCost, t.destroyerCost) <= gold)
            {
                bool tank = destroyers ? tanks && rng.NextFloat() < 0.6f : true;
                int c = tank ? t.tankCost : t.destroyerCost;
                if (b.Cost(t) + c > gold) break;
                if (tank) b.Tanks++; else b.Destroyers++;
                armour -= c;
            }
            if (squads) b.Squads = math.max(0, (gold - b.Cost(t)) / math.max(1, t.squadCost));
            return b;
        }
    }

    /// <summary>A game as set up in the menu: how many sides, which is the person playing's, and what each brings.</summary>
    public class GameSetup
    {
        public int Teams = 2;
        public byte Player;
        public uint Seed;
        public readonly Buy[] First = new Buy[Team.Max], Wave = new Buy[Team.Max];
        /// <summary>Which kinds may be bought at all.</summary>
        public bool Squads = true, Tanks = true, Destroyers = true;

        /// <summary>Every side the same: so many squads, no armour.</summary>
        public static GameSetup Even(int teams, int squads, in Tuning t)
        {
            var g = new GameSetup { Teams = teams };
            for (int i = 0; i < Team.Max; i++) { g.First[i] = new Buy { Squads = squads }; g.Wave[i] = new Buy { Squads = squads / 2 }; }
            return g;
        }

        /// <summary>What's allowed of a buy.</summary>
        public Buy Allowed(Buy b)
        {
            if (!Squads) b.Squads = 0;
            if (!Tanks) b.Tanks = 0;
            if (!Destroyers) b.Destroyers = 0;
            return b;
        }

        /// <summary>The computer's sides choose for themselves, within what's allowed.</summary>
        public void PickForComputer(in Tuning t)
        {
            var rng = new Random((Seed | 1u) * 747796405u);
            for (int i = 0; i < Teams; i++)
            {
                if (i == Player) continue;
                First[i] = Allowed(Buy.Pick(t, t.startGold, ref rng, Squads, Tanks, Destroyers));
                Wave[i] = Allowed(Buy.Pick(t, t.waveGold, ref rng, Squads, Tanks, Destroyers));
            }
        }
    }

    public unsafe partial class BattleSystem
    {
        public readonly Buy[] StartBuy = new Buy[Team.Max], WaveBuy = new Buy[Team.Max];

        /// <summary>Everything bought comes in by air, a transport each, one after another.</summary>
        void Deliver(byte team, Buy buy, bool first)
        {
            float delay = first ? 0f : 0.5f;
            int n = 0;
            // armour first, so the infantry land about it
            for (int i = 0; i < buy.Tanks; i++) Bring(team, true, TankKind.Tank, delay + n++ * 0.6f);
            for (int i = 0; i < buy.Destroyers; i++) Bring(team, true, TankKind.Destroyer, delay + n++ * 0.6f);
            for (int i = 0; i < buy.Squads; i++) Bring(team, false, TankKind.Tank, delay + n++ * 0.35f);
        }

        // ------------------------------------------------------------------ the computer's commanders

        /// <summary>
        /// Each side the computer plays gathers its squads at a place off to one side of the objective, then sends them in
        /// together; then picks another way in for the next lot. Holding the objective, it lets its captains look to it.
        /// </summary>
        struct Plan
        {
            public float Angle, Timer, Think;
            public bool Gathering;
        }

        readonly Plan[] _plans = new Plan[Team.Max];

        void CommanderReset()
        {
            for (int t = 0; t < Team.Max; t++)
            {
                float2 home = Map.Objectives.IsCreated && Front.Home(t) < Map.Objectives.Length ? Map.Objectives[Front.Home(t)] : new float2(1, 0);
                _plans[t] = new Plan { Angle = math.atan2(home.y, home.x) + _rng.NextFloat(-0.8f, 0.8f), Think = _rng.NextFloat(2f, 6f), Gathering = true };
            }
        }

        /// <summary>Where a side gathers before going in.</summary>
        float2 Staging(int team) => MapView.Clamp(Map.Objectives[Front.Centre] + new float2(math.cos(_plans[team].Angle), math.sin(_plans[team].Angle)) * 85f);

        void Commanders(float dt)
        {
            for (byte t = 0; t < TeamCount; t++)
            {
                if (t == PlayerTeam) continue;
                var p = _plans[t];
                p.Think -= dt;
                p.Timer += dt;
                if (p.Think > 0f) { _plans[t] = p; continue; }
                p.Think = 4f;
                _plans[t] = p;
                Direct(t);
            }
        }

        void Direct(byte team)
        {
            var p = _plans[team];
            bool ours = Owner.Length > 0 && Owner[Front.Centre] == team;
            float2 stage = Staging(team);
            int sent = 0, there = 0;
            int high = math.min(_squadHigh, MaxSoldiers);
            for (int i = 0; i < high; i++)
            {
                var q = Squads[i];
                if (!q.Active || q.Team != team || q.Crew || q.Inside || q.Alive == 0) continue;
                if (q.Ordered)
                {
                    sent++;
                    if (math.distance(q.Pos, q.OrderAt) < 30f) there++;
                    continue;
                }
                // fresh squads, back at the landing ground and not fighting: to the gathering place
                if (!ours && q.Contacts == 0 && math.distance(q.Pos, Map.Objectives[Front.Home(team)]) < 110f)
                {
                    Order(i, stage, stage, team);
                    sent++;
                }
            }
            // in they go: most of them there, or waited long enough, or the objective's ours to hold
            if (sent > 0 && (there >= sent * 0.7f || p.Timer > 75f || ours))
            {
                for (int i = 0; i < high; i++)
                {
                    var q = Squads[i];
                    if (q.Active && q.Team == team && q.Ordered && !q.Inside) Release(i, team);
                }
                p.Timer = 0f;
                // the next lot come another way
                float2 home = Map.Objectives[Front.Home(team)];
                p.Angle = math.atan2(home.y, home.x) + _rng.NextFloat(-1.1f, 1.1f);
            }
            if (sent == 0) p.Timer = 0f;
            _plans[team] = p;
        }
    }
}

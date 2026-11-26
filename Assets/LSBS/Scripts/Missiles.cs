using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace LSBS
{
    /// <summary>A homing missile in the air, after one vehicle.</summary>
    public class Missile
    {
        public float3 Pos, Vel;
        public Tank Target;
        public Aircraft Air;
        public byte Team;
        public int Shooter, ShooterGen;
        public float Age;
        /// <summary>Seconds of fuel: after that it falls.</summary>
        public const float Burn = 5f;
        /// <summary>Hit points one takes off whatever it hits.</summary>
        public const float TankHurt = 250f;
        /// <summary>Only something in the air is followed; at anything on the ground it flies straight where it was aimed.</summary>
        public bool Guided;
    }

    public partial class BattleSystem
    {
        public NativeQueue<Launch> Launches;
        public readonly List<Missile> Missiles = new();
        public int MissilesFired, MissileHits;

        void MissilesCreate() => Launches = new NativeQueue<Launch>(Allocator.Persistent);
        void MissilesDestroy() => Launches.Dispose();
        void MissilesClear() { Launches.Clear(); Missiles.Clear(); }

        /// <summary>The person playing a launcher fires at a vehicle.</summary>
        public void PlayerLaunch(int slot, float3 from, int vehicle, float3 dir)
        {
            var st = Slots[slot];
            Launches.Enqueue(new Launch { From = from, Tank = vehicle, Dir = dir, Aimed = true, Team = st.Team, Shooter = slot, ShooterGen = st.Gen });
        }

        void MissileTick(float dt)
        {
            while (Launches.TryDequeue(out var l))
            {
                // a tank, or past the tanks, a transport
                Tank t = l.Tank >= 0 && l.Tank < Tanks.Count ? Tanks[l.Tank] : null;
                Aircraft air = t == null && l.Tank >= Tanks.Count && l.Tank - Tanks.Count < Flights.Count ? Flights[l.Tank - Tanks.Count] : null;
                if (t == null && air == null && !math.any(l.Dir)) continue;
                float3 to;
                if (air != null)
                {
                    // off the shoulder a little up, then after it
                    to = math.normalizesafe(math.normalizesafe(air.Pos - l.From) + new float3(0, 0.25f, 0));
                }
                else
                {
                    // fired straight: where the tank will be, by the firer's reckoning, give or take
                    float3 aim = math.any(l.Dir) && t == null ? l.From + l.Dir * 100f : t.Pos + new float3(0, 1.4f, 0);
                    if (t != null) aim += new float3(math.sin(t.Yaw), 0, math.cos(t.Yaw)) * t.Speed * math.distance(aim, l.From) / math.max(1f, T.missileSpeed);
                    to = Wobble(math.normalizesafe(aim - l.From), l.Aimed ? 0.4f : 1f);
                }
                Missiles.Add(new Missile { Pos = l.From, Vel = to * T.missileSpeed * (air != null ? 0.45f : 0.8f), Target = air != null ? null : t, Air = air, Guided = air != null, Team = l.Team, Shooter = l.Shooter, ShooterGen = l.ShooterGen });
                Blasts.Add(new BlastEvent { At = l.From, Dir = to, Cannon = true, Team = l.Team });
                MissilesFired++;
            }
            float turn = math.radians(T.missileTurn) * dt;
            for (int i = Missiles.Count - 1; i >= 0; i--)
            {
                var m = Missiles[i];
                m.Age += dt;
                float speed = math.length(m.Vel);
                float3 dir = speed > 0.01f ? m.Vel / speed : new float3(0, 0, 1);
                if (m.Age < Missile.Burn)
                {
                    // speeds up off the launcher, and steers for where it's aimed at, no faster than it can turn
                    speed = math.min(T.missileSpeed, speed + T.missileSpeed * 1.5f * dt);
                    bool chasing = m.Guided && m.Air != null && m.Air.Flying;
                    if (chasing)
                    {
                        float3 aim = m.Air.Pos;
                        // lead it a little
                        float3 run = m.Air.Vel;
                        aim += run * math.min(1.5f, math.distance(aim, m.Pos) / math.max(1f, speed));
                        float3 want = math.normalizesafe(aim - m.Pos, dir);
                        float angle = math.acos(math.clamp(math.dot(dir, want), -1f, 1f));
                        if (angle > 1e-4f) dir = math.normalizesafe(Slerp(dir, want, math.min(1f, turn / angle)), dir);
                    }
                    m.Vel = dir * speed;
                }
                else m.Vel.y -= 9.81f * dt;
                m.Pos += m.Vel * dt;

                bool boom = false;
                Tank hit = null;
                foreach (var t in Tanks)
                {
                    if (t.Wreck || t.Carried || t.Team == m.Team && m.Age < 0.6f) continue;
                    if (m.Pos.y < t.Pos.y - 0.5f || m.Pos.y > t.Pos.y + 3.4f) continue;
                    if (math.distancesq(t.Pos.xz, m.Pos.xz) > 49f || t.Distance(m.Pos) > 0.4f) continue;
                    hit = t; boom = true; break;
                }
                Aircraft downed = null;
                if (!boom)
                    foreach (var a in Flights)
                        if (a.Flying && a.Team != m.Team && math.distancesq(a.Pos, m.Pos) < 4.5f * 4.5f) { downed = a; boom = true; break; }
                if (!boom && m.Pos.y < Map.Height(m.Pos.xz) + 0.1f) boom = true;
                if (!boom && Map.IsBlocked(m.Pos.xz) && m.Pos.y < Map.PropTop[MapView.Index(m.Pos.xz)]) boom = true;
                if (!boom && (m.Age > Missile.Burn + 6f || math.abs(m.Pos.x) > MapView.HalfW || math.abs(m.Pos.z) > MapView.HalfH)) { Missiles.RemoveAt(i); continue; }
                if (!boom) continue;
                Missiles.RemoveAt(i);
                if (hit != null)
                {
                    MissileHits++;
                    hit.Health -= Missile.TankHurt;
                    hit.PitchV += _rng.NextFloat(-0.6f, 0.6f);
                    hit.RollV += _rng.NextFloat(-0.6f, 0.6f);
                    if (hit.Health <= 0.01f) Destroy(hit);
                }
                if (downed != null) { MissileHits++; HurtAircraft(downed, Missile.TankHurt); }
                // a small blast for anybody standing by; tanks are hurt only by the hit itself
                Blasts.Add(new BlastEvent { At = m.Pos, Radius = 6f, Team = m.Team });
                new BlastJob
                {
                    Slots = Slots, Near = _near, Map = Map, Shots = Shots, At = m.Pos, FullRadius = 2.5f, FullDamage = 60f, EdgeRadius = 6f, EdgeDamage = 15f,
                    Team = m.Team, Friendly = T.friendlyBlasts, Big = false, Warning = false, Low = T.lowBlast, Shooter = m.Shooter, ShooterGen = m.ShooterGen,
                }.Run();
            }
        }

        /// <summary>A direction thrown off by up to the launcher's error (scaled), every way equally.</summary>
        float3 Wobble(float3 dir, float scale)
        {
            float err = math.radians(T.missileError) * scale * math.sqrt(_rng.NextFloat());
            float3 side = math.normalizesafe(math.cross(dir, new float3(0, 1, 0)), new float3(1, 0, 0)), up = math.cross(side, dir);
            float a = _rng.NextFloat(0f, math.PI * 2f);
            return math.normalizesafe(dir + (side * math.cos(a) + up * math.sin(a)) * math.tan(err));
        }

        static float3 Slerp(float3 a, float3 b, float t)
        {
            float d = math.clamp(math.dot(a, b), -1f, 1f);
            float th = math.acos(d) * t;
            float3 rel = math.normalizesafe(b - a * d);
            return a * math.cos(th) + rel * math.sin(th);
        }
    }
}

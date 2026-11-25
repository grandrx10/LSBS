using System.Collections.Generic;
using UnityEngine;

namespace Gunbound
{
    /// <summary>
    /// Hitscan bullet with ricochet (off any world surface) and penetration (through players).
    /// Against players the bullet is a cylinder (radius = GunDefinition.bulletRadius): every body part the
    /// cylinder touches counts, and the most damaging one is the part that's hit. Against the world it's a thin ray,
    /// so it doesn't snag on corners.
    /// After a ricochet the bullet can hit anyone again — including the shooter.
    /// </summary>
    public static class BulletSim
    {
        public const byte KStart = 0, KRicochet = 1, KImpact = 2, KPlayer = 3, KEnd = 4;

        public struct Hit
        {
            public Combatant target;
            public int segment;
            public Vector3 point, dir;
        }

        public class Result
        {
            public readonly List<Vector3> points = new();
            public readonly List<Vector3> normals = new();
            public readonly List<byte> kinds = new();
            public readonly List<Hit> hits = new();
            public readonly List<Transform> surfaces = new();

            public void Clear() { points.Clear(); normals.Clear(); kinds.Clear(); hits.Clear(); surfaces.Clear(); }

            public void Add(Vector3 p, Vector3 n, byte k, Transform surface)
            {
                points.Add(p); normals.Add(n); kinds.Add(k); surfaces.Add(surface);
            }
        }

        /// <summary>One body touched by the bullet cylinder on the current segment.</summary>
        struct BodyHit
        {
            public Combatant owner;
            public float entry;      // distance along the ray where the cylinder first touches this body
            public Hitbox best;      // most damaging part touched
            public float bestDamage;
            public float bestDist;
            public Vector3 bestPoint;
        }

        static readonly RaycastHit[] Buffer = new RaycastHit[64];
        static readonly HashSet<Combatant> Ignore = new();
        static readonly List<BodyHit> Bodies = new();

        /// <param name="radiusScale">Bullet size multiplier (Sharpshooter perk).</param>
        public static void Trace(Vector3 origin, Vector3 dir, GunDefinition gun, Combatant shooter, Result r, float radiusScale = 1f)
        {
            r.Clear();
            r.Add(origin, -dir, KStart, null);
            int ricochetsLeft = gun.ricochets;
            int penetrationsLeft = gun.penetrations;
            float radius = Mathf.Max(0.001f, gun.bulletRadius * radiusScale);
            Ignore.Clear();
            if (shooter) Ignore.Add(shooter); // can't shoot yourself point-blank, only via ricochet

            Vector3 pos = origin;
            dir.Normalize();
            for (int guard = 0; guard < 32; guard++)
            {
                // 1) the world stops the bullet as a thin ray
                bool hitWorld = Physics.Raycast(pos, dir, out var world, gun.maxRange, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
                float segLen = hitWorld ? world.distance : gun.maxRange;

                // 2) bodies in front of that, using the bullet cylinder
                CollectBodies(pos, dir, segLen, radius, gun);
                foreach (var b in Bodies)
                {
                    r.hits.Add(new Hit { target = b.owner, segment = b.best.SegmentIndex, point = b.bestPoint, dir = dir });
                    r.Add(b.bestPoint, -dir, KPlayer, b.best.transform);
                    if (penetrationsLeft > 0)
                    {
                        penetrationsLeft--;
                        Ignore.Add(b.owner); // pass through this body (don't double-count its other hitboxes)
                        continue;
                    }
                    return; // stopped inside the player
                }

                if (!hitWorld)
                {
                    r.Add(pos + dir * gun.maxRange, dir, KEnd, null);
                    return;
                }

                if (ricochetsLeft > 0)
                {
                    ricochetsLeft--;
                    r.Add(world.point, world.normal, KRicochet, world.transform);
                    dir = Vector3.Reflect(dir, world.normal).normalized;
                    pos = world.point + world.normal * 0.01f;
                    Ignore.Clear(); // after a bounce everyone (including the shooter) is fair game again
                }
                else
                {
                    r.Add(world.point, world.normal, KImpact, world.transform);
                    return;
                }
            }
        }

        /// <summary>Fills Bodies (nearest first) with every combatant the cylinder touches and its most damaging part.</summary>
        static void CollectBodies(Vector3 pos, Vector3 dir, float length, float radius, GunDefinition gun)
        {
            Bodies.Clear();
            int n = Physics.SphereCastNonAlloc(pos, radius, dir, Buffer, length, 1 << Layers.Hitbox, QueryTriggerInteraction.Collide);
            var ray = new Ray(pos, dir);
            for (int i = 0; i < n; i++)
            {
                var h = Buffer[i];
                var hb = h.collider.GetComponent<Hitbox>();
                if (hb == null || hb.Owner == null || !hb.Owner.IsAlive || Ignore.Contains(hb.Owner)) continue;

                // where along the bullet this part is touched, and the point to report
                float dist;
                Vector3 point;
                if (h.collider.Raycast(ray, out var exact, length))
                {
                    dist = exact.distance;
                    point = exact.point; // the centre line actually hits it: use the precise point
                }
                else if (h.distance <= 0f && h.point == Vector3.zero)
                {
                    // overlapping the cylinder at its start: Unity reports no point
                    point = h.collider.ClosestPoint(pos);
                    dist = Mathf.Max(0f, Vector3.Dot(point - pos, dir));
                }
                else
                {
                    dist = h.distance;
                    point = h.point;
                }

                float dmg = gun.damage * gun.Multiplier(hb.Region);
                int bi = -1;
                for (int k = 0; k < Bodies.Count; k++) if (Bodies[k].owner == hb.Owner) { bi = k; break; }
                if (bi < 0)
                {
                    Bodies.Add(new BodyHit { owner = hb.Owner, entry = dist, best = hb, bestDamage = dmg, bestDist = dist, bestPoint = point });
                    continue;
                }
                var b = Bodies[bi];
                b.entry = Mathf.Min(b.entry, dist);
                if (dmg > b.bestDamage + 0.001f || (Mathf.Abs(dmg - b.bestDamage) <= 0.001f && dist < b.bestDist))
                {
                    b.best = hb;
                    b.bestDamage = dmg;
                    b.bestDist = dist;
                    b.bestPoint = point;
                }
                Bodies[bi] = b;
            }
            Bodies.Sort((a, b) => a.entry.CompareTo(b.entry));
        }
    }
}

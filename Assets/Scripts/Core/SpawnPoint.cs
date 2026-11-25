using System.Collections.Generic;
using UnityEngine;

namespace Gunbound
{
    /// <summary>
    /// Drop as many of these around the map as you like (Gunbound > Add Spawn Point). Put them inside the map
    /// prefab: only the spawn points of the map being played are used.
    /// The host picks a random spawn that no living enemy can see and that is far enough from all of them
    /// (teammates don't count as enemies).
    /// </summary>
    public class SpawnPoint : MonoBehaviour
    {
        static readonly List<SpawnPoint> Points = new();
        static readonly List<SpawnPoint> Valid = new();
        const float EyeHeight = 0.85f;

        [Tooltip("Random horizontal offset around this point (metres). Keep small so you don't spawn in walls.")]
        public float randomRadius = 0.5f;

        /// <summary>The map this spawn belongs to (set when the map loads), or null for a loose scene spawn.</summary>
        public GameMap Map { get; set; }

        /// <summary>Where this spawn is once its map has fully risen.</summary>
        public Vector3 HomePosition => Map != null ? transform.position - Vector3.up * Map.Offset : transform.position;

        void OnEnable() => Points.Add(this);
        void OnDisable() => Points.Remove(this);

        /// <summary>The current map's spawns; loose scene spawns when no map rotation is running.</summary>
        static IReadOnlyList<SpawnPoint> Candidates
        {
            get
            {
                var map = MatchManager.CurrentMap;
                if (map != null && map.Spawns.Count > 0) return map.Spawns;
                return Points;
            }
        }

        /// <summary>Any spawn, no rules (used as a client-side placeholder).</summary>
        public static void Pick(out Vector3 position, out float yaw)
        {
            var list = Candidates;
            Resolve(list.Count > 0 ? list[Random.Range(0, list.Count)] : null, out position, out yaw);
        }

        /// <summary>Server: choose a safe spawn for this player.</summary>
        public static void PickFor(PlayerController who, out Vector3 position, out float yaw)
        {
            MatchManager.ServerEnsureStarted();
            Valid.Clear();
            SpawnPoint best = null;
            float bestScore = float.MinValue;

            foreach (var sp in Candidates)
            {
                if (sp == null) continue;
                Vector3 p = sp.HomePosition + Vector3.up * EyeHeight;
                float nearest = float.MaxValue;
                bool seen = false;
                foreach (var pc in PlayerController.All)
                {
                    if (pc == null || pc == who || !pc.IsAlive || MatchManager.AreTeammates(pc, who)) continue;
                    Vector3 eye = pc.transform.position + Vector3.up * EyeHeight;
                    nearest = Mathf.Min(nearest, Vector3.Distance(eye, p));
                    if (!seen && GameRules.RequireNoLineOfSight &&
                        !Physics.Linecast(eye, p, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                        seen = true;
                }
                if (!seen && nearest >= GameRules.MinSpawnDistance) Valid.Add(sp);

                // fallback if nothing is fully safe: prefer hidden, then farthest
                float score = (seen ? 0f : 100000f) + Mathf.Min(nearest, 99999f);
                if (score > bestScore) { bestScore = score; best = sp; }
            }

            Resolve(Valid.Count > 0 ? Valid[Random.Range(0, Valid.Count)] : best, out position, out yaw);
        }

        static void Resolve(SpawnPoint sp, out Vector3 position, out float yaw)
        {
            if (sp == null)
            {
                position = new Vector3(0, 3, 0);
                yaw = 0;
                return;
            }
            Vector2 r = Random.insideUnitCircle * sp.randomRadius;
            position = sp.HomePosition + new Vector3(r.x, 0, r.y);
            yaw = sp.transform.eulerAngles.y;
        }

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.8f);
            Gizmos.DrawWireSphere(transform.position, 0.4f);
            Gizmos.DrawLine(transform.position, transform.position + transform.forward);
            Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.25f);
            Gizmos.DrawWireCube(transform.position + Vector3.down * 0.0f, new Vector3(0.7f, 1.95f, 0.7f));
        }
    }
}

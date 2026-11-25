using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Gunbound
{
    /// <summary>
    /// Runtime wrapper around one map prefab instance (added by MatchManager — nothing to set up).
    /// Moves the whole map up/down for the sink / rise transition and knows its spawn points and hill.
    /// </summary>
    public class GameMap : MonoBehaviour
    {
        public int Index { get; private set; }
        public string DisplayName { get; private set; }
        /// <summary>Where the prefab puts the map (offset 0).</summary>
        public Vector3 HomePosition { get; private set; }
        /// <summary>How far down the map goes to be completely under the ground.</summary>
        public float Depth { get; private set; }
        /// <summary>Current vertical offset from HomePosition (0 = in play, -Depth = hidden underground).</summary>
        public float Offset { get; private set; }
        public Rigidbody Body { get; private set; }
        public HillZone Hill { get; private set; }
        public IReadOnlyList<SpawnPoint> Spawns => _spawns;

        readonly List<SpawnPoint> _spawns = new();

        public void Init(int index, string prefabName)
        {
            Index = index;
            DisplayName = Pretty(prefabName);
            HomePosition = transform.position;

            // one kinematic body for the whole map: cheap to move, and colliders can be told apart by attachedRigidbody
            Body = GetComponent<Rigidbody>();
            if (Body == null) Body = gameObject.AddComponent<Rigidbody>();
            Body.isKinematic = true;
            Body.useGravity = false;
            Body.interpolation = RigidbodyInterpolation.None;

            foreach (var sp in GetComponentsInChildren<SpawnPoint>(true))
            {
                sp.Map = this;
                _spawns.Add(sp);
            }
            if (_spawns.Count == 0) Debug.LogWarning($"[Gunbound] Map '{prefabName}' has no spawn points.");

            Hill = GetComponentInChildren<HillZone>(true);
            if (Hill != null) Hill.Build();

            // sink until the top of the map is below where its bottom was
            var b = new Bounds(transform.position, Vector3.zero);
            bool any = false;
            foreach (var r in GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled) continue;
                if (any) b.Encapsulate(r.bounds); else { b = r.bounds; any = true; }
            }
            foreach (var c in GetComponentsInChildren<Collider>())
            {
                if (!c.enabled) continue;
                if (any) b.Encapsulate(c.bounds); else { b = c.bounds; any = true; }
            }
            Depth = Mathf.Max(5f, b.size.y + 2f);
        }

        public void SetOffset(float y)
        {
            Offset = y;
            var p = HomePosition + Vector3.up * y;
            transform.position = p;
            Body.position = p;
        }

        public void SetHillShown(bool on)
        {
            if (Hill == null) return;
            var root = Hill.Root;
            if (root.activeSelf != on) root.SetActive(on);
        }

        /// <summary>Is this collider part of this map?</summary>
        public bool Owns(Collider c) => c != null && c.attachedRigidbody == Body;

        /// <summary>"MapVertigo" -> "VERTIGO", "MapBasic" -> "BASIC".</summary>
        static string Pretty(string n)
        {
            if (n.StartsWith("Map") && n.Length > 3) n = n.Substring(3);
            n = n.Trim(' ', '_', '-');
            var sb = new StringBuilder();
            for (int i = 0; i < n.Length; i++)
            {
                char ch = n[i];
                if (ch == '_' || ch == '-') { sb.Append(' '); continue; }
                if (i > 0 && char.IsUpper(ch) && char.IsLower(n[i - 1])) sb.Append(' ');
                sb.Append(ch);
            }
            return sb.ToString().ToUpperInvariant();
        }
    }
}

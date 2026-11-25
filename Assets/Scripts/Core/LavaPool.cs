using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace Gunbound
{
    /// <summary>
    /// Lava hazard. Add this ("Lava Pool") to the lava object: anyone touching any of its colliders
    /// (on this object or its children) dies instantly. Whoever damaged them last during that life gets
    /// the kill ("Killer forced Player to jump into lava"); if nobody did, it counts as a suicide.
    /// Works with solid or trigger colliders, including non-convex mesh colliders. Checked by the server.
    /// </summary>
    [AddComponentMenu("Gunbound/Lava Pool")]
    [DisallowMultipleComponent]
    public class LavaPool : MonoBehaviour
    {
        [Tooltip("How close (metres) a body has to get to the lava surface to count as touching it.")]
        public float touchMargin = 0.08f;

        [Header("Sinking (trigger colliders: bodies fall in and sink like in a thick liquid)")]
        [Tooltip("Speed (m/s) bodies settle at while sinking.")]
        public float sinkSpeed = 0.7f;
        [Tooltip("How quickly the lava slows bodies down to the sink speed (higher = thicker).")]
        public float viscosity = 4f;

        Collider[] _colliders;
        static readonly Collider[] Buf = new Collider[64];
        static readonly Collider[] SinkBuf = new Collider[256];
        readonly HashSet<Rigidbody> _sinking = new();
        static readonly List<Combatant> Combatants = new();
        static float _nextRefresh;

        void Awake()
        {
            _colliders = GetComponentsInChildren<Collider>(true);
            if (_colliders.Length == 0)
            {
                // no collider: fit a box to the meshes so the hazard still works
                var box = gameObject.AddComponent<BoxCollider>();
                var r = GetComponentInChildren<Renderer>();
                if (r != null)
                {
                    box.center = transform.InverseTransformPoint(r.bounds.center);
                    var s = transform.lossyScale;
                    box.size = new Vector3(r.bounds.size.x / Mathf.Max(1e-4f, Mathf.Abs(s.x)),
                                           r.bounds.size.y / Mathf.Max(1e-4f, Mathf.Abs(s.y)),
                                           r.bounds.size.z / Mathf.Max(1e-4f, Mathf.Abs(s.z)));
                }
                _colliders = new Collider[] { box };
                Debug.LogWarning($"[Gunbound] Lava Pool '{name}' had no collider; added a box around its mesh.");
            }
        }

        void FixedUpdate()
        {
            Sink(); // ragdolls / casings are local to each client, so this runs everywhere

            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;

            if (Time.time >= _nextRefresh || Combatants.Exists(c => c == null))
            {
                _nextRefresh = Time.time + 1f;
                Combatants.Clear();
                Combatants.AddRange(FindObjectsByType<Combatant>(FindObjectsSortMode.None));
            }

            foreach (var c in Combatants)
            {
                if (c == null || !c.IsAlive) continue;
                if (Touching(c, out var where)) c.ServerKillByHazard(DeathCause.Lava, where);
            }
        }

        /// <summary>Bodies (ragdolls, sliced halves, casings) inside the lava are slowed and drift down.</summary>
        void Sink()
        {
            _sinking.Clear();
            int mask = (1 << Layers.Ragdoll) | (1 << Layers.Fx);
            float dt = Time.fixedDeltaTime;
            float k = 1f - Mathf.Exp(-viscosity * dt);
            foreach (var col in _colliders)
            {
                if (col == null || !col.enabled) continue;
                var b = col.bounds;
                int n = Physics.OverlapBoxNonAlloc(b.center, b.extents, SinkBuf, Quaternion.identity, mask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    var rb = SinkBuf[i].attachedRigidbody;
                    if (rb == null || rb.isKinematic || !_sinking.Add(rb)) continue;
                    if (!Inside(col, rb.worldCenterOfMass)) continue;
                    rb.linearVelocity = Vector3.Lerp(rb.linearVelocity, Vector3.down * sinkSpeed, k);
                    rb.angularVelocity = Vector3.Lerp(rb.angularVelocity, Vector3.zero, k);
                    rb.AddForce(-Physics.gravity, ForceMode.Acceleration); // the lava, not gravity, sets the pace
                }
            }
        }

        static bool Inside(Collider col, Vector3 p)
        {
            // ClosestPoint needs a primitive or convex collider; non-convex meshes fall back to their bounds
            if (col is MeshCollider mc && !mc.convex) return col.bounds.Contains(p);
            return (col.ClosestPoint(p) - p).sqrMagnitude < 1e-6f;
        }

        bool Touching(Combatant c, out Vector3 where)
        {
            BodyCapsule(c, out var a, out var b, out float radius);
            where = a;
            int n = Physics.OverlapCapsuleNonAlloc(a, b, radius + touchMargin, Buf, Physics.AllLayers, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
                foreach (var col in _colliders)
                    if (Buf[i] == col)
                    {
                        where = col.ClosestPointOnBounds(a);
                        return true;
                    }
            return false;
        }

        /// <summary>The body as a capsule: the player's CharacterController, else a man-sized capsule at the feet.</summary>
        static void BodyCapsule(Combatant c, out Vector3 bottom, out Vector3 top, out float radius)
        {
            var t = c.transform;
            if (c.TryGetComponent<CharacterController>(out var cc))
            {
                float sXZ = Mathf.Max(Mathf.Abs(t.lossyScale.x), Mathf.Abs(t.lossyScale.z));
                float sY = Mathf.Abs(t.lossyScale.y);
                radius = cc.radius * sXZ;
                float half = Mathf.Max(radius, cc.height * sY * 0.5f);
                Vector3 centre = t.TransformPoint(cc.center);
                bottom = centre - t.up * (half - radius);
                top = centre + t.up * (half - radius);
                return;
            }
            radius = 0.25f;
            bottom = t.position + t.up * radius;
            top = t.position + t.up * 0.9f;
        }
    }
}

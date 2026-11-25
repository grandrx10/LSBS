using System.Collections.Generic;
using UnityEngine;

namespace Gunbound
{
    public static class SurfaceRules
    {
        static readonly Dictionary<int, bool> Cache = new();

        public static bool IsGlass(Collider c)
        {
            if (c == null) return false;
            int id = c.GetInstanceID();
            if (Cache.TryGetValue(id, out bool v)) return v;
            v = Evaluate(c);
            Cache[id] = v;
            return v;
        }

        public static bool CanGrapple(Collider c) => !IsGlass(c);
        public static bool CanWallRun(Collider c) => !IsGlass(c);

        static bool Evaluate(Collider c)
        {
            if (c.GetComponentInParent<GlassSurface>() != null) return true;
            var r = c.GetComponent<Renderer>();
            if (r == null) r = c.GetComponentInParent<Renderer>();
            if (r == null) return false;
            foreach (var m in r.sharedMaterials)
                if (m != null && m.name.StartsWith("Glass", System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Call if you swap materials at runtime.</summary>
        public static void ClearCache() => Cache.Clear();
    }
}

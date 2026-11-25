using UnityEngine;

namespace Gunbound
{
    /// <summary>
    /// Marks an object as glass: it can't be grappled or wall-run on (bullets still ricochet).
    /// You usually don't need this — any material whose name starts with "Glass" does the same.
    /// </summary>
    public class GlassSurface : MonoBehaviour { }
}

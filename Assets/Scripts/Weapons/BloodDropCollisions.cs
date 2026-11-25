using System.Collections.Generic;
using UnityEngine;

namespace Gunbound
{
    /// <summary>Turns blood droplets that hit the world into small stains.</summary>
    [RequireComponent(typeof(ParticleSystem))]
    public class BloodDropCollisions : MonoBehaviour
    {
        public FxManager fx;
        ParticleSystem _ps;
        readonly List<ParticleCollisionEvent> _events = new();

        void Awake() => _ps = GetComponent<ParticleSystem>();

        void OnParticleCollision(GameObject other)
        {
            if (fx == null) return;
            int n = _ps.GetCollisionEvents(other, _events);
            for (int i = 0; i < n; i++)
                fx.OnBloodDropLanded(_events[i].intersection, _events[i].normal);
        }
    }
}

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

namespace Gunbound
{
    /// <summary>Scene entry point: settings, physics layers, VFX, UI and camera.</summary>
    [DefaultExecutionOrder(-100)]
    public class GameBootstrap : MonoBehaviour
    {
        [Tooltip("Optional custom ragdoll prefab (same bone names as the player model). Leave empty to auto-build one.")]
        public GameObject ragdollPrefab;

        [Header("Game format: Free-for-all")]
        public int pointsPerKill = 1;
        [Tooltip("Spawns closer than this to a living enemy are skipped (metres).")]
        public float minSpawnDistance = 15f;
        [Tooltip("Skip spawns that a living enemy can see.")]
        public bool requireNoLineOfSight = true;

        void Awake()
        {
            GameSettings.Load();
            GameSettings.Apply(applyResolution: true);
            Layers.ConfigureCollisionMatrix();
            FxManager.Ensure();
            RagdollFactory.CustomPrefab = ragdollPrefab;
            GameRules.PointsPerKill = pointsPerKill;
            GameRules.MinSpawnDistance = minSpawnDistance;
            GameRules.RequireNoLineOfSight = requireNoLineOfSight;

            if (FindAnyObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
                es.transform.SetParent(transform, false);
            }

            var cam = Camera.main;
            if (cam != null && cam.GetComponent<CameraRig>() == null) cam.gameObject.AddComponent<CameraRig>();

            if (GetComponent<MusicNet>() == null) gameObject.AddComponent<MusicNet>();
            if (GetComponent<MatchManager>() == null)
            {
                gameObject.AddComponent<MatchManager>();
                Debug.LogWarning("[Gunbound] No Match Manager in the scene: rounds run without maps. Use Gunbound > Setup Map Rotation.");
            }
            if (GetComponent<GameUI>() == null) gameObject.AddComponent<GameUI>();
        }
    }
}

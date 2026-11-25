using System.IO;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Gunbound.EditorTools
{
    /// <summary>
    /// Gunbound > Setup Scene: builds the networked player prefab from the scene's "Player",
    /// and adds NetworkManager, SpawnPoint and GameBootstrap to the open scene. Safe to re-run.
    /// </summary>
    public static class GunboundSetup
    {
        const string Root = "Assets/Gunbound";
        const string PrefabPath = "Assets/Prefab/NetPlayer.prefab";

        [MenuItem("Gunbound/Setup Scene")]
        public static void Setup()
        {
            EnsureLayers();
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory("Assets/Prefab");

            var gun = EnsureGunDefinition();
            var gunMat = EnsureMaterial(Root + "/GunBlock.mat", new Color(0.12f, 0.12f, 0.14f), 0.4f, 0.55f);
            var casingMat = EnsureMaterial(Root + "/Casing.mat", new Color(0.85f, 0.62f, 0.22f), 0.9f, 0.7f);

            // ---------- player prefab ----------
            var scenePlayer = GameObject.Find("Player");
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (scenePlayer != null)
            {
                prefab = BuildPlayerPrefab(scenePlayer, gun, gunMat, casingMat);
                // spawn point where the original player stood
                var sp = Object.FindAnyObjectByType<SpawnPoint>();
                if (sp == null)
                {
                    sp = new GameObject("SpawnPoint").AddComponent<SpawnPoint>();
                    sp.transform.SetPositionAndRotation(scenePlayer.transform.position, Quaternion.Euler(0, scenePlayer.transform.eulerAngles.y, 0));
                    Undo.RegisterCreatedObjectUndo(sp.gameObject, "SpawnPoint");
                }
                Undo.RecordObject(scenePlayer, "Disable template player");
                scenePlayer.SetActive(false);
                scenePlayer.name = "Player (template - disabled)";
            }
            else if (prefab == null)
            {
                EditorUtility.DisplayDialog("Gunbound", "Couldn't find a GameObject named \"Player\" in the open scene to build the player prefab from.", "OK");
                return;
            }

            // ---------- network manager ----------
            var nm = Object.FindAnyObjectByType<NetworkManager>();
            if (nm == null)
            {
                var go = new GameObject("NetworkManager");
                Undo.RegisterCreatedObjectUndo(go, "NetworkManager");
                nm = go.AddComponent<NetworkManager>();
            }
            var utp = nm.GetComponent<UnityTransport>();
            if (utp == null) utp = nm.gameObject.AddComponent<UnityTransport>();
            if (nm.NetworkConfig == null) nm.NetworkConfig = new NetworkConfig();
            nm.NetworkConfig.NetworkTransport = utp;
            nm.NetworkConfig.PlayerPrefab = prefab;
            nm.NetworkConfig.TickRate = 60;
            EditorUtility.SetDirty(nm);

            // ---------- bootstrap ----------
            var boot = Object.FindAnyObjectByType<GameBootstrap>();
            if (boot == null)
            {
                var go = new GameObject("Game");
                Undo.RegisterCreatedObjectUndo(go, "Game");
                go.AddComponent<GameBootstrap>();
            }

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            AssetDatabase.SaveAssets();
            Selection.activeObject = prefab;
        }

        [MenuItem("Gunbound/Add Spawn Point")]
        public static void AddSpawnPoint()
        {
            Vector3 pos = Vector3.zero;
            float yaw = 0;
            var sv = SceneView.lastActiveSceneView;
            if (sv != null)
            {
                pos = sv.pivot;
                if (sv.camera != null)
                {
                    Vector3 f = sv.camera.transform.forward; f.y = 0;
                    if (f.sqrMagnitude > 0.001f) yaw = Quaternion.LookRotation(f).eulerAngles.y;
                }
            }
            if (Physics.Raycast(pos + Vector3.up * 50f, Vector3.down, out var hit, 200f))
                pos = hit.point + Vector3.up * 0.97f; // player root sits at the hips

            var parent = GameObject.Find("SpawnPoints");
            if (parent == null)
            {
                parent = new GameObject("SpawnPoints");
                Undo.RegisterCreatedObjectUndo(parent, "SpawnPoints");
            }
            var go = new GameObject("SpawnPoint");
            go.transform.SetParent(parent.transform, true);
            go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0));
            go.AddComponent<SpawnPoint>();
            Undo.RegisterCreatedObjectUndo(go, "Add Spawn Point");
            Selection.activeGameObject = go;
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        }

        /// <summary>
        /// Adds a Match Manager to the scene's Game object and fills its map list with every prefab in
        /// Assets/Prefab whose name starts with "Map". Safe to re-run after adding maps.
        /// </summary>
        [MenuItem("Gunbound/Setup Map Rotation")]
        public static void SetupMapRotation()
        {
            var boot = Object.FindAnyObjectByType<GameBootstrap>();
            if (boot == null)
            {
                EditorUtility.DisplayDialog("Gunbound", "Run Gunbound > Setup Scene first (no Game object in the scene).", "OK");
                return;
            }
            var mm = EnsureMapRotation(boot, true);
            Selection.activeGameObject = boot.gameObject;

            int hills = 0;
            foreach (var m in mm.maps) if (m.GetComponentInChildren<HillZone>(true) != null) hills++;
            EditorUtility.DisplayDialog("Gunbound",
                $"Map rotation: {mm.maps.Count} map(s)\n• " + string.Join("\n• ", mm.maps.ConvertAll(m => m.name)) +
                $"\n\n{hills} of them have a Hill Zone (King of the Hill only runs on those).\n\nSave the scene.", "OK");
        }

        /// <summary>All Assets/Prefab/Map*.prefab, by name.</summary>
        static System.Collections.Generic.List<GameObject> FindMapPrefabs()
        {
            var list = new System.Collections.Generic.List<GameObject>();
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Prefab" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!Path.GetFileNameWithoutExtension(path).StartsWith("Map")) continue;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null) list.Add(prefab);
            }
            list.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return list;
        }

        /// <summary>
        /// Makes sure the Game object has a Match Manager listing every map prefab. Only touches the scene when
        /// something is missing (or when forced). Returns the Match Manager.
        /// </summary>
        public static MatchManager EnsureMapRotation(GameBootstrap boot, bool force)
        {
            var mm = boot.GetComponent<MatchManager>();
            var prefabs = FindMapPrefabs();
            bool changed = false;
            if (mm == null)
            {
                mm = Undo.AddComponent<MatchManager>(boot.gameObject);
                changed = true;
            }
            mm.maps.RemoveAll(m => m == null);
            bool missing = prefabs.Exists(p => !mm.maps.Contains(p));
            if (force || missing || changed)
            {
                Undo.RecordObject(mm, "Setup Map Rotation");
                if (force) mm.maps.Clear();
                foreach (var p in prefabs) if (!mm.maps.Contains(p)) mm.maps.Add(p);
                changed = true;
            }
            if (changed)
            {
                EditorUtility.SetDirty(mm);
                EditorSceneManager.MarkSceneDirty(boot.gameObject.scene);
                Debug.Log($"[Gunbound] Map rotation: {string.Join(", ", mm.maps.ConvertAll(m => m.name))}. Save the scene to keep it.");
            }
            return mm;
        }

        /// <summary>Before Play / a build: hook up any map prefabs that aren't in the rotation yet.</summary>
        public static bool AutoEnsureMapRotation()
        {
            var boot = Object.FindAnyObjectByType<GameBootstrap>();
            if (boot == null) return false;
            int before = boot.GetComponent<MatchManager>() is MatchManager m ? m.maps.Count : -1;
            var mm = EnsureMapRotation(boot, false);
            return mm.maps.Count != before;
        }

        const string DummyPath = "Assets/Prefab/TestDummy.prefab";

        [MenuItem("Gunbound/Add Test Dummy")]
        public static void AddTestDummy()
        {
            var dummyPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DummyPath);
            if (dummyPrefab == null)
            {
                var player = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
                if (player == null)
                {
                    EditorUtility.DisplayDialog("Gunbound", "Run Gunbound > Setup Scene first (the dummy is built from the player prefab).", "OK");
                    return;
                }
                dummyPrefab = BuildDummyPrefab(player);
            }

            // place it where the Scene view is looking, standing on the ground
            Vector3 pos = Vector3.zero;
            float yaw = 0;
            var sv = SceneView.lastActiveSceneView;
            if (sv != null)
            {
                pos = sv.pivot;
                Vector3 camFwd = sv.camera != null ? sv.camera.transform.forward : Vector3.forward;
                yaw = Quaternion.LookRotation(new Vector3(-camFwd.x, 0, -camFwd.z).sqrMagnitude > 0.001f ? new Vector3(-camFwd.x, 0, -camFwd.z) : Vector3.back).eulerAngles.y;
            }
            if (Physics.Raycast(pos + Vector3.up * 50f, Vector3.down, out var hit, 200f))
                pos = hit.point + Vector3.up * 0.97f;

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(dummyPrefab);
            inst.transform.SetPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0));
            Undo.RegisterCreatedObjectUndo(inst, "Add Test Dummy");
            Selection.activeGameObject = inst;
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        }

        static GameObject BuildDummyPrefab(GameObject playerPrefab)
        {
            var root = Object.Instantiate(playerPrefab);
            root.name = "TestDummy";
            // strip player-only parts (order matters: PlayerMotor requires CharacterController)
            Object.DestroyImmediate(root.GetComponent<WeaponController>());
            Object.DestroyImmediate(root.GetComponent<PlayerController>());
            Object.DestroyImmediate(root.GetComponent<PlayerMotor>());
            Object.DestroyImmediate(root.GetComponent<CharacterController>());
            var gun = root.transform.Find("Gun");
            if (gun != null) Object.DestroyImmediate(gun.gameObject);

            var anim = Get<ProceduralAnimator>(root);
            anim.gun = null;

            var dummy = Get<TestDummy>(root);
            dummy.model = root.transform.Find("PlayerModel");

            // solid body so players bump into it (bullets use the hitboxes instead)
            var col = Get<CapsuleCollider>(root);
            col.height = 1.95f;
            col.radius = 0.35f;
            col.center = new Vector3(0, -0.945f + 1.95f * 0.5f, 0);
            int playerLayer = LayerMask.NameToLayer("Player");
            if (playerLayer >= 0) root.layer = playerLayer;

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, DummyPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        static GameObject BuildPlayerPrefab(GameObject scenePlayer, GunDefinition gunDef, Material gunMat, Material casingMat)
        {
            var root = Object.Instantiate(scenePlayer);
            root.name = "NetPlayer";
            root.SetActive(true);
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            // the placeholder capsule is replaced by the CharacterController
            var cap = root.transform.Find("Capsule");
            if (cap != null) Object.DestroyImmediate(cap.gameObject);
            foreach (var c in root.GetComponents<Collider>()) Object.DestroyImmediate(c);

            var model = root.transform.Find("PlayerModel");

            root.layer = LayerMask.NameToLayer("Player");

            var cc = Get<CharacterController>(root);
            cc.height = 1.95f;
            cc.radius = 0.35f;
            cc.center = new Vector3(0, -0.945f + 1.95f * 0.5f, 0);
            cc.stepOffset = 0.4f;
            cc.skinWidth = 0.04f;
            cc.slopeLimit = 50f;
            cc.minMoveDistance = 0f;

            Get<NetworkObject>(root);
            Get<PlayerMotor>(root);
            var pc = Get<PlayerController>(root);
            pc.model = model;
            var anim = Get<ProceduralAnimator>(root);
            var weapon = Get<WeaponController>(root);

            // placeholder gun: a block with grip / muzzle / eject markers you can move when the real model arrives
            var gun = root.transform.Find("Gun");
            if (gun != null) Object.DestroyImmediate(gun.gameObject);
            gun = new GameObject("Gun").transform;
            gun.SetParent(root.transform, false);
            var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = "Model (replace me)";
            Object.DestroyImmediate(block.GetComponent<Collider>());
            block.transform.SetParent(gun, false);
            block.transform.localPosition = new Vector3(0, 0, 0.08f);
            block.transform.localScale = new Vector3(0.05f, 0.09f, 0.36f);
            block.GetComponent<Renderer>().sharedMaterial = gunMat;
            Marker(gun, "GripR", new Vector3(0, -0.045f, -0.06f));
            Marker(gun, "GripL", new Vector3(-0.01f, -0.05f, 0.14f));
            Marker(gun, "Muzzle", new Vector3(0, 0.01f, 0.27f));
            Marker(gun, "Eject", new Vector3(0.03f, 0.03f, 0.04f));

            anim.gun = gun;
            weapon.gunRoot = gun;
            weapon.muzzle = gun.Find("Muzzle");
            weapon.ejectPort = gun.Find("Eject");
            weapon.definition = gunDef;
            weapon.casingMaterial = casingMat;
            InstallGunModel(gun, weapon); // swaps the block for Assets/Models/Gun.prefab if it exists
            AssignWeaponModels(weapon);

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        const string GunModelPath = "Assets/Models/Gun.prefab";
        /// <summary>Scale applied to Gun.prefab (it was modelled ~0.7 m long).</summary>
        const float GunModelScale = 0.4f;

        [MenuItem("Gunbound/Install Gun Model")]
        public static void InstallGunModelMenu()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
            {
                EditorUtility.DisplayDialog("Gunbound", "Run Gunbound > Setup Scene first.", "OK");
                return;
            }
            var contents = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var gun = contents.transform.Find("Gun");
                if (gun == null)
                {
                    gun = new GameObject("Gun").transform;
                    gun.SetParent(contents.transform, false);
                }
                var weapon = contents.GetComponent<WeaponController>();
                if (InstallGunModel(gun, weapon))
                {
                    var anim = contents.GetComponent<ProceduralAnimator>();
                    if (anim != null) anim.gun = gun;
                    PrefabUtility.SaveAsPrefabAsset(contents, PrefabPath);
                }
                else EditorUtility.DisplayDialog("Gunbound", "Couldn't find " + GunModelPath, "OK");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        [MenuItem("Gunbound/Install Weapon Models")]
        public static void InstallWeaponModelsMenu()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
            {
                EditorUtility.DisplayDialog("Gunbound", "Run Gunbound > Setup Scene first.", "OK");
                return;
            }
            var contents = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var weapon = contents.GetComponent<WeaponController>();
                int n = AssignWeaponModels(weapon);
                PrefabUtility.SaveAsPrefabAsset(contents, PrefabPath);
                Debug.Log($"[Gunbound] Stored {n} weapon model reference(s) on {PrefabPath}.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// <summary>
        /// Before a build: make sure the player prefab references every weapon model (builds can't load them by path).
        /// Only rewrites the prefab when something is missing.
        /// </summary>
        public static void EnsureWeaponModelsForBuild()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var wc = prefab != null ? prefab.GetComponent<WeaponController>() : null;
            if (wc == null) return;
            bool missing = false;
            foreach (var w in WeaponCatalog.All)
            {
                if (string.IsNullOrEmpty(w.modelPath)) continue;
                if (!wc.models.Exists(m => m != null && m.id == w.id && m.prefab != null)) { missing = true; break; }
            }
            if (!missing) return;
            InstallWeaponModelsMenu();
            AssetDatabase.SaveAssets();
        }

        /// <summary>Stores each catalog weapon's model prefab (WeaponInfo.modelPath) on the WeaponController.</summary>
        static int AssignWeaponModels(WeaponController weapon)
        {
            if (weapon == null) return 0;
            weapon.models.Clear();
            foreach (var w in WeaponCatalog.All)
            {
                if (string.IsNullOrEmpty(w.modelPath)) continue;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(w.modelPath);
                if (prefab == null) { Debug.LogWarning($"[Gunbound] Missing model for {w.displayName}: {w.modelPath}"); continue; }
                weapon.models.Add(new WeaponController.WeaponModel { id = w.id, prefab = prefab });
            }
            return weapon.models.Count;
        }

        /// <summary>Replaces everything under the Gun transform with the Gun.prefab model and wires up markers.</summary>
        static bool InstallGunModel(Transform gun, WeaponController weapon)
        {
            var src = AssetDatabase.LoadAssetAtPath<GameObject>(GunModelPath);
            if (src == null) return false;

            for (int i = gun.childCount - 1; i >= 0; i--) Object.DestroyImmediate(gun.GetChild(i).gameObject);

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(src, gun);
            inst.name = "GunModel";
            // model barrel points along +X; the Gun transform aims along +Z
            inst.transform.localRotation = Quaternion.Euler(0, -90, 0);
            inst.transform.localScale = Vector3.one * GunModelScale;
            inst.transform.localPosition = new Vector3(0, -0.045f, -0.06f); // where the Trigger sits relative to the eyes' aim line

            var trigger = Deep(inst.transform, "Trigger");
            var barrel = Deep(inst.transform, "BarrelPoint");
            var slide = Deep(inst.transform, "Slide");

            // The hand is placed on the model's "TriggerPoint" at runtime (see ProceduralAnimator).
            // Eject marker lives inside the model (model units: +X = forward, +Y = up, +Z = left).
            Marker(slide != null ? slide : inst.transform, "Eject", new Vector3(0.05f, 0.17f, -0.06f));

            if (weapon != null)
            {
                weapon.gunRoot = gun;
                weapon.muzzle = barrel;
                weapon.ejectPort = Deep(inst.transform, "Eject");
                weapon.slide = slide;
            }
            return true;
        }

        static Transform Deep(Transform root, string name)
        {
            if (root.name == name) return root;
            foreach (Transform c in root)
            {
                var f = Deep(c, name);
                if (f != null) return f;
            }
            return null;
        }

        static T Get<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : go.AddComponent<T>();
        }

        static void Marker(Transform parent, string name, Vector3 pos)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = pos;
        }

        static GunDefinition EnsureGunDefinition()
        {
            string path = Root + "/DefaultGun.asset";
            var g = AssetDatabase.LoadAssetAtPath<GunDefinition>(path);
            if (g == null)
            {
                g = ScriptableObject.CreateInstance<GunDefinition>();
                AssetDatabase.CreateAsset(g, path);
            }
            return g;
        }

        static Material EnsureMaterial(string path, Color c, float metallic, float smooth)
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            m = new Material(shader);
            m.SetColor("_BaseColor", c);
            m.color = c;
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_Smoothness", smooth);
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        static void EnsureLayers()
        {
            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layers = tagManager.FindProperty("layers");
            string[] wanted = { Layers.PlayerName, Layers.HitboxName, Layers.RagdollName, Layers.FxName };
            int[] preferred = { 8, 9, 10, 11 };
            for (int i = 0; i < wanted.Length; i++)
            {
                if (LayerMask.NameToLayer(wanted[i]) >= 0) continue;
                int slot = -1;
                var p = layers.GetArrayElementAtIndex(preferred[i]);
                if (string.IsNullOrEmpty(p.stringValue)) slot = preferred[i];
                else
                    for (int l = 8; l < 32; l++)
                        if (string.IsNullOrEmpty(layers.GetArrayElementAtIndex(l).stringValue)) { slot = l; break; }
                if (slot < 0) continue;
                layers.GetArrayElementAtIndex(slot).stringValue = wanted[i];
                tagManager.ApplyModifiedPropertiesWithoutUndo();
            }
        }
    }
}

using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace LSBS.EditorTools
{
    /// <summary>LSBS menu: builds the soldiers, the scene and the render settings, and player builds.</summary>
    public static class LSBSSetup
    {
        const string Scene = "Assets/LSBS/LSBS.unity", Pipeline = "Assets/LSBS/Generated/LSBS_RPAsset.asset", RendererPath = "Assets/LSBS/Generated/LSBS_Renderer.asset";

        [MenuItem("LSBS/Rebuild Everything")]
        public static void All()
        {
            var data = SoldierBaker.Build();
            var pipe = MakePipeline();
            // an existing scene is left alone: it has your settings in it
            if (!System.IO.File.Exists(Scene)) MakeScene(data, pipe);
            Debug.Log("[LSBS] set up");
        }

        /// <summary>The project's own pipeline, without Gunbound's cartoon outline, and with shadows that reach further.</summary>
        static RenderPipelineAsset MakePipeline()
        {
            var had = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Pipeline);
            if (had != null && AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath) != null) return had;
            AssetDatabase.CopyAsset("Assets/Settings/PC_Renderer.asset", RendererPath);
            AssetDatabase.CopyAsset("Assets/Settings/PC_RPAsset.asset", Pipeline);
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            foreach (var f in renderer.rendererFeatures.Where(f => f is FullScreenPassRendererFeature)) f.SetActive(false);
            EditorUtility.SetDirty(renderer);
            var rp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Pipeline);
            rp.shadowDistance = 90f;
            rp.shadowCascadeCount = 2;
            rp.msaaSampleCount = 1;
            rp.supportsHDR = true;
            var so = new SerializedObject(rp);
            so.FindProperty("m_RendererDataList").GetArrayElementAtIndex(0).objectReferenceValue = renderer;
            so.FindProperty("m_DefaultRendererIndex").intValue = 0;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(rp);
            AssetDatabase.SaveAssets();
            return rp;
        }

        /// <summary>The first time the project's opened with this in it: its name, and its scene at the top of the build.</summary>
        [InitializeOnLoadMethod]
        static void FirstOpen()
        {
            if (Application.isBatchMode || !System.IO.File.Exists(Scene) || EditorBuildSettings.scenes.Any(s => s.path == Scene)) return;
            AddToBuild();
        }

        static void AddToBuild()
        {
            var list = EditorBuildSettings.scenes.Where(s => s.path != Scene).Select(s => new EditorBuildSettingsScene(s.path, false)).ToList();
            list.Insert(0, new EditorBuildSettingsScene(Scene, true));
            EditorBuildSettings.scenes = list.ToArray();
            PlayerSettings.productName = "L.S.B.S";
        }

        static void MakeScene(SoldierData data, RenderPipelineAsset pipe)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cam = new GameObject("Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            cam.transform.position = new Vector3(-70, 26, -60);
            var b = new GameObject("Battle").AddComponent<Battle>();
            b.tuning = Tuning.Default;
            b.soldiers = data;
            b.pipeline = pipe;
            b.soldierShader = Shader.Find("LSBS/Soldier");
            b.worldShader = Shader.Find("LSBS/World");
            b.fxShader = Shader.Find("LSBS/Fx");
            b.skyShader = Shader.Find("LSBS/Sky");
            RenderSettings.fog = true; // so builds keep the fog variants
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            EditorSceneManager.SaveScene(scene, Scene);
            AddToBuild();
        }

        [MenuItem("LSBS/Build Game")]
        public static void Build() => BuildTo("Builds/LSBS/LSBS.exe");

        static void BuildTo(string path)
        {
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { Scene }, locationPathName = path, target = BuildTarget.StandaloneWindows64, options = BuildOptions.None,
            });
            Debug.Log($"[LSBS] build {report.summary.result}, {report.summary.totalErrors} errors, {path}");
            if (Application.isBatchMode && report.summary.result != BuildResult.Succeeded) EditorApplication.Exit(1);
        }

        /// <summary>For the command line: everything, then a build.</summary>
        public static void Batch()
        {
            All();
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1920;
            PlayerSettings.defaultScreenHeight = 1080;
            PlayerSettings.runInBackground = true;
            Build();
        }
    }
}

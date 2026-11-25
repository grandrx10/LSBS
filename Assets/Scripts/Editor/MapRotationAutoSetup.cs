using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;

namespace Gunbound.EditorTools
{
    /// <summary>
    /// Every map prefab (Assets/Prefab/Map*.prefab) is put in the scene's map rotation automatically when you press
    /// Play and before a build — no need to run Gunbound > Setup Map Rotation by hand.
    /// </summary>
    [InitializeOnLoad]
    public static class MapRotationAutoSetup
    {
        static MapRotationAutoSetup()
        {
            EditorApplication.playModeStateChanged += s =>
            {
                if (s == PlayModeStateChange.ExitingEditMode) GunboundSetup.AutoEnsureMapRotation();
            };
        }
    }

    public class MapRotationBuildStep : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;
        public void OnPreprocessBuild(BuildReport report)
        {
            // builds use the saved scene, so save it if maps had to be added
            if (GunboundSetup.AutoEnsureMapRotation()) EditorSceneManager.SaveOpenScenes();
        }
    }
}

using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Gunbound.EditorTools
{
    /// <summary>Runs Gunbound > Install Weapon Models automatically before every build if any model reference is missing.</summary>
    public class WeaponModelsBuildStep : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;
        public void OnPreprocessBuild(BuildReport report) => GunboundSetup.EnsureWeaponModelsForBuild();
    }
}

using System.IO;
using UnityEditor;
using UnityEngine;

namespace LiftoffFpvGogglesBuild
{
    /// <summary>
    /// Builds the shader into an AssetBundle the plugin loads at runtime.
    ///
    /// Driven from build-bundle.ps1 rather than from the editor UI, so the whole thing stays a
    /// one-command build and nobody has to remember which menu item to click. Run by hand with:
    ///
    ///   Unity.exe -batchmode -quit -projectPath unity -executeMethod LiftoffFpvGogglesBuild.BuildBundle.Build
    /// </summary>
    public static class BuildBundle
    {
        private const string BundleName = "fpvanalog";
        private const string ShaderPath = "Assets/Shaders/FpvComposite.shader";
        private const string OutputDirectory = "../assets";

        [MenuItem("FPV Goggles/Build Asset Bundle")]
        public static void Build()
        {
            // Set from code rather than in the .meta file, so the assignment cannot silently go
            // missing when the meta is regenerated on a fresh clone.
            AssetImporter importer = AssetImporter.GetAtPath(ShaderPath);
            if (importer == null)
            {
                Fail("Shader not found at " + ShaderPath);
                return;
            }

            importer.assetBundleName = BundleName;
            importer.SaveAndReimport();

            // A shader that failed to compile still gets packed, and the bundle still builds, and
            // the mod then loads a shader that draws nothing. Ask before shipping it.
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null)
            {
                Fail("The shader did not import at all.");
                return;
            }
            if (ShaderUtil.ShaderHasError(shader))
            {
                Fail("The shader has compile errors - see the 'Shader error in' lines above.");
                return;
            }

            string output = Path.GetFullPath(Path.Combine(Application.dataPath, "../" + OutputDirectory));
            Directory.CreateDirectory(output);

            AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
                output,
                BuildAssetBundleOptions.None,
                BuildTarget.StandaloneWindows64);

            if (manifest == null)
            {
                Fail("BuildPipeline.BuildAssetBundles returned nothing.");
                return;
            }

            string bundle = Path.Combine(output, BundleName);
            if (!File.Exists(bundle))
            {
                Fail("Expected a bundle at " + bundle + " and it is not there.");
                return;
            }

            Debug.Log("FPV Goggles: built " + bundle + " (" + new FileInfo(bundle).Length + " bytes)");
        }

        /// <summary>
        /// Batch mode ignores a failed build unless the exit code says so, and a silently
        /// missing bundle would only turn up as a broken effect much later.
        /// </summary>
        private static void Fail(string message)
        {
            Debug.LogError("FPV Goggles: " + message);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }
}

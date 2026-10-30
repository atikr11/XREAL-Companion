using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor.Build.Reporting;

namespace XREALCompanionStandalone.Editor
{
    public static class BuildXREALCompanion
    {
        private const string ScenePath = "Assets/XREALCompanionStandalone/Scenes/Air2Companion.unity";
        private const string OutputDirectory = "Builds/XREALCompanionAir2";
        private const string OutputPath = OutputDirectory + "/XREAL Companion.exe";

        [MenuItem("XREAL Companion/Build Air 2 Prototype")]
        public static void BuildFromMenu()
        {
            CreateScene();
            Build();
        }

        public static void BuildFromCommandLine()
        {
            try
            {
                CreateScene();
                Build();
                EditorApplication.Exit(0);
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        private static void CreateScene()
        {
            Directory.CreateDirectory("Assets/XREALCompanionStandalone/Scenes");
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject app = new GameObject("XREAL Companion App");
            app.AddComponent<XREALCompanionApp>();
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new System.InvalidOperationException("Could not save prototype scene to " + ScenePath);

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log("[XREAL Companion] Created direct USB prototype scene; no XREAL tracking session or loader startup.");
        }

        private static void Build()
        {
            PlayerSettings.SplashScreen.show = false;
            PlayerSettings.SplashScreen.showUnityLogo = false;
            if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64))
                throw new System.InvalidOperationException("Could not select Windows x86_64 build target.");

            Directory.CreateDirectory(OutputDirectory);
            BuildPlayerOptions options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = OutputPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
                throw new System.InvalidOperationException("Windows build failed: " + report.summary.result +
                    ", errors=" + report.summary.totalErrors + ". See the Unity Editor log for details.");

            Debug.Log("[XREAL Companion] Windows build ready: " + Path.GetFullPath(OutputPath));
            foreach (string name in new[] { "README.md", "THIRD-PARTY-NOTICES.txt", "ar-drivers-LICENSE.txt", "Apache-2.0-Vosk.txt" })
                File.Copy("Assets/XREALCompanionStandalone/" + name, OutputDirectory + "/" + name, true);
        }
    }
}


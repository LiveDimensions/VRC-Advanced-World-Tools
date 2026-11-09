using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UdonSharp.Updater;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Udon;
using Object = UnityEngine.Object;

namespace LiveDimensions.UdonSharpFastBuild
{
    /// <summary>
    /// Proves UdonSharp Fast Build gives the same result as UdonSharp's original scene build step.
    /// Runs the UdonSharp build step (UdonSharpEditorManager.OnSceneBuildInternal, the same call a VRChat build makes) on the
    /// open scene twice, first the original way and then the fast way, each time on a freshly loaded copy of the scene.
    /// After each run it records every UdonBehaviour's program and public variables (scene and intermediate prefabs U#
    /// generates for the build), then compares both. Nothing is saved, the scene is reloaded at the end.
    /// The report is written to Logs/UdonSharpFastBuildVerify_*.txt.
    /// </summary>
    internal static class UdonSharpFastSceneBuildVerifier
    {
        private const string Title = "Verify UdonSharp Fast Build";

        [MenuItem(UdonSharpFastSceneBuild.MenuRoot + "Verify On Open Scene (slow)", false, 20)]
        private static void Verify()
        {
            if (!UdonSharpFastSceneBuild.IsActive)
            {
                EditorUtility.DisplayDialog(Title, "UdonSharp Fast Build isn't active, there's nothing to compare.", "Ok");
                return;
            }

            if (SceneManager.sceneCount != 1 || string.IsNullOrEmpty(SceneManager.GetActiveScene().path))
            {
                EditorUtility.DisplayDialog(Title, "Open a single saved scene (the scene you build) first.", "Ok");
                return;
            }

            Scene scene = SceneManager.GetActiveScene();
            string scenePath = scene.path;

            if (!EditorUtility.DisplayDialog(Title,
                    $"This runs UdonSharp's build step on '{scene.name}' twice (original, then fast) and compares every UdonBehaviour.\n\n" +
                    "The original run takes about as long as UdonSharp's step in a build (minutes for large worlds), Unity is unresponsive meanwhile.\n\n" +
                    (scene.isDirty ? "The scene has unsaved changes, they will be DISCARDED. " : "") +
                    "The scene is never saved and is reloaded at the end.",
                    "Run", "Cancel"))
                return;

            var sceneBuild = AccessTools.Method("UdonSharpEditor.UdonSharpEditorManager:OnSceneBuildInternal");
            string prefabFolder = UdonSharpLocator.IntermediatePrefabPath.Replace('\\', '/');

            var report = new StringBuilder();
            try
            {
                EditorUtility.DisplayProgressBar(Title, "Original UdonSharp scene build (slow)...", 0.1f);
                var (original, originalSeconds) = RunAndSnapshot(sceneBuild, scenePath, prefabFolder, bypass: true);

                EditorUtility.DisplayProgressBar(Title, "Fast UdonSharp scene build...", 0.8f);
                var (fast, fastSeconds) = RunAndSnapshot(sceneBuild, scenePath, prefabFolder, bypass: false);

                Compare(original, fast, originalSeconds, fastSeconds, scenePath, report);
            }
            catch (Exception e)
            {
                report.AppendLine("Verification failed with an exception:");
                report.AppendLine(e.ToString());
                Debug.LogException(e);
            }
            finally
            {
                UdonSharpFastSceneBuild.Bypass = false;
                UdonSharpFastSceneBuild.ForceMappingFix = false;
                EditorUtility.ClearProgressBar();
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            }

            Directory.CreateDirectory("Logs");
            string reportPath = Path.Combine("Logs", $"UdonSharpFastBuildVerify_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(reportPath, report.ToString());

            string summary = report.ToString().Split('\n').FirstOrDefault()?.Trim();
            Debug.Log($"{UdonSharpFastSceneBuild.LogTag} {summary} Report: {Path.GetFullPath(reportPath)}");
            EditorUtility.DisplayDialog(Title, $"{summary}\n\nReport: {reportPath}", "Ok");
        }

        private static (Dictionary<string, string> snapshot, double seconds) RunAndSnapshot(MethodInfo sceneBuild, string scenePath, string prefabFolder, bool bypass)
        {
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            DateTime start = DateTime.Now;
            var timer = System.Diagnostics.Stopwatch.StartNew();

            var snapshot = new Dictionary<string, string>();

            UdonSharpFastSceneBuild.Bypass = bypass;
            //U# can throw "Component layout mismatch in prefab mapping" in builds and then skips the rest of its remapping, in
            //an order that changes with every scene load. The fix makes both runs complete the same way so they can be compared
            //(the fast path's refresh of behaviours outside the scene covers the case without the fix)
            UdonSharpFastSceneBuild.ForceMappingFix = true;
            try
            {
                //same call a VRChat build makes (isBuildingPlayer: true strips the proxies like a build does)
                sceneBuild.Invoke(null, new object[] { true, null });
            }
            catch (TargetInvocationException e)
            {
                //recorded in both snapshots so a difference shows up in the comparison
                snapshot["exception"] = e.InnerException?.Message ?? e.Message;
                Debug.LogException(e.InnerException ?? e);
            }
            finally
            {
                UdonSharpFastSceneBuild.Bypass = false;
                UdonSharpFastSceneBuild.ForceMappingFix = false;
            }

            double seconds = timer.Elapsed.TotalSeconds;

            //scene behaviours
            foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
                foreach (UdonBehaviour behaviour in root.GetComponentsInChildren<UdonBehaviour>(true))
                    snapshot["scene:" + ComponentPath(behaviour)] = Describe(behaviour);

            //intermediate prefabs U# generated in this run
            if (AssetDatabase.IsValidFolder(prefabFolder))
            {
                foreach (string guid in AssetDatabase.FindAssets("t:GameObject", new[] { prefabFolder }))
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (File.GetLastWriteTime(assetPath) < start) continue;

                    GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                    if (prefab == null) continue;

                    foreach (UdonBehaviour behaviour in prefab.GetComponentsInChildren<UdonBehaviour>(true))
                        snapshot[$"prefab:{assetPath}:{ComponentPath(behaviour)}"] = Describe(behaviour);
                }
            }

            return (snapshot, seconds);
        }

        private static void Compare(Dictionary<string, string> original, Dictionary<string, string> fast, double originalSeconds, double fastSeconds, string scenePath, StringBuilder report)
        {
            var onlyOriginal = original.Keys.Except(fast.Keys).OrderBy(k => k).ToList();
            var onlyFast = fast.Keys.Except(original.Keys).OrderBy(k => k).ToList();
            var different = original.Keys.Intersect(fast.Keys).Where(k => original[k] != fast[k]).OrderBy(k => k).ToList();

            bool identical = onlyOriginal.Count == 0 && onlyFast.Count == 0 && different.Count == 0;
            int sceneCount = original.Keys.Count(k => k.StartsWith("scene:"));
            int prefabCount = original.Keys.Count(k => k.StartsWith("prefab:"));

            report.AppendLine(identical
                ? $"IDENTICAL: all {sceneCount + prefabCount} UdonBehaviours match ({sceneCount} in the scene, {prefabCount} in build prefabs)."
                : $"DIFFERENT: {different.Count} UdonBehaviours differ, {onlyOriginal.Count} only in the original, {onlyFast.Count} only in the fast run.");
            report.AppendLine();
            report.AppendLine($"Scene: {scenePath}");
            report.AppendLine($"Original UdonSharp scene build: {originalSeconds:F1}s");
            report.AppendLine($"Fast UdonSharp scene build:     {fastSeconds:F1}s");
            report.AppendLine("Compared: program asset, enabled state and every public variable (Unity object references by scene path / asset GUID)");
            report.AppendLine("Both runs used the U# prefab remapping fix so they complete the same way.");
            if (original.TryGetValue("exception", out string originalException)) report.AppendLine($"Original run threw: {originalException}");
            if (fast.TryGetValue("exception", out string fastException)) report.AppendLine($"Fast run threw: {fastException}");
            report.AppendLine();

            void List(string title, List<string> keys, Func<string, string> details)
            {
                if (keys.Count == 0) return;
                report.AppendLine($"{title} ({keys.Count}):");
                foreach (string key in keys.Take(200))
                {
                    report.AppendLine("  " + key);
                    if (details != null) report.AppendLine(details(key));
                }
                report.AppendLine();
            }

            List("Different", different, key => DiffLines(original[key], fast[key]));
            List("Only in original", onlyOriginal, null);
            List("Only in fast", onlyFast, null);
        }

        private static string DiffLines(string a, string b)
        {
            var linesA = a.Split('\n');
            var linesB = b.Split('\n');
            var sb = new StringBuilder();

            foreach (string line in linesA.Except(linesB).Take(20)) sb.AppendLine("    original: " + line);
            foreach (string line in linesB.Except(linesA).Take(20)) sb.AppendLine("    fast:     " + line);

            return sb.ToString().TrimEnd();
        }

        private static string Describe(UdonBehaviour behaviour)
        {
            var sb = new StringBuilder();

            var programAsset = new SerializedObject(behaviour).FindProperty("serializedProgramAsset")?.objectReferenceValue;
            sb.Append("program=").Append(ObjectId(programAsset)).Append('\n');
            sb.Append("enabled=").Append(behaviour.enabled).Append('\n');

            var variables = behaviour.publicVariables;
            foreach (string symbol in variables.VariableSymbols.OrderBy(s => s, StringComparer.Ordinal))
            {
                variables.TryGetVariableValue(symbol, out object value);
                sb.Append(symbol).Append('=').Append(FormatValue(value, 0)).Append('\n');
            }

            return sb.ToString();
        }

        private static string FormatValue(object value, int depth)
        {
            switch (value)
            {
                case null:
                    return "null";
                case Object unityObject:
                    return ObjectId(unityObject);
                case string text:
                    return "\"" + text + "\"";
                case IFormattable formattable when !(value is Enum):
                    //round-trip format so float based values (float, Vector3, Quaternion, Color, ...) compare exactly
                    string formatted;
                    try { formatted = formattable.ToString("R", CultureInfo.InvariantCulture); }
                    catch (FormatException) { formatted = formattable.ToString(null, CultureInfo.InvariantCulture); }
                    return value.GetType().Name + ":" + formatted;
                case IEnumerable enumerable when depth < 4:
                    return value.GetType().Name + "[" + string.Join(", ", enumerable.Cast<object>().Select(e => FormatValue(e, depth + 1))) + "]";
                default:
                    return value.GetType().Name + ":" + value;
            }
        }

        //stable id for an object that's the same across scene reloads
        internal static string ObjectId(Object obj)
        {
            if (obj == null) return "null";

            if (EditorUtility.IsPersistent(obj))
            {
                string assetPath = AssetDatabase.GetAssetPath(obj);

                //objects inside the generated build prefabs get new file ids when the prefab is saved again, use the hierarchy path instead
                if (assetPath.Replace('\\', '/').StartsWith(UdonSharpLocator.IntermediatePrefabPath.Replace('\\', '/')))
                {
                    if (obj is GameObject go) return $"asset:{assetPath}:{GameObjectPath(go)}";
                    if (obj is Component c) return $"asset:{assetPath}:{ComponentPath(c)}";
                }

                return AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string guid, out long localId)
                    ? $"asset:{guid}:{localId}"
                    : $"asset:{assetPath}:{obj.name}";
            }

            if (obj is GameObject gameObject) return "go:" + GameObjectPath(gameObject);
            if (obj is Component component) return "comp:" + ComponentPath(component);
            return $"{obj.GetType().Name}:{obj.name}";
        }

        internal static string GameObjectPath(GameObject gameObject)
        {
            var parts = new List<string>();
            for (Transform t = gameObject.transform; t != null; t = t.parent)
                parts.Add($"{t.name}[{t.GetSiblingIndex()}]");
            parts.Reverse();
            return string.Join("/", parts);
        }

        internal static string ComponentPath(Component component)
        {
            Type type = component.GetType();
            int index = Array.IndexOf(component.GetComponents(type), component);
            return $"{GameObjectPath(component.gameObject)}/{type.Name}#{index}";
        }
    }
}

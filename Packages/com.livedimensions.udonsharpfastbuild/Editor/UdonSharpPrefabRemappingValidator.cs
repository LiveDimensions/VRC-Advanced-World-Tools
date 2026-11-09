using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UdonSharp;
using UdonSharp.Updater;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Udon;
using Object = UnityEngine.Object;

namespace LiveDimensions.UdonSharpFastBuild
{
    /// <summary>
    /// Checks UdonSharp's prefab remapping in a build, with and without the remapping fix.
    ///
    /// When building, U# saves a build copy of every prefab with UdonSharpBehaviours that the scene references, and should
    /// point every scene reference to such a prefab at the same object in the build copy. This records every reference from
    /// a scene UdonSharpBehaviour into a prefab asset before U#'s build step, runs the step, and checks every reference
    /// against an independently worked out target: the object at the same hierarchy path in the build copy, and for
    /// components the same type and index among components of that type (a stripped proxy maps to its backing UdonBehaviour).
    /// It also checks the build copies themselves for references still pointing at original prefabs.
    ///
    /// Runs twice on freshly loaded copies of the open scene: U# as is, then with the fix. Nothing is saved.
    /// The report is written to Logs/UdonSharpPrefabRemapping_*.txt.
    /// </summary>
    internal static class UdonSharpPrefabRemappingValidator
    {
        private const string Title = "Validate UdonSharp Prefab Remapping";

        private class Reference
        {
            public UdonBehaviour behaviour;
            public string location;
            public string symbol;
            public string element;
            public Object before;
        }

        private class RunResult
        {
            public string name;
            public string exception;
            public double seconds;
            public int intoCopied, correct, leftOnOriginal, wrong, noCounterpart, intoNotCopied, notCopiedChanged, copyLeftovers;
            public readonly List<string> wrongDetails = new List<string>();
            public readonly List<string> noCounterpartDetails = new List<string>();
            public readonly List<string> leftDetails = new List<string>();
            public readonly List<string> notCopiedChangedDetails = new List<string>();
            public readonly List<string> copyLeftoverDetails = new List<string>();
            public readonly HashSet<string> leftPrefabs = new HashSet<string>();
            public readonly List<string> repaired = new List<string>();
            public int buildCopies;
        }

        [MenuItem(UdonSharpFastSceneBuild.MenuRoot + "Validate Prefab Remapping On Open Scene (slow)", false, 21)]
        private static void Validate()
        {
            if (!UdonSharpFastSceneBuild.IsActive)
            {
                EditorUtility.DisplayDialog(Title, "UdonSharp Fast Build isn't active (it provides the remapping fix).", "Ok");
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
                    $"This runs UdonSharp's build step on '{scene.name}' twice (UdonSharp as is, then with the remapping fix) and checks " +
                    "every reference from the scene into prefabs.\n\nEach run takes about a minute for large worlds, Unity is unresponsive meanwhile.\n\n" +
                    (scene.isDirty ? "The scene has unsaved changes, they will be DISCARDED. " : "") +
                    "The scene is never saved and is reloaded at the end.",
                    "Run", "Cancel"))
                return;

            var sceneBuild = AccessTools.Method("UdonSharpEditor.UdonSharpEditorManager:OnSceneBuildInternal");
            var runs = new List<RunResult>();
            var report = new StringBuilder();

            try
            {
                EditorUtility.DisplayProgressBar(Title, "UdonSharp as is...", 0.2f);
                runs.Add(Run(sceneBuild, scenePath, "UdonSharp as is", fix: false));

                EditorUtility.DisplayProgressBar(Title, "With the remapping fix...", 0.7f);
                runs.Add(Run(sceneBuild, scenePath, "With the remapping fix", fix: true));

                WriteReport(runs, scenePath, report);
            }
            catch (Exception e)
            {
                report.Insert(0, "Validation failed with an exception:\n" + e + "\n\n");
                Debug.LogException(e);
            }
            finally
            {
                UdonSharpFastSceneBuild.ForceMappingFix = false;
                EditorUtility.ClearProgressBar();
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            }

            Directory.CreateDirectory("Logs");
            string reportPath = Path.Combine("Logs", $"UdonSharpPrefabRemapping_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(reportPath, report.ToString());

            string summary = report.ToString().Split('\n').FirstOrDefault()?.Trim();
            Debug.Log($"{UdonSharpFastSceneBuild.LogTag} {summary} Report: {Path.GetFullPath(reportPath)}");
            EditorUtility.DisplayDialog(Title, $"{summary}\n\nReport: {reportPath}", "Ok");
        }

        private static RunResult Run(MethodInfo sceneBuild, string scenePath, string name, bool fix)
        {
            var result = new RunResult { name = name };
            string copyFolder = UdonSharpLocator.IntermediatePrefabPath.Replace('\\', '/');

            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            DateTime start = DateTime.Now;

            //what the references are before U# remaps them: copy every proxy to its UdonBehaviour like U#'s build step does
            //(it re-copies them itself, this only fills in the same data first so it can be read)
            var references = new List<Reference>();
            foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                foreach (UdonSharpBehaviour proxy in root.GetComponentsInChildren<UdonSharpBehaviour>(true))
                {
                    UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);
                    if (backing == null) continue;

                    UdonSharpEditorUtility.CopyProxyToUdon(proxy, ProxySerializationPolicy.RootOnly);

                    string location = UdonSharpFastSceneBuildVerifier.ComponentPath(backing);
                    foreach (var (symbol, element, value) in ReadReferences(backing))
                    {
                        if (!IsPrefabAssetObject(value, copyFolder)) continue;
                        references.Add(new Reference { behaviour = backing, location = location, symbol = symbol, element = element, before = value });
                    }
                }
            }

            var timer = System.Diagnostics.Stopwatch.StartNew();
            UdonSharpFastSceneBuild.ForceMappingFix = fix;
            try
            {
                //same call a VRChat build makes
                sceneBuild.Invoke(null, new object[] { true, null });
            }
            catch (TargetInvocationException e)
            {
                result.exception = e.InnerException?.Message ?? e.Message;
            }
            finally
            {
                UdonSharpFastSceneBuild.ForceMappingFix = false;
            }
            result.seconds = timer.Elapsed.TotalSeconds;

            foreach (var (original, copy) in UdonSharpFastSceneBuild.FixedMappings)
                result.repaired.Add($"{AssetDatabase.GetAssetPath(original)}: {UdonSharpFastSceneBuildVerifier.GameObjectPath(original)}");

            //build copies U# wrote in this run, by the path of their original prefab
            var copies = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            GameObject CopyOf(string originalPath)
            {
                if (copies.TryGetValue(originalPath, out GameObject cached)) return cached;

                string copyPath = Path.Combine(UdonSharpLocator.IntermediatePrefabPath, originalPath).Replace('\\', '/');
                GameObject copy = File.Exists(copyPath) && File.GetLastWriteTime(copyPath) >= start ? AssetDatabase.LoadAssetAtPath<GameObject>(copyPath) : null;
                copies[originalPath] = copy;
                return copy;
            }

            //scene references
            foreach (var group in references.GroupBy(r => r.behaviour))
            {
                Dictionary<string, Object> after = group.Key != null
                    ? ReadReferences(group.Key).ToDictionary(r => r.symbol + r.element, r => r.value)
                    : new Dictionary<string, Object>();

                foreach (Reference reference in group)
                {
                    after.TryGetValue(reference.symbol + reference.element, out Object now);
                    string originalPath = AssetDatabase.GetAssetPath(reference.before);
                    GameObject copy = CopyOf(originalPath);
                    string where = $"{reference.location} .{reference.symbol}{reference.element}";

                    if (copy == null)
                    {
                        result.intoNotCopied++;
                        if (now != reference.before)
                        {
                            result.notCopiedChanged++;
                            result.notCopiedChangedDetails.Add($"{where}: {Id(reference.before)} -> {Id(now)}");
                        }
                        continue;
                    }

                    result.intoCopied++;
                    Object expected = Counterpart(reference.before, copy);

                    if (expected == null)
                    {
                        result.noCounterpart++;
                        result.noCounterpartDetails.Add($"{where}: {Id(reference.before)}, now {Id(now)}");
                    }
                    else if (now == expected)
                    {
                        result.correct++;
                    }
                    else if (now == reference.before)
                    {
                        result.leftOnOriginal++;
                        result.leftPrefabs.Add(originalPath);
                        result.leftDetails.Add($"{where}: {Id(reference.before)}");
                    }
                    else
                    {
                        result.wrong++;
                        result.wrongDetails.Add($"{where}: was {Id(reference.before)}, expected {Id(expected)}, got {Id(now)}");
                    }
                }
            }

            //references inside the build copies to original prefabs that have a build copy
            foreach (string guid in AssetDatabase.FindAssets("t:GameObject", new[] { copyFolder }))
            {
                string copyPath = AssetDatabase.GUIDToAssetPath(guid);
                if (File.GetLastWriteTime(copyPath) < start) continue;

                GameObject copy = AssetDatabase.LoadAssetAtPath<GameObject>(copyPath);
                if (copy == null) continue;
                result.buildCopies++;

                foreach (UdonBehaviour behaviour in copy.GetComponentsInChildren<UdonBehaviour>(true))
                {
                    foreach (var (symbol, element, value) in ReadReferences(behaviour))
                    {
                        if (!IsPrefabAssetObject(value, copyFolder) || CopyOf(AssetDatabase.GetAssetPath(value)) == null) continue;

                        result.copyLeftovers++;
                        result.copyLeftoverDetails.Add($"{copyPath}: {UdonSharpFastSceneBuildVerifier.ComponentPath(behaviour)} .{symbol}{element} -> {Id(value)}");
                    }
                }
            }

            return result;
        }

        private static void WriteReport(List<RunResult> runs, string scenePath, StringBuilder report)
        {
            RunResult asIs = runs[0], fixedRun = runs[1];

            bool fixOk = fixedRun.exception == null && fixedRun.wrong == 0 && fixedRun.noCounterpart == 0 && fixedRun.leftOnOriginal == 0 &&
                         fixedRun.copyLeftovers == 0 && fixedRun.notCopiedChanged == 0;

            report.AppendLine(fixOk
                ? $"FIX OK: with the fix all {fixedRun.correct} references into build-copied prefabs point at the right object; without it {asIs.leftOnOriginal} stay on the original prefab" +
                  (asIs.exception != null ? " (UdonSharp threw)." : ".")
                : $"CHECK REPORT: with the fix {fixedRun.wrong} wrong, {fixedRun.noCounterpart} without counterpart, {fixedRun.leftOnOriginal} left on original, " +
                  $"{fixedRun.copyLeftovers} leftovers in build copies" + (fixedRun.exception != null ? ", UdonSharp threw" : "") + ".");
            report.AppendLine();
            report.AppendLine($"Scene: {scenePath}");
            report.AppendLine("Expected target: the object at the same hierarchy path in U#'s build copy of the prefab, for components the same");
            report.AppendLine("type and index among components of that type (stripped proxies map to their backing UdonBehaviour).");
            report.AppendLine();

            foreach (RunResult run in runs)
            {
                report.AppendLine($"== {run.name} ({run.seconds:F1}s)");
                report.AppendLine($"  UdonSharp exception:                        {run.exception ?? "none"}");
                report.AppendLine($"  build copies written:                       {run.buildCopies}");
                report.AppendLine($"  scene references into build-copied prefabs: {run.intoCopied}");
                report.AppendLine($"    point at the right object in the copy:    {run.correct}");
                report.AppendLine($"    still point at the original prefab:       {run.leftOnOriginal}" + (run.leftPrefabs.Count > 0 ? $" (in {run.leftPrefabs.Count} prefabs)" : ""));
                report.AppendLine($"    point at a different object (WRONG):      {run.wrong}");
                report.AppendLine($"    no counterpart in the copy:               {run.noCounterpart}");
                report.AppendLine($"  scene references into other prefabs:        {run.intoNotCopied} ({run.notCopiedChanged} changed, should be 0)");
                report.AppendLine($"  build copies referencing original prefabs:  {run.copyLeftovers}");
                report.AppendLine($"  prefab objects repaired by the fix:         {run.repaired.Count}");
                report.AppendLine();
            }

            void List(string title, IEnumerable<string> lines, int max)
            {
                var all = lines.ToList();
                if (all.Count == 0) return;
                report.AppendLine($"{title} ({all.Count}{(all.Count > max ? $", first {max}" : "")}):");
                foreach (string line in all.Take(max)) report.AppendLine("  " + line);
                report.AppendLine();
            }

            foreach (RunResult run in runs)
            {
                List($"[{run.name}] WRONG target", run.wrongDetails, 500);
                List($"[{run.name}] no counterpart", run.noCounterpartDetails, 500);
                List($"[{run.name}] changed although the prefab has no build copy", run.notCopiedChangedDetails, 200);
                List($"[{run.name}] build copies referencing original prefabs", run.copyLeftoverDetails, 200);
                List($"[{run.name}] prefabs with references left on the original", run.leftPrefabs.OrderBy(p => p), 200);
                List($"[{run.name}] references left on the original", run.leftDetails, 100);
                List($"[{run.name}] prefab objects repaired by the fix", run.repaired, 200);
            }
        }

        //the object at the same place in the build copy: same hierarchy path, for components same type and index
        private static Object Counterpart(Object original, GameObject copyRoot)
        {
            GameObject gameObject = original as GameObject ?? (original as Component)?.gameObject;
            if (gameObject == null) return null;

            var path = new List<Transform>();
            for (Transform t = gameObject.transform; t != null; t = t.parent) path.Add(t);
            path.Reverse();

            Transform target = copyRoot.transform;
            for (int i = 1; i < path.Count; i++)
            {
                int index = path[i].GetSiblingIndex();
                if (index >= target.childCount) return null;
                target = target.GetChild(index);
                if (target.name != path[i].name) return null;
            }

            if (original is GameObject) return target.gameObject;

            Component component = (Component)original;
            if (component is UdonSharpBehaviour proxy)
            {
                component = UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);
                if (component == null) return null;
            }

            Type type = component.GetType();
            int componentIndex = Array.IndexOf(gameObject.GetComponents(type), component);
            Component[] candidates = target.GetComponents(type);
            return componentIndex >= 0 && componentIndex < candidates.Length ? candidates[componentIndex] : null;
        }

        private static bool IsPrefabAssetObject(Object value, string copyFolder)
        {
            if (value == null || !(value is GameObject || value is Component) || !EditorUtility.IsPersistent(value)) return false;

            string path = AssetDatabase.GetAssetPath(value).Replace('\\', '/');
            return path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) && !path.StartsWith(copyFolder, StringComparison.OrdinalIgnoreCase);
        }

        //every Unity object in a behaviour's public variables, including array elements
        private static IEnumerable<(string symbol, string element, Object value)> ReadReferences(UdonBehaviour behaviour)
        {
            var variables = behaviour.publicVariables;
            var output = new List<(string, string, Object)>();

            void Flatten(string symbol, string element, object value, int depth)
            {
                if (value is Object unityObject)
                {
                    output.Add((symbol, element, unityObject));
                }
                else if (value is IEnumerable enumerable && !(value is string) && depth < 3)
                {
                    int i = 0;
                    foreach (object item in enumerable) Flatten(symbol, $"{element}[{i++}]", item, depth + 1);
                }
            }

            foreach (string symbol in variables.VariableSymbols)
            {
                if (variables.TryGetVariableValue(symbol, out object value)) Flatten(symbol, "", value, 0);
            }

            return output;
        }

        private static string Id(Object obj) => obj == null ? "null" : $"{UdonSharpFastSceneBuildVerifier.ObjectId(obj)} ({AssetDatabase.GetAssetPath(obj)})";
    }
}

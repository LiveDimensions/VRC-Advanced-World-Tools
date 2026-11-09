using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Udon;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace LiveDimensions.UdonSharpFastBuild
{
    /// <summary>
    /// Build time fix for UdonSharp's [PostProcessScene] hook (UdonSharpEditorManager.OnSceneBuildInternal), which runs in
    /// every VRChat world build and every time play mode is entered.
    ///
    /// That hook loops over every UdonSharpBehaviour in the active scene and calls
    /// CopyProxyToUdon(behaviour, ProxySerializationPolicy.All). 'All' recursively re-serializes every behaviour reachable
    /// through references, so in a scene with thousands of cross-referenced behaviours this is effectively O(n^2)
    /// (Project Aincrad Floor 1: ~7k behaviours, 15 minutes of a 21 minute build).
    ///
    /// Why a depth 1 (RootOnly) copy gives the same result inside that hook:
    /// - the loop already visits every UdonSharpBehaviour in the active scene, so every one of them still gets its own
    ///   fields copied. The depth only decides whether *referenced* behaviours are re-copied as well
    /// - PrepareUdonSharpBehavioursForPlay (called right after) clears and re-copies every scene behaviour with depth 1,
    ///   and instantiates, re-copies and saves every referenced prefab, so the build doesn't use the extra copies
    /// - behaviours 'All' reaches outside the active scene (prefab assets, other loaded scenes) are re-copied once by
    ///   PrepareForPlayPrefix, for the case where U# still ends up referencing them (see GetRemappedObjectPrefix)
    ///
    /// Guards:
    /// - only during a build, or when a single scene is loaded. With several scenes open in play mode the original
    ///   behaviour is kept
    /// - only for the VRChat Worlds SDK version this was verified against. After an SDK update the patch turns itself off
    ///   until the code is checked again (update VerifiedSdkVersion, and run Tools/UdonSharp Fast Build/Verify)
    /// </summary>
    [InitializeOnLoad]
    internal static class UdonSharpFastSceneBuild
    {
        internal const string LogTag = "[UdonSharpFastBuild]";
        internal const string MenuRoot = "Tools/UdonSharp Fast Build/";

        private const string VerifiedSdkVersion = "3.10.6";

        private const string EnabledMenuPath = MenuRoot + "Enabled";
        private const string MappingFixMenuPath = MenuRoot + "Fix UdonSharp Prefab Remapping In Builds";
        private const string SettingsMenuPath = MenuRoot + "Project Settings...";

        /// <summary>Set by the verifier to run the original UdonSharp behaviour while the patch is installed.</summary>
        internal static bool Bypass;

        /// <summary>Set by the verifier so both of its runs complete the same way.</summary>
        internal static bool ForceMappingFix;

        internal static bool IsActive { get; private set; }

        private static MethodInfo collectRootDependencies;

        private static readonly Type mappingType = AccessTools.TypeByName("UdonSharpEditor.UdonSharpEditorManager+GameObjectPrefabMapping");
        private static FieldInfo mappingOriginal;
        private static FieldInfo mappingNew;
        private static FieldInfo mappingComponents;

        private static int sceneBuildDepth;
        private static int fastCopies;
        private static int originalCopies;
        private static int outsideRefreshed;
        private static readonly Stopwatch sceneBuildTimer = new Stopwatch();

        /// <summary>Prefab objects whose component map the remapping fix built in the last scene build (original -> build copy).</summary>
        internal static readonly List<(GameObject original, GameObject copy)> FixedMappings = new List<(GameObject, GameObject)>();

        private static bool MappingFixEnabled => ForceMappingFix || UdonSharpFastBuildSettings.instance.prefabRemappingFix;

        /// <summary>True when the installed VRChat Worlds SDK is the version the patches in this package were verified against.</summary>
        internal static bool IsVerifiedSdk(out string sdkVersion)
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UdonSharpEditorUtility).Assembly);
            sdkVersion = package?.version ?? "unknown";
            return sdkVersion == VerifiedSdkVersion;
        }

        static UdonSharpFastSceneBuild()
        {
            if (!UdonSharpFastBuildSettings.instance.fastSceneBuild)
                return;

            if (!IsVerifiedSdk(out string sdkVersion))
            {
                Debug.LogWarning($"{LogTag} VRChat Worlds SDK is {sdkVersion}, UdonSharp Fast Build was verified for {VerifiedSdkVersion}. " +
                                 "It's turned off until UdonSharpEditorManager.OnSceneBuildInternal is checked again, builds will be slow until then.");
                return;
            }

            var sceneBuild = AccessTools.Method("UdonSharpEditor.UdonSharpEditorManager:OnSceneBuildInternal");
            var copyProxyToUdon = AccessTools.Method(typeof(UdonSharpEditorUtility), nameof(UdonSharpEditorUtility.CopyProxyToUdon),
                new[] { typeof(UdonSharpBehaviour), typeof(ProxySerializationPolicy) });
            var prepareForPlay = AccessTools.Method("UdonSharpEditor.UdonSharpEditorManager:PrepareUdonSharpBehavioursForPlay");
            collectRootDependencies = AccessTools.Method(typeof(UdonSharpEditorUtility), "CollectUdonSharpBehaviourRootDependencies");
            var getRemappedObject = mappingType != null ? AccessTools.Method(mappingType, "GetRemappedObject") : null;

            if (mappingType != null)
            {
                mappingOriginal = AccessTools.Field(mappingType, "_originalObject");
                mappingNew = AccessTools.Field(mappingType, "_newObject");
                mappingComponents = AccessTools.Field(mappingType, "_lazyComponentMap");
            }

            if (sceneBuild == null || copyProxyToUdon == null || prepareForPlay == null || collectRootDependencies == null ||
                getRemappedObject == null || mappingOriginal == null || mappingNew == null || mappingComponents == null)
            {
                Debug.LogWarning($"{LogTag} UdonSharp methods not found (SDK changed?), UdonSharp Fast Build is not active");
                return;
            }

            var harmony = new Harmony("com.livedimensions.udonsharpfastbuild");
            harmony.Patch(sceneBuild,
                prefix: new HarmonyMethod(typeof(UdonSharpFastSceneBuild), nameof(SceneBuildPrefix)),
                finalizer: new HarmonyMethod(typeof(UdonSharpFastSceneBuild), nameof(SceneBuildFinalizer)));
            harmony.Patch(copyProxyToUdon,
                prefix: new HarmonyMethod(typeof(UdonSharpFastSceneBuild), nameof(CopyProxyToUdonPrefix)));
            harmony.Patch(prepareForPlay,
                prefix: new HarmonyMethod(typeof(UdonSharpFastSceneBuild), nameof(PrepareForPlayPrefix)));
            harmony.Patch(getRemappedObject,
                prefix: new HarmonyMethod(typeof(UdonSharpFastSceneBuild), nameof(GetRemappedObjectPrefix)));

            IsActive = true;
        }

        private static void SceneBuildPrefix()
        {
            if (sceneBuildDepth++ > 0) return;

            fastCopies = 0;
            originalCopies = 0;
            outsideRefreshed = 0;
            FixedMappings.Clear();
            sceneBuildTimer.Restart();
        }

        private static void SceneBuildFinalizer()
        {
            if (--sceneBuildDepth > 0) return;

            //one line per scene build, so it's visible in every build and play mode log which path was used
            string reason = Bypass ? " (bypassed)" : originalCopies > 0 ? $" (original path: building={BuildPipeline.isBuildingPlayer}, scenes={SceneManager.sceneCount})" : "";
            Debug.Log($"{LogTag} UdonSharp scene build took {sceneBuildTimer.Elapsed.TotalSeconds:F1}s, {fastCopies} fast copies, " +
                      $"{outsideRefreshed} referenced behaviours outside the scene refreshed, {originalCopies} original copies{reason}" +
                      (MappingFixEnabled ? $", prefab remapping fix on (repaired {FixedMappings.Count} prefab objects)" : ""));
        }

        private static void CopyProxyToUdonPrefix(ref ProxySerializationPolicy serializationPolicy)
        {
            if (sceneBuildDepth <= 0 || serializationPolicy != ProxySerializationPolicy.All)
                return;

            if (Bypass || (!BuildPipeline.isBuildingPlayer && SceneManager.sceneCount != 1))
            {
                originalCopies++;
                return;
            }

            fastCopies++;
            serializationPolicy = ProxySerializationPolicy.RootOnly;
        }

        /// <summary>
        /// 'All' also re-copied behaviours outside the active scene that scene behaviours reference (prefab assets, other loaded
        /// scenes). Normally that doesn't matter because U# replaces prefab references with freshly built copies, but when U#'s
        /// remapping throws (see GetRemappedObjectPrefix) some references keep pointing at the original prefab assets, and then
        /// their data has to be as fresh as 'All' left it. So: walk the same reference graph once, linearly, and re-copy every
        /// behaviour outside the active scene that 'All' would have reached. Runs right after the copy loop.
        /// </summary>
        private static void PrepareForPlayPrefix(IEnumerable<UdonBehaviour> rootBehaviours)
        {
            outsideRefreshed = 0;
            if (sceneBuildDepth <= 0 || fastCopies == 0) return;

            Scene activeScene = SceneManager.GetActiveScene();
            var visited = new HashSet<UdonSharpBehaviour>();
            var queue = new Queue<UdonSharpBehaviour>();

            void EnqueueDependencies(UdonSharpBehaviour behaviour)
            {
                var dependencies = (IEnumerable)collectRootDependencies.Invoke(null, new object[] { behaviour });
                foreach (object dependency in dependencies)
                {
                    UdonSharpBehaviour proxy = dependency as UdonSharpBehaviour;
                    if (proxy == null && dependency is UdonBehaviour udonBehaviour && UdonSharpEditorUtility.IsUdonSharpBehaviour(udonBehaviour))
                        proxy = UdonSharpEditorUtility.GetProxyBehaviour(udonBehaviour);

                    if (proxy != null && visited.Add(proxy))
                        queue.Enqueue(proxy);
                }
            }

            foreach (UdonBehaviour rootBehaviour in rootBehaviours)
            {
                UdonSharpBehaviour proxy = UdonSharpEditorUtility.GetProxyBehaviour(rootBehaviour);
                if (proxy != null) EnqueueDependencies(proxy);
            }

            while (queue.Count > 0)
            {
                UdonSharpBehaviour behaviour = queue.Dequeue();

                //behaviours in the active scene were all copied by the loop already (and their dependencies were walked from there)
                bool outside = EditorUtility.IsPersistent(behaviour) || behaviour.gameObject.scene != activeScene;
                if (!outside || UdonSharpEditorUtility.GetBackingUdonBehaviour(behaviour) == null) continue;

                try
                {
                    UdonSharpEditorUtility.CopyProxyToUdon(behaviour, ProxySerializationPolicy.RootOnly);
                    outsideRefreshed++;
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }

                EnqueueDependencies(behaviour);
            }
        }

        /// <summary>
        /// Fix for a U# bug (opt in, project setting UdonSharpFastBuildSettings.prefabRemappingFix): when building, U#
        /// strips the UdonSharpBehaviour proxies from its built prefab copies and then maps components of the original prefab to
        /// the copy by index, which throws "Component layout mismatch in prefab mapping" because the copy has fewer components.
        /// The exception aborts the remapping, so the rest of the scene keeps references to the original prefab assets.
        /// This builds the map without the stripped proxies instead (a proxy maps to the copy of its backing UdonBehaviour).
        /// </summary>
        private static bool GetRemappedObjectPrefix(object __instance, Object originalObject, ref Object __result)
        {
            if (!MappingFixEnabled || !(originalObject is Component component)) return true;

            var map = (Dictionary<Component, Component>)mappingComponents.GetValue(__instance);
            if (map == null)
            {
                GameObject originalObjectRoot = (GameObject)mappingOriginal.GetValue(__instance);
                GameObject newObjectRoot = (GameObject)mappingNew.GetValue(__instance);
                Component[] originalComponents = originalObjectRoot.GetComponents<Component>();
                Component[] newComponents = newObjectRoot.GetComponents<Component>();

                //nothing was stripped, U#'s own code handles it
                if (originalComponents.Length == newComponents.Length) return true;

                Component[] kept = originalComponents.Where(c => !(c is UdonSharpBehaviour)).ToArray();
                if (kept.Length != newComponents.Length) return true;

                map = new Dictionary<Component, Component>();
                for (int i = 0; i < kept.Length; i++)
                {
                    //a missing script or different type means the layouts really don't match, let U# throw as before
                    if (kept[i] == null || newComponents[i] == null || kept[i].GetType() != newComponents[i].GetType()) return true;
                    map.Add(kept[i], newComponents[i]);
                }

                foreach (Component original in originalComponents)
                {
                    if (original is UdonSharpBehaviour proxy)
                    {
                        UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);
                        if (backing != null && map.TryGetValue(backing, out Component newBacking)) map[proxy] = newBacking;
                    }
                }

                mappingComponents.SetValue(__instance, map);
                FixedMappings.Add((originalObjectRoot, newObjectRoot));
            }

            if (!map.TryGetValue(component, out Component remapped)) return true;

            __result = remapped;
            return false;
        }

        //both settings are project-wide (UdonSharpFastBuildSettings), so every machine and automated build behaves the same
        [MenuItem(EnabledMenuPath, false, 0)]
        private static void ToggleEnabled()
        {
            var settings = UdonSharpFastBuildSettings.instance;
            settings.fastSceneBuild = !settings.fastSceneBuild;
            settings.SaveSettings();
            EditorUtility.RequestScriptReload();
        }

        [MenuItem(EnabledMenuPath, true)]
        private static bool ToggleEnabledValidate()
        {
            Menu.SetChecked(EnabledMenuPath, UdonSharpFastBuildSettings.instance.fastSceneBuild);
            return true;
        }

        [MenuItem(MappingFixMenuPath, false, 1)]
        private static void ToggleMappingFix()
        {
            var settings = UdonSharpFastBuildSettings.instance;
            settings.prefabRemappingFix = !settings.prefabRemappingFix;
            settings.SaveSettings();
        }

        [MenuItem(MappingFixMenuPath, true)]
        private static bool ToggleMappingFixValidate()
        {
            Menu.SetChecked(MappingFixMenuPath, UdonSharpFastBuildSettings.instance.prefabRemappingFix);
            return true;
        }

        [MenuItem(SettingsMenuPath, false, 2)]
        private static void OpenSettings() => SettingsService.OpenProjectSettings("Project/UdonSharp Fast Build");
    }
}

using UnityEditor;
using UnityEngine;

namespace LiveDimensions.UdonSharpFastBuild
{
    /// <summary>
    /// Project-wide settings, stored in ProjectSettings/Packages/com.livedimensions.udonsharpfastbuild/Settings.asset so
    /// they're committed with the project and every machine (and automated builds) builds the same way.
    /// Edit them in Project Settings > UdonSharp Fast Build or the Tools > UdonSharp Fast Build menu.
    /// </summary>
    [FilePath("ProjectSettings/Packages/com.livedimensions.udonsharpfastbuild/Settings.asset", FilePathAttribute.Location.ProjectFolder)]
    internal class UdonSharpFastBuildSettings : ScriptableSingleton<UdonSharpFastBuildSettings>
    {
        [Tooltip("Copy each UdonSharpBehaviour on its own in UdonSharp's scene build step instead of recursively. Needs a script reload to take effect.")]
        public bool fastSceneBuild = true;

        [Tooltip("Fix UdonSharp's \"Component layout mismatch in prefab mapping\" error in builds, so references to prefabs are remapped to UdonSharp's build copies.")]
        public bool prefabRemappingFix;

        public void SaveSettings() => Save(true);
    }

    internal static class UdonSharpFastBuildSettingsProvider
    {
        [SettingsProvider]
        private static SettingsProvider Create()
        {
            return new SettingsProvider("Project/UdonSharp Fast Build", SettingsScope.Project)
            {
                keywords = new[] { "UdonSharp", "build", "prefab", "remapping" },
                guiHandler = _ =>
                {
                    var settings = UdonSharpFastBuildSettings.instance;
                    var serialized = new SerializedObject(settings);

                    EditorGUI.BeginChangeCheck();
                    EditorGUILayout.PropertyField(serialized.FindProperty(nameof(UdonSharpFastBuildSettings.fastSceneBuild)), new GUIContent("Fast Scene Build"));
                    EditorGUILayout.PropertyField(serialized.FindProperty(nameof(UdonSharpFastBuildSettings.prefabRemappingFix)), new GUIContent("Fix Prefab Remapping In Builds"));
                    bool fastChanged = serialized.FindProperty(nameof(UdonSharpFastBuildSettings.fastSceneBuild)).boolValue != settings.fastSceneBuild;

                    if (EditorGUI.EndChangeCheck())
                    {
                        serialized.ApplyModifiedPropertiesWithoutUndo();
                        settings.SaveSettings();
                        if (fastChanged) EditorUtility.RequestScriptReload();
                    }

                    EditorGUILayout.Space();
                    EditorGUILayout.HelpBox(
                        UdonSharpFastSceneBuild.IsActive
                            ? "Fast scene build is active."
                            : "Fast scene build is not active (turned off, or the VRChat SDK version isn't the verified one, see the console).",
                        UdonSharpFastSceneBuild.IsActive ? MessageType.Info : MessageType.Warning);
                    EditorGUILayout.HelpBox("These settings are saved in ProjectSettings/Packages/com.livedimensions.udonsharpfastbuild/Settings.asset, commit it so everyone builds the same way.", MessageType.None);
                }
            };
        }
    }
}

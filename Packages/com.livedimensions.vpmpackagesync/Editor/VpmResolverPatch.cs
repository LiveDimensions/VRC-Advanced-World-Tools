using System.IO;
using UnityEditor;
using UnityEngine;

namespace LiveDimensions.PackageSync
{
    /// <summary>
    /// Keeps the VRChat VPM resolver from running its own check when the project loads, for everyone on the team.
    ///
    /// The resolver's [InitializeOnLoad] is commented out in Packages/com.vrchat.core.vpm-resolver/Editor/Resolver/Resolver.cs.
    /// Updating the VRChat SDK replaces that file, so this re-applies the change on load; if the resolver package is committed,
    /// commit the patched file along with the SDK update. The check it did is replaced by PackageSyncChecker, which also catches
    /// version mismatches (the resolver only looks for missing packages).
    ///
    /// This is a project setting on purpose (not a per-machine pref like VRCFury's "Disable Buggy VRCSDK VPM Resolver")
    /// so the behaviour is the same on every machine. It uses the same edit as VRCFury so the two don't fight; if VRCFury
    /// is ever added to the project, its pref is turned on here so it doesn't undo the change.
    /// </summary>
    [InitializeOnLoad]
    internal static class VpmResolverPatch
    {
        public const string ResolverPath = "Packages/com.vrchat.core.vpm-resolver/Editor/Resolver/Resolver.cs";
        private const string EnabledLine = "    [InitializeOnLoad]";
        private const string DisabledLine = "    // [InitializeOnLoad]";

        private const string VrcFuryPackagePath = "Packages/com.vrcfury.vrcfury";
        private const string VrcFuryPref = "com.vrcfury.disableVpmResolver";

        private const string WarnedKey = "PackageSync.ResolverPatchWarned";

        static VpmResolverPatch()
        {
            if (Directory.Exists(VrcFuryPackagePath) && !EditorPrefs.GetBool(VrcFuryPref, false))
            {
                EditorPrefs.SetBool(VrcFuryPref, true);
                Debug.Log("[PackageSync] Turned on VRCFury's 'Disable Buggy VRCSDK VPM Resolver' so it matches this project's setting.");
            }

            Apply();
        }

        public static bool IsApplied()
        {
            return File.Exists(ResolverPath) && File.ReadAllText(ResolverPath).Contains(DisabledLine);
        }

        private static void Apply()
        {
            //the resolver is missing too: PackageSyncChecker reports that
            if (!File.Exists(ResolverPath)) return;

            var text = File.ReadAllText(ResolverPath);
            if (text.Contains(DisabledLine)) return;

            if (!text.Contains(EnabledLine))
            {
                //VRChat changed the resolver, warn once per session so someone updates this script
                if (SessionState.GetBool(WarnedKey, false)) return;
                SessionState.SetBool(WarnedKey, true);
                Debug.LogWarning($"[PackageSync] Couldn't disable the VRChat VPM resolver's load check, {ResolverPath} changed. " +
                                 "It will run on load on this machine until VpmResolverPatch is updated.");
                return;
            }

            File.WriteAllText(ResolverPath, text.Replace(EnabledLine, DisabledLine));
            Debug.Log($"[PackageSync] Disabled the VRChat VPM resolver's load check in {ResolverPath} (the SDK was probably updated). If that package is committed, commit this change with the SDK update.");
            EditorApplication.delayCall += () => AssetDatabase.ImportAsset(ResolverPath);
        }
    }
}

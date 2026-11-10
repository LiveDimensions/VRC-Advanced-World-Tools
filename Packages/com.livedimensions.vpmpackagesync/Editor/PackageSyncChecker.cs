using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace LiveDimensions.PackageSync
{
    public enum PackageIssueKind
    {
        /// <summary>In vpm-manifest.json but not in Packages/.</summary>
        Missing,
        /// <summary>Installed, but not the version vpm-manifest.json has locked.</summary>
        VersionMismatch,
        /// <summary>In Packages/ but not in vpm-manifest.json, manifest.json or the committed packages: only on this machine.</summary>
        LocalOnly,
    }

    public class PackageIssue
    {
        public PackageIssueKind kind;
        public string id;
        public string expectedVersion;
        public string installedVersion;
        /// <summary>The folder is committed to git (tracked, or not ignored) rather than resolved from a listing.</summary>
        public bool committed;
        public string listingUrl;
        public bool listingInstalled;

        /// <summary>Missing and mismatched packages break things, local-only ones are just reported.</summary>
        public bool Blocking => kind != PackageIssueKind.LocalOnly;

        public string Message
        {
            get
            {
                var message = BaseMessage;
                if (Blocking && !committed && listingUrl == null)
                    message += " No listing is recorded for it in Packages/vpm-listings.json, so only machines that already have its listing can install it.";
                return message;
            }
        }

        private string BaseMessage
        {
            get
            {
                switch (kind)
                {
                    case PackageIssueKind.Missing:
                        return committed
                            ? $"{id} {expectedVersion} is missing. It's committed to git, so pulling or restoring Packages/{id} should bring it back (Resolve also works)."
                            : $"{id} {expectedVersion} is missing.";
                    case PackageIssueKind.VersionMismatch:
                        return committed
                            ? $"{id} is {installedVersion} but vpm-manifest.json wants {expectedVersion}. It's committed to git, so the folder and vpm-manifest.json don't match: " +
                              "pull, or check git status for an upgrade that was only half committed."
                            : $"{id} is {installedVersion} but vpm-manifest.json wants {expectedVersion}.";
                    case PackageIssueKind.LocalOnly:
                        return $"{id} {installedVersion} is only installed on this machine (not in vpm-manifest.json, manifest.json or committed to git). " +
                               "Remove it, or add it to the project properly.";
                }
                return id;
            }
        }
    }

    public enum GitIgnoreResult
    {
        /// <summary>Git doesn't ignore the folder, nothing needed doing.</summary>
        NotIgnored,
        /// <summary>Packages/.gitignore ignores the folder, and a "!id/" line there can fix that.</summary>
        CanAdd,
        /// <summary>A "!id/" line was added to Packages/.gitignore.</summary>
        Added,
        /// <summary>Something other than Packages/.gitignore ignores the folder, it wasn't changed.</summary>
        IgnoredByOtherRule,
        /// <summary>Git isn't installed or the project isn't in a git repository.</summary>
        GitUnavailable,
        Failed,
    }

    public enum PackageSource
    {
        /// <summary>A VPM package in vpm-manifest.json (resolved from a listing, or committed to git).</summary>
        Vpm,
        /// <summary>A package folder committed to git that isn't in vpm-manifest.json.</summary>
        Committed,
        /// <summary>A Unity Package Manager git package from manifest.json.</summary>
        UpmGit,
        /// <summary>A Unity registry package (manifest.json or a dependency of one).</summary>
        UpmRegistry,
        /// <summary>A package folder that's only on this machine.</summary>
        LocalOnly,
    }

    /// <summary>One package in the project, whether or not it has a problem.</summary>
    public class PackageStatus
    {
        public string id;
        public string displayName;
        public PackageSource source;
        /// <summary>null when it isn't installed.</summary>
        public string installedVersion;
        /// <summary>The locked version (VPM, registry) or git ref and hash (git). null for packages nothing locks.</summary>
        public string expectedVersion;
        public bool committed;
        public string listingUrl;
        public bool listingInstalled = true;
        public string upmUrl;
        /// <summary>Pulled in by another package rather than listed directly.</summary>
        public bool isDependency;
        /// <summary>null when the package is fine.</summary>
        public PackageIssue issue;

        public PackageIssue MakeIssue(PackageIssueKind kind) => new PackageIssue
        {
            kind = kind,
            id = id,
            expectedVersion = expectedVersion,
            installedVersion = installedVersion,
            committed = committed,
            listingUrl = listingUrl,
            listingInstalled = listingInstalled,
        };
    }

    /// <summary>
    /// Compares the packages in Packages/ with what the project says should be installed:
    /// vpm-manifest.json "locked" (exact versions), git (which package folders are committed) and packages-lock.json.
    ///
    /// This is in its own assembly with no references so it still compiles and runs when the rest of the project doesn't,
    /// e.g. when the VRChat SDK is missing, which is when it's needed most.
    /// </summary>
    public static class PackageSyncChecker
    {
        public const string PackagesDir = "Packages";
        private const string VpmManifestPath = PackagesDir + "/vpm-manifest.json";
        private const string ListingsPath = PackagesDir + "/vpm-listings.json";
        private const string GitIgnorePath = PackagesDir + "/.gitignore";

        /// <summary>This package's own id.</summary>
        public const string OwnPackageId = "com.livedimensions.vpmpackagesync";

        //every VCC/ALCOM install has these
        private static readonly string[] BuiltInListings =
        {
            "https://packages.vrchat.com/official?download",
            "https://packages.vrchat.com/curated?download",
        };

        //packages tools generate locally, never part of the project
        private static readonly HashSet<string> GeneratedPackages = new HashSet<string>
        {
            "com.vrcfury.temp",
        };

        private static List<PackageStatus> cachedStatuses;
        private static double cachedTime;

        /// <summary>Same as GetStatuses, but reuses the result for a couple of seconds (for OnGUI).</summary>
        public static List<PackageStatus> GetStatusesCached()
        {
            if (cachedStatuses == null || EditorApplication.timeSinceStartup - cachedTime > 2)
            {
                cachedStatuses = GetStatuses();
                cachedTime = EditorApplication.timeSinceStartup;
            }
            return cachedStatuses;
        }

        /// <summary>Same as GetIssues, but reuses the result for a couple of seconds (for OnGUI).</summary>
        public static List<PackageIssue> GetIssuesCached() => IssuesOf(GetStatusesCached());

        public static void ClearCache()
        {
            cachedStatuses = null;
            committedCacheKey = null;
        }

        public static List<PackageIssue> GetIssues() => IssuesOf(GetStatuses());

        private static List<PackageIssue> IssuesOf(List<PackageStatus> statuses) =>
            statuses.Where(s => s.issue != null).Select(s => s.issue).ToList();

        /// <summary>Every package in the project and where it comes from, with an issue on the ones that don't match.</summary>
        public static List<PackageStatus> GetStatuses()
        {
            var statuses = new List<PackageStatus>();
            var locked = ReadLocked();
            var upmLock = ReadUpmLock();
            var folderIds = Directory.Exists(PackagesDir)
                ? Directory.GetDirectories(PackagesDir).Select(Path.GetFileName).ToList()
                : new List<string>();
            var committedPackages = ReadCommittedPackages(locked.Keys.Concat(folderIds));
            var listings = ReadListings();
            var installedListings = ReadInstalledListings();

            //VPM packages
            foreach (var pair in locked)
            {
                var id = pair.Key;
                var status = new PackageStatus
                {
                    id = id,
                    source = PackageSource.Vpm,
                    expectedVersion = pair.Value,
                    installedVersion = GetInstalledVersion(id),
                    displayName = GetDisplayName(id),
                    committed = IsCommitted(id, committedPackages),
                };
                listings.TryGetValue(id, out status.listingUrl);
                status.listingInstalled = status.listingUrl == null || installedListings == null ||
                                          installedListings.Contains(NormalizeUrl(status.listingUrl));

                if (status.installedVersion == null)
                    status.issue = status.MakeIssue(PackageIssueKind.Missing);
                else if (!string.Equals(status.installedVersion, pair.Value, StringComparison.OrdinalIgnoreCase))
                    status.issue = status.MakeIssue(PackageIssueKind.VersionMismatch);
                statuses.Add(status);
            }

            //package folders that aren't VPM packages: committed to git, or only on this machine
            if (Directory.Exists(PackagesDir))
            {
                foreach (var dir in Directory.GetDirectories(PackagesDir))
                {
                    var id = Path.GetFileName(dir);
                    if (locked.ContainsKey(id) || GeneratedPackages.Contains(id)) continue;
                    var installed = GetInstalledVersion(id);
                    if (installed == null) continue; //not a package

                    var status = new PackageStatus
                    {
                        id = id,
                        installedVersion = installed,
                        displayName = GetDisplayName(id),
                        committed = IsCommitted(id, committedPackages),
                    };
                    var partOfProject = status.committed || committedPackages == null;
                    status.source = partOfProject ? PackageSource.Committed : PackageSource.LocalOnly;
                    if (!partOfProject) status.issue = status.MakeIssue(PackageIssueKind.LocalOnly);
                    statuses.Add(status);
                }
            }

            //Unity Package Manager packages (git and registry); Unity resolves these itself from packages-lock.json
            foreach (var pair in upmLock)
            {
                var id = pair.Key;
                var entry = pair.Value;
                if (!entry.TryGetValue("source", out var sourceObj) || !(sourceObj is string upmSource)) continue;
                if (upmSource != "git" && upmSource != "registry") continue;

                entry.TryGetValue("version", out var versionObj);
                entry.TryGetValue("hash", out var hashObj);
                entry.TryGetValue("depth", out var depthObj);
                var version = versionObj as string ?? "";
                var hash = hashObj as string;

                var status = new PackageStatus
                {
                    id = id,
                    source = upmSource == "git" ? PackageSource.UpmGit : PackageSource.UpmRegistry,
                    isDependency = depthObj is double depth && depth > 0,
                    upmUrl = upmSource == "git" ? version : null,
                };

                var cacheDir = FindPackageCacheDir(id);
                if (cacheDir != null)
                {
                    status.installedVersion = ReadPackageField(Path.Combine(cacheDir, "package.json"), "version");
                    status.displayName = ReadPackageField(Path.Combine(cacheDir, "package.json"), "displayName");
                }

                if (upmSource == "git")
                {
                    var refIndex = version.LastIndexOf('#');
                    var reference = refIndex >= 0 ? version.Substring(refIndex + 1) : null;
                    var shortHash = hash != null && hash.Length > 7 ? hash.Substring(0, 7) : hash;
                    status.expectedVersion = reference != null ? $"{reference} ({shortHash})" : shortHash;
                }
                else
                {
                    status.expectedVersion = version;
                }
                statuses.Add(status);
            }

            return statuses;
        }

        /// <summary>A string that changes when the set of issues changes, so the same issues aren't shown over and over.</summary>
        public static string Signature(IEnumerable<PackageIssue> issues)
        {
            return string.Join("|", issues.Select(i => $"{i.kind}:{i.id}:{i.installedVersion}:{i.expectedVersion}"));
        }

        public static string GetInstalledVersion(string id) => ReadPackageField(Path.Combine(PackagesDir, id, "package.json"), "version");

        private static string GetDisplayName(string id) => ReadPackageField(Path.Combine(PackagesDir, id, "package.json"), "displayName");

        private static string ReadPackageField(string packageJsonPath, string field)
        {
            if (!File.Exists(packageJsonPath)) return null;
            return ReadJson(packageJsonPath) is Dictionary<string, object> json && json.TryGetValue(field, out var v) ? v as string : null;
        }

        /// <summary>Where Unity extracted a git/registry package: Library/PackageCache/id@version-or-hash.</summary>
        private static string FindPackageCacheDir(string id)
        {
            const string cache = "Library/PackageCache";
            if (!Directory.Exists(cache)) return null;
            return Directory.GetDirectories(cache, id + "@*").FirstOrDefault();
        }

        private static Dictionary<string, Dictionary<string, object>> ReadUpmLock()
        {
            var result = new Dictionary<string, Dictionary<string, object>>();
            if (ReadJson(PackagesDir + "/packages-lock.json") is Dictionary<string, object> json &&
                json.TryGetValue("dependencies", out var depsObj) && depsObj is Dictionary<string, object> deps)
            {
                foreach (var pair in deps)
                    if (pair.Value is Dictionary<string, object> entry) result[pair.Key] = entry;
            }
            return result;
        }

        #region Fixing

        /// <summary>
        /// Installs missing packages with VRChat's resolver (the same thing its "packages missing" dialog does).
        /// It only installs missing packages, it doesn't change installed ones.
        /// </summary>
        public static bool ResolveMissing(out string error)
        {
            error = null;
            var resolve = FindType("VRC.PackageManagement.Resolver.Resolver")?.GetMethod("ResolveManifest", BindingFlags.Public | BindingFlags.Static);
            if (resolve == null)
            {
                error = "VRChat's resolver (com.vrchat.core.vpm-resolver) isn't loaded. Restore Packages/com.vrchat.core.vpm-resolver from git if it's committed, or open the project in VCC/ALCOM once.";
                return false;
            }

            //accessing the resolver runs its static constructor, which would show VRChat's own "packages missing" dialog
            //if it doesn't think the project already did its load check
            SessionState.SetBool("PROJECT_LOADED", true);

            try
            {
                resolve.Invoke(null, null);
                return true;
            }
            catch (TargetInvocationException e)
            {
                error = (e.InnerException ?? e).Message;
                Debug.LogException(e.InnerException ?? e);
                return false;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                ClearCache();
            }
        }

        private const string PendingReinstallKey = "PackageSync.PendingReinstall";

        /// <summary>True while a reinstall started by BeginReinstall hasn't finished yet.</summary>
        public static bool ReinstallPending => !string.IsNullOrEmpty(SessionState.GetString(PendingReinstallKey, ""));

        /// <summary>
        /// Replaces a mismatched (not committed) package with the locked version, in two steps: this moves the installed
        /// version to Temp and lets Unity unload it, then ContinueReinstall installs the locked version like a missing package
        /// (and moves the old one back if that fails, e.g. the listing isn't added on this machine).
        ///
        /// It's split because installing the new version into the folder while Unity still has the old one registered makes
        /// the VRChat resolver's refresh (Client.Resolve + AssetDatabase.Refresh) hang the editor. Installing a package that
        /// isn't registered works fine.
        /// </summary>
        public static bool BeginReinstall(PackageIssue issue, out string error)
        {
            error = null;
            if (issue.committed)
            {
                error = $"{issue.id} is committed to git, fix it through git instead.";
                return false;
            }
            if (ReinstallPending)
            {
                error = "Another reinstall is still running.";
                return false;
            }

            var packageDir = Path.Combine(PackagesDir, issue.id);
            //in Library, not Temp: Unity empties Temp when it starts, so a backup there wouldn't survive a crash mid-reinstall
            var backupDir = Path.Combine("Library", "PackageSyncBackup", $"{issue.id}-{DateTime.Now:yyyyMMdd-HHmmss}");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backupDir));
                //a rename, so if a file is locked nothing has moved yet
                Directory.Move(packageDir, backupDir);
            }
            catch (Exception e)
            {
                error = $"Couldn't move {packageDir} out of the way ({e.Message}). Close Unity and delete that folder, then reopen the project.";
                return false;
            }

            SessionState.SetString(PendingReinstallKey, string.Join("\n", issue.id, issue.expectedVersion, backupDir, issue.listingUrl ?? ""));
            ClearCache();
            Debug.Log($"[PackageSync] Reinstalling {issue.id} {issue.expectedVersion}: removed {issue.installedVersion}, installing the locked version next.");

            //runs after this refresh, or after the script reload it causes (PackageSyncWindow's static constructor schedules it again)
            EditorApplication.delayCall += ContinueReinstall;
            AssetDatabase.Refresh();
            return true;
        }

        /// <summary>Second step of BeginReinstall. Does nothing if no reinstall is pending.</summary>
        public static void ContinueReinstall()
        {
            var pending = SessionState.GetString(PendingReinstallKey, "");
            if (string.IsNullOrEmpty(pending)) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += ContinueReinstall;
                return;
            }
            SessionState.EraseString(PendingReinstallKey);

            var parts = pending.Split('\n');
            var id = parts[0];
            var expectedVersion = parts[1];
            var backupDir = parts[2];
            var listingUrl = parts.Length > 3 && parts[3].Length > 0 ? parts[3] : null;
            var packageDir = Path.Combine(PackagesDir, id);

            var resolved = ResolveMissing(out var resolveError);
            var installed = GetInstalledVersion(id);
            if (resolved && string.Equals(installed, expectedVersion, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(backupDir, true); } catch { /* it's in Library, harmless if it stays */ }
                Debug.Log($"[PackageSync] Reinstalled {id} {expectedVersion}.");
                return;
            }

            var error = resolveError ?? (installed == null
                ? DownloadFailureReason(id, expectedVersion, listingUrl)
                : $"The resolver installed {id} {installed} instead of {expectedVersion}.");

            //put the old version back so the project isn't left without the package
            if (Directory.Exists(backupDir))
            {
                try
                {
                    if (Directory.Exists(packageDir)) Directory.Delete(packageDir, true);
                    Directory.Move(backupDir, packageDir);
                    error += "\n\nThe previous version was put back.";
                }
                catch (Exception e)
                {
                    error += $"\n\nPutting the previous version back failed too ({e.Message}), it's in {Path.GetFullPath(backupDir)}.";
                }
                AssetDatabase.Refresh();
            }

            Debug.LogError("[PackageSync] Reinstall failed: " + error);
            //nobody is there to dismiss a dialog during an automatic install, it would hang the build
            if (!Application.isBatchMode && !SessionState.GetBool(AutoInstallActiveKey, false))
                EditorUtility.DisplayDialog("VPM Package Sync", "Reinstall failed: " + error, "Ok");
        }

        /// <summary>Launch Unity with this argument to fix packages when the project loads, without any dialogs. Batch mode does this without the argument.</summary>
        public const string AutoInstallArgument = "-vpmPackageSyncAutoInstall";
        /// <summary>Launch Unity with this argument to turn automatic installing off, including in batch mode and from code. Wins over AutoInstallArgument.</summary>
        public const string NoAutoInstallArgument = "-vpmPackageSyncNoAutoInstall";
        private const string AutoInstallAttemptedKey = "PackageSync.AutoInstallAttempted";
        private const string AutoInstallActiveKey = "PackageSync.AutoInstallActive";

        private static bool HasArgument(string argument) => Environment.GetCommandLineArgs().Contains(argument, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Whether to fix packages when the project loads: in batch mode or with AutoInstallArgument, unless NoAutoInstallArgument
        /// is there. Never in asset import workers, which are batch mode processes of their own.
        /// </summary>
        public static bool AutoInstallRequested =>
            !AssetDatabase.IsAssetImportWorkerProcess() && !HasArgument(NoAutoInstallArgument) && (Application.isBatchMode || HasArgument(AutoInstallArgument));

        /// <summary>
        /// Installs missing packages and reinstalls mismatched ones, with no dialogs. Does one piece of work per call, because
        /// each can reload scripts: keep calling until it returns false, waiting for compiling and ReinstallPending in between.
        /// Each package is only tried once per editor session, so a failure can't loop. Check GetIssues afterwards for what's
        /// still wrong (a mismatched committed package is never touched, that has to be fixed through git).
        /// </summary>
        public static bool AutoInstall()
        {
            if (HasArgument(NoAutoInstallArgument)) return false;
            if (ReinstallPending) return true;
            SessionState.SetBool(AutoInstallActiveKey, true);

            var attempted = new HashSet<string>(SessionState.GetString(AutoInstallAttemptedKey, "").Split('\n'));
            var issues = GetIssues().Where(i => i.Blocking && !attempted.Contains(AttemptKey(i))).ToList();
            var missing = issues.Where(i => i.kind == PackageIssueKind.Missing).ToList();
            var mismatched = issues.Where(i => i.kind == PackageIssueKind.VersionMismatch && !i.committed).ToList();
            if (missing.Count == 0 && mismatched.Count == 0) return false;

            AddNeededListings(missing.Concat(mismatched), null);

            if (missing.Count > 0)
            {
                attempted.UnionWith(missing.Select(AttemptKey));
                SessionState.SetString(AutoInstallAttemptedKey, string.Join("\n", attempted));
                Debug.Log($"[PackageSync] Installing missing packages: {string.Join(", ", missing.Select(i => i.id))}");
                if (!ResolveMissing(out var error)) Debug.LogError("[PackageSync] Resolving failed: " + error);
                foreach (var issue in GetIssues().Where(i => i.kind == PackageIssueKind.Missing))
                    Debug.LogError("[PackageSync] Still missing: " + DownloadFailureReason(issue.id, issue.expectedVersion, issue.listingUrl));
                return true;
            }

            foreach (var issue in mismatched)
            {
                attempted.Add(AttemptKey(issue));
                SessionState.SetString(AutoInstallAttemptedKey, string.Join("\n", attempted));
                if (BeginReinstall(issue, out var error)) return true;
                Debug.LogError($"[PackageSync] Couldn't reinstall {issue.id}: {error}");
            }
            return false;
        }

        private static string AttemptKey(PackageIssue issue) => $"{issue.id}:{issue.expectedVersion}";

        #endregion

        #region Listings

        /// <summary>
        /// Writes the listing of every package in vpm-manifest.json that vpm-listings.json doesn't have yet into vpm-listings.json,
        /// taken from the listings added on this machine (preferring one that has the locked version). Whoever installs a package
        /// has its listing, so the file stays complete without anyone editing it by hand. Returns the package ids it recorded.
        /// </summary>
        public static List<string> RecordListings()
        {
            var recorded = new List<string>();
            var locked = ReadLocked();
            var listings = ReadListings();
            var unrecorded = locked.Keys.Where(id => !listings.ContainsKey(id)).ToList();
            if (unrecorded.Count == 0) return recorded;

            var local = ReadLocalListingContents();
            foreach (var id in unrecorded)
            {
                var match = local.FirstOrDefault(l => l.packages.TryGetValue(id, out var versions) && versions.Contains(locked[id]))
                            ?? local.FirstOrDefault(l => l.packages.ContainsKey(id));
                if (match == null) continue;
                listings[id] = match.url;
                recorded.Add(id);
            }

            if (recorded.Count > 0)
            {
                WriteListings(listings);
                ClearCache();
            }
            return recorded;
        }

        /// <summary>
        /// Adds the listings needed by missing or mismatched packages to VCC/ALCOM on this machine, with VRChat's resolver library
        /// (the same code VCC uses). Committed packages don't need their listing. Returns the listings it added.
        /// </summary>
        public static List<string> AddNeededListings(IEnumerable<PackageIssue> issues, List<string> errors)
        {
            var added = new List<string>();
            foreach (var url in issues.Where(i => i.Blocking && !i.committed && !i.listingInstalled && i.listingUrl != null)
                         .Select(i => i.listingUrl).Distinct())
            {
                if (AddListingLocally(url, out var error)) added.Add(url);
                else errors?.Add(error);
            }
            return added;
        }

        /// <summary>Adds a listing to VCC/ALCOM on this machine. Falls back to VCC/ALCOM's "add listing" prompt if the resolver isn't loaded.</summary>
        public static bool AddListingLocally(string url, out string error)
        {
            error = null;
            var addRepo = FindType("VRC.PackageManagement.Core.Repos")
                ?.GetMethod("AddRepo", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Uri), typeof(Dictionary<string, string>) }, null);
            if (addRepo == null)
            {
                OpenAddListingPrompt(url);
                error = $"VRChat's resolver isn't loaded, so VCC/ALCOM was asked to add {url} instead.";
                return false;
            }

            try
            {
                EditorUtility.DisplayProgressBar("VPM Package Sync", $"Adding VPM listing {url}", 0.5f);
                if ((bool)addRepo.Invoke(null, new object[] { new Uri(url), new Dictionary<string, string>() }))
                {
                    Debug.Log($"[PackageSync] Added the VPM listing {url} to VCC/ALCOM on this machine.");
                    return true;
                }
                error = $"Couldn't add the VPM listing {url} (is it reachable?).";
            }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
                error = $"Couldn't add the VPM listing {url}: {inner.Message}";
                Debug.LogException(inner);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                ClearCache();
            }
            Debug.LogWarning("[PackageSync] " + error);
            return false;
        }

        /// <summary>Opens VCC/ALCOM's "add listing" prompt.</summary>
        public static void OpenAddListingPrompt(string url)
        {
            Application.OpenURL("vcc://vpm/addRepo?url=" + Uri.EscapeDataString(url));
        }

        private class LocalListing
        {
            public string url;
            public Dictionary<string, HashSet<string>> packages;
        }

        /// <summary>Why the resolver probably couldn't install a package version, from the listings added on this machine.</summary>
        public static string DownloadFailureReason(string id, string version, string listingUrl)
        {
            var withPackage = ReadLocalListingContents().Where(l => l.packages.ContainsKey(id)).ToList();
            if (withPackage.Any(l => l.packages[id].Contains(version)))
                return $"The resolver couldn't download {id} {version}, although a listing on this machine has it. Check your connection and try again.";

            if (withPackage.Count > 0)
            {
                var newest = withPackage.SelectMany(l => l.packages[id]).Distinct()
                    .OrderByDescending(v => Version.TryParse(v.Split('-')[0], out var parsed) ? parsed : new Version(0, 0))
                    .Take(5);
                return $"No listing on this machine has {id} {version} (newest versions: {string.Join(", ", newest)}). Check the version in vpm-manifest.json.";
            }

            return $"No listing on this machine has {id}. " + (listingUrl != null
                ? $"Add {listingUrl} in VCC/ALCOM."
                : "Packages/vpm-listings.json doesn't say which listing it comes from, add its listing in VCC/ALCOM.");
        }

        /// <summary>The package ids and versions in each listing added on this machine, from VCC/ALCOM's cached copies.</summary>
        private static List<LocalListing> ReadLocalListingContents()
        {
            var result = new List<LocalListing>();
            var root = VccFolder();

            void Add(string url, string cachePath)
            {
                if (string.IsNullOrEmpty(url) || !File.Exists(cachePath)) return;
                if (!(ReadJson(cachePath) is Dictionary<string, object> json)) return;
                //ALCOM wraps the listing in "repo", VCC doesn't
                if (json.TryGetValue("repo", out var repo) && repo is Dictionary<string, object> inner) json = inner;
                if (!json.TryGetValue("packages", out var packagesObj) || !(packagesObj is Dictionary<string, object> packages)) return;

                var listing = new LocalListing { url = url, packages = new Dictionary<string, HashSet<string>>() };
                foreach (var pair in packages)
                {
                    var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (pair.Value is Dictionary<string, object> package &&
                        package.TryGetValue("versions", out var versionsObj) && versionsObj is Dictionary<string, object> versionDict)
                        versions.UnionWith(versionDict.Keys);
                    listing.packages[pair.Key] = versions;
                }
                result.Add(listing);
            }

            Add(BuiltInListings[0], Path.Combine(root, "Repos", "vrc-official.json"));
            Add(BuiltInListings[1], Path.Combine(root, "Repos", "vrc-curated.json"));
            if (ReadJson(Path.Combine(root, "settings.json")) is Dictionary<string, object> settings &&
                settings.TryGetValue("userRepos", out var reposObj) && reposObj is List<object> repos)
            {
                foreach (var repo in repos.OfType<Dictionary<string, object>>())
                {
                    if (repo.TryGetValue("url", out var url) && repo.TryGetValue("localPath", out var localPath))
                        Add(url as string, localPath as string);
                }
            }
            return result;
        }

        private static void WriteListings(Dictionary<string, string> listings)
        {
            string comment = DefaultListingsComment;
            if (ReadJson(ListingsPath) is Dictionary<string, object> existing && existing.TryGetValue("_comment", out var c) && c is string s)
                comment = s;

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"_comment\": {JsonString(comment)},\n");
            sb.Append("  \"listings\": {\n");
            sb.Append(string.Join(",\n", listings.Select(pair => $"    {JsonString(pair.Key)}: {JsonString(pair.Value)}")));
            sb.Append("\n  }\n}\n");
            File.WriteAllText(ListingsPath, sb.ToString());
        }

        private const string DefaultListingsComment =
            "Which VPM listing each package in vpm-manifest.json comes from. vpm-manifest.json doesn't record this. " +
            "VPM Package Sync fills it in from the machine that installs a package, and adds the listings to VCC/ALCOM on machines that are missing them.";

        private static string JsonString(string value)
        {
            var sb = new StringBuilder("\"");
            foreach (var ch in value)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < ' ') sb.Append($"\\u{(int)ch:x4}");
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        #endregion

        #region Project files

        private static Dictionary<string, string> ReadLocked()
        {
            var locked = new Dictionary<string, string>();
            if (ReadJson(VpmManifestPath) is Dictionary<string, object> vpm &&
                vpm.TryGetValue("locked", out var lockedObj) && lockedObj is Dictionary<string, object> lockedDict)
            {
                foreach (var pair in lockedDict)
                {
                    if (pair.Value is Dictionary<string, object> entry && entry.TryGetValue("version", out var v) && v is string version)
                        locked[pair.Key] = version;
                }
            }
            return locked;
        }

        private static string VccFolder() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRChatCreatorCompanion");

        private static Type FindType(string fullName) =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(fullName, false)).FirstOrDefault(t => t != null);

        /// <summary>False when the last check couldn't ask git (not installed, or the project isn't in a git repository).</summary>
        public static bool GitAvailable { get; private set; } = true;

        private static HashSet<string> committedCache;
        private static string committedCacheKey;
        private static DateTime committedCacheTime;

        /// <summary>
        /// Which of these package folders are committed to git: tracked, or not ignored (so they'll be committed with the
        /// next commit). Asks git so any .gitignore layout works. Null when git can't be asked: then VPM packages count as not
        /// committed (fixed from their listing) and other package folders as part of the project (nothing to compare against).
        /// </summary>
        private static HashSet<string> ReadCommittedPackages(IEnumerable<string> ids)
        {
            var idList = ids.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
            var key = string.Join("|", idList);
            if (committedCacheKey == key && (DateTime.UtcNow - committedCacheTime).TotalSeconds < 10)
                return committedCache;

            var committed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ok = true;
            if (idList.Count > 0)
            {
                //tracked: committed even if a .gitignore rule matches it
                ok = RunGit("-c core.quotepath=off ls-files -- " + PackagesDir, null, out var tracked);
                if (ok)
                {
                    foreach (var line in tracked.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = line.Split('/');
                        if (parts.Length > 2) committed.Add(parts[1]);
                    }

                    //not ignored: git check-ignore prints the paths that are ignored. Asking about a file inside the folder
                    //(not "folder/") because git matches a trailing-slash path against blank .gitignore lines.
                    var input = string.Join("\n", idList.Select(id => $"{PackagesDir}/{id}/package.json")) + "\n";
                    ok = RunGit("-c core.quotepath=off check-ignore --stdin", input, out var ignoredOut);
                    if (ok)
                    {
                        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var line in ignoredOut.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            var parts = line.Split('/');
                            if (parts.Length > 2) ignored.Add(parts[1]);
                        }
                        foreach (var id in idList)
                            if (!ignored.Contains(id)) committed.Add(id);
                    }
                }
            }

            GitAvailable = ok;
            committedCache = ok ? committed : null;
            committedCacheKey = key;
            committedCacheTime = DateTime.UtcNow;
            return committedCache;
        }

        private static bool IsCommitted(string id, HashSet<string> committed) => committed != null && committed.Contains(id);

        /// <summary>Whether git ignores a package folder and, if so, whether a line in Packages/.gitignore can fix it. Changes nothing.</summary>
        public static GitIgnoreResult CheckIgnored(string id, out string detail)
        {
            detail = null;
            var input = $"{PackagesDir}/{id}/package.json\n";
            if (!RunGit("-c core.quotepath=off check-ignore --stdin", input, out var ignored)) return GitIgnoreResult.GitUnavailable;
            if (string.IsNullOrWhiteSpace(ignored)) return GitIgnoreResult.NotIgnored;

            //-v says which rule matched: "source:line:pattern<tab>path"
            if (!RunGit("-c core.quotepath=off check-ignore -v --stdin", input, out var verbose)) return GitIgnoreResult.GitUnavailable;
            var rule = Regex.Match(verbose.Split('\t')[0].Trim(), @"^(.*):(\d+):(.*)$");
            if (!rule.Success)
            {
                detail = verbose.Trim();
                return GitIgnoreResult.Failed;
            }

            var source = rule.Groups[1].Value.Replace('\\', '/');
            if (!string.Equals(source, GitIgnorePath, StringComparison.OrdinalIgnoreCase))
            {
                detail = $"{source}, line {rule.Groups[2].Value} ({rule.Groups[3].Value})";
                return GitIgnoreResult.IgnoredByOtherRule;
            }

            return GitIgnoreResult.CanAdd;
        }

        /// <summary>
        /// Makes sure git doesn't ignore a package folder, by adding a "!id/" line to Packages/.gitignore if that file is what
        /// ignores it. Never edits any other ignore file, and doesn't stage or commit anything.
        /// </summary>
        public static GitIgnoreResult EnsureNotIgnored(string id, out string detail)
        {
            var state = CheckIgnored(id, out detail);
            if (state != GitIgnoreResult.CanAdd) return state;

            var input = $"{PackagesDir}/{id}/package.json\n";
            string original;
            var encoding = new UTF8Encoding(false);
            try
            {
                original = File.ReadAllText(GitIgnorePath);
                var newline = original.Contains("\r\n") ? "\r\n" : "\n";
                var updated = original.Length > 0 && !original.EndsWith("\n") ? original + newline : original;
                File.WriteAllText(GitIgnorePath, $"{updated}!{id}/{newline}", encoding);
            }
            catch (Exception e)
            {
                detail = e.Message;
                return GitIgnoreResult.Failed;
            }

            //make sure it worked, and leave the file as it was if it didn't
            if (!RunGit("-c core.quotepath=off check-ignore --stdin", input, out var stillIgnored) || !string.IsNullOrWhiteSpace(stillIgnored))
            {
                File.WriteAllText(GitIgnorePath, original, encoding);
                detail = $"adding !{id}/ to {GitIgnorePath} didn't stop git ignoring it";
                return GitIgnoreResult.Failed;
            }

            ClearCache();
            return GitIgnoreResult.Added;
        }

        /// <summary>Runs git in the project folder. False if git isn't there or failed (exit code 1 is fine, check-ignore uses it for "nothing ignored").</summary>
        private static bool RunGit(string arguments, string stdin, out string stdout)
        {
            stdout = "";
            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo("git", arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Directory.GetCurrentDirectory(),
                    StandardOutputEncoding = Encoding.UTF8,
                };
                using (var process = System.Diagnostics.Process.Start(startInfo))
                {
                    if (process == null) return false;
                    var outTask = process.StandardOutput.ReadToEndAsync();
                    var errTask = process.StandardError.ReadToEndAsync();
                    if (stdin != null) process.StandardInput.Write(stdin);
                    process.StandardInput.Close();
                    if (!process.WaitForExit(10000))
                    {
                        try { process.Kill(); } catch { /* already exited */ }
                        return false;
                    }
                    stdout = outTask.Result;
                    errTask.Wait();
                    return process.ExitCode == 0 || process.ExitCode == 1;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Dictionary<string, string> ReadListings()
        {
            var result = new Dictionary<string, string>();
            if (ReadJson(ListingsPath) is Dictionary<string, object> json &&
                json.TryGetValue("listings", out var obj) && obj is Dictionary<string, object> listings)
            {
                foreach (var pair in listings)
                    if (pair.Value is string url) result[pair.Key] = url;
            }
            return result;
        }

        /// <summary>
        /// The listings added in VCC/ALCOM on this machine (they share this settings file), or null if it can't be read,
        /// in which case every listing is assumed to be added.
        /// </summary>
        private static HashSet<string> ReadInstalledListings()
        {
            try
            {
                if (!(ReadJson(Path.Combine(VccFolder(), "settings.json")) is Dictionary<string, object> settings)) return null;

                var result = new HashSet<string>(BuiltInListings.Select(NormalizeUrl));
                if (settings.TryGetValue("userRepos", out var reposObj) && reposObj is List<object> repos)
                {
                    foreach (var repo in repos.OfType<Dictionary<string, object>>())
                        if (repo.TryGetValue("url", out var url) && url is string s) result.Add(NormalizeUrl(s));
                }
                return result;
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeUrl(string url) => url.Trim().TrimEnd('/').ToLowerInvariant();

        private static object ReadJson(string path)
        {
            try
            {
                return File.Exists(path) ? MiniJson.Parse(File.ReadAllText(path)) : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PackageSync] Couldn't read {path}: {e.Message}");
                return null;
            }
        }

        #endregion

        /// <summary>
        /// Tiny JSON reader (objects → Dictionary&lt;string, object&gt;, arrays → List&lt;object&gt;, numbers → double),
        /// so this assembly doesn't depend on Newtonsoft being resolved.
        /// </summary>
        private class MiniJson
        {
            private readonly string s;
            private int i;

            private MiniJson(string s) { this.s = s; }

            public static object Parse(string text)
            {
                var parser = new MiniJson(text);
                if (parser.s.Length > 0 && parser.s[0] == '﻿') parser.i = 1;
                return parser.ParseValue();
            }

            private void SkipWhitespace()
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            }

            private object ParseValue()
            {
                SkipWhitespace();
                if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
                switch (s[i])
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default: return ParseNumber();
                }
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw new FormatException($"Expected {word} at {i}");
                i += word.Length;
            }

            private Dictionary<string, object> ParseObject()
            {
                var result = new Dictionary<string, object>();
                i++;
                SkipWhitespace();
                if (i < s.Length && s[i] == '}') { i++; return result; }
                while (true)
                {
                    SkipWhitespace();
                    var key = ParseString();
                    SkipWhitespace();
                    if (i >= s.Length || s[i] != ':') throw new FormatException($"Expected : at {i}");
                    i++;
                    result[key] = ParseValue();
                    SkipWhitespace();
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return result; }
                    throw new FormatException($"Expected , or }} at {i}");
                }
            }

            private List<object> ParseArray()
            {
                var result = new List<object>();
                i++;
                SkipWhitespace();
                if (i < s.Length && s[i] == ']') { i++; return result; }
                while (true)
                {
                    result.Add(ParseValue());
                    SkipWhitespace();
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return result; }
                    throw new FormatException($"Expected , or ] at {i}");
                }
            }

            private string ParseString()
            {
                if (i >= s.Length || s[i] != '"') throw new FormatException($"Expected string at {i}");
                i++;
                var sb = new StringBuilder();
                while (i < s.Length)
                {
                    var c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (i >= s.Length) break;
                    var e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber));
                            i += 4;
                            break;
                        default: sb.Append(e); break;
                    }
                }
                throw new FormatException("Unterminated string");
            }

            private double ParseNumber()
            {
                var start = i;
                while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
                if (start == i) throw new FormatException($"Unexpected character {s[i]} at {i}");
                return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
            }
        }
    }
}

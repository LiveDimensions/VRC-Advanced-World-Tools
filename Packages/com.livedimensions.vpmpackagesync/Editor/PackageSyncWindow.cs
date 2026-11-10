using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LiveDimensions.PackageSync
{
    /// <summary>
    /// Shows what PackageSyncChecker found, with buttons to fix it. Opens by itself after the project loads (and after
    /// script reloads, e.g. a git pull) when packages are missing or the wrong version. This replaces the VRChat resolver's
    /// load check, see VpmResolverPatch.
    /// </summary>
    [InitializeOnLoad]
    public class PackageSyncWindow : EditorWindow
    {
        private const string MenuPath = "Tools/VPM Package Sync";
        private const string LastShownKey = "PackageSync.LastShownSignature";
        private const string TriedListingsKey = "PackageSync.TriedListings";

        private Vector2 scroll;

        static PackageSyncWindow()
        {
            //finish a reinstall that was interrupted by the script reload it caused, then check
            EditorApplication.delayCall += PackageSyncChecker.ContinueReinstall;
            EditorApplication.delayCall += CheckWhenReady;
        }

        [MenuItem(MenuPath)]
        public static void ShowWindow()
        {
            PackageSyncChecker.ClearCache();
            var window = GetWindow<PackageSyncWindow>();
            window.titleContent = new GUIContent("VPM Package Sync");
            window.minSize = new Vector2(560, 250);
        }

        private static void CheckWhenReady()
        {
            //a reinstall in progress has its package missing on purpose, check once it's done
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode ||
                PackageSyncChecker.ReinstallPending)
            {
                EditorApplication.delayCall += CheckWhenReady;
                return;
            }

            OfferToCommitOwnPackage();

            var recorded = PackageSyncChecker.RecordListings();
            if (recorded.Count > 0)
                Debug.Log($"[PackageSync] Recorded the VPM listings of {string.Join(", ", recorded)} in Packages/vpm-listings.json. Commit it with vpm-manifest.json.");

            var issues = PackageSyncChecker.GetIssues();

            //add the listings missing packages need, once per session per listing (so being offline doesn't retry every reload)
            var tried = new HashSet<string>(SessionState.GetString(TriedListingsKey, "").Split('\n'));
            //only listings that actually need adding count as tried, so removing one later in the session still gets it re-added
            var toAdd = issues.Where(i => i.Blocking && !i.committed && !i.listingInstalled && i.listingUrl != null && !tried.Contains(i.listingUrl)).ToList();
            if (toAdd.Count > 0)
            {
                tried.UnionWith(toAdd.Select(i => i.listingUrl));
                SessionState.SetString(TriedListingsKey, string.Join("\n", tried));
                if (PackageSyncChecker.AddNeededListings(toAdd, null).Count > 0) issues = PackageSyncChecker.GetIssues();
            }

            var autoInstall = PackageSyncChecker.AutoInstallRequested;
            if (autoInstall && PackageSyncChecker.AutoInstall())
            {
                EditorApplication.delayCall += CheckWhenReady;
                return;
            }

            foreach (var issue in issues)
            {
                if (issue.Blocking) Debug.LogWarning("[PackageSync] " + issue.Message);
                else Debug.Log("[PackageSync] " + issue.Message);
            }

            //only open the window for problems, and only once per editor session for the same set of problems
            var blocking = issues.Where(i => i.Blocking).ToList();
            if (blocking.Count == 0)
            {
                SessionState.EraseString(LastShownKey);
                return;
            }
            if (autoInstall) return;

            var signature = PackageSyncChecker.Signature(blocking);
            if (SessionState.GetString(LastShownKey, "") == signature) return;
            SessionState.SetString(LastShownKey, signature);
            ShowWindow();
        }

        /// <summary>
        /// VPM Package Sync installs everything else, so it can't install itself: it has to come with the clone. If git ignores
        /// its folder, this asks once per editor session whether to add it to Packages/.gitignore.
        /// </summary>
        private static void OfferToCommitOwnPackage()
        {
            if (Application.isBatchMode || PackageSyncChecker.AutoInstallRequested || SessionState.GetBool(OwnPackageCheckedKey, false)) return;
            SessionState.SetBool(OwnPackageCheckedKey, true);
            if (EditorUserSettings.GetConfigValue(DontAskToCommitKey) == "1") return;

            var folder = PackageSyncChecker.PackagesDir + "/" + PackageSyncChecker.OwnPackageId;
            //not installed in Packages/ (e.g. a git URL), or a link to a development copy that shouldn't be committed
            if (!Directory.Exists(folder) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return;

            var state = PackageSyncChecker.CheckIgnored(PackageSyncChecker.OwnPackageId, out var detail);
            if (state == GitIgnoreResult.IgnoredByOtherRule)
            {
                Debug.LogWarning($"[PackageSync] Git ignores {folder} because of {detail}. Change that rule so VPM Package Sync is committed with the project, it can't install itself on a fresh clone.");
                return;
            }
            if (state != GitIgnoreResult.CanAdd) return;

            var choice = EditorUtility.DisplayDialogComplex("VPM Package Sync",
                "Git is ignoring VPM Package Sync, so your team won't get it.\n\n" +
                "It needs to be committed, because it's what installs the other packages on a fresh clone.\n\n" +
                "Add it to Packages/.gitignore's list of committed packages? This doesn't stage or commit anything.",
                "Add it", "Not now", "Don't ask again");
            if (choice == 2) EditorUserSettings.SetConfigValue(DontAskToCommitKey, "1");
            if (choice != 0) return;

            var result = PackageSyncChecker.EnsureNotIgnored(PackageSyncChecker.OwnPackageId, out detail);
            if (result == GitIgnoreResult.Added)
                Debug.Log($"[PackageSync] Added VPM Package Sync to Packages/.gitignore. Commit Packages/.gitignore and {folder} to share it with the team.");
            else
                EditorUtility.DisplayDialog("VPM Package Sync", $"Couldn't make git stop ignoring {folder}: {detail}", "Ok");
        }

        private const string OwnPackageCheckedKey = "PackageSync.OwnPackageChecked";
        private const string DontAskToCommitKey = "PackageSync.DontAskToCommitOwnPackage";
        private const string ShowRegistryPref = "PackageSync.ShowRegistryPackages";
        private const string ShowHelpPref = "PackageSync.ShowHelp";

        private static readonly (string title, string body)[] HelpTopics =
        {
            ("VPM package (from VCC/ALCOM)",
                "Add the listing and the package in VCC/ALCOM. When Unity reloads, VPM Package Sync records which listing it came from in " +
                "Packages/vpm-listings.json. Commit vpm-manifest.json and vpm-listings.json, not the package folder. " +
                "Teammates get a prompt to install it, and the listing is added to their VCC/ALCOM if they don't have it."),
            ("When to commit a VPM package's folder",
                "Only if you changed it, if it's needed before anything can install packages (VPM Package Sync, VRChat's resolver), or if its " +
                "listing is private or unreliable. Make sure git doesn't ignore the folder (with VRChat's default Packages/.gitignore, add a " +
                "!folder-name/ line) and commit it in the same commit as vpm-manifest.json."),
            ("Git package (Unity Package Manager)",
                "Package Manager > + > Add package from git URL, pinned to a tag or commit (...git#v1.2.0). Commit manifest.json and " +
                "packages-lock.json. Unity downloads it on every machine. Private repositories need each person's git to be signed in."),
            ("Unity registry package",
                "Add it in Package Manager, then commit manifest.json and packages-lock.json."),
            ("Package without a listing or git URL",
                "Put the folder in Packages/, make sure git doesn't ignore it (a !folder-name/ line in Packages/.gitignore) and commit it. " +
                "If git ignores it, it shows up under \"Only on this machine\"."),
            ("Updating or removing a VPM package",
                "Do it in VCC/ALCOM and commit vpm-manifest.json. If the folder is committed, commit it in the same commit, VPM Package Sync " +
                "points out when only one of them was. Removing a package leaves its line in vpm-listings.json, which does no harm."),
            ("Avoid",
                "Editing vpm-manifest.json by hand, copying a VPM package's folder into Packages/ yourself, and committing " +
                "vpm-manifest.json without vpm-listings.json (teammates without the listing wouldn't get it added)."),
        };

        private static void DrawHelp()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            for (var i = 0; i < HelpTopics.Length; i++)
            {
                if (i > 0) EditorGUILayout.Space(4);
                EditorGUILayout.LabelField(HelpTopics[i].title, EditorStyles.boldLabel);
                EditorGUILayout.LabelField(HelpTopics[i].body, EditorStyles.wordWrappedLabel);
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(6);
        }

        private static readonly (PackageSource source, string title, string tooltip)[] Sections =
        {
            (PackageSource.Vpm, "VPM packages", "In Packages/vpm-manifest.json. Downloaded from a VPM listing, or committed to git."),
            (PackageSource.Committed, "Committed to git", "Package folders committed to git (tracked, or not ignored) that aren't in vpm-manifest.json."),
            (PackageSource.UpmGit, "Git packages (Unity Package Manager)", "Git URLs in Packages/manifest.json. Unity downloads these itself, locked to the commit in packages-lock.json."),
            (PackageSource.UpmRegistry, "Unity registry packages", "Unity's own packages and their dependencies. Unity downloads these itself."),
            (PackageSource.LocalOnly, "Only on this machine", "Package folders that aren't part of the project for anyone else."),
        };

        private string search = "";
        private readonly Dictionary<PackageSource, bool> collapsed = new Dictionary<PackageSource, bool>();
        private GUIStyle problemLabel;

        //table column widths, worked out from the window width each OnGUI so long listing hosts and git refs fit
        private float packageColumn, installedColumn, expectedColumn, sourceColumn;

        private void LayoutColumns()
        {
            const float iconColumn = 24, scrollbarAndMargins = 28;
            installedColumn = 110;
            expectedColumn = 180;
            var rest = Mathf.Max(0, position.width - iconColumn - scrollbarAndMargins - installedColumn - expectedColumn);
            packageColumn = Mathf.Max(140, rest * 0.45f);
            sourceColumn = Mathf.Max(150, rest - packageColumn);
        }

        private void OnGUI()
        {
            LayoutColumns();

            if (problemLabel == null)
            {
                problemLabel = new GUIStyle(EditorStyles.label);
                problemLabel.normal.textColor = EditorGUIUtility.isProSkin ? new Color(1f, 0.45f, 0.4f) : new Color(0.75f, 0.1f, 0.1f);
            }

            var statuses = PackageSyncChecker.GetStatusesCached();
            var issues = statuses.Where(s => s.issue != null).Select(s => s.issue).ToList();
            var showRegistry = EditorPrefs.GetBool(ShowRegistryPref, false);

            //toolbar
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            search = EditorGUILayout.TextField(search, EditorStyles.toolbarSearchField, GUILayout.MinWidth(120));
            GUILayout.FlexibleSpace();
            var newShowRegistry = GUILayout.Toggle(showRegistry, new GUIContent("Unity registry", "Also list Unity's own packages"), EditorStyles.toolbarButton);
            if (newShowRegistry != showRegistry) EditorPrefs.SetBool(ShowRegistryPref, newShowRegistry);
            if (File.Exists("Packages/vpm-manifest.json") && GUILayout.Button("vpm-manifest.json", EditorStyles.toolbarButton))
                EditorUtility.RevealInFinder(Path.GetFullPath("Packages/vpm-manifest.json"));
            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton)) PackageSyncChecker.ClearCache();
            var showHelp = EditorPrefs.GetBool(ShowHelpPref, false);
            var newShowHelp = GUILayout.Toggle(showHelp, new GUIContent("How to add packages", "How to add, update and remove packages so everyone gets them"), EditorStyles.toolbarButton);
            if (newShowHelp != showHelp) EditorPrefs.SetBool(ShowHelpPref, newShowHelp);
            EditorGUILayout.EndHorizontal();

            scroll = EditorGUILayout.BeginScrollView(scroll);

            if (newShowHelp) DrawHelp();

            //summary
            var blocking = issues.Count(i => i.Blocking);
            var projectPackages = statuses.Count(s => s.source != PackageSource.UpmRegistry && s.source != PackageSource.LocalOnly);
            var summary = blocking > 0
                ? $"{blocking} of {projectPackages} project packages don't match what the project expects."
                : $"All {projectPackages} project packages are in sync.";
            EditorGUILayout.HelpBox(summary, blocking > 0 ? MessageType.Error : MessageType.Info);

            if (issues.Count > 0)
            {
                EditorGUILayout.LabelField("Problems", EditorStyles.boldLabel);
                DrawIssues(issues);
                EditorGUILayout.Space(8);
            }

            foreach (var (source, title, tooltip) in Sections)
            {
                if (source == PackageSource.UpmRegistry && !showRegistry) continue;
                var rows = statuses.Where(s => s.source == source && Matches(s)).OrderBy(s => s.id).ToList();
                if (rows.Count == 0) continue;
                if (source == PackageSource.Committed && !PackageSyncChecker.GitAvailable)
                    DrawSection(source, "In the Packages folder", "git isn't installed or the project isn't in a git repository, so VPM Package Sync can't tell which package folders are committed.", rows);
                else
                    DrawSection(source, title, tooltip, rows);
            }

            EditorGUILayout.EndScrollView();
        }

        private bool Matches(PackageStatus status)
        {
            if (string.IsNullOrWhiteSpace(search)) return true;
            var term = search.Trim();
            return status.id.IndexOf(term, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (status.displayName?.IndexOf(term, System.StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
        }

        private void DrawSection(PackageSource source, string title, string tooltip, List<PackageStatus> rows)
        {
            collapsed.TryGetValue(source, out var isCollapsed);
            var problems = rows.Count(r => r.issue != null);
            var label = problems > 0 ? $"{title} ({rows.Count}, {problems} with problems)" : $"{title} ({rows.Count})";
            isCollapsed = !EditorGUILayout.Foldout(!isCollapsed, new GUIContent(label, tooltip), true, EditorStyles.foldoutHeader);
            collapsed[source] = isCollapsed;
            if (isCollapsed) return;

            //column headers
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(24);
            GUILayout.Label("Package", EditorStyles.miniBoldLabel, GUILayout.Width(packageColumn));
            GUILayout.Label("Installed", EditorStyles.miniBoldLabel, GUILayout.Width(installedColumn));
            GUILayout.Label(source == PackageSource.UpmGit ? "Locked to" : "Expected", EditorStyles.miniBoldLabel, GUILayout.Width(expectedColumn));
            GUILayout.Label("Comes from", EditorStyles.miniBoldLabel, GUILayout.Width(sourceColumn));
            EditorGUILayout.EndHorizontal();

            for (var i = 0; i < rows.Count; i++) DrawRow(rows[i], i);
            EditorGUILayout.Space(6);
        }

        private void DrawRow(PackageStatus status, int index)
        {
            var rowRect = EditorGUILayout.BeginHorizontal(GUILayout.Height(18));
            if (Event.current.type == EventType.Repaint && index % 2 == 1)
                EditorGUI.DrawRect(rowRect, EditorGUIUtility.isProSkin ? new Color(1, 1, 1, 0.03f) : new Color(0, 0, 0, 0.04f));

            GUILayout.Space(4);
            GUILayout.Label(StatusIcon(status), GUILayout.Width(20), GUILayout.Height(18));

            var name = string.IsNullOrEmpty(status.displayName) ? status.id : status.displayName;
            GUILayout.Label(new GUIContent(name, status.id), GUILayout.Width(packageColumn));

            var badVersion = status.issue != null && status.issue.Blocking;
            GUILayout.Label(new GUIContent(status.installedVersion ?? "—", status.installedVersion ?? "Not installed"),
                badVersion ? problemLabel : EditorStyles.label, GUILayout.Width(installedColumn));
            GUILayout.Label(new GUIContent(status.expectedVersion ?? "—", status.expectedVersion), GUILayout.Width(expectedColumn));
            GUILayout.Label(SourceLabel(status), GUILayout.Width(sourceColumn));
            EditorGUILayout.EndHorizontal();

            if (Event.current.type == EventType.ContextClick && rowRect.Contains(Event.current.mousePosition))
            {
                ShowContextMenu(status);
                Event.current.Use();
            }
        }

        private static GUIContent StatusIcon(PackageStatus status)
        {
            string icon, tooltip;
            if (status.issue != null && status.issue.Blocking) { icon = "TestFailed"; tooltip = status.issue.Message; }
            else if (status.issue != null) { icon = "console.warnicon.sml"; tooltip = status.issue.Message; }
            else if (status.installedVersion == null) { icon = "TestIgnored"; tooltip = "Not downloaded yet. Unity resolves it when the project loads."; }
            else if (status.source == PackageSource.Vpm && !status.committed && !status.listingInstalled)
            {
                icon = "TestPassed";
                tooltip = $"In sync. Its listing ({status.listingUrl}) isn't added on this machine, so it can't be upgraded from here.";
            }
            else { icon = "TestPassed"; tooltip = "In sync"; }

            var content = new GUIContent(EditorGUIUtility.IconContent(icon)) { tooltip = tooltip };
            return content;
        }

        private static GUIContent SourceLabel(PackageStatus status)
        {
            switch (status.source)
            {
                case PackageSource.Vpm:
                {
                    var listing = ListingName(status.listingUrl);
                    if (status.committed) return new GUIContent($"Committed · {listing}", $"Committed to git. Listing: {status.listingUrl ?? "not recorded"}");
                    var text = status.listingInstalled ? listing : listing + " (not added)";
                    return new GUIContent(text, status.listingUrl ?? "No listing recorded in Packages/vpm-listings.json");
                }
                case PackageSource.Committed:
                    return PackageSyncChecker.GitAvailable
                        ? new GUIContent("Committed to git", "Not in vpm-manifest.json, so nothing checks its version: git keeps it in sync.")
                        : new GUIContent("Packages folder", "Not in vpm-manifest.json. VPM Package Sync can't ask git whether it's committed.");
                case PackageSource.UpmGit:
                    return new GUIContent(Host(status.upmUrl) ?? "git", status.upmUrl);
                case PackageSource.UpmRegistry:
                    return new GUIContent(status.isDependency ? "Unity registry (dependency)" : "Unity registry");
                default:
                    return new GUIContent("This machine only");
            }
        }

        private static string ListingName(string url)
        {
            if (url == null) return "unknown listing";
            if (url.StartsWith("https://packages.vrchat.com/official")) return "VRChat official";
            if (url.StartsWith("https://packages.vrchat.com/curated")) return "VRChat curated";
            return Host(url) ?? url;
        }

        private static string Host(string url)
        {
            if (url == null) return null;
            if (url.StartsWith("git+")) url = url.Substring(4);
            return System.Uri.TryCreate(url, System.UriKind.Absolute, out var uri) ? uri.Host : null;
        }

        private static void ShowContextMenu(PackageStatus status)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Copy package id"), false, () => EditorGUIUtility.systemCopyBuffer = status.id);

            var folder = Path.Combine(PackageSyncChecker.PackagesDir, status.id);
            if (Directory.Exists(folder))
                menu.AddItem(new GUIContent("Show in Explorer"), false, () => EditorUtility.RevealInFinder(Path.GetFullPath(folder)));

            var url = status.listingUrl ?? status.upmUrl;
            if (url != null)
            {
                var browsable = url.StartsWith("git+") ? url.Substring(4) : url;
                var hash = browsable.IndexOf('#');
                if (hash >= 0) browsable = browsable.Substring(0, hash);
                menu.AddItem(new GUIContent(status.listingUrl != null ? "Open listing URL" : "Open repository"), false, () => Application.OpenURL(browsable));
                menu.AddItem(new GUIContent("Copy URL"), false, () => EditorGUIUtility.systemCopyBuffer = url);
            }
            menu.ShowAsContext();
        }

        /// <summary>Draws the issues with fix buttons. Also used by the build checklist.</summary>
        public static void DrawIssues(List<PackageIssue> issues)
        {
            if (issues.Count == 0)
            {
                EditorGUILayout.HelpBox("All packages match vpm-manifest.json.", MessageType.Info);
                return;
            }

            var missing = issues.Where(i => i.kind == PackageIssueKind.Missing).ToList();
            var missingListings = issues.Where(i => i.Blocking && !i.committed && !i.listingInstalled)
                .Select(i => i.listingUrl).Distinct().ToList();

            foreach (var url in missingListings)
            {
                EditorGUILayout.HelpBox($"The VPM listing {url} isn't added in VCC/ALCOM on this machine and adding it automatically failed, " +
                                        "so the packages from it can't be downloaded.", MessageType.Warning);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Try again"))
                {
                    if (!PackageSyncChecker.AddListingLocally(url, out var error))
                        EditorUtility.DisplayDialog("VPM Package Sync", error, "Ok");
                    GUIUtility.ExitGUI();
                }
                if (GUILayout.Button("Add in VCC/ALCOM")) PackageSyncChecker.OpenAddListingPrompt(url);
                EditorGUILayout.EndHorizontal();
            }

            if (missing.Count > 0 && GUILayout.Button($"Install missing packages ({missing.Count})", GUILayout.Height(26)))
            {
                var errors = new List<string>();
                PackageSyncChecker.AddNeededListings(missing, errors);
                if (!PackageSyncChecker.ResolveMissing(out var error)) errors.Add("Resolving failed: " + error);
                errors.AddRange(PackageSyncChecker.GetIssues().Where(i => i.kind == PackageIssueKind.Missing)
                    .Select(i => "Still missing: " + PackageSyncChecker.DownloadFailureReason(i.id, i.expectedVersion, i.listingUrl)));
                if (errors.Count > 0) EditorUtility.DisplayDialog("VPM Package Sync", string.Join("\n\n", errors), "Ok");
                GUIUtility.ExitGUI();
            }

            foreach (var issue in issues)
            {
                EditorGUILayout.HelpBox(issue.Message, issue.Blocking ? MessageType.Error : MessageType.Warning);

                if (issue.kind == PackageIssueKind.VersionMismatch && !issue.committed &&
                    GUILayout.Button($"Reinstall {issue.id} {issue.expectedVersion}"))
                {
                    if (EditorUtility.DisplayDialog("VPM Package Sync",
                            $"Replace {issue.id} {issue.installedVersion} with {issue.expectedVersion}?\n\n" +
                            "The current version is moved to Library/PackageSyncBackup and put back if the download fails.", "Reinstall", "Cancel"))
                    {
                        PackageSyncChecker.AddNeededListings(new[] { issue }, null);
                        if (!PackageSyncChecker.BeginReinstall(issue, out var error))
                            EditorUtility.DisplayDialog("VPM Package Sync", error, "Ok");
                    }
                    GUIUtility.ExitGUI();
                }

                if (issue.kind == PackageIssueKind.LocalOnly && GUILayout.Button($"Show {issue.id} in Explorer"))
                    EditorUtility.RevealInFinder(Path.GetFullPath(Path.Combine(PackageSyncChecker.PackagesDir, issue.id)));
            }
        }
    }
}

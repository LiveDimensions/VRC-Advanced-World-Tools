# VPM Package Sync

Keeps a Unity project's packages the same on every machine on the team, with extra handling for VRChat's VPM packages.

VRChat's package resolver only installs packages from the listings each person has added to VCC/ALCOM, and only checks
for packages that are missing, not for packages that are the wrong version. So two people can open the same commit and
end up with different packages. VPM Package Sync fixes that:

- **It checks every package against the project** when the project loads and after every script reload (e.g. a git pull):
  - VPM packages against the exact versions locked in `Packages/vpm-manifest.json`
  - package folders in `Packages/` against git, to find ones that are only on this machine
  - git and registry packages from `Packages/packages-lock.json`, for display (Unity keeps these in sync itself)
- **It records which listing each VPM package comes from** in `Packages/vpm-listings.json`, which `vpm-manifest.json`
  doesn't do. On machines that are missing a listing, it adds it to VCC/ALCOM automatically.
- **It replaces the VRChat resolver's load check** (see below) with its own window, which also catches wrong versions
  and can install the missing packages or reinstall the right version.

## Installing

Add this listing to VCC/ALCOM and add VPM Package Sync to the project:

```
https://livedimensions.github.io/VRC-Advanced-World-Tools/index.json
```

**Then commit the package folder itself**, even though it comes from VPM. If git is ignoring its folder when the project
loads, VPM Package Sync asks (once per editor session) whether to add a `!com.livedimensions.vpmpackagesync/` line to
`Packages/.gitignore`, and logs what to commit if you agree. It doesn't stage or commit anything. If a different ignore
file is what ignores it, it leaves that file alone and logs which rule to change.

VPM Package Sync is what adds missing listings and installs missing packages, so it can't install itself: on a fresh clone
without this listing, an uncommitted VPM Package Sync would simply be missing, and since it turns off the VRChat
resolver's load check, nothing would point out the other missing packages either. VRChat commits its own resolver
package for the same reason. When you update VPM Package Sync from VCC/ALCOM, commit the updated folder.

Then commit `Packages/vpm-listings.json` once it appears, along with `vpm-manifest.json`, whenever packages change.

It has no dependencies and its own assembly, so it still compiles and runs when the rest of the project doesn't, for
example when the VRChat SDK itself is missing.

## Using it

**Tools > VPM Package Sync** opens the window. It opens by itself when packages are missing or the wrong version (once
per editor session for the same set of problems).

- At the top: a summary, and the problems with buttons to fix them (**Install missing packages**, **Reinstall** a
  package at the locked version, add a missing listing).
- Below: every package, grouped by where it comes from, with its installed and expected version. The search box filters
  by name or id, **Unity registry** also lists Unity's own packages, and right-clicking a row copies its id, shows it in
  Explorer or opens its listing or repository.

Packages that are committed to git are never reinstalled from a listing. The window tells you to fix them through git
instead, since the committed folder is what everyone else gets.

### Checking packages before a build

```csharp
var problems = LiveDimensions.PackageSync.PackageSyncChecker.GetIssues().Where(i => i.Blocking).ToList();
if (problems.Count > 0) throw new Exception(string.Join("\n", problems.Select(i => i.Message)));

// or draw the problems and their fix buttons in your own editor UI:
LiveDimensions.PackageSync.PackageSyncWindow.DrawIssues(LiveDimensions.PackageSync.PackageSyncChecker.GetIssuesCached());
```

## The VRChat resolver's load check

`VpmResolverPatch` comments out `[InitializeOnLoad]` in
`Packages/com.vrchat.core.vpm-resolver/Editor/Resolver/Resolver.cs`, so VRChat's resolver no longer runs its own check
when the project loads. The **Install missing packages** button still uses the resolver to install packages.

It's the same edit VRCFury's "Disable Buggy VRCSDK VPM Resolver" makes, but it's applied for everyone who opens the
project instead of being a per-machine setting. If VRCFury is in the project, its setting is turned on as well so the
two don't undo each other.

Updating the VRChat SDK replaces `Resolver.cs`; the patch is reapplied the next time the project loads. If the resolver
package is committed, commit the patched file with the SDK update. Removing VPM Package Sync doesn't undo the patch;
reinstalling the resolver package from VCC/ALCOM does.

## Notes

- **git**: VPM Package Sync asks git which package folders are committed, so any `.gitignore` layout works. Without git (not
  installed, or the project isn't a git repository) it can't tell, so it doesn't report local-only packages.
- **Listings it adds**: whatever URL is in `vpm-listings.json` gets added to the VCC/ALCOM of everyone who opens the
  project, so review changes to that file like any other code change.
- **VCC/ALCOM open at the same time**: adding a listing writes VCC/ALCOM's settings file. If VCC/ALCOM is open and
  saves its settings afterwards, it can drop the listing again; VPM Package Sync adds it back the next session.
- **Without VPM**: in a project with no `vpm-manifest.json`, it still lists the project's packages and reports
  package folders that are only on this machine.

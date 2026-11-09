# VRC Advanced World Tools

Tools for developing large VRChat worlds as a team, published as VPM packages.

| Package | What it does |
|---|---|
| [VPM Package Sync](Packages/com.livedimensions.vpmpackagesync/README.md) (`com.livedimensions.vpmpackagesync`) | Keeps every machine's packages the same as the project's: checks them against `vpm-manifest.json`, adds missing VPM listings to VCC/ALCOM, and replaces the VRChat resolver's load check with one that also catches wrong versions. |
| [UdonSharp Fast Build](Packages/com.livedimensions.udonsharpfastbuild/README.md) (`com.livedimensions.udonsharpfastbuild`) | Makes UdonSharp's scene build step linear instead of O(n²) for worlds with many cross-referenced UdonSharpBehaviours. |

## Installing

Add the listing to VCC/ALCOM, then add the packages to your project from there:

```
https://livedimensions.github.io/VRC-Advanced-World-Tools/index.json
```

## Developing

This repository is a Unity 2022.3.22f1 project with the VRChat Worlds SDK. The packages live in `Packages/` and are
edited in place: open the project in ALCOM/VCC or Unity Hub and work on them like any other code.

- `Assets/` is for test scenes and scratch work. It's never part of a release.
- Each package keeps its own version in its `package.json`.
- A new package goes in `Packages/`, gets a `!folder/` line in `Packages/.gitignore`, and gets added to the
  `package` options in [release.yml](.github/workflows/release.yml).

### Testing in another project

To try a change in a real project before releasing it, replace that project's copy of the package with a link to this
repository's folder (close Unity first):

```
mklink /J "<project>\Packages\com.livedimensions.udonsharpfastbuild" "<this repo>\Packages\com.livedimensions.udonsharpfastbuild"
```

Delete the link afterwards and reinstall the released version from VCC/ALCOM.

## Releasing

1. Bump `version` in the package's `package.json` (and note the change in its README or changelog).
2. Commit and push.
3. Run the **Build Release** workflow in the Actions tab and pick the package.

The workflow zips the package, creates a `.unitypackage`, and publishes a GitHub release tagged `<package>-<version>`
(it refuses to release a version that already has a tag). The **Build Repo Listing** workflow then rebuilds the listing
from every release in this repository and publishes it, with a landing page, to GitHub Pages.

The listing's name and id are in [source.json](source.json). Don't change the id once people have added the listing.
The landing page is in [Website](Website).

### One-time setup

In the repository's **Settings → Pages**, set **Source** to **GitHub Actions**.

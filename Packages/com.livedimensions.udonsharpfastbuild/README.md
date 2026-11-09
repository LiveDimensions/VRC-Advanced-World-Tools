# UdonSharp Fast Build

Speeds up UdonSharp's scene build step, which runs in every VRChat world build and every time you enter play mode.
On Project Aincrad's Floor 1 (~7,000 UdonSharpBehaviours) it cut a 21 minute world build to 7 minutes.

| | Original | Fast |
|---|---|---|
| Whole VRChat build (Floor 1) | 1,277s | 412s |
| UdonSharp scene build step in a build | 894s | 71s |
| UdonSharp scene build step, in editor (verifier) | 305s | 44s |

## What it changes

UdonSharp's `[PostProcessScene]` hook (`UdonSharpEditorManager.OnSceneBuildInternal`) loops over every
UdonSharpBehaviour in the scene and copies its data into its UdonBehaviour with `ProxySerializationPolicy.All`.
`All` recursively copies every behaviour the current one references as well. In a world where behaviours reference
each other heavily, every iteration walks most of the scene, so the step is effectively O(n²).

This package makes that one call copy only the behaviour itself (`RootOnly`), which gives the same result because:

- the loop already visits every UdonSharpBehaviour in the scene, so each one still gets its own data copied
- right after the loop UdonSharp clears and re-copies every scene behaviour again, and builds fresh copies of every
  referenced prefab, so the build doesn't use what the recursive copies wrote
- behaviours the recursive copy reached outside the scene (prefab assets, other open scenes) are re-copied once,
  in linear time, so they're exactly as fresh as before

It only changes that one call inside that one hook, and only:

- during a build, or in play mode with a single scene loaded (with several scenes open the original behaviour is kept)
- on the VRChat Worlds SDK version it was verified against (`VerifiedSdkVersion` in `UdonSharpFastSceneBuild.cs`,
  currently 3.10.6). On any other version it turns itself off with a warning until it's checked again

Every scene build logs one line, so you can see in any build log which path was used:

```
[UdonSharpFastBuild] UdonSharp scene build took 43.6s, 6896 fast copies, 55 referenced behaviours outside the scene refreshed, 0 original copies
```

## Settings

Settings are project-wide, saved in `ProjectSettings/Packages/com.livedimensions.udonsharpfastbuild/Settings.asset`.
Commit that file so every machine and automated build (e.g. AutoBuilder) builds the same way.

Edit them in **Project Settings > UdonSharp Fast Build**, or from the menu:

**Tools > UdonSharp Fast Build**

- **Enabled**: turns the speedup on/off (reloads scripts). On by default
- **Fix UdonSharp Prefab Remapping In Builds**: fix for an UdonSharp bug (see below). Off by default
- **Project Settings...**: opens the settings page
- **Verify On Open Scene (slow)**: A/B test of the speedup, see below
- **Validate Prefab Remapping On Open Scene (slow)**: checks UdonSharp's prefab remapping with and without the fix, see below

## Verifying

Open the scene you build (on its own) and run **Tools > UdonSharp Fast Build > Verify On Open Scene (slow)**.

It runs UdonSharp's build step on the scene twice, the original way and the fast way, each on a freshly loaded copy of
the scene, and compares every UdonBehaviour in the scene and in UdonSharp's generated build prefabs: program asset,
enabled state and every public variable (object references compared by scene path / asset GUID). The scene is never
saved and is reloaded afterwards. The report is written to `Logs/UdonSharpFastBuildVerify_*.txt`.

Unity is unresponsive while it runs. The original run takes about as long as UdonSharp's step in a build.

Run it again after updating the VRChat SDK, then update `VerifiedSdkVersion`.

## UdonSharp prefab remapping bug

When building, UdonSharp strips the UdonSharpBehaviour proxy components from its build copies of prefabs, then maps the
components of the original prefab to the copy by index. If a scene behaviour references a component on such a prefab,
the lists have different lengths and UdonSharp throws:

```
InvalidOperationException: Component layout mismatch in prefab mapping <prefab> -> <prefab>
```

The build continues, but UdonSharp stops remapping, so the remaining scene behaviours keep referencing the original prefab
assets instead of the build copies (in a different order every build). It usually works anyway because the original
prefabs carry working Udon data, which is why it's easy to miss.

**Fix UdonSharp Prefab Remapping In Builds** builds the map without the stripped proxies, so UdonSharp finishes remapping
like it's meant to. It's off by default because it changes the build output (references now point at UdonSharp's build
copies instead of the original prefabs). The verifier always turns it on for both of its runs, so the two runs are
comparable. When the fix is used, the build log line ends with `prefab remapping fix on (repaired N prefab objects)`.

**Validate Prefab Remapping On Open Scene (slow)** checks the fix: it records every reference from a scene
UdonSharpBehaviour into a prefab before UdonSharp's build step, runs the step without and with the fix, and checks every
reference against an independently worked out target (same hierarchy path in the build copy, and for components the same
type and index among components of that type). The report (`Logs/UdonSharpPrefabRemapping_*.txt`) starts with `FIX OK`
or `CHECK REPORT`. On Project Aincrad's Floor 1: 107 of 107 references correct with the fix, all 107 left on the original
prefabs without it, and the 566 references into prefabs without a build copy unchanged in both runs.

## Requirements

- VRChat Worlds SDK (UdonSharp included), verified on 3.10.6
- Harmony, which ships with the VRChat SDK

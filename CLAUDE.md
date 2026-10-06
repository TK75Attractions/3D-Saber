# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Unity 2D game project ("3D-Saber") targeting Unity **6000.3.9f1** with the Universal Render Pipeline. Uses the new Input System (`com.unity.inputsystem`) — not legacy `UnityEngine.Input`. The Unity project root is this repository directory, which contains `Assets/`, `Packages/`, `ProjectSettings/`, and the `.slnx` solution files. Open this directory in Unity Hub.

In-code comments are written in Japanese. Preserve that convention when editing existing files.

## PhoneSaber subtree

PhoneSaber sender apps, Mac launchers, diagnostics, and history are in [PhoneSaber/](PhoneSaber/README.md).
Read [PhoneSaber/CLAUDE.md](PhoneSaber/CLAUDE.md) and [PhoneSaber/AGENTS.md](PhoneSaber/AGENTS.md) for that code.
One clone (`git clone https://github.com/TK75Attractions/3D-Saber.git`) with Git LFS is sufficient; run `git lfs install`, `git lfs pull`, and `./PhoneSaber/setup_mac.command`.
Verification is `bash PhoneSaber/tools/verify_phone_saber.sh`. Unity uses this checkout as the project root; `PhoneSaber/` is outside `Assets/` and is not imported.

## Build / Run / Test

There is no CLI build pipeline. All builds, play-testing, and tests run through the Unity Editor:

- **Play the game:** open `Assets/Scenes/Base.unity` (or `SampleScene.unity` / `InputTest.unity`) in the Editor and press Play.
- **Build:** File → Build Profiles in the Editor.
- **Tests:** Window → General → Test Runner (the `com.unity.test-framework` package is installed, but no test assemblies currently exist under `Assets/`).
- `Assembly-CSharp.csproj` and the `.slnx` files are **generated** by Unity — do not hand-edit; regenerate via Edit → Preferences → External Tools → Regenerate project files.

## Architecture

The runtime is organized around a single **`GManager` singleton** (`Assets/Scripts/Managers/GManager.cs`) that owns the whole frame loop. Understanding this flow is the key to being productive here:

1. `GManager.Awake()` assigns the static `GManager.Control`, calls `DontDestroyOnLoad` on its parent, locks the target framerate to 30, grabs the sibling `InputManager` component, and `Instantiate`s the `PlayerPrefab` to produce the `PlayerController` it will drive.
2. `GManager.Update()` is the **only** driver: it calls `IManager.UpdateInput()` and then `Player.UpdatePlayer(dt)` each frame. `InputManager` and `PlayerController` intentionally have **no** `Update()` methods of their own — adding one breaks the manual ordering. If you add a new system that needs per-frame work, route it through `GManager.Update()` the same way.
3. Everything reaches input through `GManager.Control.IManager.*Pressed/GetDown/GetUp` (see `PlayerController.UpdatePlayer` for the canonical pattern). Do not read `Keyboard.current` directly from gameplay code — the `InputManager` fields are the contract.

**`InputPoint` / `Pointer` are a separate, parallel subsystem** (not owned by `GManager`). `InputPoint` opens a UDP socket on port 5005 on a background thread, parses `"x,y"` packets, normalizes to `[-1, 1]`, and exposes `NormalizedPosition` via its own singleton `InputPoint.Instance`. `Pointer` reads that each frame in its own `Update()` to position a transform. This exists to accept pose/tracking data from an external source (e.g. a Python CV script posting coordinates). It uses `Thread.Abort()` in `OnDestroy` — keep this in mind if rewriting; `Abort` is unsupported on .NET 5+ and flaky in Unity.

**`FreezeAspectRate`** is a camera-rig utility that enforces a fixed aspect (default 16:9) by driving five cameras (`main`, `backCamera`, `UICamera`, `frontCamera`, `backImageCamera`) and four letterbox sprites found by name from the parent/grandparent transforms. It runs `[ExecuteInEditMode]`, so broken parent-hierarchy assumptions will throw in the Editor, not just at runtime. The README's note "あんまりいじらなくていいよ" (don't touch this much) applies.

## Prefabs and scene wiring

`GManager`, `InputManager`, and the player are wired through prefabs in `Assets/Scripts/Managers/` (`GameManager.prefab`, `rumia.prefab`), not constructed in code. When renaming or moving these MonoBehaviours, update the prefabs in the Editor so the serialized references (`PlayerPrefab`, `IManager`, etc.) don't become missing-script placeholders — searching for the class name in text won't catch GUID-based prefab references.

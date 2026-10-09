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

There is no CLI build pipeline other than the PhoneSaber Player build method below. All builds, play-testing, and tests run through the Unity Editor:

- **Play the game:** open `Assets/Scenes/Base.unity` (or `SampleScene.unity` / `InputTest.unity`) in the Editor and press Play.
- **Build:** File → Build Profiles, or **Tools → PhoneSaber → Build → macOS Player / Windows Player** (`Assets/Editor/PhoneSaberPlayerBuild.cs`, outputs `Builds/Mac/3D-Saber.app` / `Builds/Windows/3D-Saber.exe` used by the PhoneSaber launchers; also callable with `-batchmode -executeMethod PhoneSaberPlayerBuild.BuildMac`).
- **Tests:** Window → General → Test Runner. Test assemblies exist under `Assets/Tests/Editor/` and `Assets/Tests/PlayMode/` (`SaberTests.Editor.asmdef` and `SaberTests.PlayMode.asmdef`).
- `Assembly-CSharp.csproj` and the `.slnx` files are **generated** by Unity — do not hand-edit; regenerate via Edit → Preferences → External Tools → Regenerate project files.

## Architecture

The runtime is organized around a single **`GManager` singleton** (`Assets/Scripts/Managers/GManager.cs`) that owns the whole frame loop. Understanding this flow is the key to being productive here:

1. `GManager.Awake()` assigns the static `GManager.Control`, calls `DontDestroyOnLoad` on its parent, locks the target framerate to 30, grabs the sibling `InputManager` component, and `Instantiate`s the `PlayerPrefab` to produce the `PlayerController` it will drive.
2. `GManager.Update()` is the **only** driver: it calls `IManager.UpdateInput()` and then `Player.UpdatePlayer(dt)` each frame. `InputManager` and `PlayerController` intentionally have **no** `Update()` methods of their own — adding one breaks the manual ordering. If you add a new system that needs per-frame work, route it through `GManager.Update()` the same way.
3. Everything reaches input through `GManager.Control.IManager.*Pressed/GetDown/GetUp` (see `PlayerController.UpdatePlayer` for the canonical pattern). Do not read `Keyboard.current` directly from gameplay code — the `InputManager` fields are the contract.

**`InputPoint` / `SaberInputBridge` are a separate input path** (not owned by `GManager`). `InputPoint` is its own singleton and, while enabled in Play Mode, receives red UDP on port 5005 and blue UDP on port 5006 using two background receiver threads. It accepts either `"x,y"` or `"x1,y1,x2,y2"` payloads, keeps the latest values, and exposes normalized coordinates and stick endpoints. `SaberInputBridge` consumes those values for the saber; other pointer components also use `InputPoint` where configured.

On macOS Editor and macOS Standalone, starting `InputPoint` also starts `PhoneSaberBonjourPublisher`. It invokes `/usr/bin/dns-sd` to publish `Phone Saber Unity` as `_phonesaber._udp` on the red port (default 5005); blue remains UDP 5006 on the same host. Bonjour is for host discovery only. Disabling/destroying `InputPoint`, leaving Play Mode, or quitting the Editor stops the publisher. `PhoneSaberP2PBridgeProcess` follows the publisher exactly (started only while both the 5005 and 5006 receivers are bound, stopped when either is lost or on shutdown): it runs the optional PhoneSaber P2P bridge from this project’s `PhoneSaber/` subtree (`PhoneSaber/ios/PhoneSaberSender/Tools/phone_saber_p2p_bridge.py`, override with `PHONESABER_P2P_BRIDGE_SCRIPT`, disable with `PHONESABER_P2P_BRIDGE=0`). The bridge receives coordinates from the iPhone over Apple peer-to-peer Wi-Fi and forwards the unchanged payload to `127.0.0.1:5005` / `:5006`, so `InputPoint` receives them like any other UDP datagram; if the bridge cannot start, Unity logs one warning and the LAN path works as before. The bridge advertises `Phone Saber Unity P2P (<Mac name>)`; the Editor pre-builds it in the background once per session. A built `.app` cannot locate the repo script itself, so the Mac launchers (`PhoneSaber/mac/Start-Saber-A/B.command` → `saber-watchdog.sh`) pass `PHONESABER_P2P_BRIDGE_SCRIPT` via `open --env` when the script exists (launcher log `p2p-bridge=yes`); a `.app` opened directly runs LAN-only. The EditMode/PlayMode test assemblies disable bridge auto-start via `PhoneSaberP2PBridgeProcess.AutoStartEnabled = false` in a `[SetUpFixture]`. The iPhone sender prefers LAN while Unity answers its UDP 5007 discovery probe and uses P2P only as a fallback. The receiver threads use stop signals, socket closure, and joins for shutdown; this implementation does not use `Thread.Abort()`.

**`FreezeAspectRate`** is a camera-rig utility that enforces a fixed aspect (default 16:9) by driving five cameras (`main`, `backCamera`, `UICamera`, `frontCamera`, `backImageCamera`) and four letterbox sprites found by name from the parent/grandparent transforms. It runs `[ExecuteInEditMode]`, so broken parent-hierarchy assumptions will throw in the Editor, not just at runtime. The README's note "あんまりいじらなくていいよ" (don't touch this much) applies.

## Prefabs and scene wiring

`GManager`, `InputManager`, and the player are wired through prefabs in `Assets/Scripts/Managers/` (`GameManager.prefab`, `rumia.prefab`), not constructed in code. When renaming or moving these MonoBehaviours, update the prefabs in the Editor so the serialized references (`PlayerPrefab`, `IManager`, etc.) don't become missing-script placeholders — searching for the class name in text won't catch GUID-based prefab references.

# unity_cli

Windows-first npm CLI for Unity CLI automation. The current scope follows the frozen ALP-22 direction: independent CLI implementation, Windows-only runtime target, npm-first installation, and fully open capabilities without default risk gates or secondary confirmation prompts.

## Requirements

- Windows shell environment
- Node.js 20 or newer
- npm global install support

## Install and Build

```powershell
npm install -g unity_cli
unity_cli --help
```

For local development:

```bash
npm install
npm run build
npm test
npm link
unity_cli --help
unity_cli --version
```

## Help Output

`unity_cli --help` prints the standard JSON envelope. The basic output starts with:

```json
{
  "ok": true,
  "data": {
    "usage": "unity_cli <command> [options]",
    "commands": [
      "job status --id <jobId>",
      "job logs --id <jobId> [--tail 100]",
      "job cancel --id <jobId>"
    ]
  }
}
```

`unity_cli --version` prints the package version in the same envelope:

```json
{
  "ok": true,
  "data": {
    "version": "0.1.0"
  }
}
```

## Job Model

Long-running Unity batchmode operations create durable jobs under `%LOCALAPPDATA%\\UnityCli` on Windows. Every command emits stable JSON with `ok`, `requestId`, `data` or `error`. Package manifest operations also use the same envelope and can return either a terminal manifest update result or a Unity resolve job.

Job statuses:

- `queued`
- `running`
- `succeeded`
- `failed`
- `cancelled`
- `timeout`

## Commands

```powershell
unity_cli camera list --bridge-endpoint http://127.0.0.1:47392
unity_cli camera info "Main Camera" --bridge-endpoint http://127.0.0.1:47392
unity_cli camera screenshot "Main Camera" --output Artifacts\main-camera.png --width 1920 --height 1080 --format png --transparent --overwrite --bridge-endpoint http://127.0.0.1:47392
unity_cli scene screenshot Artifacts\scene-view.jpg --width 1280 --height 720 --format jpg --quality 90 --overwrite --bridge-endpoint http://127.0.0.1:47392
unity_cli screenshot capture Artifacts\after.png --bridge-endpoint http://127.0.0.1:47392
unity_cli instances list
unity_cli instances cleanup
unity_cli asset search Player --type Prefab --bridge-endpoint http://127.0.0.1:47392
unity_cli asset info Assets\Prefabs\Player.prefab --bridge-endpoint http://127.0.0.1:47392
unity_cli asset create Assets\Generated\config.json --content "{}" --overwrite --bridge-endpoint http://127.0.0.1:47392
unity_cli asset duplicate Assets\Generated\config.json --to Assets\Generated\config-copy.json --bridge-endpoint http://127.0.0.1:47392
unity_cli prefab info Assets\Prefabs\Player.prefab --bridge-endpoint http://127.0.0.1:47392
unity_cli prefab create Player --path Assets\Prefabs\Player.prefab --overwrite --bridge-endpoint http://127.0.0.1:47392
unity_cli prefab modify Assets\Prefabs\Player.prefab --name Hero --add-child Socket --bridge-endpoint http://127.0.0.1:47392
unity_cli prefab stage-open Assets\Prefabs\Player.prefab --bridge-endpoint http://127.0.0.1:47392
unity_cli prefab hierarchy Assets\Prefabs\Player.prefab --page-size 100 --bridge-endpoint http://127.0.0.1:47392
unity_cli prefab stage-save --bridge-endpoint http://127.0.0.1:47392
unity_cli prefab stage-close --save --bridge-endpoint http://127.0.0.1:47392
unity_cli material info Assets\Materials\Player.mat --bridge-endpoint http://127.0.0.1:47392
unity_cli material create Assets\Materials\Player.mat --shader Standard --overwrite --bridge-endpoint http://127.0.0.1:47392
unity_cli material assign --target Player --material Assets\Materials\Player.mat --slot 0 --bridge-endpoint http://127.0.0.1:47392
unity_cli material set Assets\Materials\Player.mat --property _Color --value "#ff00ffff" --type color --bridge-endpoint http://127.0.0.1:47392
unity_cli graphics quality --quality-level 2 --v-sync-count 1 --bridge-endpoint http://127.0.0.1:47392
unity_cli graphics settings --anti-aliasing 4 --shadow-distance 75 --bridge-endpoint http://127.0.0.1:47392
unity_cli graphics pipeline --bridge-endpoint http://127.0.0.1:47392
unity_cli graphics volume "Global Volume" --bridge-endpoint http://127.0.0.1:47392
unity_cli graphics screenshot Artifacts\graphics.png --include-diagnostics --overwrite --bridge-endpoint http://127.0.0.1:47392
unity_cli physics settings --gravity-y -9.81 --queries-hit-triggers false --bridge-endpoint http://127.0.0.1:47392
unity_cli physics raycast --origin-x 0 --origin-y 2 --origin-z -5 --direction-z 1 --distance 100 --bridge-endpoint http://127.0.0.1:47392
unity_cli physics simulation --steps 3 --delta-time 0.02 --simulate --bridge-endpoint http://127.0.0.1:47392
unity_cli animation clip Assets\Animations\Player.anim --create --overwrite --frame-rate 60 --bridge-endpoint http://127.0.0.1:47392
unity_cli animation state Player --layer 0 --bridge-endpoint http://127.0.0.1:47392
unity_cli animation play Player --state Run --layer 0 --normalized-time 0 --bridge-endpoint http://127.0.0.1:47392
unity_cli animation stop Player --bridge-endpoint http://127.0.0.1:47392
unity_cli vfx graph Assets\VFX\Explosion.vfx --create --overwrite --bridge-endpoint http://127.0.0.1:47392
unity_cli vfx effect Effects\Emitter --graph Assets\VFX\Explosion.vfx --add --bridge-endpoint http://127.0.0.1:47392
unity_cli shader info Assets\Shaders\Unlit.shader --bridge-endpoint http://127.0.0.1:47392
unity_cli shader read Assets\Shaders\Unlit.shader --bridge-endpoint http://127.0.0.1:47392
unity_cli shader create Assets\Shaders\Generated.shader --content-file .\Generated.shader --overwrite --bridge-endpoint http://127.0.0.1:47392
unity_cli shader update Assets\Shaders\Unlit.shader --content-file .\Updated.shader --bridge-endpoint http://127.0.0.1:47392
unity_cli texture info Assets\Textures\Icon.png --bridge-endpoint http://127.0.0.1:47392
unity_cli texture create Assets\Textures\Icon.png --source C:\tmp\icon.png --overwrite --texture-type Sprite --bridge-endpoint http://127.0.0.1:47392
unity_cli texture sprite Assets\Textures\Icon.png --pixels-per-unit 128 --pivot-x 0.5 --pivot-y 0.5 --bridge-endpoint http://127.0.0.1:47392
unity_cli texture import-settings Assets\Textures\Icon.png --readable false --mipmap false --srgb true --bridge-endpoint http://127.0.0.1:47392
unity_cli ui canvas/create HUD --bridge-endpoint http://127.0.0.1:47392
unity_cli ui text/create Title --parent HUD --text "Start" --width 320 --height 80 --bridge-endpoint http://127.0.0.1:47392
unity_cli ui button/create PlayButton --parent HUD --label "Play" --bridge-endpoint http://127.0.0.1:47392
unity_cli ui image/create Logo --parent HUD --sprite Assets\UI\logo.png --bridge-endpoint http://127.0.0.1:47392
unity_cli ui document/create --name MainUIDocument --uxml Assets\UI\Main.uxml --uss Assets\UI\Main.uss --panel-settings Assets\UI\Panel.asset --overwrite --bridge-endpoint http://127.0.0.1:47392
unity_cli ui document/info MainUIDocument --bridge-endpoint http://127.0.0.1:47392
unity_cli ui document/info Assets\UI\Main.uxml
unity_cli ui hierarchy HUD --page-size 100 --bridge-endpoint http://127.0.0.1:47392
unity_cli package list --project C:\path\UnityProject
unity_cli package info com.unity.textmeshpro --project C:\path\UnityProject
unity_cli package search textmeshpro --registry https://packages.unity.com
unity_cli package install com.acme.tool@1.2.3 --project C:\path\UnityProject --unity "C:\Program Files\Unity\Hub\Editor\2022.3.45f1\Editor\Unity.exe" --wait
unity_cli package remove com.acme.tool --project C:\path\UnityProject --unity "C:\Program Files\Unity\Hub\Editor\2022.3.45f1\Editor\Unity.exe" --wait
unity_cli package resolve --project C:\path\UnityProject --unity "C:\Program Files\Unity\Hub\Editor\2022.3.45f1\Editor\Unity.exe" --wait
unity_cli package registry get --project C:\path\UnityProject
unity_cli package registry set --url https://packages.unity.com --project C:\path\UnityProject
unity_cli package scoped-registry add --name Acme --url https://registry.acme.test --scope com.acme --project C:\path\UnityProject
unity_cli package scoped-registry list --project C:\path\UnityProject
unity_cli editor state --project C:\path\UnityProject
unity_cli editor play --project C:\path\UnityProject --timeout-ms 7000
unity_cli editor pause --project C:\path\UnityProject
unity_cli editor stop --project C:\path\UnityProject --timeout-ms 7000
unity_cli editor menu --project C:\path\UnityProject File/Save --timeout-ms 7000
unity_cli code exec --file .\runtime-code.csx --project C:\path\UnityProject --bridge-endpoint http://127.0.0.1:47392
unity_cli code exec --stdin --channel live --bridge-endpoint http://127.0.0.1:47392
unity_cli code exec --code "return UnityEditor.EditorApplication.isPlaying;" --channel batchmode --project C:\path\UnityProject --unity "C:\Program Files\Unity\Hub\Editor\2022.3.45f1\Editor\Unity.exe" --wait
unity_cli editor refresh --compile --wait --project C:\path\UnityProject --unity "C:\Program Files\Unity\Hub\Editor\2022.3.45f1\Editor\Unity.exe"
unity_cli test run --platform EditMode --wait --project C:\path\UnityProject --unity "C:\Program Files\Unity\Hub\Editor\2022.3.45f1\Editor\Unity.exe"
unity_cli test poll --id <jobId>
unity_cli build run --target StandaloneWindows64 --output Builds\Windows\Game.exe --wait
unity_cli build status --id <jobId>
unity_cli build cancel --id <jobId>
unity_cli job status --id <jobId>
unity_cli job logs --id <jobId> --tail 100
unity_cli job cancel --id <jobId>
```

Use `--wait-timeout-ms <ms>` to limit synchronous waiting. Use `--timeout-ms <ms>` to time out the Unity batchmode process itself.

`camera list` returns Camera summaries including hierarchy path, tag, enabled state, `targetTexture`, clear flags, projection, FOV/orthographic size and pixel dimensions. `camera info <target>` resolves by Camera name, hierarchy path or instance id. `camera screenshot <target>` and `scene screenshot` write PNG/JPG artifacts from the live Unity Editor Bridge and return `screenshotPath`, `artifactPath`, `format`, `width`, `height`, `requestId` and `logPath` in the shared envelope. Relative output paths are resolved under the Unity project root, output directories are created automatically, and existing files require `--overwrite`. Structured errors include `E_CAMERA_NOT_FOUND` for `camera info` when a target Camera cannot be resolved, `E_TARGET_NOT_FOUND` for `camera screenshot` when the target is empty or cannot be resolved, `E_SCENE_VIEW_NOT_AVAILABLE`, `E_OUTPUT_EXISTS`, `E_OUTPUT_PATH_REQUIRED`, `E_SCREENSHOT_FORMAT_UNSUPPORTED`, `E_IO_ERROR`, `E_UNITY_BUSY` and bridge connection diagnostics.

`asset search <keyword>` pages AssetDatabase results by query/type/path. `asset info <path>` returns GUID, type, extension, folder flag and size. `asset create`, `asset import`, `asset modify`, `asset delete`, `asset duplicate`, `asset move` and `asset rename` perform AssetDatabase-backed project asset operations and return `plannedEffects`. `asset mkdir` remains a CLI alias for `asset create --type folder`. Structured errors include `E_ASSET_PATH_REQUIRED`, `E_ASSET_PATH_INVALID`, `E_ASSET_NOT_FOUND`, `E_ASSET_OUTPUT_EXISTS`, `E_ASSET_SOURCE_NOT_FOUND`, `E_ASSET_CONTENT_REQUIRED`, `E_ASSET_DELETE_FAILED`, `E_ASSET_DUPLICATE_FAILED`, `E_ASSET_MOVE_FAILED`, `E_ASSET_RENAME_FAILED` and `E_UNITY_BUSY`.

`prefab info <path>` returns Prefab metadata including GUID, root name, asset type, `variant` base/source paths and GUIDs, `dependencies.items`, and `nestedPrefabs.items` rather than counts only. `prefab create <source> --path <path>` creates a Prefab from a scene GameObject target or copies an existing Prefab asset and returns `plannedEffects`. `prefab modify <path>` applies small asset edits such as root rename and direct child add/remove without opening a Prefab Stage. `prefab stage-open`, `prefab stage-save` and `prefab stage-close --save` manage Prefab Stage lifecycle through the live Unity Editor Bridge. `prefab hierarchy <path>` pages hierarchy items with `pageInfo`, component summaries, the same Prefab variant/dependency summary, `nestedPrefabs.items` and `logPath` to avoid blocking large Prefabs. Prefab paths must be `Assets/*.prefab`. Structured errors include `E_PREFAB_PATH_REQUIRED`, `E_PREFAB_PATH_INVALID`, `E_PREFAB_NOT_FOUND`, `E_ASSET_NOT_PREFAB`, `E_PREFAB_OUTPUT_EXISTS`, `E_PREFAB_MODIFY_NOOP`, `E_PREFAB_CHILD_EXISTS`, `E_PREFAB_CHILD_NOT_FOUND`, `E_PREFAB_DIRTY_CONFLICT`, `E_PREFAB_STAGE_NOT_OPEN`, `E_PREFAB_STAGE_MISMATCH` and `E_UNITY_BUSY`.

`material info <path>` returns shader, keywords, ShaderUtil property metadata, current values, texture references, dependencies and `logPath`. `material create <path> --shader <name>` creates an `Assets/*.mat` Material and returns `plannedEffects`. `material assign --target <go> --material <path>` assigns to a Renderer shared material slot or a UI Graphic `material` property. `material set <path> --property <name> --value <value>` writes color (`#RRGGBB`, `#RRGGBBAA` or `r,g,b[,a]`), float/range, vector (`x,y,z,w`) and texture (`Assets/*` texture path or `null`) values according to the Shader property type. Structured errors include `E_MATERIAL_PATH_REQUIRED`, `E_MATERIAL_PATH_INVALID`, `E_MATERIAL_NOT_FOUND`, `E_SHADER_NOT_FOUND`, `E_MATERIAL_PROPERTY_NOT_FOUND`, `E_MATERIAL_PROPERTY_TYPE_MISMATCH`, `E_TARGET_AMBIGUOUS`, `E_MATERIAL_SLOT_INVALID`, `E_TEXTURE_NOT_FOUND` and `E_UNITY_BUSY`.

`graphics quality` reads and updates `QualitySettings` values including quality level, v-sync, anti-aliasing, shadows, texture limits, LOD bias, realtime reflection probes and soft particles. `graphics settings` returns GraphicsSettings and render pipeline summaries, can clear to built-in pipeline or set an `Assets/*.asset` `RenderPipelineAsset`, and supports generic `--property/--value` edits for selected settings. `graphics pipeline` returns the current/default render pipeline summary without mutating project settings. `graphics volume [target]` lists active scene Volume components and their assigned profiles using optional render pipeline APIs when present. `graphics screenshot` reuses live screenshot capture and returns the artifact plus graphically relevant diagnostics. Structured errors include `E_RENDER_PIPELINE_NOT_FOUND`, `E_QUALITY_LEVEL_INVALID`, `E_GRAPHICS_SETTING_UNSUPPORTED`, `E_GRAPHICS_SETTING_INVALID`, `E_SETTING_VALUE_INVALID`, screenshot IO/path errors and `E_UNITY_BUSY`.

`physics settings` reads and updates Physics/Physics2D project settings including 3D/2D gravity, solver iterations, contact offset, bounce threshold, ray query flags and auto simulation. `physics raycast` supports 3D and 2D ray queries with origin, direction, distance, layer mask and `--all`, returning hit summaries or `nextActions` when no collider matches. `physics simulation` reports simulation state and can run explicit 3D/2D simulation steps with `--simulate`. Structured errors include `E_PHYSICS_SETTING_UNSUPPORTED`, `E_SETTING_VALUE_INVALID` and `E_UNITY_BUSY`.

`profiler status/start/stop/capture` reads Profiler state, toggles capture, and writes JSON or raw profiler artifacts through the live Bridge or Local Agent job path for capture. Returned envelopes include `artifactPath`, `profilerPath`, duration, requestId-compatible logs and `plannedEffects`. Structured errors include `E_OUTPUT_PATH_REQUIRED`, `E_OUTPUT_EXISTS`, `E_PROFILER_FORMAT_UNSUPPORTED`, `E_PROFILER_CAPTURE_FAILED`, `E_IO_ERROR` and `E_UNITY_BUSY`.

`probuilder shape/create/edit/csg` creates ProBuilder-compatible primitive objects, edits target objects and exercises CSG envelope/boundary flows through the live Bridge or Local Agent job path. The bundled package includes `unitypkg/OfflinePackages/com.unity.probuilder` as an offline validation stub for environments that cannot download the official package; do not keep that stub both inside the bridge package and copied into `Packages/com.unity.probuilder` at the same time. Structured errors include `E_PROBUILDER_PACKAGE_NOT_FOUND`, `E_PROBUILDER_SHAPE_UNSUPPORTED`, `E_PROBUILDER_OPERATION_REQUIRED`, `E_TARGET_NOT_FOUND` and `E_UNITY_BUSY`.

`animation clip <path>` reads or creates `Assets/*.anim` AnimationClip assets and returns clip metadata, frame rate, legacy flag and editor curve binding summaries. `animation state/play/stop <target>` resolves a unique GameObject, reads Animator state, plays an Animator state by name/path/hash-compatible string, or rebinds/stops the Animator. Structured errors include `E_ANIMATION_CLIP_PATH_REQUIRED`, `E_ANIMATION_CLIP_PATH_INVALID`, `E_ANIMATION_CLIP_NOT_FOUND`, `E_ANIMATION_CLIP_EXISTS`, `E_ANIMATOR_NOT_FOUND`, `E_ANIMATION_STATE_REQUIRED`, `E_ANIMATION_LAYER_INVALID`, `E_TARGET_AMBIGUOUS` and `E_UNITY_BUSY`.

`vfx graph <path>` reads or creates `Assets/*.vfx` Visual Effect Graph assets when `com.unity.visualeffectgraph` is installed. `vfx effect <target>` reads a VisualEffect component, can `--add` one to a resolved GameObject, and can assign `--graph <Assets/*.vfx>`, returning target/component metadata and `plannedEffects`. Windows-style `Assets\VFX\Explosion.vfx` paths are normalized to Unity asset paths. Structured errors include `E_VFX_PACKAGE_MISSING`, `E_VFX_GRAPH_PATH_REQUIRED`, `E_VFX_GRAPH_PATH_INVALID`, `E_VFX_GRAPH_NOT_FOUND`, `E_VFX_GRAPH_CREATE_FAILED`, `E_VFX_COMPONENT_NOT_FOUND`, `E_VFX_ASSIGN_UNSUPPORTED`, `E_TARGET_AMBIGUOUS` and `E_UNITY_BUSY`.

`shader info/read/create/update` targets `Assets/*.shader` and `Assets/*.shadergraph` files through the live Unity Editor Bridge. `shader read` returns UTF-8 content plus GUID, file metadata and ShaderUtil compile diagnostics when Unity imports the asset as a `Shader`. `shader create` writes supplied content or a minimal default `.shader`, refreshes/imports it and returns `plannedEffects`; ShaderGraph creation requires explicit content. Structured errors include `E_SHADER_PATH_REQUIRED`, `E_SHADER_PATH_INVALID`, `E_SHADER_NOT_FOUND`, `E_SHADER_OUTPUT_EXISTS`, `E_SHADER_CONTENT_REQUIRED`, `E_IO_ERROR` and `E_UNITY_BUSY`.

`texture info/create/sprite/import-settings` targets texture assets under `Assets/`. `texture create` copies `--source` or generates a solid PNG/JPG from `--width`, `--height` and `--color`, refreshes the AssetDatabase and returns texture metadata plus importer settings. `texture sprite` configures Sprite import mode, pixels-per-unit and pivot. `texture import-settings` updates TextureImporter fields such as readability, mipmaps, sRGB, alpha transparency, wrap/filter mode, max size and compression. Structured errors include `E_TEXTURE_PATH_REQUIRED`, `E_TEXTURE_PATH_INVALID`, `E_TEXTURE_NOT_FOUND`, `E_TEXTURE_OUTPUT_EXISTS`, `E_TEXTURE_SOURCE_NOT_FOUND`, `E_TEXTURE_FORMAT_UNSUPPORTED`, `E_TEXTURE_COLOR_INVALID`, `E_IMPORT_FAILED`, `E_IO_ERROR` and `E_UNITY_BUSY`.

`ui canvas/create`, `ui text/create`, `ui button/create` and `ui image/create` create uGUI objects through the live Unity Editor Bridge. They return `created`, `rectTransform`, `plannedEffects`, `requestId` and `logPath`; text/button/image commands accept parent/canvas targets, dimensions, anchored position, text/label, color and sprite asset options. `ui document/create` creates or overwrites `.uxml`/`.uss` assets, links them to a `UIDocument`, optionally assigns `PanelSettings`, and returns resource references. `ui document/info` supports live UIDocument inspection and offline `.uxml`/`.uss` file reads without a Bridge endpoint for basic file diagnostics. `ui hierarchy` pages Canvas, RectTransform and UIDocument summaries with `pageInfo`. Ambiguous UI parent, UIDocument and hierarchy targets return `E_TARGET_AMBIGUOUS` with `matchCount` and candidate summaries so callers can retry with hierarchy path or instance id. UI structured errors include `E_UI_PARENT_NOT_FOUND`, `E_UI_SPRITE_NOT_FOUND`, `E_UI_UXML_NOT_FOUND`, `E_UI_USS_NOT_FOUND`, `E_UI_PANEL_SETTINGS_NOT_FOUND`, `E_TARGET_NOT_FOUND`, `E_TARGET_AMBIGUOUS` and `E_UNITY_BUSY`.

`package list` and `package info` read `Packages/manifest.json` and `Packages/packages-lock.json`. `package install`, `package remove`, `package registry` and `package scoped-registry` update `manifest.json` directly. When `--unity` or `UNITY_EXE` is available, `package install/remove/resolve` create a `package` job that invokes Unity batchmode with `UnityCli.UnityCliBatch.ResolvePackages`; otherwise install/remove return a terminal `manifest_updated` result with a `nextActions` hint to run Unity resolve.

`instances list` reads the local Unity instance registry and reports Editor instances with project path/hash, bridge endpoint, status and heartbeat timestamps. `instances cleanup` removes stale entries after the 30s heartbeat window. Live Bridge commands auto-select a healthy instance by current working directory, by the only healthy instance, or by explicit `--instance <instanceId|projectHash|projectPathPrefix>`; ambiguous healthy instances return `E_INSTANCE_AMBIGUOUS` instead of guessing.

`editor state` returns playMode, pause, busy, activeScene and Prefab Stage status through the live Unity Editor Bridge. `editor play`, `editor pause` and `editor stop` control Editor PlayMode and pause state with optional `--timeout-ms`. `editor menu <path>` executes a Unity Editor menu item and returns execution result, elapsed time and structured diagnostics. `editor console --count <n>` returns recent Unity Console entries and can clear them with `--clear`. Editor commands do not require a risk-confirmation flag; `plannedEffects` is returned as observable output. Structured errors include `E_UNITY_BUSY` (editor is compiling, updating, building or switching PlayMode), `E_PLAYMODE_TIMEOUT`, `E_MENU_NOT_FOUND`, `E_MENU_TIMEOUT`, `E_INSTANCE_NOT_FOUND`, `E_INSTANCE_STALE` and `E_INSTANCE_AMBIGUOUS` with `nextActions` to resolve.

`code exec` executes Runtime Code from exactly one source: `--file <path>`, `--stdin`, or `--code <text>`. Live mode sends `code.exec` to the Unity Editor Bridge and returns `compile.diagnostics`, `runtime.stdout`, `runtime.stderr`, `runtime.returnValue`, `requestId`, `logs`, `audit`, `artifacts`, and `nextActions` in the shared envelope. `--channel batchmode` or `--job` deploys the bundled bridge package and creates a durable `code` job that invokes `UnityCli.UnityCliBatch.ExecuteCode`, writes a `manifest.json` under the artifact directory, and loads the batchmode result artifact back into job status. Runtime Code references UnityEngine, UnityEditor and loaded project/Unity assemblies by default; `--refs <comma-list>`, `--define <symbol>`, `--args <json>`, `--timeout-ms <ms>` and `--artifact-dir <path>` extend the request without adding default disablement, `--yes`, secondary confirmation, bottom-layer pass-through or risk gates. Structured errors include `E_CODE_SOURCE_INVALID`, `E_CODE_COMPILE_FAILED`, `E_CODE_RUNTIME_EXCEPTION`, `E_REFERENCE_RESOLUTION_FAILED`, `E_EXECUTION_TIMEOUT`, `E_UNITY_BUSY` and bridge/job diagnostics.

`custom-tool list` returns Unity Editor Package registered tools with `name`, display metadata, `inputSchema`, `outputSchema`, capability type, timeout limits, artifact kinds and registration provenance. `custom-tool run <name>` accepts `--input`, `--input-file` or `--stdin`, forwards `timeoutMs`, explicit `--job` preference and `artifactDir`, validates the supplied input against the registered `inputSchema` object/required/property type subset, then returns `command`, `tool`, `execution`, `result`, `artifacts`, `audit` and `logs.bridge` in the shared envelope. Project code registers first-class tools with `UnityCliCustomToolAttribute` or `UnityCliBridge.RegisterCustomTool`; the Bridge only dispatches registered names and does not expose bottom-layer pass-through, arbitrary method names or hidden tool escape hatches. `--job` is currently a declared Local Agent preference only: the bundled Live Bridge returns `E_JOB_UNSUPPORTED` for explicit `--job` instead of pretending to run an asynchronous Local Agent custom-tool job.

## Unity Editor Bridge

`init` and `package install/remove/resolve` install the bundled `com.unity_cli` package by writing a local `file:` dependency into the target project's `Packages/manifest.json`; no scoped registry is configured during local development. `init` also writes `Packages/unity_cli.install.json` with the project hash, package version and CLI compatibility range, while `init --check` verifies those values without mutating the project. When a live Bridge endpoint is already reachable, `init` asks the open Editor to run `Assets/Refresh` so a resident Editor sees the package update. When the Editor compiles that package, `UnityCliBridge` starts a Windows loopback HTTP listener on `http://127.0.0.1:47392` (probing later ports when needed), writes `instances.json` under `%LOCALAPPDATA%\UnityCli` or `UNITY_CLI_DATA_DIR`, and refreshes a heartbeat every 5 seconds. During domain reload or Editor quit it marks the instance stale so the CLI can report stale state or remove it with `instances cleanup`. The bridge serves `POST /bridge/execute` for the currently bundled live Bridge commands only: `camera.list`, `camera.info`, `camera.screenshot`, `scene.hierarchy`, `scene.screenshot`, `go.find`, `asset.search`, `asset.info`, `asset.create`, `asset.import`, `asset.modify`, `asset.delete`, `asset.duplicate`, `asset.move`, `asset.rename`, `diagnose.preflight`, `screenshot.capture`, `prefab.info`, `prefab.hierarchy`, `prefab.create`, `prefab.modify`, `prefab.stage-open`, `prefab.stage-save`, `prefab.stage-close`, `ui.canvas.create`, `ui.text.create`, `ui.button.create`, `ui.image.create`, `ui.document.info`, `ui.document.create`, `ui.hierarchy`, `material.info`, `material.create`, `material.assign`, `material.set`, `graphics.quality`, `graphics.settings`, `graphics.screenshot`, `physics.settings`, `physics.raycast`, `physics.simulation`, `profiler.start`, `profiler.stop`, `profiler.capture`, `profiler.status`, `probuilder.shape`, `probuilder.create`, `probuilder.csg`, `probuilder.edit`, `animation.clip`, `animation.state`, `animation.play`, `animation.stop`, `vfx.graph`, `vfx.effect`, `shader.info`, `shader.read`, `shader.create`, `shader.update`, `texture.info`, `texture.create`, `texture.sprite`, `texture.import-settings`, `editor.state`, `editor.play`, `editor.pause`, `editor.stop`, `editor.menu`, `editor.console`, `code.exec`, `custom-tool.list` and `custom-tool.run`. Other CLI query/edit commands such as `component set` and `script edit` are not routed by this package yet and return `E_BRIDGE_COMMAND_UNSUPPORTED` if sent to the bundled Bridge endpoint. The bridge executes Unity APIs on the Editor main thread and writes diagnostics to `<project>\Logs\UnityCliBridge.log`. If a request waits 60 seconds before Unity starts it, the bridge cancels that queued work before execution and returns `E_UNITY_BUSY` with no side effects applied. If a mutating request already started before the HTTP timeout, the bridge returns a success envelope with `status: "running"`, `sideEffectSemantics: "started-before-http-timeout"`, the original `requestId` and `logPath` so callers do not treat a possible in-flight side effect as a failed/no-op request. During Unity domain reload or Editor quit, the Bridge stops its loopback listener before the new assembly starts it again; on startup it also asks any older loaded `UnityCliBridge` type in the same Editor process to stop its listener before binding the port, preventing stale runtime capabilities from serving `/health` after package replacement.

## Batch Automation

`batch run <file.json>` composes registered Unity CLI first-class commands only. It does not provide hidden bottom-layer commands, arbitrary tool calls, or capability-gap escape hatches.

```powershell
unity_cli batch schema
unity_cli batch run .\automation\camera-test-screenshot.json --preflight
unity_cli batch run .\automation\camera-test-screenshot.json --fail-fast true --parallel 1
```

Example `camera-test-screenshot.json`:

```json
{
  "defaults": {
    "project": "C:/work/Game",
    "unity": "C:/Program Files/Unity/Hub/Editor/2022.3.45f1/Editor/Unity.exe",
    "wait": true,
    "timeoutMs": 600000,
    "waitTimeoutMs": 600000,
    "env": {
      "UNITY_CLI_BRIDGE_ENDPOINT": "http://127.0.0.1:18080"
    }
  },
  "envAllowlist": ["UNITY_CLI_BRIDGE_ENDPOINT"],
  "failFast": true,
  "parallel": 1,
  "steps": [
    {
      "id": "list-cameras",
      "command": ["camera", "list"]
    },
    {
      "id": "inspect-main-camera",
      "command": ["camera", "info", "Main Camera"],
      "dependsOn": ["list-cameras"]
    },
    {
      "id": "run-editmode-tests",
      "command": ["test", "run"],
      "args": ["--platform", "EditMode"],
      "dependsOn": ["inspect-main-camera"]
    },
    {
      "id": "capture-after-test",
      "command": ["camera", "screenshot", "Main Camera"],
      "args": ["--output", "Artifacts/after-test.png", "--overwrite"],
      "dependsOn": ["run-editmode-tests"]
    }
  ]
}
```

Batch output includes a batch requestId, each step requestId/envelope, summary counts, aggregated `plannedEffects`, and a `logIndex` that links batch record path, step requestIds, jobIds, and Unity log paths.

`--parallel` is intentionally restricted to commands declared parallel-safe. `material info`, `shader info`, `shader read`, `texture info`, `ui document/info`, `ui hierarchy`, `animation state`, `physics raycast` and `profiler status` are parallel-safe. Mutating/artifact commands such as `screenshot capture`, `camera screenshot`, `scene screenshot`, `prefab create`, `prefab stage-open`, `prefab stage-save`, `prefab stage-close`, `graphics quality`, `graphics settings`, `graphics screenshot`, `physics settings`, `physics simulation`, `profiler start`, `profiler stop`, `profiler capture`, `probuilder shape`, `probuilder create`, `probuilder csg`, `probuilder edit`, `material create`, `material assign`, `material set`, `animation clip`, `animation play`, `animation stop`, `vfx graph`, `vfx effect`, `shader create`, `shader update`, `texture create`, `texture sprite`, `texture import-settings`, `ui canvas/create`, `ui text/create`, `ui button/create`, `ui image/create`, `ui document/create`, `code exec`, `editor refresh`, `test run`, and `build run` must run serially; preflight returns a structured `BATCH_PARALLEL_CONFLICT` if a batch requests unsafe parallelism.

## Windows logs and diagnostics

The default diagnostic data and log directory is:

```text
%LOCALAPPDATA%\UnityCli
```

`requestId`, `stepRequestId`, `jobId`, batch record paths and log paths are observability fields only. They do not enable risk controls, confirmation prompts, or high-risk command blocking.

## Windows + Unity Editor Follow-up Verification

Automated tests use fake Unity and injected bridge executors to verify CLI behavior without requiring Unity. TypeScript tests cover CLI protocol forwarding, offline UI Toolkit file reads and failure-envelope preservation for Bridge responses; `src/camera-bridge-source.test.ts` is a source-level guard for the bundled C# Bridge implementation. These tests do not instantiate Unity Editor, so C# runtime behavior for empty camera targets, missing Cameras, screenshot IO failures, actual PNG/JPG artifacts, Prefab Stage open/save/close, Prefab dirty conflicts, nested/variant Prefab summaries, Material asset creation, Renderer/UI Graphic assignment, Material property writes, GraphicsSettings/QualitySettings persistence, render pipeline asset resolution, Physics/Physics2D project setting persistence, raycast collision hits, explicit simulation steps, AnimationClip asset creation/binding summaries, Animator state/play/stop behavior, VFX Graph asset creation, VisualEffect assignment, Shader import diagnostics, TextureImporter mutations, uGUI object creation, UIDocument/PanelSettings linking, UI hierarchy summaries and Runtime Code live execution must still be proven by real Windows + Unity Editor validation before release.

- Unity Hub / `Unity.exe` discovery across installed Editor versions.
- Unity Bridge endpoint discovery and live camera/scene/screenshot execution against an open Editor.
- `editor refresh --compile --wait` against a real project compilation cycle.
- EditMode and PlayMode result XML parsing against a real Unity project.
- Build output path creation and module-missing diagnostics for `StandaloneWindows64`.
- Process cancellation behavior for Unity batchmode under Windows shells and CI.
- Unity PackageManager resolve hook `UnityCli.UnityCliBatch.ResolvePackages` against a real Unity project after manifest/scoped registry changes.
- Registry reachability and credential behavior for private scoped registries.

Advanced or custom extension commands should enter a later backlog as registered first-class commands. They are not implemented through bottom-layer pass-through in this batch.

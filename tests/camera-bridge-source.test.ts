import assert from "node:assert/strict";
import { readdir, readFile } from "node:fs/promises";
import test from "node:test";

const editorUrl = new URL("../../unitypkg/Editor/", import.meta.url);
const sourceUrl = new URL("UnityCliBridge.cs", editorUrl);
const readmeUrl = new URL("../../README.md", import.meta.url);
const extensionsUrl = new URL("../../extensions/", import.meta.url);

test("Unity Bridge source keeps C# files structurally balanced", async () => {
  const files = await listCSharpFiles(editorUrl);

  for (const file of files) {
    const source = await readFile(file, "utf8");
    const strippedSource = stripCSharpTrivia(source);
    let braceDepth = 0;

    for (const [index, line] of strippedSource.split("\n").entries()) {
      for (const character of line) {
        if (character === "{") {
          braceDepth += 1;
        } else if (character === "}") {
          braceDepth -= 1;
        }
      }
      assert.ok(braceDepth >= 0, `C# brace depth went negative before ${file.pathname}:${index + 1}`);
    }

    assert.equal(braceDepth, 0, `C# brace depth did not return to zero for ${file.pathname}`);
  }
});

test("Unity Bridge source does not duplicate C# method signatures", async () => {
  const source = await readUnityBridgeSources();

  assert.deepEqual(duplicateCSharpMethodSignatures(source), []);
});

test("Unity Bridge exposes documented core camera, screenshot, prefab, UI, material, graphics, physics, profiler, animation, shader, texture and editor commands", async () => {
  const source = await readUnityBridgeSources();
  const vfxSource = await readExtensionSources("vfx-graph");
  const bridgeSource = await readFile(sourceUrl, "utf8");
  const readme = await readFile(readmeUrl, "utf8");
  const bridgeSection = readme.slice(readme.indexOf("## Unity Editor Bridge"), readme.indexOf("## 项目自定义工具"));

  assert.match(source, /HttpListener/);
  assert.doesNotMatch(bridgeSource, /switch \(request\.Command\)/);
  assert.match(source, /POST" \|\| context\.Request\.Url\.AbsolutePath != "\/bridge\/execute"/);
  assert.match(source, /CapabilityJson\("camera", CameraCommands\)/);
  assert.match(source, /CapabilityJson\("asset", AssetCommands\)/);
  assert.match(source, /CapabilityJson\("editor", EditorCommands\)/);
  assert.match(source, /private static readonly string\[\] AssetCommands = \{ [^}]*"asset\.refresh"[^}]* \};/);
  assert.match(source, /private static readonly string\[\] EditorCommands = \{ [^}]*"editor\.compile"[^}]* \};/);
  assert.match(source, /CapabilityJson\("prefab", PrefabCommands\)/);
  assert.match(source, /CapabilityJson\("ui", UiCommands\)/);
  assert.match(source, /CapabilityJson\("material", MaterialCommands\)/);
  assert.match(source, /CapabilityJson\("graphics", GraphicsCommands\)/);
  assert.match(source, /CapabilityJson\("physics", PhysicsCommands\)/);
  assert.match(source, /CapabilityJson\("profiler", ProfilerCommands\)/);
  assert.match(source, /CapabilityJson\("animation", AnimationCommands\)/);
  assert.doesNotMatch(source, /CapabilityJson\("(probuilder|cinemachine|vfx|urp|hdrp)",/);
  assert.match(source, /w\.RawValue\(CapabilityJson\("physics", PhysicsCommands\)\)/);
  assert.match(source, /CapabilityJson\("shader", ShaderCommands\)/);
  assert.match(source, /CapabilityJson\("texture", TextureCommands\)/);
  assert.match(source, /CapabilityJson\("code", CodeCommands\)/);
  assert.match(source, /CapabilityJson\("project-tool", ProjectToolCommands\)/);
  assert.match(source, /CapabilityJson\("runtime-tool", RuntimeToolCommands\)/);
  assert.match(source, /CapabilityJson\("context", ContextCommands\)/);
  assert.match(source, /CapabilityJson\("extension", ExtensionCommands\)/);
  assert.match(source, /AssemblyReloadEvents\.beforeAssemblyReload \+= OnBeforeAssemblyReload/);
  assert.match(source, /EditorApplication\.quitting \+= UnregisterAndStop/);
  assert.match(source, /RegisterInstance\("healthy"\)/);
  assert.match(source, /RegisterInstance\("stale"\)/);
  assert.match(source, /return "unity-" \+ ProjectHash\(\) \+ "-" \+ System\.Diagnostics\.Process\.GetCurrentProcess\(\)\.Id\.ToString/);
  assert.doesNotMatch(source, /InstanceSuffix/);
  assert.match(source, /DeleteSupersededInstanceFiles\(\)/);
  assert.match(source, /ShouldRemoveRegistration\(item\)/);
  assert.match(source, /RegistrationPid\(string json\)/);
  assert.match(source, /RegistrationTimestamp\(string json\)/);
  assert.match(source, /StaleRegistrationSeconds/);
  assert.match(source, /"instances"/);
  assert.match(source, /\.Prop\("domainReloading", domainReloading\)/);
  assert.doesNotMatch(source, /\.Prop\("domainReloading", EditorApplication\.isCompiling\)/);
  assert.match(source, /private static Type FindType\(string fullName\)/);
  assert.match(source, /private static string JsonLiteralOrString\(string value\)/);
  assert.doesNotMatch(source, /new\[\] \{ \(object\)/);
  assert.doesNotMatch(source, /private static readonly string\[\] (ProBuilder|Cinemachine|Vfx|Urp|Hdrp)Commands/);
  assert.match(source, /RegisterCommand\(registry, "camera\.list", "camera", false/);
  assert.match(source, /RegisterCommand\(registry, "camera\.info", "camera", false/);
  assert.match(source, /RegisterCommand\(registry, "camera\.screenshot", "camera", true/);
  assert.match(source, /RegisterCommand\(registry, "camera\.create", "camera", true/);
  assert.match(source, /RegisterCommand\(registry, "camera\.target", "camera", true/);
  assert.match(source, /RegisterCommand\(registry, "camera\.lens", "camera", true/);
  assert.match(source, /RegisterCommand\(registry, "camera\.multiview", "camera", true/);
  assert.match(source, /RegisterCommand\(registry, "scene\.active", "scene", false/);
  assert.match(source, /RegisterCommand\(registry, "scene\.build-settings", "scene", false/);
  assert.match(source, /RegisterCommand\(registry, "scene\.screenshot", "scene", true/);
  assert.match(source, /RegisterCommand\(registry, "scene\.create", "scene", true/);
  assert.match(source, /RegisterCommand\(registry, "scene\.load", "scene", true/);
  assert.match(source, /RegisterCommand\(registry, "scene\.save", "scene", true/);
  assert.match(source, /RegisterCommand\(registry, "screenshot\.capture", "scene", true/);
  assert.match(source, /RegisterCommand\(registry, "prefab\.info", "prefab", false/);
  assert.match(source, /RegisterCommand\(registry, "prefab\.create", "prefab", true/);
  assert.match(source, /RegisterCommand\(registry, "prefab\.modify", "prefab", false/);
  assert.match(source, /RegisterCommand\(registry, "prefab\.stage-open", "prefab", true/);
  assert.match(source, /RegisterCommand\(registry, "prefab\.stage-save", "prefab", true/);
  assert.match(source, /RegisterCommand\(registry, "prefab\.stage-close", "prefab", true/);
  assert.match(source, /RegisterCommand\(registry, "prefab\.hierarchy", "prefab", false/);
  assert.match(source, /RegisterCommand\(registry, "ui\.canvas\.create", "ui", true/);
  assert.match(source, /RegisterCommand\(registry, "ui\.text\.create", "ui", true/);
  assert.match(source, /RegisterCommand\(registry, "ui\.button\.create", "ui", true/);
  assert.match(source, /RegisterCommand\(registry, "ui\.image\.create", "ui", true/);
  assert.match(source, /RegisterCommand\(registry, "ui\.document\.info", "ui", false/);
  assert.match(source, /RegisterCommand\(registry, "ui\.document\.create", "ui", true/);
  assert.match(source, /RegisterCommand\(registry, "ui\.hierarchy", "ui", false/);
  assert.match(source, /RegisterCommand\(registry, "material\.info", "material", false/);
  assert.match(source, /RegisterCommand\(registry, "material\.create", "material", true/);
  assert.match(source, /RegisterCommand\(registry, "material\.assign", "material", true/);
  assert.match(source, /RegisterCommand\(registry, "material\.set", "material", true/);
  assert.match(source, /RegisterCommand\(registry, "graphics\.quality", "graphics", true/);
  assert.match(source, /RegisterCommand\(registry, "graphics\.settings", "graphics", true/);
  assert.match(source, /RegisterCommand\(registry, "graphics\.screenshot", "graphics", true/);
  assert.match(source, /RegisterCommand\(registry, "graphics\.bake-start", "graphics", true/);
  assert.match(source, /RegisterCommand\(registry, "graphics\.bake-cancel", "graphics", true/);
  assert.match(source, /RegisterCommand\(registry, "graphics\.bake-status", "graphics", false/);
  assert.match(source, /RegisterCommand\(registry, "graphics\.bake-clear", "graphics", true/);
  assert.match(source, /RegisterCommand\(registry, "graphics\.skybox-get", "graphics", false/);
  assert.match(source, /RegisterCommand\(registry, "graphics\.pipeline-settings", "graphics", false/);
  assert.match(source, /RegisterCommand\(registry, "graphics\.pipeline-info", "graphics", false/);
  assert.match(source, /RegisterCommand\(registry, "physics\.settings", "physics", true/);
  assert.match(source, /RegisterCommand\(registry, "physics\.collision-matrix", "physics", true/);
  assert.match(source, /RegisterCommand\(registry, "physics\.material", "physics", true/);
  assert.match(source, /RegisterCommand\(registry, "physics\.joint", "physics", true/);
  assert.match(source, /RegisterCommand\(registry, "physics\.force", "physics", true/);
  assert.match(source, /RegisterCommand\(registry, "physics\.overlap", "physics", false/);
  assert.match(source, /RegisterCommand\(registry, "physics\.rigidbody", "physics", true/);
  assert.match(source, /RegisterCommand\(registry, "physics\.raycast", "physics", false/);
  assert.match(source, /RegisterCommand\(registry, "physics\.simulation", "physics", true/);
  assert.match(source, /RegisterCommand\(registry, "profiler\.start", "profiler", true/);
  assert.match(source, /RegisterCommand\(registry, "profiler\.stop", "profiler", true/);
  assert.match(source, /RegisterCommand\(registry, "profiler\.pause", "profiler", true/);
  assert.match(source, /RegisterCommand\(registry, "profiler\.counter", "profiler", false/);
  assert.match(source, /RegisterCommand\(registry, "profiler\.memory", "profiler", false/);
  assert.match(source, /RegisterCommand\(registry, "profiler\.frame", "profiler", false/);
  assert.match(source, /RegisterCommand\(registry, "profiler\.capture", "profiler", true/);
  assert.match(source, /RegisterCommand\(registry, "profiler\.export", "profiler", true/);
  assert.match(source, /RegisterCommand\(registry, "profiler\.status", "profiler", false/);
  assert.doesNotMatch(source, /ProfilerFrozenDecision|ProfilerCoverageScopeJson/);
  assert.match(source, /private const string ProfilerHandlerFingerprint = "profiler-driver-session-v2"/);
  assert.match(source, /FindType\("UnityEditorInternal\.ProfilerDriver"\)/);
  assert.match(source, /private static string ProfilerStart\(BridgeRequest request\)[\s\S]*?SetProfilerSessionEnabled\(true\);[\s\S]*?private static string ProfilerStop\(BridgeRequest request\)/);
  assert.match(source, /private static string ProfilerPause\(BridgeRequest request\)[\s\S]*?SetProfilerSessionEnabled\(!paused\);[\s\S]*?private static string ProfilerStatus\(BridgeRequest request\)/);
  assert.doesNotMatch(source, /private static string ProfilerStart\(BridgeRequest request\)[\s\S]*?UnityEngine\.Profiling\.Profiler\.enabled = true;[\s\S]*?private static string ProfilerStop\(BridgeRequest request\)/);
  // ProfilerStart 仅在传入 logFile 时条件性启用 enableBinaryLog，对齐 CoplayDev profiler_start log_file
  assert.match(source, /IsNullOrEmpty\(logFile\)[\s\S]*?Profiler\.enableBinaryLog = true/, "ProfilerStart should gate enableBinaryLog behind the logFile check");
  assert.doesNotMatch(source, /private static string ProfilerPause\(BridgeRequest request\)[\s\S]*?UnityEngine\.Profiling\.Profiler\.enabled = !paused;[\s\S]*?private static string ProfilerStatus\(BridgeRequest request\)/);
  assert.doesNotMatch(source, /private static string ProfilerPause\(BridgeRequest request\)[\s\S]*?UnityEngine\.Profiling\.Profiler\.enableBinaryLog = true;[\s\S]*?private static string ProfilerStatus\(BridgeRequest request\)/);
  assert.match(source, /\.Prop\("handlerFingerprint",/);
  assert.match(source, /RegisterInstalledExtensionCommands\(registry\)/);
  assert.doesNotMatch(source, /RegisterCommand\(registry, "(probuilder|cinemachine|vfx|urp|hdrp)\./);
  assert.match(source, /RegisterCommand\(registry, "animation\.clip", "animation", false/);
  assert.match(source, /RegisterCommand\(registry, "animation\.clip\.create", "animation", true/);
  assert.match(source, /RegisterCommand\(registry, "animation\.clip\.modify", "animation", true/);
  assert.match(source, /RegisterCommand\(registry, "animation\.state", "animation", false/);
  assert.match(source, /RegisterCommand\(registry, "animation\.play", "animation", true/);
  assert.match(source, /RegisterCommand\(registry, "animation\.stop", "animation", true/);
  assert.match(source, /RegisterCommand\(registry, "animator\.get-info", "animation", false/);
  assert.match(source, /RegisterCommand\(registry, "animator\.get-parameter", "animation", false/);
  assert.match(source, /RegisterCommand\(registry, "animator\.set-parameter", "animation", true/);
  assert.match(source, /RegisterCommand\(registry, "animator\.crossfade", "animation", true/);
  assert.match(source, /RegisterCommand\(registry, "animator\.set-speed", "animation", true/);
  assert.match(source, /RegisterCommand\(registry, "animator\.set-enabled", "animation", true/);
  assert.match(source, /RegisterCommand\(registry, "controller\.create", "animation", true/);
  assert.match(source, /RegisterCommand\(registry, "controller\.add-state", "animation", true/);
  assert.match(source, /RegisterCommand\(registry, "controller\.add-transition", "animation", true/);
  assert.match(source, /RegisterCommand\(registry, "controller\.add-parameter", "animation", true/);
  assert.match(source, /RegisterCommand\(registry, "controller\.get-info", "animation", false/);
  assert.match(source, /RegisterCommand\(registry, "controller\.assign", "animation", true/);
  assert.match(source, /RegisterCommand\(registry, "controller\.add-layer", "animation", true/);
  assert.match(source, /RegisterCommand\(registry, "controller\.remove-layer", "animation", true/);
  assert.match(source, /RegisterCommand\(registry, "controller\.set-layer-weight", "animation", true/);
  assert.match(source, /RegisterCommand\(registry, "controller\.create-blend-tree-1d", "animation", true/);
  assert.match(source, /RegisterCommand\(registry, "controller\.create-blend-tree-2d", "animation", true/);
  assert.match(source, /RegisterCommand\(registry, "controller\.add-blend-tree-child", "animation", true/);
  assert.match(vfxSource, /RegisterCommand\(registry, "vfx\.graph", "vfx", true/);
  assert.match(vfxSource, /RegisterCommand\(registry, "vfx\.effect", "vfx", true/);
  assert.match(vfxSource, /RegisterCommand\(registry, "vfx\.particle-create", "vfx", true/);
  assert.match(vfxSource, /RegisterCommand\(registry, "vfx\.particle-info", "vfx", false/);
  assert.match(vfxSource, /RegisterCommand\(registry, "vfx\.particle-play", "vfx", true/);
  assert.match(vfxSource, /RegisterCommand\(registry, "vfx\.line-create", "vfx", true/);
  assert.match(vfxSource, /RegisterCommand\(registry, "vfx\.line-positions", "vfx", true/);
  assert.match(vfxSource, /RegisterCommand\(registry, "vfx\.trail", "vfx", true/);
  assert.match(source, /RegisterCommand\(registry, "shader\.info", "shader", false/);
  assert.match(source, /RegisterCommand\(registry, "shader\.read", "shader", false/);
  assert.match(source, /RegisterCommand\(registry, "shader\.create", "shader", true/);
  assert.match(source, /RegisterCommand\(registry, "shader\.update", "shader", true/);
  assert.match(source, /RegisterCommand\(registry, "texture\.info", "texture", false/);
  assert.match(source, /RegisterCommand\(registry, "texture\.create", "texture", true/);
  assert.match(source, /RegisterCommand\(registry, "texture\.sprite", "texture", true/);
  assert.match(source, /RegisterCommand\(registry, "texture\.import-settings", "texture", true/);
  assert.match(source, /RegisterCommand\(registry, "scene\.hierarchy", "scene", false/);
  assert.match(source, /RegisterCommand\(registry, "go\.find", "go", false/);
  assert.match(source, /RegisterCommand\(registry, "go\.inspect", "go", false/);
  assert.match(source, /RegisterCommand\(registry, "go\.create", "go", true/);
  assert.match(source, /RegisterCommand\(registry, "go\.modify", "go", true/);
  assert.match(source, /RegisterCommand\(registry, "go\.duplicate", "go", true/);
  assert.match(source, /RegisterCommand\(registry, "go\.move-relative", "go", true/);
  assert.match(source, /RegisterCommand\(registry, "go\.look-at", "go", true/);
  assert.match(source, /RegisterCommand\(registry, "go\.delete", "go", false/);
  assert.match(source, /RegisterCommand\(registry, "component\.add", "component", true/);
  assert.match(source, /RegisterCommand\(registry, "component\.remove", "component", false/);
  assert.match(source, /RegisterCommand\(registry, "component\.set", "component", true/);
  assert.match(source, /RegisterCommand\(registry, "asset\.search", "asset", false/);
  assert.match(source, /RegisterCommand\(registry, "asset\.info", "asset", false/);
  assert.match(source, /RegisterCommand\(registry, "asset\.create", "asset", false/);
  assert.match(source, /RegisterCommand\(registry, "asset\.import", "asset", false/);
  assert.match(source, /RegisterCommand\(registry, "asset\.modify", "asset", false/);
  assert.match(source, /RegisterCommand\(registry, "asset\.delete", "asset", false/);
  assert.match(source, /RegisterCommand\(registry, "asset\.duplicate", "asset", false/);
  assert.match(source, /RegisterCommand\(registry, "asset\.move", "asset", false/);
  assert.match(source, /RegisterCommand\(registry, "asset\.rename", "asset", false/);
  assert.match(source, /RegisterCommand\(registry, "asset\.refresh", "asset", false/);
  assert.match(source, /RegisterCommand\(registry, "diagnose\.preflight", "diagnose", false/);
  assert.match(source, /RegisterCommand\(registry, "editor\.state", "editor", false/);
  assert.match(source, /RegisterCommand\(registry, "editor\.play", "editor", false/);
  assert.match(source, /RegisterCommand\(registry, "editor\.pause", "editor", false/);
  assert.match(source, /RegisterCommand\(registry, "editor\.stop", "editor", false/);
  assert.match(source, /RegisterCommand\(registry, "editor\.menu", "editor", false/);
  assert.match(source, /RegisterCommand\(registry, "editor\.compile", "editor", false, EditorCompile\)/);
  assert.match(source, /CompilationPipeline\.RequestScriptCompilation\(\)/);
  assert.match(source, /var allowedDuringCompileOrImport = request\.Command == "editor\.compile";/);
  assert.match(source, /if \(EditorApplication\.isCompiling && !allowedDuringCompileOrImport\)/);
  assert.match(source, /if \(cachedIsCompiling && !allowedDuringCompileOrImport\)/);
  assert.match(source, /skippedReason = alreadyBusy \? \(wasCompiling \? "already-compiling" : "already-importing"\) : null/);
  assert.match(source, /RegisterCommand\(registry, "editor\.console", "editor", false/);
  assert.match(source, /RegisterCommand\(registry, "code\.exec", "code", true/);
  assert.match(source, /RegisterCommand\(registry, "project-tool\.list", "project-tool", false/);
  assert.match(source, /RegisterCommand\(registry, "project-tool\.run", "project-tool", true/);
  assert.match(bridgeSection, /`camera\.list`, `camera\.info`, `camera\.screenshot`, `scene\.active`, `scene\.hierarchy`, `scene\.build-settings`, `scene\.screenshot`, `scene\.create`, `scene\.load`, `scene\.save`, `go\.find`, `go\.inspect`, `go\.create`, `go\.modify`, `go\.duplicate`, `go\.move-relative`, `go\.look-at`, `go\.delete`, `component\.add`, `component\.remove`, `component\.set`/);
  assert.match(bridgeSection, /`code\.exec`\./);
  assert.doesNotMatch(bridgeSection, /live query\/edit\/screenshot/);
  assert.doesNotMatch(bridgeSection, /not routed by this package yet/);
  assert.doesNotMatch(bridgeSection, /E_BRIDGE_COMMAND_UNSUPPORTED/);

});

test("Unity Bridge animation and vfx commands expose assets, target resolver diagnostics and package checks", async () => {
  const source = await readUnityBridgeSources();
  const vfxSource = await readExtensionSources("vfx-graph");
  const readme = await readFile(readmeUrl, "utf8");

  assert.match(source, /RequireAnimationClipPath/);
  assert.match(source, /AnimationUtility\.GetCurveBindings/);
  assert.match(source, /AnimatorJson/);
  assert.match(source, /ResolveUniqueTarget/);
  assert.match(source, /E_ANIMATION_CLIP_PATH_REQUIRED/);
  assert.match(source, /E_ANIMATION_CLIP_PATH_INVALID/);
  assert.match(source, /E_ANIMATION_CLIP_NOT_FOUND/);
  assert.match(source, /E_ANIMATION_STATE_REQUIRED/);
  assert.match(source, /E_ANIMATION_NO_CONTROLLER/);
  assert.match(source, /E_ANIMATOR_NOT_FOUND/);
  assert.match(source, /E_ANIMATOR_PARAMETER_NAME_REQUIRED/);
  assert.match(source, /E_ANIMATOR_PARAMETER_NOT_FOUND/);
  assert.match(source, /E_ANIMATOR_NO_CONTROLLER/);
  assert.match(source, /E_CONTROLLER_NOT_FOUND/);
  assert.match(source, /E_CONTROLLER_EXISTS/);
  assert.match(source, /E_CONTROLLER_PATH_REQUIRED/);
  assert.match(source, /E_STATE_NOT_FOUND/);
  assert.match(source, /E_STATE_EXISTS/);
  assert.match(source, /E_PARAMETER_EXISTS/);
  assert.match(source, /E_LAYER_NOT_FOUND/);
  assert.match(source, /E_LAYER_BASE_IMMUTABLE/);
  assert.match(source, /E_NOT_A_BLEND_TREE/);
  assert.match(vfxSource, /FindType\("UnityEngine\.VFX\.VisualEffectAsset"\)/);
  assert.match(vfxSource, /FindType\("UnityEngine\.VFX\.VisualEffect"\)/);
  assert.match(vfxSource, /E_VFX_PACKAGE_MISSING/);
  assert.match(vfxSource, /CreateVfxGraphAsset/);
  assert.match(vfxSource, /FindType\("UnityEditor\.VisualEffectAssetEditorUtility"\)/);
  assert.match(vfxSource, /GetMethod\("CreateNewAsset"/);
  assert.match(vfxSource, /WriteMinimalVfxGraphYaml/);
  assert.match(vfxSource, /VisualEffectResource:\\n/);
  assert.doesNotMatch(vfxSource, /SetAssetPath/);
  assert.doesNotMatch(vfxSource, /graphProperty\.SetValue/);
  assert.match(vfxSource, /AssetDatabase\.LoadAssetAtPath\(path, assetType\)/);
  assert.match(source, /E_TARGET_AMBIGUOUS/);
  assert.match(source, /rawPath\.Replace\('\\\\', '\/'\)\.Trim\(\)/);
  assert.match(source, /Replace\("\\\\t", "\\t"\)/);
  assert.match(readme, /`tools run animation\.clip --input '\{"path":"<path>"\}'` reads `Assets\/\*\.anim` AnimationClip settings, events, and bounded curve summaries/);
  assert.match(readme, /`tools run animation\.clip\.create --input '\{"path":"<path>","length":1\}' --yes` creates or overwrites persistent `Assets\/\*\.anim` assets/);
  assert.match(readme, /`tools run animation\.clip\.modify --input '\{"path":"<path>","loopTime":true\}' --yes` updates persistent clip-level settings/);
  assert.match(readme, /`tools run vfx\.graph --input/);
  assert.match(readme, /Windows-style `Assets\\VFX\\Explosion\.vfx` paths are normalized to Unity asset paths/);
  assert.match(readme, /`E_VFX_PACKAGE_MISSING`/);
  assert.match(readme, /`E_VFX_GRAPH_NOT_FOUND`/);
});

async function readUnityBridgeSources(): Promise<string> {
  const files = await listCSharpFiles(editorUrl);
  return (await Promise.all(files.map(async (url) => readFile(url, "utf8")))).join("\n");
}

async function readExtensionSources(extensionId: string): Promise<string> {
  const files = await listCSharpFiles(new URL(`${extensionId}/unitypkg/Editor/`, extensionsUrl));
  return (await Promise.all(files.map(async (url) => readFile(url, "utf8")))).join("\n");
}

async function listCSharpFiles(directory: URL): Promise<URL[]> {
  const entries = await readdir(directory, { withFileTypes: true });
  const urls = await Promise.all(entries.map(async (entry) => {
    const child = new URL(`${entry.name}${entry.isDirectory() ? "/" : ""}`, directory);
    if (entry.isDirectory()) {
      return listCSharpFiles(child);
    }
    return entry.isFile() && entry.name.endsWith(".cs") ? [child] : [];
  }));
  return urls.flat().sort((left, right) => left.pathname.localeCompare(right.pathname));
}

function stripCSharpTrivia(source: string): string {
  let output = "";
  let index = 0;

  while (index < source.length) {
    const current = source[index];
    const next = source[index + 1];
    const following = source[index + 2];

    if (current === "/" && next === "/") {
      output += "  ";
      index += 2;
      while (index < source.length && source[index] !== "\n") {
        output += " ";
        index += 1;
      }
      continue;
    }

    if (current === "/" && next === "*") {
      output += "  ";
      index += 2;
      while (index < source.length) {
        if (source[index] === "*" && source[index + 1] === "/") {
          output += "  ";
          index += 2;
          break;
        }
        output += source[index] === "\n" ? "\n" : " ";
        index += 1;
      }
      continue;
    }

    const isInterpolatedVerbatimString = current === "$" && next === "@" && following === "\"";
    const isVerbatimInterpolatedString = current === "@" && next === "$" && following === "\"";
    const isVerbatimString = current === "@" && next === "\"";
    if (isInterpolatedVerbatimString || isVerbatimInterpolatedString || isVerbatimString) {
      const prefixLength = isVerbatimString ? 2 : 3;
      output += " ".repeat(prefixLength);
      index += prefixLength;
      while (index < source.length) {
        if (source[index] === "\"" && source[index + 1] === "\"") {
          output += "  ";
          index += 2;
          continue;
        }
        const done = source[index] === "\"";
        output += source[index] === "\n" ? "\n" : " ";
        index += 1;
        if (done) {
          break;
        }
      }
      continue;
    }

    const isInterpolatedString = current === "$" && next === "\"";
    const isRegularString = current === "\"";
    const isCharacterLiteral = current === "'";
    if (isInterpolatedString || isRegularString || isCharacterLiteral) {
      const delimiter = isCharacterLiteral ? "'" : "\"";
      const prefixLength = isInterpolatedString ? 2 : 1;
      output += " ".repeat(prefixLength);
      index += prefixLength;
      while (index < source.length) {
        if (source[index] === "\\") {
          output += "  ";
          index += 2;
          continue;
        }
        const done = source[index] === delimiter;
        output += source[index] === "\n" ? "\n" : " ";
        index += 1;
        if (done) {
          break;
        }
      }
      continue;
    }

    output += current;
    index += 1;
  }

  return output;
}

function duplicateCSharpMethodSignatures(source: string): string[] {
  const strippedSource = stripCSharpTrivia(source);
  const seen = new Map<string, number>();
  const duplicates: string[] = [];
  const pendingTypes: string[] = [];
  const typeStack: Array<{ name: string; depth: number }> = [];
  let braceDepth = 0;

  for (const [index, line] of strippedSource.split("\n").entries()) {
    while (typeStack.length > 0 && braceDepth < typeStack[typeStack.length - 1].depth) {
      typeStack.pop();
    }

    const typeMatch = line.match(/^\s*(?:(?:public|private|internal|protected)\s+)?(?:(?:sealed|static|partial|abstract)\s+)*class\s+(\w+)/);
    if (typeMatch) {
      pendingTypes.push(typeMatch[1]);
    }

    const methodMatch = line.match(/^\s*(?:public|private|internal|protected)\s+(?:static\s+)?[\w.<>,\[\]?]+\s+(\w+)(?:<[^>]+>)?\((.*)\)\s*(?:where\b.*)?$/);
    if (methodMatch) {
      const owner = typeStack.length > 0 ? typeStack[typeStack.length - 1].name : "<global>";
      const signature = `${owner}.${methodMatch[1]}(${normalizeCSharpParameterTypes(methodMatch[2]).join(",")})`;
      const firstLine = seen.get(signature);
      if (firstLine !== undefined) {
        duplicates.push(`${signature} first declared at line ${firstLine}, duplicated at line ${index + 1}`);
      } else {
        seen.set(signature, index + 1);
      }
    }

    for (const character of line) {
      if (character === "{") {
        braceDepth += 1;
        const typeName = pendingTypes.shift();
        if (typeName !== undefined) {
          typeStack.push({ name: typeName, depth: braceDepth });
        }
      } else if (character === "}") {
        braceDepth -= 1;
        while (typeStack.length > 0 && braceDepth < typeStack[typeStack.length - 1].depth) {
          typeStack.pop();
        }
      }
    }
  }

  return duplicates;
}

function normalizeCSharpParameterTypes(parameters: string): string[] {
  const trimmedParameters = parameters.trim();
  if (trimmedParameters.length === 0) {
    return [];
  }

  return splitTopLevelCommas(trimmedParameters).map((parameter) => {
    const withoutDefault = parameter.split("=")[0].trim();
    const tokens = withoutDefault.split(/\s+/);
    return tokens.slice(0, -1).join(" ");
  });
}

function splitTopLevelCommas(value: string): string[] {
  const parts: string[] = [];
  let current = "";
  let angleDepth = 0;

  for (const character of value) {
    if (character === "<") {
      angleDepth += 1;
    } else if (character === ">") {
      angleDepth -= 1;
    }

    if (character === "," && angleDepth === 0) {
      parts.push(current.trim());
      current = "";
    } else {
      current += character;
    }
  }

  parts.push(current.trim());
  return parts;
}

test("Unity Bridge camera screenshot captures render texture and returns artifact metadata", async () => {
  const source = await readUnityBridgeSources();

  assert.match(source, /Camera\.allCameras/);
  assert.match(source, /SceneView\.lastActiveSceneView/);
  assert.match(source, /new RenderTexture\(width, height, 24, RenderTextureFormat\.ARGB32\)/);
  assert.match(source, /texture\.ReadPixels/);
  assert.match(source, /texture\.EncodeToPNG\(\)/);
  assert.match(source, /texture\.EncodeToJPG\(quality\)/);
  assert.match(source, /File\.WriteAllBytes\(outputPath, bytes\)/);
  assert.match(source, /\.Prop\("screenshotPath",/);
  assert.match(source, /\.Prop\("artifactPath",/);
  assert.match(source, /\.Prop\("logPath",/);
});

test("Unity Bridge prefab operations expose structured diagnostics", async () => {
  const source = await readUnityBridgeSources();
  const readme = await readFile(readmeUrl, "utf8");

  assert.match(source, /PrefabUtility\.SaveAsPrefabAssetAndConnect/);
  assert.match(source, /PrefabUtility\.LoadPrefabContents/);
  assert.match(source, /PrefabStageUtility/);
  assert.match(source, /E_PREFAB_PATH_REQUIRED/);
  assert.match(source, /E_PREFAB_NOT_FOUND/);
  assert.match(source, /E_ASSET_NOT_PREFAB/);
  assert.match(source, /E_PREFAB_DIRTY_CONFLICT/);
  assert.match(source, /E_PREFAB_STAGE_NOT_OPEN/);
  assert.match(source, /plannedEffects/);
  assert.match(source, /PrefabVariantJson/);
  assert.match(source, /DependencySummaryJson/);
  assert.match(source, /NestedPrefabReferencesJson/);
  assert.match(source, /\.PropRaw\("variant",/);
  assert.match(source, /\.PropRaw\("dependencies",/);
  assert.match(source, /\.PropRaw\("nestedPrefabs",/);
  assert.match(source, /PrefabAssetJson\(path, root, root\)/);
  assert.match(readme, /`tools run prefab\.info --input '\{"path":"<path>"\}'` returns Prefab metadata including GUID, root name, asset type, `variant` base\/source paths and GUIDs, `dependencies\.items`, and `nestedPrefabs\.items` rather than counts only/);
  assert.match(readme, /`tools run prefab\.hierarchy --input '\{"path":"<path>"\}'` pages hierarchy items with `pageInfo`, component summaries, the same Prefab variant\/dependency summary, `nestedPrefabs\.items` and `logPath`/);
  assert.match(readme, /`E_PREFAB_PATH_REQUIRED`, `E_PREFAB_PATH_INVALID`, `E_PREFAB_NOT_FOUND`, `E_ASSET_NOT_PREFAB`, `E_PREFAB_OUTPUT_EXISTS`, `E_PREFAB_MODIFY_NOOP`, `E_PREFAB_CHILD_EXISTS`, `E_PREFAB_CHILD_NOT_FOUND`, `E_PREFAB_DIRTY_CONFLICT`, `E_PREFAB_STAGE_NOT_OPEN`, `E_PREFAB_STAGE_MISMATCH` and `E_UNITY_BUSY`/);
});


test("Unity Bridge shader and texture operations expose diagnostics", async () => {
  const source = await readUnityBridgeSources();
  const readme = await readFile(readmeUrl, "utf8");

  assert.match(source, /ShaderUtil,UnityEditor/);
  assert.match(source, /ShaderMessagesMethod/);
  assert.match(source, /method\.Name != "GetShaderMessages"/);
  assert.match(source, /parameters\.Length == 1 && parameters\[0\]\.ParameterType == typeof\(Shader\)/);
  assert.doesNotMatch(source, /GetMethod\("GetShaderMessages", BindingFlags\.Static \| BindingFlags\.Public \| BindingFlags\.NonPublic\)/);
  assert.match(source, /File\.ReadAllText\(fullPath, Encoding\.UTF8\)/);
  assert.match(source, /File\.WriteAllText\(fullPath, content, new UTF8Encoding\(false\)\)/);
  assert.match(source, /TextureImporterType\.Sprite/);
  assert.match(source, /TextureImporterJson/);
  assert.match(source, /SaveAndReimport\(\)/);
  assert.match(source, /E_SHADER_NOT_FOUND/);
  assert.match(source, /E_TEXTURE_NOT_FOUND/);
  assert.match(source, /E_IMPORT_FAILED/);
  assert.match(source, /plannedEffects/);
  assert.match(readme, /`tools run shader\.info`, `tools run shader\.read`, `tools run shader\.create`, `tools run shader\.update`/);
  assert.match(readme, /`tools run texture\.info`, `tools run texture\.create`, `tools run texture\.sprite`, `tools run texture\.import-settings`/);
  assert.match(readme, /`E_SHADER_NOT_FOUND`/);
  assert.match(readme, /`E_TEXTURE_NOT_FOUND`/);
  assert.match(readme, /`E_IMPORT_FAILED`/);
});

test("Unity Bridge material commands expose shader properties, assignment and diagnostics", async () => {
  const source = await readUnityBridgeSources();
  const readme = await readFile(readmeUrl, "utf8");

  assert.match(source, /ShaderUtil\.GetPropertyCount/);
  assert.match(source, /ShaderUtil\.GetPropertyType/);
  assert.match(source, /MaterialPropertiesJson/);
  assert.match(source, /MaterialTextureReferencesJson/);
  assert.match(source, /Shader\.Find\(shaderName\)/);
  assert.match(source, /AssetDatabase\.CreateAsset\(material, path\)/);
  assert.match(source, /renderer\.sharedMaterials/);
  assert.match(source, /FindUiGraphic/);
  assert.match(source, /material\.SetColor/);
  assert.match(source, /material\.SetFloat/);
  assert.match(source, /material\.SetVector/);
  assert.match(source, /material\.SetTexture/);
  assert.match(source, /E_MATERIAL_PROPERTY_TYPE_MISMATCH/);
  assert.match(source, /E_TARGET_AMBIGUOUS/);
  assert.match(readme, /`tools run material\.info --input '\{"path":"<path>"\}'` returns shader, keywords, ShaderUtil property metadata, current values, texture references, dependencies and `logPath`/);
  assert.match(readme, /`tools run material\.assign --input '\{"target":"<go>","material":"<path>"\}'` assigns to a Renderer shared material slot or a UI Graphic `material` property/);
  assert.match(readme, /`E_MATERIAL_PATH_REQUIRED`, `E_MATERIAL_PATH_INVALID`, `E_MATERIAL_NOT_FOUND`, `E_SHADER_NOT_FOUND`, `E_MATERIAL_PROPERTY_NOT_FOUND`, `E_MATERIAL_PROPERTY_TYPE_MISMATCH`, `E_TARGET_AMBIGUOUS`, `E_MATERIAL_SLOT_INVALID`, `E_TEXTURE_NOT_FOUND` and `E_UNITY_BUSY`/);
});

test("Unity Bridge UI operations expose uGUI and UI Toolkit diagnostics", async () => {
  const source = await readUnityBridgeSources();
  const readme = await readFile(readmeUrl, "utf8");

  assert.match(source, /using UiCanvasScaler = UnityEngine\.UI\.CanvasScaler;/);
  assert.match(source, /using UiGraphicRaycaster = UnityEngine\.UI\.GraphicRaycaster;/);
  assert.match(source, /new GameObject\(name, typeof\(RectTransform\), typeof\(Canvas\), typeof\(UiCanvasScaler\), typeof\(UiGraphicRaycaster\)\)/);
  assert.match(source, /GetComponent<UiCanvasScaler>\(\)/);
  assert.match(source, /UiCanvasScaler\.ScaleMode\.ScaleWithScreenSize/);
  assert.match(source, /AddComponent<UiText>\(\)/);
  assert.equal(source.match(/Resources\.GetBuiltinResource<Font>\("LegacyRuntime\.ttf"\)/g)?.length, 2);
  assert.doesNotMatch(source, /Arial\.ttf/);
  assert.match(source, /AddComponent<UiButton>\(\)/);
  assert.match(source, /AddComponent<UiImage>\(\)/);
  assert.match(source, /HierarchyPath\(parent\.gameObject\)/);
  assert.doesNotMatch(source, /HierarchyPath\(parent\)\)/);
  assert.match(source, /UIDocument/);
  assert.match(source, /VisualTreeAsset/);
  assert.match(source, /StyleSheet/);
  assert.match(source, /UiDocumentAssetInfoJson\(path, visualTree, uss, styleSheet\)/);
  assert.match(source, /private static string UiDocumentReadDiagnosticsJson\(\)/);
  assert.doesNotMatch(source, /SuccessJson\("\{\\"document\":\{" \+ UiAssetFieldsJson\(path, visualTree\) \+ ",\\"styleSheet\\":"/);
  assert.match(source, /PanelSettings/);
  assert.match(source, /E_UI_PARENT_NOT_FOUND/);
  assert.match(source, /E_TARGET_AMBIGUOUS/);
  assert.match(source, /TargetAmbiguousErrorJson\("UI parent"/);
  assert.match(source, /TargetAmbiguousErrorJson\("UIDocument"/);
  assert.match(source, /TargetAmbiguousErrorJson\("UI hierarchy"/);
  assert.match(source, /TargetMatchesJson/);
  assert.match(source, /matchCount/);
  assert.match(source, /ResolveGameObjectTarget/);
  assert.match(source, /private sealed class TargetResolution/);
  assert.match(source, /E_UI_PANEL_SETTINGS_NOT_FOUND/);
  assert.match(source, /E_UI_UXML_NOT_FOUND/);
  assert.match(source, /E_UI_USS_NOT_FOUND/);
  assert.match(source, /CollectUiHierarchy/);
  assert.match(readme, /`tools run ui\.canvas\.create`, `tools run ui\.text\.create`, `tools run ui\.button\.create` and `tools run ui\.image\.create` create uGUI objects/);
  assert.match(readme, /`tools run ui\.document\.info` supports live UIDocument inspection and offline `.uxml`\/`.uss` file reads/);
  assert.match(readme, /`E_UI_PARENT_NOT_FOUND`, `E_UI_SPRITE_NOT_FOUND`, `E_UI_UXML_NOT_FOUND`, `E_UI_USS_NOT_FOUND`, `E_UI_PANEL_SETTINGS_NOT_FOUND`, `E_TARGET_NOT_FOUND`, `E_TARGET_AMBIGUOUS` and `E_UNITY_BUSY`/);
});

test("Unity Bridge C# string literals stay balanced", async () => {
  const source = await readUnityBridgeSources();
  for (const [index, line] of source.split(/\r?\n/).entries()) {
    let inString = false;
    let inVerbatim = false;
    let inChar = false;
    let escaped = false;
    for (let offset = 0; offset < line.length; offset += 1) {
      const char = line[offset];
      if (escaped) {
        escaped = false;
        continue;
      }
      if (!inString && !inChar && !inVerbatim && char === "/" && line[offset + 1] === "/") {
        break;
      }
      // C# verbatim strings: @"..." — backslash is literal, "" is an escaped quote
      if (inVerbatim) {
        if (char === '"') {
          if (line[offset + 1] === '"') {
            offset += 1; // skip escaped quote pair
          } else {
            inVerbatim = false;
            inString = false;
          }
        }
        continue;
      }
      if (char === "\\" && (inString || inChar)) {
        escaped = true;
        continue;
      }
      if (char === "'" && !inString) {
        inChar = !inChar;
        continue;
      }
      if (char === '"' && !inChar) {
        if (!inString && offset > 0 && line[offset - 1] === "@") {
          inVerbatim = true;
          inString = true;
        } else {
          inString = !inString;
        }
      }
    }
    assert.equal(inString, false, `unterminated string literal on UnityCliBridge.cs:${index + 1}`);
    assert.equal(inChar, false, `unterminated char literal on UnityCliBridge.cs:${index + 1}`);
  }
});

test("Unity Bridge graphics and physics commands expose settings, raycast and diagnostics", async () => {
  const source = await readUnityBridgeSources();
  const readme = await readFile(readmeUrl, "utf8");

  assert.match(source, /QualitySettings\.SetQualityLevel/);
  assert.match(source, /GraphicsSettings\.defaultRenderPipeline/);
  assert.match(source, /RenderPipelineAsset/);
  assert.match(source, /RegisterCommand\(registry, "graphics\.pipeline", "graphics", false/);
  assert.match(source, /RegisterCommand\(registry, "graphics\.volume", "graphics", false/);
  assert.match(source, /FindType\("UnityEngine\.Rendering\.Volume"\)/);
  assert.match(source, /Physics\.gravity/);
  assert.match(source, /Physics2D\.gravity/);
  assert.match(source, /Physics\.Raycast/);
  assert.match(source, /Physics2D\.Raycast/);
  assert.match(source, /Physics\.Simulate/);
  assert.match(source, /Physics2D\.Simulate/);
  assert.match(source, /E_RENDER_PIPELINE_NOT_FOUND/);
  assert.match(source, /E_QUALITY_LEVEL_INVALID/);
  assert.match(source, /E_GRAPHICS_SETTING_UNSUPPORTED/);
  assert.match(source, /E_PHYSICS_SETTING_UNSUPPORTED/);
  assert.match(source, /RaycastNextActionsJson/);
  assert.match(readme, /`tools run graphics\.quality` reads and updates `QualitySettings`/);
  assert.match(readme, /`tools run physics\.raycast` supports 3D and 2D ray queries/);
});

test("Unity Bridge HTTP timeout prevents queued side effects from stale requests", async () => {
  const source = await readUnityBridgeSources();
  const readme = await readFile(readmeUrl, "utf8");

  assert.match(source, /CancelBeforeStart/);
  assert.match(source, /TryStart/);
  assert.match(source, /MarkTimedOut/);
  assert.match(source, /bridge_execute_accepted/);
  assert.match(source, /bridge_request_body_read requestId=/);
  assert.match(source, /bridge_request_enqueued requestId=/);
  assert.match(source, /bridge_request_dequeued requestId=/);
  assert.match(source, /bridge_request_started requestId=/);
  assert.match(source, /bridge_request_finished requestId=/);
  assert.match(source, /request\.ContentLength64/);
  assert.match(source, /stream\.Read\(bytes, offset, bytes\.Length - offset\)/);
  assert.match(source, /bridge_request_timeout_cancelled/);
  assert.match(source, /bridge_request_timeout_started/);
  assert.match(source, /bridge_request_skipped_expired/);
  assert.match(source, /bridge_request_completed_after_http_timeout/);
  assert.match(source, /sideEffectSemantics/);
  assert.match(source, /started-before-http-timeout/);
  assert.match(source, /IsMutatingCommand/);
  assert.match(readme, /the bridge cancels that queued work before execution and returns `E_UNITY_BUSY` with no side effects applied/);
  assert.match(readme, /returns a success envelope with `status: "running"`, `sideEffectSemantics: "started-before-http-timeout"`, the original `requestId` and `logPath`/);
});

test("Unity Bridge screenshot validates path, format, overwrite and busy diagnostics", async () => {
  const source = await readUnityBridgeSources();
  const readme = await readFile(readmeUrl, "utf8");

  assert.match(source, /E_UNITY_BUSY/);
  assert.match(source, /E_CAMERA_NOT_FOUND/);
  assert.match(source, /E_TARGET_NOT_FOUND/);
  assert.match(source, /E_SCENE_VIEW_NOT_AVAILABLE/);
  assert.match(source, /E_OUTPUT_PATH_REQUIRED/);
  assert.match(source, /E_OUTPUT_EXISTS/);
  assert.match(source, /E_SCREENSHOT_FORMAT_UNSUPPORTED/);
  assert.match(source, /E_SCREENSHOT_SIZE_INVALID/);
  assert.match(source, /E_IO_ERROR/);
  assert.match(source, /string\.IsNullOrEmpty\(target\)/);
  assert.match(source, /File\.Exists\(outputPath\) && !overwrite/);
  assert.match(source, /CreateScreenshotDirectory\(outputPath\)/);
  assert.match(source, /WriteScreenshotBytes\(outputPath, bytes\)/);
  assert.match(source, /Path\.GetFullPath/);
  assert.match(readme, /`E_CAMERA_NOT_FOUND` for `tools run camera\.info` when a target Camera cannot be resolved/);
  assert.match(readme, /`E_TARGET_NOT_FOUND` for `tools run camera\.screenshot` when the target is empty or cannot be resolved/);
  assert.match(readme, /C# runtime behavior for empty camera targets, missing Cameras, screenshot IO failures, actual PNG\/JPG artifacts, Prefab Stage open\/save\/close, Prefab dirty conflicts, nested\/variant Prefab summaries, Material asset creation, Renderer\/UI Graphic assignment, Material property writes, GraphicsSettings\/QualitySettings persistence, render pipeline asset resolution, Physics\/Physics2D project setting persistence, raycast collision hits, explicit simulation steps, AnimationClip asset creation\/binding summaries, Animator state\/play\/stop behavior, VFX Graph asset creation, VisualEffect assignment, Shader import diagnostics, TextureImporter mutations, uGUI object creation, UIDocument\/PanelSettings linking, UI hierarchy summaries and Runtime Code live execution must still be proven by real Windows \+ Unity Editor validation/);
});

test("Unity Bridge camera creation validates inputs before scene mutation", async () => {
  const source = await readUnityBridgeSources();
  const createStart = source.indexOf("private static string CameraCreate");
  const createEnd = source.indexOf("private static string CameraTarget", createStart);
  const createBlock = source.slice(createStart, createEnd);
  const lensStart = source.indexOf("private static string CameraLens");
  const lensEnd = source.indexOf("private static int ClampScreenshotDimension", lensStart);
  const lensBlock = source.slice(lensStart, lensEnd);

  assert.ok(createStart > -1, "CameraCreate should exist");
  assert.ok(createEnd > createStart, "CameraCreate block should be bounded");
  assert.match(source, /private static CameraClearFlags ParseCameraClearFlags/);
  assert.match(source, /E_CAMERA_CLEAR_FLAGS_INVALID/);
  assert.match(createBlock, /ResolveUniqueTarget\(parent, "camera\.create --parent <go> not found\."\)/);
  assert.match(createBlock, /ParseCameraClearFlags\(request\.StringArg\("clearFlags"\)\)/);
  assert.match(createBlock, /var gameObject = new GameObject\(name\)/);
  assert.ok(createBlock.indexOf("ResolveUniqueTarget(parent") < createBlock.indexOf("new GameObject(name)"), "camera.create must validate parent before creating a GameObject");
  assert.ok(createBlock.indexOf("ParseCameraClearFlags") < createBlock.indexOf("new GameObject(name)"), "camera.create must validate clearFlags before creating a GameObject");
  assert.match(lensBlock, /ParseCameraClearFlags\(request\.StringArg\("clearFlags"\)\)/);
});

test("Unity Bridge editor commands expose state, play/pause/stop, menu execution and busy diagnostics", async () => {
  const source = await readUnityBridgeSources();
  const readme = await readFile(readmeUrl, "utf8");

  assert.match(source, /private static string EditorStateJson\(\)/);
  assert.match(source, /private static string EditorPlayJson\(BridgeRequest request\)/);
  assert.match(source, /private static string EditorPauseJson\(BridgeRequest request\)/);
  assert.match(source, /private static string EditorStopJson\(BridgeRequest request\)/);
  assert.match(source, /private static string EditorMenuJson\(BridgeRequest request\)/);
  assert.match(source, /private static string EditorConsole\(BridgeRequest request\)/);
  assert.match(source, /private static string BridgeBuildStamp\(\)/);
  assert.match(source, /private static string BridgeHealthJson\(\)/);
  assert.match(source, /private const string BridgeDiagnosticsFingerprint = "health-fingerprint-play-mode-guard-v3"/);
  assert.match(source, /private static string AssemblyMvid\(Assembly assembly\)/);
  assert.match(source, /private static string AssemblyLocation\(Assembly assembly\)/);
  assert.match(source, /private static string RuntimeDiagnosticsPath\(\)/);
  assert.match(source, /private static void WriteRuntimeLoadDiagnostics\(string phase\)/);
  assert.match(source, /private static string RuntimeDiagnosticsJson\(string phase\)/);
  assert.match(source, /UnityCliBridgeRuntime\.json/);
  assert.match(source, /bridge_runtime_loaded phase=/);
  assert.match(source, /BridgePackageAssetPath\(\)/);
  assert.match(source, /BridgePackageName\(\)/);
  assert.match(source, /BridgePackageResolvedPath\(\)/);
  assert.match(source, /PackageInfo\.FindForAssetPath\(BridgePackageAssetPath\(\)\)/);
  assert.match(source, /Packages\/com\.unity_cli\/Editor\/UnityCliBridge\.cs/);
  assert.match(source, /private static void StopStaleBridgeListeners\(\)/);
  assert.match(source, /stale_bridge_listener_stopped/);
  assert.match(source, /diagnosticsFingerprint/);
  assert.match(source, /moduleVersionId/);
  assert.match(source, /bridgeDiagnosticsFingerprint/);
  assert.match(source, /bridgeListenerId/);
  assert.match(source, /bridgeAssemblyMvid/);
  assert.match(source, /packageName/);
  assert.match(source, /packageAssetPath/);
  assert.match(source, /packageResolvedPath/);
  assert.match(source, /runtimeLoadFile/);
  assert.match(source, /buildStamp/);
  assert.match(source, /EditorApplication\.ExecuteMenuItem\(path\)/);
  assert.match(source, /EditorApplication\.isPlaying/);
  assert.match(source, /EditorApplication\.isPaused/);
  assert.match(source, /EditorApplication\.isPlayingOrWillChangePlaymode/);
  assert.match(source, /E_UNITY_BUSY/);
  assert.match(source, /E_PLAYMODE_TIMEOUT/);
  assert.match(source, /E_MENU_NOT_FOUND/);
  assert.match(source, /E_MENU_TIMEOUT/);
  assert.match(source, /E_SCHEMA_INVALID/);
  assert.match(source, /EnsureEditorNotBusy/);
  assert.match(source, /WaitForPlayModeState/);
  assert.match(source, /PrefabStageJson/);
  assert.match(readme, /`tools run editor\.state` returns playMode, pause, busy, activeScene and Prefab Stage status/);
  assert.match(readme, /`tools run editor\.play`, `tools run editor\.pause` and `tools run editor\.stop` control Editor PlayMode/);
  assert.match(readme, /`tools run editor\.menu --input '\{"path":"<path>"\}'` executes a Unity Editor menu item/);
  assert.match(readme, /`tools run console\.get --input '\{"count":<n>\}'` returns recent Unity Console entries/);
  assert.match(readme, /E_UNITY_BUSY/);
  assert.match(readme, /E_PLAYMODE_TIMEOUT/);
  assert.match(readme, /E_MENU_NOT_FOUND/);
  assert.match(readme, /E_MENU_TIMEOUT/);
});

test("Unity Bridge treats PlayMode switching as editor-command busy state", async () => {
  const source = await readUnityBridgeSources();
  const busyStart = source.indexOf("private static void EnsureEditorNotBusy");
  // WaitForPlayModeState has been replaced by StartDeferredWait; use the closing
  // brace of EnsureEditorNotBusy followed by the doc comment as boundary.
  const busyEnd = source.indexOf("Removed: WaitForPlayModeState", busyStart);
  const busyBlock = source.slice(busyStart, busyEnd);

  assert.ok(busyStart > -1);
  assert.ok(busyEnd > busyStart);
  assert.match(busyBlock, /EditorApplication\.isPlayingOrWillChangePlaymode/);
  assert.match(busyBlock, /E_UNITY_BUSY/);
});

test("Bridge Execute only blocks Play Mode transitions", async () => {
  // 回归保护：稳定 Play Mode 不应阻塞 CLI 工具，AI 需要能在游戏运行时继续通过 Bridge 操作 Unity。
  const source = await readUnityBridgeSources();
  const executeStart = source.indexOf("private static string Execute(BridgeRequest request)");
  const executeEnd = source.indexOf("RequiresDestructiveSecurityProof", executeStart);
  const executeBlock = source.slice(executeStart, executeEnd);

  assert.ok(executeStart > -1, "Could not find Execute method");
  assert.ok(executeEnd > executeStart, "Could not find end of Execute method");

  // guard 只应在 Play Mode 切换期拒绝命令，稳定运行态必须放行。
  assert.match(executeBlock, /!EditorApplication\.isPlaying && !allowedDuringPlayModeTransition/, "Execute must only block transition-time commands");
  assert.match(executeBlock, /execute-guard-v3/, "Execute transition error must include the v3 marker");
  assert.doesNotMatch(executeBlock, /editor-domain write commands are not available/, "Stable Play Mode must not reject editor-domain tools");

  // 切换期 allowlist 仍保留，用于安全的状态查询和运行态控制。
  assert.match(executeBlock, /request\.Command == "editor\.state"/, "editor.state must remain allowed during Play Mode transitions");
  assert.match(executeBlock, /request\.Command == "runtime-tool\.list"/, "runtime-tool.list must remain allowed during Play Mode transitions");
  assert.match(executeBlock, /request\.Command == "runtime-tool\.run"/, "runtime-tool.run must remain allowed during Play Mode transitions");
});

test("BridgeHttpServer Play Mode guard only blocks transitions", async () => {
  // 回归保护：HTTP 入队前 guard 不应在稳定 Play Mode 下提前拒绝工具调用。
  const source = await readUnityBridgeSources();
  const handleHttpCoreStart = source.indexOf("private static void HandleHttpCore");
  const handleHttpCoreEnd = source.indexOf("private sealed class BridgeWorkItem", handleHttpCoreStart);
  const httpBlock = source.slice(handleHttpCoreStart, handleHttpCoreEnd > handleHttpCoreStart ? handleHttpCoreEnd : source.length);

  assert.ok(handleHttpCoreStart > -1, "Could not find HandleHttpCore method");

  assert.match(httpBlock, /!cachedIsPlaying && !allowedDuringPlayModeTransition/, "HandleHttpCore must only block transition-time commands");
  assert.match(httpBlock, /http-guard-v3/, "HandleHttpCore transition error must include the v3 marker");
  assert.doesNotMatch(httpBlock, /editor-domain write commands are not available/, "Stable Play Mode must not be rejected before queueing");

  // HandleHttpCore 的切换期 allowlist 需要与 Execute 保持一致。
  assert.match(httpBlock, /request\.Command == "editor\.state"/, "HandleHttpCore must allow editor.state during Play Mode transitions");
  assert.match(httpBlock, /request\.Command == "editor\.console"/, "HandleHttpCore must allow editor.console during Play Mode transitions");
  assert.match(httpBlock, /request\.Command == "editor\.pause"/, "HandleHttpCore must allow editor.pause during Play Mode transitions");
  assert.match(httpBlock, /request\.Command == "editor\.stop"/, "HandleHttpCore must allow editor.stop during Play Mode transitions");
  assert.match(httpBlock, /request\.Command == "runtime-tool\.list"/, "HandleHttpCore must allow runtime-tool.list during Play Mode transitions");
  assert.match(httpBlock, /request\.Command == "runtime-tool\.run"/, "HandleHttpCore must allow runtime-tool.run during Play Mode transitions");
  assert.match(httpBlock, /request\.Command == "project-tool\.list"/, "HandleHttpCore must allow project-tool.list during Play Mode transitions");
  assert.match(httpBlock, /request\.Command == "diagnose\.preflight"/, "HandleHttpCore must allow diagnose.preflight during Play Mode transitions");
});

test("EnsureEditorNotBusy only blocks Play Mode transitions", async () => {
  // 回归保护：命令级 busy 检查不应在稳定 Play Mode 下拒绝工具。
  const source = await readUnityBridgeSources();
  const busyStart = source.indexOf("private static void EnsureEditorNotBusy");
  const busyEnd = source.indexOf("Removed: WaitForPlayModeState", busyStart);
  const busyBlock = source.slice(busyStart, busyEnd);

  assert.ok(busyStart > -1);
  assert.ok(busyEnd > busyStart);

  assert.match(busyBlock, /&& !EditorApplication\.isPlaying/, "EnsureEditorNotBusy must only block transition-time commands");
  assert.doesNotMatch(busyBlock, /this editor-domain command is not available/, "Stable Play Mode must not reject editor-domain commands");
});

test("Play Mode guard includes diagnostic version markers (http-guard-v3 / execute-guard-v3)", async () => {
  // 回归保护：版本标记用于确认当前运行的是稳定 Play Mode 放行 guard。
  const source = await readUnityBridgeSources();

  // HandleHttpCore guard
  const httpStart = source.indexOf("private static void HandleHttpCore");
  const httpEnd = source.indexOf("private static void StopStaleBridgeListeners", httpStart);
  const httpBlock = source.slice(httpStart, httpEnd > httpStart ? httpEnd : undefined);
  assert.ok(httpStart > -1, "Could not find HandleHttpCore method");
  assert.match(httpBlock, /http-guard-v3/, "HandleHttpCore Play Mode guard must include http-guard-v3 version marker");

  // Execute() guard
  const executeStart = source.indexOf("private static string Execute(BridgeRequest request)");
  const executeEnd = source.indexOf("RequiresDestructiveSecurityProof", executeStart);
  const executeBlock = source.slice(executeStart, executeEnd > executeStart ? executeEnd : undefined);
  assert.ok(executeStart > -1, "Could not find Execute method");
  assert.match(executeBlock, /execute-guard-v3/, "Execute() Play Mode guard must include execute-guard-v3 version marker");

  // Bridge diagnostics fingerprint must be updated to reflect the guard change
  assert.match(source, /play-mode-guard-v3/, "BridgeDiagnosticsFingerprint must include play-mode-guard-v3");
});

import assert from "node:assert/strict";
import { readdir, readFile } from "node:fs/promises";
import test from "node:test";

const editorUrl = new URL("../unitypkg/Editor/", import.meta.url);
const sourceUrl = new URL("UnityCliBridge.cs", editorUrl);
const readmeUrl = new URL("../README.md", import.meta.url);

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

test("Unity Bridge exposes documented camera, screenshot, prefab, UI, material, graphics, physics, profiler, probuilder, animation, vfx, shader, texture and editor commands", async () => {
  const source = await readUnityBridgeSources();
  const bridgeSource = await readFile(sourceUrl, "utf8");
  const readme = await readFile(readmeUrl, "utf8");
  const bridgeSection = readme.slice(readme.indexOf("## Unity Editor Bridge"), readme.indexOf("## Batch Automation"));

  assert.match(source, /HttpListener/);
  assert.doesNotMatch(bridgeSource, /switch \(request\.Command\)/);
  assert.match(source, /private static readonly BridgeCommandRegistry CommandRegistry = CreateCommandRegistry\(\)/);
  assert.match(source, /POST" \|\| context\.Request\.Url\.AbsolutePath != "\/bridge\/execute"/);
  assert.match(source, /CapabilityJson\("camera", CameraCommands\)/);
  assert.match(source, /CapabilityJson\("prefab", PrefabCommands\)/);
  assert.match(source, /CapabilityJson\("ui", UiCommands\)/);
  assert.match(source, /CapabilityJson\("material", MaterialCommands\)/);
  assert.match(source, /CapabilityJson\("graphics", GraphicsCommands\)/);
  assert.match(source, /CapabilityJson\("physics", PhysicsCommands\)/);
  assert.match(source, /CapabilityJson\("profiler", ProfilerCommands\)/);
  assert.match(source, /CapabilityJson\("probuilder", ProBuilderCommands\)/);
  assert.match(source, /CapabilityJson\("animation", AnimationCommands\)/);
  assert.match(source, /CapabilityJson\("vfx", VfxCommands\)/);
  assert.match(source, /CapabilityJson\("physics", PhysicsCommands\) \+ ","\s+\+ CapabilityJson\("profiler", ProfilerCommands\) \+ ","\s+\+ CapabilityJson\("probuilder", ProBuilderCommands\) \+ ","\s+\+ CapabilityJson\("animation", AnimationCommands\) \+ ","\s+\+ CapabilityJson\("vfx", VfxCommands\) \+ ","\s+\+ CapabilityJson\("shader", ShaderCommands\) \+ ","/);
  assert.match(source, /CapabilityJson\("shader", ShaderCommands\)/);
  assert.match(source, /CapabilityJson\("texture", TextureCommands\)/);
  assert.match(source, /CapabilityJson\("script", ScriptCommands\)/);
  assert.match(source, /CapabilityJson\("code", CodeCommands\)/);
  assert.match(source, /CapabilityJson\("project-tool", ProjectToolCommands\)/);
  assert.match(source, /AssemblyReloadEvents\.beforeAssemblyReload \+= OnBeforeAssemblyReload/);
  assert.match(source, /EditorApplication\.quitting \+= UnregisterAndStop/);
  assert.match(source, /RegisterInstance\("healthy"\)/);
  assert.match(source, /RegisterInstance\("stale"\)/);
  assert.match(source, /"instances"/);
  assert.match(source, /\\"domainReloading\\":\" \+ Bool\(domainReloading\)/);
  assert.doesNotMatch(source, /\\"domainReloading\\":\" \+ Bool\(EditorApplication\.isCompiling\)/);
  assert.match(source, /private static Type FindType\(string fullName\)/);
  assert.match(source, /private static string QuotePair\(string key, string value\)/);
  assert.doesNotMatch(source, /new\[\] \{ \(object\)/);
  assert.match(source, /private static readonly string\[\] CameraCommands = \{ "camera\.list", "camera\.info", "camera\.screenshot" \}/);
  assert.match(source, /private static readonly string\[\] SceneCommands = \{ "scene\.active", "scene\.hierarchy", "scene\.build-settings", "scene\.screenshot", "scene\.create", "scene\.load", "scene\.save" \}/);
  assert.match(source, /private static readonly string\[\] GameObjectCommands = \{ "go\.find", "go\.inspect", "go\.create", "go\.modify", "go\.delete", "component\.add", "component\.remove", "component\.set" \}/);
  assert.match(source, /private static readonly string\[\] PrefabCommands = \{ "prefab\.info", "prefab\.hierarchy", "prefab\.create", "prefab\.modify", "prefab\.stage-open", "prefab\.stage-save", "prefab\.stage-close" \}/);
  assert.match(source, /private static readonly string\[\] UiCommands = \{ "ui\.canvas\.create", "ui\.text\.create", "ui\.button\.create", "ui\.image\.create", "ui\.document\.info", "ui\.document\.create", "ui\.hierarchy" \}/);
  assert.match(source, /private static readonly string\[\] MaterialCommands = \{ "material\.info", "material\.create", "material\.assign", "material\.set" \}/);
  assert.match(source, /private static readonly string\[\] GraphicsCommands = \{ "graphics\.quality", "graphics\.settings", "graphics\.pipeline", "graphics\.volume", "graphics\.screenshot" \}/);
  assert.match(source, /private static readonly string\[\] PhysicsCommands = \{ "physics\.settings", "physics\.raycast", "physics\.simulation" \}/);
  assert.match(source, /private static readonly string\[\] ProfilerCommands = \{ "profiler\.start", "profiler\.stop", "profiler\.pause", "profiler\.counter", "profiler\.memory", "profiler\.frame", "profiler\.capture", "profiler\.export", "profiler\.status" \}/);
  assert.match(source, /private static readonly string\[\] ProBuilderCommands = \{ "probuilder\.shape", "probuilder\.create", "probuilder\.csg", "probuilder\.edit" \}/);
  assert.match(source, /private static readonly string\[\] AnimationCommands = \{ "animation\.clip", "animation\.state", "animation\.play", "animation\.stop" \}/);
  assert.match(source, /private static readonly string\[\] VfxCommands = \{ "vfx\.graph", "vfx\.effect" \}/);
  assert.match(source, /private static readonly string\[\] ShaderCommands = \{ "shader\.info", "shader\.read", "shader\.create", "shader\.update" \}/);
  assert.match(source, /private static readonly string\[\] TextureCommands = \{ "texture\.info", "texture\.create", "texture\.sprite", "texture\.import-settings" \}/);
  assert.match(source, /private static readonly string\[\] ScriptCommands = \{ "script\.create", "script\.read", "script\.edit", "script\.validate" \}/);
  assert.match(source, /private static readonly string\[\] CodeCommands = \{ "code\.exec" \}/);
  assert.match(source, /private static readonly string\[\] ProjectToolCommands = \{ "project-tool\.list", "project-tool\.run" \}/);
  assert.match(source, /RegisterCommand\(registry, "camera\.list", "camera", false/);
  assert.match(source, /RegisterCommand\(registry, "camera\.info", "camera", false/);
  assert.match(source, /RegisterCommand\(registry, "camera\.screenshot", "camera", true/);
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
  assert.match(source, /RegisterCommand\(registry, "physics\.settings", "physics", true/);
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
  assert.match(source, /private const string ProfilerFrozenDecision = "ALP-165-Q4"/);
  assert.match(source, /private const string ProfilerCoverageScopeJson = "\[\\"session\\",\\"counter\\",\\"memory\\",\\"frame-debugger\\",\\"export\\"\]"/);
  assert.match(source, /private const string ProfilerHandlerFingerprint = "alp340-profiler-driver-session-v2"/);
  assert.match(source, /FindType\("UnityEditorInternal\.ProfilerDriver"\)/);
  assert.match(source, /private static string ProfilerStart\(BridgeRequest request\)[\s\S]*?SetProfilerSessionEnabled\(true\);[\s\S]*?private static string ProfilerStop\(BridgeRequest request\)/);
  assert.match(source, /private static string ProfilerPause\(BridgeRequest request\)[\s\S]*?SetProfilerSessionEnabled\(!paused\);[\s\S]*?private static string ProfilerStatus\(BridgeRequest request\)/);
  assert.doesNotMatch(source, /private static string ProfilerStart\(BridgeRequest request\)[\s\S]*?UnityEngine\.Profiling\.Profiler\.enabled = true;[\s\S]*?private static string ProfilerStop\(BridgeRequest request\)/);
  assert.doesNotMatch(source, /private static string ProfilerStart\(BridgeRequest request\)[\s\S]*?UnityEngine\.Profiling\.Profiler\.enableBinaryLog = true;[\s\S]*?private static string ProfilerStop\(BridgeRequest request\)/);
  assert.doesNotMatch(source, /private static string ProfilerPause\(BridgeRequest request\)[\s\S]*?UnityEngine\.Profiling\.Profiler\.enabled = !paused;[\s\S]*?private static string ProfilerStatus\(BridgeRequest request\)/);
  assert.doesNotMatch(source, /private static string ProfilerPause\(BridgeRequest request\)[\s\S]*?UnityEngine\.Profiling\.Profiler\.enableBinaryLog = true;[\s\S]*?private static string ProfilerStatus\(BridgeRequest request\)/);
  assert.match(source, /\\"handlerFingerprint\\":/);
  assert.match(source, /\\"architectureSensitive\\":true/);
  assert.match(source, /\\"frozenDecision\\":/);
  assert.match(source, /\\"coverageScope\\":/);
  assert.match(source, /RegisterCommand\(registry, "probuilder\.shape", "probuilder", true/);
  assert.match(source, /RegisterCommand\(registry, "probuilder\.create", "probuilder", true/);
  assert.match(source, /RegisterCommand\(registry, "probuilder\.csg", "probuilder", true/);
  assert.match(source, /RegisterCommand\(registry, "probuilder\.edit", "probuilder", true/);
  assert.match(source, /RegisterCommand\(registry, "animation\.clip", "animation", true/);
  assert.match(source, /RegisterCommand\(registry, "animation\.state", "animation", false/);
  assert.match(source, /RegisterCommand\(registry, "animation\.play", "animation", true/);
  assert.match(source, /RegisterCommand\(registry, "animation\.stop", "animation", true/);
  assert.match(source, /RegisterCommand\(registry, "vfx\.graph", "vfx", true/);
  assert.match(source, /RegisterCommand\(registry, "vfx\.effect", "vfx", true/);
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
  assert.match(source, /RegisterCommand\(registry, "go\.delete", "go", true/);
  assert.match(source, /RegisterCommand\(registry, "component\.add", "component", true/);
  assert.match(source, /RegisterCommand\(registry, "component\.remove", "component", true/);
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
  assert.match(source, /RegisterCommand\(registry, "diagnose\.preflight", "diagnose", false/);
  assert.match(source, /RegisterCommand\(registry, "editor\.state", "editor", false/);
  assert.match(source, /RegisterCommand\(registry, "editor\.play", "editor", false/);
  assert.match(source, /RegisterCommand\(registry, "editor\.pause", "editor", false/);
  assert.match(source, /RegisterCommand\(registry, "editor\.stop", "editor", false/);
  assert.match(source, /RegisterCommand\(registry, "editor\.menu", "editor", false/);
  assert.match(source, /RegisterCommand\(registry, "editor\.console", "editor", false/);
  assert.match(source, /RegisterCommand\(registry, "script\.create", "script", true/);
  assert.match(source, /RegisterCommand\(registry, "script\.read", "script", false/);
  assert.match(source, /RegisterCommand\(registry, "script\.edit", "script", true/);
  assert.match(source, /RegisterCommand\(registry, "script\.validate", "script", false/);
  assert.match(source, /RegisterCommand\(registry, "code\.exec", "code", true/);
  assert.match(source, /RegisterCommand\(registry, "project-tool\.list", "project-tool", false/);
  assert.match(source, /RegisterCommand\(registry, "project-tool\.run", "project-tool", true/);
  assert.match(bridgeSection, /`camera\.list`, `camera\.info`, `camera\.screenshot`, `scene\.active`, `scene\.hierarchy`, `scene\.build-settings`, `scene\.screenshot`, `scene\.create`, `scene\.load`, `scene\.save`, `go\.find`, `go\.inspect`, `go\.create`, `go\.modify`, `go\.delete`, `component\.add`, `component\.remove`, `component\.set`/);
  assert.match(bridgeSection, /`script\.create`, `script\.read`, `script\.edit`, `script\.validate`, `code\.exec`\./);
  assert.doesNotMatch(bridgeSection, /live query\/edit\/screenshot/);
  assert.doesNotMatch(bridgeSection, /not routed by this package yet/);
  assert.doesNotMatch(bridgeSection, /E_BRIDGE_COMMAND_UNSUPPORTED/);

});

test("Unity Bridge animation and vfx commands expose assets, target resolver diagnostics and package checks", async () => {
  const source = await readUnityBridgeSources();
  const readme = await readFile(readmeUrl, "utf8");

  assert.match(source, /RequireAnimationClipPath/);
  assert.match(source, /AnimationUtility\.GetCurveBindings/);
  assert.match(source, /AnimatorJson/);
  assert.match(source, /ResolveUniqueTarget/);
  assert.match(source, /E_ANIMATION_CLIP_PATH_REQUIRED/);
  assert.match(source, /E_ANIMATION_CLIP_PATH_INVALID/);
  assert.match(source, /E_ANIMATION_CLIP_NOT_FOUND/);
  assert.match(source, /E_ANIMATION_STATE_REQUIRED/);
  assert.match(source, /FindType\("UnityEngine\.VFX\.VisualEffectAsset"\)/);
  assert.match(source, /FindType\("UnityEngine\.VFX\.VisualEffect"\)/);
  assert.match(source, /E_VFX_PACKAGE_MISSING/);
  assert.match(source, /CreateVfxGraphAsset/);
  assert.match(source, /FindType\("UnityEditor\.VisualEffectAssetEditorUtility"\)/);
  assert.match(source, /GetMethod\("CreateNewAsset"/);
  assert.match(source, /WriteMinimalVfxGraphYaml/);
  assert.match(source, /VisualEffectResource:\\n/);
  assert.doesNotMatch(source, /SetAssetPath/);
  assert.doesNotMatch(source, /graphProperty\.SetValue/);
  assert.match(source, /AssetDatabase\.LoadAssetAtPath\(path, assetType\)/);
  assert.match(source, /E_TARGET_AMBIGUOUS/);
  assert.match(source, /rawPath\.Replace\('\\\\', '\/'\)\.Trim\(\)/);
  assert.match(source, /Replace\("\\t", "\\\\t"\)/);
  assert.match(readme, /`animation clip <path>` reads or creates `Assets\/\*\.anim` AnimationClip assets/);
  assert.match(readme, /`vfx graph <path>` reads or creates `Assets\/\*\.vfx` Visual Effect Graph assets/);
  assert.match(readme, /Windows-style `Assets\\VFX\\Explosion\.vfx` paths are normalized to Unity asset paths/);
  assert.match(readme, /`E_VFX_PACKAGE_MISSING`/);
  assert.match(readme, /`E_VFX_GRAPH_NOT_FOUND`/);
});

async function readUnityBridgeSources(): Promise<string> {
  const files = await listCSharpFiles(editorUrl);
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
  assert.match(source, /"\\\"screenshotPath\\\":"/);
  assert.match(source, /"\\\"artifactPath\\\":"/);
  assert.match(source, /"\\\"logPath\\\":"/);
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
  assert.match(source, /"\\\"variant\\\":"/);
  assert.match(source, /"\\\"dependencies\\\":"/);
  assert.match(source, /"\\\"nestedPrefabs\\\":"/);
  assert.match(source, /PrefabAssetJson\(path, root, root\)/);
  assert.match(readme, /`prefab info <path>` returns Prefab metadata including GUID, root name, asset type, `variant` base\/source paths and GUIDs, `dependencies\.items`, and `nestedPrefabs\.items` rather than counts only/);
  assert.match(readme, /`prefab hierarchy <path>` pages hierarchy items with `pageInfo`, component summaries, the same Prefab variant\/dependency summary, `nestedPrefabs\.items` and `logPath`/);
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
  assert.match(readme, /`shader info\/read\/create\/update`/);
  assert.match(readme, /`texture info\/create\/sprite\/import-settings`/);
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
  assert.match(readme, /`material info <path>` returns shader, keywords, ShaderUtil property metadata, current values, texture references, dependencies and `logPath`/);
  assert.match(readme, /`material assign --target <go> --material <path>` assigns to a Renderer shared material slot or a UI Graphic `material` property/);
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
  assert.match(readme, /`ui canvas\/create`, `ui text\/create`, `ui button\/create` and `ui image\/create` create uGUI objects/);
  assert.match(readme, /`ui document\/info` supports live UIDocument inspection and offline `.uxml`\/`.uss` file reads/);
  assert.match(readme, /`E_UI_PARENT_NOT_FOUND`, `E_UI_SPRITE_NOT_FOUND`, `E_UI_UXML_NOT_FOUND`, `E_UI_USS_NOT_FOUND`, `E_UI_PANEL_SETTINGS_NOT_FOUND`, `E_TARGET_NOT_FOUND`, `E_TARGET_AMBIGUOUS` and `E_UNITY_BUSY`/);
});

test("Unity Bridge C# string literals stay balanced", async () => {
  const source = await readUnityBridgeSources();
  for (const [index, line] of source.split(/\r?\n/).entries()) {
    let inString = false;
    let inChar = false;
    let escaped = false;
    for (let offset = 0; offset < line.length; offset += 1) {
      const char = line[offset];
      if (escaped) {
        escaped = false;
        continue;
      }
      if (!inString && !inChar && char === "/" && line[offset + 1] === "/") {
        break;
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
        inString = !inString;
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
  assert.match(readme, /`graphics quality` reads and updates `QualitySettings`/);
  assert.match(readme, /`physics raycast` supports 3D and 2D ray queries/);
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
  assert.match(source, /E_IO_ERROR/);
  assert.match(source, /string\.IsNullOrEmpty\(target\)/);
  assert.match(source, /File\.Exists\(outputPath\) && !overwrite/);
  assert.match(source, /CreateScreenshotDirectory\(outputPath\)/);
  assert.match(source, /WriteScreenshotBytes\(outputPath, bytes\)/);
  assert.match(source, /Path\.GetFullPath/);
  assert.match(readme, /`E_CAMERA_NOT_FOUND` for `camera info` when a target Camera cannot be resolved/);
  assert.match(readme, /`E_TARGET_NOT_FOUND` for `camera screenshot` when the target is empty or cannot be resolved/);
  assert.match(readme, /C# runtime behavior for empty camera targets, missing Cameras, screenshot IO failures, actual PNG\/JPG artifacts, Prefab Stage open\/save\/close, Prefab dirty conflicts, nested\/variant Prefab summaries, Material asset creation, Renderer\/UI Graphic assignment, Material property writes, GraphicsSettings\/QualitySettings persistence, render pipeline asset resolution, Physics\/Physics2D project setting persistence, raycast collision hits, explicit simulation steps, AnimationClip asset creation\/binding summaries, Animator state\/play\/stop behavior, VFX Graph asset creation, VisualEffect assignment, Shader import diagnostics, TextureImporter mutations, uGUI object creation, UIDocument\/PanelSettings linking, UI hierarchy summaries and Runtime Code live execution must still be proven by real Windows \+ Unity Editor validation/);
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
  assert.match(source, /private const string BridgeDiagnosticsFingerprint = "alp276-health-fingerprint-20260615-execute-stage-v1"/);
  assert.match(source, /private static readonly string ListenerId = Guid\.NewGuid\(\)\.ToString\("N"\)/);
  assert.match(source, /private static readonly DateTime BridgeLoadedAtUtc = DateTime\.UtcNow/);
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
  assert.match(readme, /`editor state` returns playMode, pause, busy, activeScene and Prefab Stage status/);
  assert.match(readme, /`editor play`, `editor pause` and `editor stop` control Editor PlayMode/);
  assert.match(readme, /`editor menu <path>` executes a Unity Editor menu item/);
  assert.match(readme, /`editor console --count <n>` returns recent Unity Console entries/);
  assert.match(readme, /E_UNITY_BUSY/);
  assert.match(readme, /E_PLAYMODE_TIMEOUT/);
  assert.match(readme, /E_MENU_NOT_FOUND/);
  assert.match(readme, /E_MENU_TIMEOUT/);
});

test("Unity Bridge treats PlayMode switching as editor-command busy state", async () => {
  const source = await readUnityBridgeSources();
  const busyStart = source.indexOf("private static void EnsureEditorNotBusy");
  const busyEnd = source.indexOf("private static void WaitForPlayModeState", busyStart);
  const busyBlock = source.slice(busyStart, busyEnd);

  assert.ok(busyStart > -1);
  assert.ok(busyEnd > busyStart);
  assert.match(busyBlock, /EditorApplication\.isPlayingOrWillChangePlaymode/);
  assert.match(busyBlock, /E_UNITY_BUSY/);
});

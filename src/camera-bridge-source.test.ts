import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const sourceUrl = new URL("../unity-package/Editor/AlpsUnityBridge.cs", import.meta.url);
const readmeUrl = new URL("../README.md", import.meta.url);

test("Unity Bridge source keeps top-level C# methods structurally balanced", async () => {
  const source = await readFile(sourceUrl, "utf8");
  const strippedSource = stripCSharpTrivia(source);
  const topLevelSource = strippedSource.slice(0, strippedSource.indexOf("        private sealed class BridgeWorkItem"));
  let braceDepth = 0;

  for (const [index, line] of topLevelSource.split("\n").entries()) {
    assert.ok(braceDepth >= 0, `C# brace depth went negative before line ${index + 1}`);

    if (/^\s+private static (?!readonly\b).*\)\s*(?:where\b.*)?$/.test(line)) {
      assert.equal(braceDepth, 2, `Top-level C# method declaration is nested at line ${index + 1}: ${line.trim()}`);
    }

    for (const character of line) {
      if (character === "{") {
        braceDepth += 1;
      } else if (character === "}") {
        braceDepth -= 1;
      }
    }
  }

  assert.equal(braceDepth, 2);
});

test("Unity Bridge source does not duplicate C# method signatures", async () => {
  const source = await readFile(sourceUrl, "utf8");

  assert.deepEqual(duplicateCSharpMethodSignatures(source), []);
});

test("Unity Bridge exposes documented camera, screenshot, prefab, UI, material, graphics, physics, animation, vfx, shader, texture and editor commands", async () => {
  const source = await readFile(sourceUrl, "utf8");
  const readme = await readFile(readmeUrl, "utf8");
  const bridgeSection = readme.slice(readme.indexOf("## Unity Editor Bridge"), readme.indexOf("## Batch Automation"));

  assert.match(source, /HttpListener/);
  assert.match(source, /POST" \|\| context\.Request\.Url\.AbsolutePath != "\/bridge\/execute"/);
  assert.match(source, /CapabilityJson\("camera", CameraCommands\)/);
  assert.match(source, /CapabilityJson\("prefab", PrefabCommands\)/);
  assert.match(source, /CapabilityJson\("ui", UiCommands\)/);
  assert.match(source, /CapabilityJson\("material", MaterialCommands\)/);
  assert.match(source, /CapabilityJson\("graphics", GraphicsCommands\)/);
  assert.match(source, /CapabilityJson\("physics", PhysicsCommands\)/);
  assert.match(source, /CapabilityJson\("animation", AnimationCommands\)/);
  assert.match(source, /CapabilityJson\("vfx", VfxCommands\)/);
  assert.match(source, /CapabilityJson\("physics", PhysicsCommands\) \+ ","\s+\+ CapabilityJson\("animation", AnimationCommands\) \+ ","\s+\+ CapabilityJson\("vfx", VfxCommands\) \+ ","\s+\+ CapabilityJson\("shader", ShaderCommands\) \+ ","/);
  assert.match(source, /CapabilityJson\("shader", ShaderCommands\)/);
  assert.match(source, /CapabilityJson\("texture", TextureCommands\)/);
  assert.match(source, /private static readonly string\[\] CameraCommands = \{ "camera\.list", "camera\.info", "camera\.screenshot" \}/);
  assert.match(source, /private static readonly string\[\] PrefabCommands = \{ "prefab\.info", "prefab\.create", "prefab\.stage-open", "prefab\.stage-save", "prefab\.stage-close", "prefab\.hierarchy" \}/);
  assert.match(source, /private static readonly string\[\] UiCommands = \{ "ui\.canvas\.create", "ui\.text\.create", "ui\.button\.create", "ui\.image\.create", "ui\.document\.info", "ui\.document\.create", "ui\.hierarchy" \}/);
  assert.match(source, /private static readonly string\[\] MaterialCommands = \{ "material\.info", "material\.create", "material\.assign", "material\.set" \}/);
  assert.match(source, /private static readonly string\[\] GraphicsCommands = \{ "graphics\.quality", "graphics\.settings", "graphics\.screenshot" \}/);
  assert.match(source, /private static readonly string\[\] PhysicsCommands = \{ "physics\.settings", "physics\.raycast", "physics\.simulation" \}/);
  assert.match(source, /private static readonly string\[\] AnimationCommands = \{ "animation\.clip", "animation\.state", "animation\.play", "animation\.stop" \}/);
  assert.match(source, /private static readonly string\[\] VfxCommands = \{ "vfx\.graph", "vfx\.effect" \}/);
  assert.match(source, /private static readonly string\[\] ShaderCommands = \{ "shader\.info", "shader\.read", "shader\.create", "shader\.update" \}/);
  assert.match(source, /private static readonly string\[\] TextureCommands = \{ "texture\.info", "texture\.create", "texture\.sprite", "texture\.import-settings" \}/);
  assert.match(source, /case "camera\.list"/);
  assert.match(source, /case "camera\.info"/);
  assert.match(source, /case "camera\.screenshot"/);
  assert.match(source, /case "scene\.screenshot"/);
  assert.match(source, /case "screenshot\.capture"/);
  assert.match(source, /case "prefab\.info"/);
  assert.match(source, /case "prefab\.create"/);
  assert.match(source, /case "prefab\.stage-open"/);
  assert.match(source, /case "prefab\.stage-save"/);
  assert.match(source, /case "prefab\.stage-close"/);
  assert.match(source, /case "prefab\.hierarchy"/);
  assert.match(source, /case "ui\.canvas\.create"/);
  assert.match(source, /case "ui\.text\.create"/);
  assert.match(source, /case "ui\.button\.create"/);
  assert.match(source, /case "ui\.image\.create"/);
  assert.match(source, /case "ui\.document\.info"/);
  assert.match(source, /case "ui\.document\.create"/);
  assert.match(source, /case "ui\.hierarchy"/);
  assert.match(source, /case "material\.info"/);
  assert.match(source, /case "material\.create"/);
  assert.match(source, /case "material\.assign"/);
  assert.match(source, /case "material\.set"/);
  assert.match(source, /case "graphics\.quality"/);
  assert.match(source, /case "graphics\.settings"/);
  assert.match(source, /case "graphics\.screenshot"/);
  assert.match(source, /case "physics\.settings"/);
  assert.match(source, /case "physics\.raycast"/);
  assert.match(source, /case "physics\.simulation"/);
  assert.match(source, /case "animation\.clip"/);
  assert.match(source, /case "animation\.state"/);
  assert.match(source, /case "animation\.play"/);
  assert.match(source, /case "animation\.stop"/);
  assert.match(source, /case "vfx\.graph"/);
  assert.match(source, /case "vfx\.effect"/);
  assert.match(source, /case "shader\.info"/);
  assert.match(source, /case "shader\.read"/);
  assert.match(source, /case "shader\.create"/);
  assert.match(source, /case "shader\.update"/);
  assert.match(source, /case "texture\.info"/);
  assert.match(source, /case "texture\.create"/);
  assert.match(source, /case "texture\.sprite"/);
  assert.match(source, /case "texture\.import-settings"/);
  assert.match(source, /case "editor\.state"/);
  assert.match(source, /case "editor\.play"/);
  assert.match(source, /case "editor\.pause"/);
  assert.match(source, /case "editor\.stop"/);
  assert.match(source, /case "editor\.menu"/);
  assert.match(bridgeSection, /`camera\.list`, `camera\.info`, `camera\.screenshot`, `scene\.screenshot`, `screenshot\.capture`, `prefab\.info`, `prefab\.create`, `prefab\.stage-open`, `prefab\.stage-save`, `prefab\.stage-close`, `prefab\.hierarchy`, `ui\.canvas\.create`, `ui\.text\.create`, `ui\.button\.create`, `ui\.image\.create`, `ui\.document\.info`, `ui\.document\.create`, `ui\.hierarchy`, `material\.info`, `material\.create`, `material\.assign`, `material\.set`, `graphics\.quality`, `graphics\.settings`, `graphics\.screenshot`, `physics\.settings`, `physics\.raycast`, `physics\.simulation`, `animation\.clip`, `animation\.state`, `animation\.play`, `animation\.stop`, `vfx\.graph`, `vfx\.effect`, `shader\.info`, `shader\.read`, `shader\.create`, `shader\.update`, `texture\.info`, `texture\.create`, `texture\.sprite`, `texture\.import-settings`, `editor\.state`, `editor\.play`, `editor\.pause`, `editor\.stop` and `editor\.menu`/);
  assert.doesNotMatch(bridgeSection, /live query\/edit\/screenshot/);
  assert.match(bridgeSection, /`scene hierarchy`, `go find`, `component set` and `script edit` are not routed by this package yet/);
});

test("Unity Bridge animation and vfx commands expose assets, target resolver diagnostics and package checks", async () => {
  const source = await readFile(sourceUrl, "utf8");
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
  const source = await readFile(sourceUrl, "utf8");

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
  const source = await readFile(sourceUrl, "utf8");
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
  assert.match(readme, /`E_PREFAB_PATH_REQUIRED`, `E_PREFAB_PATH_INVALID`, `E_PREFAB_NOT_FOUND`, `E_ASSET_NOT_PREFAB`, `E_PREFAB_OUTPUT_EXISTS`, `E_PREFAB_DIRTY_CONFLICT`, `E_PREFAB_STAGE_NOT_OPEN`, `E_PREFAB_STAGE_MISMATCH` and `E_UNITY_BUSY`/);
});


test("Unity Bridge shader and texture operations expose diagnostics", async () => {
  const source = await readFile(sourceUrl, "utf8");
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
  const source = await readFile(sourceUrl, "utf8");
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
  const source = await readFile(sourceUrl, "utf8");
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
  const source = await readFile(sourceUrl, "utf8");
  for (const [index, line] of source.split(/\r?\n/).entries()) {
    let inString = false;
    let escaped = false;
    for (const char of line) {
      if (escaped) {
        escaped = false;
        continue;
      }
      if (char === "\\" && inString) {
        escaped = true;
        continue;
      }
      if (char === '"') {
        inString = !inString;
      }
    }
    assert.equal(inString, false, `unterminated string literal on AlpsUnityBridge.cs:${index + 1}`);
  }
});

test("Unity Bridge graphics and physics commands expose settings, raycast and diagnostics", async () => {
  const source = await readFile(sourceUrl, "utf8");
  const readme = await readFile(readmeUrl, "utf8");

  assert.match(source, /QualitySettings\.SetQualityLevel/);
  assert.match(source, /GraphicsSettings\.defaultRenderPipeline/);
  assert.match(source, /RenderPipelineAsset/);
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
  const source = await readFile(sourceUrl, "utf8");
  const readme = await readFile(readmeUrl, "utf8");

  assert.match(source, /CancelBeforeStart/);
  assert.match(source, /TryStart/);
  assert.match(source, /MarkTimedOut/);
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
  const source = await readFile(sourceUrl, "utf8");
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
  assert.match(readme, /C# runtime behavior for empty camera targets, missing Cameras, screenshot IO failures, actual PNG\/JPG artifacts, Prefab Stage open\/save\/close, Prefab dirty conflicts, nested\/variant Prefab summaries, Material asset creation, Renderer\/UI Graphic assignment, Material property writes, GraphicsSettings\/QualitySettings persistence, render pipeline asset resolution, Physics\/Physics2D project setting persistence, raycast collision hits, explicit simulation steps, AnimationClip asset creation\/binding summaries, Animator state\/play\/stop behavior, VFX Graph asset creation, VisualEffect assignment, Shader import diagnostics, TextureImporter mutations, uGUI object creation, UIDocument\/PanelSettings linking and UI hierarchy summaries must still be proven by real Windows \+ Unity Editor validation/);
});

test("Unity Bridge editor commands expose state, play/pause/stop, menu execution and busy diagnostics", async () => {
  const source = await readFile(sourceUrl, "utf8");
  const readme = await readFile(readmeUrl, "utf8");

  assert.match(source, /private static string EditorStateJson\(\)/);
  assert.match(source, /private static string EditorPlayJson\(BridgeRequest request\)/);
  assert.match(source, /private static string EditorPauseJson\(BridgeRequest request\)/);
  assert.match(source, /private static string EditorStopJson\(BridgeRequest request\)/);
  assert.match(source, /private static string EditorMenuJson\(BridgeRequest request\)/);
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
  assert.match(readme, /E_UNITY_BUSY/);
  assert.match(readme, /E_PLAYMODE_TIMEOUT/);
  assert.match(readme, /E_MENU_NOT_FOUND/);
  assert.match(readme, /E_MENU_TIMEOUT/);
});

test("Unity Bridge treats PlayMode switching as editor-command busy state", async () => {
  const source = await readFile(sourceUrl, "utf8");
  const busyStart = source.indexOf("private static void EnsureEditorNotBusy");
  const busyEnd = source.indexOf("private static void WaitForPlayModeState", busyStart);
  const busyBlock = source.slice(busyStart, busyEnd);

  assert.ok(busyStart > -1);
  assert.ok(busyEnd > busyStart);
  assert.match(busyBlock, /EditorApplication\.isPlayingOrWillChangePlaymode/);
  assert.match(busyBlock, /E_UNITY_BUSY/);
});

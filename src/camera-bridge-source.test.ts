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

test("Unity Bridge exposes documented camera, screenshot, prefab, material, graphics, physics, shader and texture commands", async () => {
  const source = await readFile(sourceUrl, "utf8");
  const readme = await readFile(readmeUrl, "utf8");
  const bridgeSection = readme.slice(readme.indexOf("## Unity Editor Bridge"), readme.indexOf("## Batch Automation"));

  assert.match(source, /HttpListener/);
  assert.match(source, /POST" \|\| context\.Request\.Url\.AbsolutePath != "\/bridge\/execute"/);
  assert.match(source, /CapabilityJson\("camera", CameraCommands\)/);
  assert.match(source, /CapabilityJson\("prefab", PrefabCommands\)/);
  assert.match(source, /CapabilityJson\("material", MaterialCommands\)/);
  assert.match(source, /CapabilityJson\("graphics", GraphicsCommands\)/);
  assert.match(source, /CapabilityJson\("physics", PhysicsCommands\)/);
  assert.match(source, /CapabilityJson\("physics", PhysicsCommands\) \+ ","\s+\+ CapabilityJson\("shader", ShaderCommands\) \+ ","/);
  assert.match(source, /private static readonly string\[\] CameraCommands = \{ "camera\.list", "camera\.info", "camera\.screenshot" \}/);
  assert.match(source, /private static readonly string\[\] PrefabCommands = \{ "prefab\.info", "prefab\.create", "prefab\.stage-open", "prefab\.stage-save", "prefab\.stage-close", "prefab\.hierarchy" \}/);
  assert.match(source, /private static readonly string\[\] MaterialCommands = \{ "material\.info", "material\.create", "material\.assign", "material\.set" \}/);
  assert.match(source, /private static readonly string\[\] GraphicsCommands = \{ "graphics\.quality", "graphics\.settings", "graphics\.screenshot" \}/);
  assert.match(source, /private static readonly string\[\] PhysicsCommands = \{ "physics\.settings", "physics\.raycast", "physics\.simulation" \}/);
  assert.match(source, /CapabilityJson\("shader", ShaderCommands\)/);
  assert.match(source, /CapabilityJson\("texture", TextureCommands\)/);
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

  assert.match(source, /case "shader\.info"/);
  assert.match(source, /case "shader\.read"/);
  assert.match(source, /case "shader\.create"/);
  assert.match(source, /case "shader\.update"/);
  assert.match(source, /case "texture\.info"/);
  assert.match(source, /case "texture\.create"/);
  assert.match(source, /case "texture\.sprite"/);
  assert.match(source, /case "texture\.import-settings"/);
  assert.match(bridgeSection, /`camera\.list`, `camera\.info`, `camera\.screenshot`, `scene\.screenshot`, `screenshot\.capture`, `prefab\.info`, `prefab\.create`, `prefab\.stage-open`, `prefab\.stage-save`, `prefab\.stage-close`, `prefab\.hierarchy`, `material\.info`, `material\.create`, `material\.assign`, `material\.set`, `graphics\.quality`, `graphics\.settings`, `graphics\.screenshot`, `physics\.settings`, `physics\.raycast`, `physics\.simulation`, `shader\.info`, `shader\.read`, `shader\.create`, `shader\.update`, `texture\.info`, `texture\.create`, `texture\.sprite` and `texture\.import-settings`/);
  assert.doesNotMatch(bridgeSection, /live query\/edit\/screenshot/);
  assert.match(bridgeSection, /`scene hierarchy`, `go find`, `component set` and `script edit` are not routed by this package yet/);
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

test("Unity Bridge Quote escapes shader read control characters as valid JSON", async () => {
  const source = await readFile(sourceUrl, "utf8");

  assert.ok(source.includes("case '\\t':"));
  assert.ok(source.includes('builder.Append("\\\\t");'));
  assert.ok(source.includes("case '\\b':"));
  assert.ok(source.includes('builder.Append("\\\\b");'));
  assert.ok(source.includes("case '\\f':"));
  assert.ok(source.includes('builder.Append("\\\\f");'));
  assert.match(source, /character < ' '/);
  assert.match(source, /ToString\("x4", CultureInfo\.InvariantCulture\)/);
  assert.ok(source.includes('"content\\":" + Quote(content)'));

  const shaderContent = "Shader \"Alps/Control\" {\n\t// tab and controls \b\f\u0001\n}";
  const json = `{"ok":true,"data":{"content":${quoteLikeBridge(shaderContent)}}}`;
  const parsed = JSON.parse(json) as { data: { content: string } };
  assert.equal(parsed.data.content, shaderContent);
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

function quoteLikeBridge(value: string): string {
  let output = "\"";
  for (const character of value) {
    switch (character) {
      case "\"":
        output += "\\\"";
        break;
      case "\\":
        output += "\\\\";
        break;
      case "\b":
        output += "\\b";
        break;
      case "\f":
        output += "\\f";
        break;
      case "\n":
        output += "\\n";
        break;
      case "\r":
        output += "\\r";
        break;
      case "\t":
        output += "\\t";
        break;
      default:
        output += character < " " ? `\\u${character.charCodeAt(0).toString(16).padStart(4, "0")}` : character;
        break;
    }
  }
  return `${output}\"`;
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
  assert.match(readme, /C# runtime behavior for empty camera targets, missing Cameras, screenshot IO failures, actual PNG\/JPG artifacts, Prefab Stage open\/save\/close, Prefab dirty conflicts, nested\/variant Prefab summaries, Material asset creation, Renderer\/UI Graphic assignment, Material property writes, GraphicsSettings\/QualitySettings persistence, render pipeline asset resolution, Physics\/Physics2D project setting persistence, raycast collision hits and explicit simulation steps, Shader import diagnostics and TextureImporter mutations must still be proven by real Windows \+ Unity Editor validation/);
});

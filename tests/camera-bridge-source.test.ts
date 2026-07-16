import assert from "node:assert/strict";
import { readdir, readFile } from "node:fs/promises";
import test from "node:test";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";

const editorUrl = new URL("../../unitypkg/Editor/", import.meta.url);
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

test("AssetImportWorker never starts or registers a Bridge listener", async () => {
  // Worker 会加载 Editor assembly，但不是用户可寻址的 Unity Editor instance。
  const source = await readFile(new URL("UnityCliBridge.cs", editorUrl), "utf8");
  const start = source.indexOf("static UnityCliBridge()");
  const end = source.indexOf("private static void StartListener", start);
  const constructor = source.slice(start, end);
  assert.ok(start > -1 && end > start);
  assert.match(constructor, /AssetDatabase\.IsAssetImportWorkerProcess\(\)/);
  assert.ok(constructor.indexOf("IsAssetImportWorkerProcess") < constructor.indexOf("EditorApplication.update += ProcessQueuedWork"));
  assert.ok(constructor.indexOf("IsAssetImportWorkerProcess") < constructor.indexOf("StartListener()"));
});

test("one-capability-per-file camera tools route through project-tool.run with matching descriptors and contracts", async () => {
  const bridgeRegistry = await readFile(new URL("Bridge/BridgeCommandRegistry.cs", editorUrl), "utf8");
  const support = await readFile(new URL("Tools/camera/CameraToolSupport.cs", editorUrl), "utf8");
  const expectedTools = new Map([
    ["list.cs", "camera.list"],
    ["info.cs", "camera.info"],
    ["screenshot.cs", "camera.screenshot"],
    ["create.cs", "camera.create"],
    ["target.cs", "camera.target"],
    ["lens.cs", "camera.lens"],
    ["multiview.cs", "camera.multiview"]
  ]);

  assert.match(bridgeRegistry, /RegisterProjectToolCommands\(registry\)/);
  assert.doesNotMatch(bridgeRegistry, /CapabilityJson|CameraCommands|RegisterCommand\(registry, "camera\./);
  assert.match(support, /ParseCameraClearFlags/);
  assert.match(support, /E_CAMERA_CLEAR_FLAGS_INVALID/);
  assert.match(support, /Camera\.allCameras/);

  const cameraDirectory = await readdir(new URL("Tools/camera/", editorUrl));
  assert.deepEqual(cameraDirectory.filter((name) => name.endsWith(".cs")).sort(), [
    "CameraToolSupport.cs", "create.cs", "info.cs", "lens.cs", "list.cs", "multiview.cs", "screenshot.cs", "target.cs"
  ]);

  for (const [file, name] of expectedTools) {
    const source = await readFile(new URL(`Tools/camera/${file}`, editorUrl), "utf8");
    const [, command] = name.split(".");
    const spec = bridgeCommandRegistry.lookup("camera", command);
    const contract = findCommandContract(name);
    assert.match(source, /IUnityCliTool/);
    assert.match(source, new RegExp(`Name = "${name.replace(".", "\\.")}"`));
    assert.match(source, /ToolDescriptor Describe\(\)/);
    assert.equal(spec?.bridgeCommand, "project-tool.run", `${name} must use the typed project-tool route`);
    assert.equal(spec?.toolName, name, `${name} route must preserve its public tool name`);
    assert.equal(contract?.csharp_handler, "present", `${name} must have a typed C# contract handler`);
  }

  const createSource = await readFile(new URL("Tools/camera/create.cs", editorUrl), "utf8");
  assert.match(createSource, /ResolveUniqueTarget\(request\.Parent/);
  assert.match(createSource, /ParseCameraClearFlags\(request\.ClearFlags\)/);
  assert.ok(createSource.indexOf("ResolveUniqueTarget(request.Parent") < createSource.indexOf("new GameObject(name)"));
  assert.ok(createSource.indexOf("ParseCameraClearFlags(request.ClearFlags)") < createSource.indexOf("new GameObject(name)"));
});

test("Unity Bridge animation and vfx commands expose assets, target resolver diagnostics and package checks", async () => {
  const source = await readUnityBridgeSources();
  const vfxSource = await readExtensionSources("vfx-graph");
  const readme = await readFile(readmeUrl, "utf8");

  assert.match(source, /RequireAnimationClipPath/);
  assert.match(source, /AnimationUtility\.GetCurveBindings/);
  assert.match(source, /Name = "animation\.state"/);
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
  assert.match(readme, /`tools run animation\.clip --input '\{"path":"<path>"\}'` reads `Assets\/\*\.anim` AnimationClip settings, events, and bounded curve summaries/);
  assert.match(readme, /`tools run animation\.clip\.create --input '\{"path":"<path>","length":1\}' --yes` creates or overwrites persistent `Assets\/\*\.anim` assets/);
  assert.match(readme, /`tools run animation\.clip\.modify --input '\{"path":"<path>","loopTime":true\}' --yes` updates persistent clip-level settings/);
  assert.match(readme, /`tools run vfx\.graph\.info --input/);
  assert.match(readme, /`tools run vfx\.graph\.create --input/);
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
  assert.match(source, /ScreenshotPath = outputPath/);
  assert.match(source, /ArtifactPath = outputPath/);
  assert.match(source, /LogPath =/);
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
  assert.match(source, /PlannedEffects/);
  assert.match(source, /PrefabVariant/);
  assert.match(source, /Dependencies/);
  assert.match(source, /NestedPrefabs/);
  assert.match(source, /BuildPrefabInfo\(/);
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
  assert.match(source, /Name = "shader\.read"/);
  assert.match(source, /Name = "shader\.update"/);
  assert.match(source, /File\.ReadAllText\(/);
  assert.match(source, /File\.WriteAllText\(/);
  assert.match(source, /TextureImporterType\.Sprite/);
  assert.match(source, /SaveAndReimport\(\)/);
  assert.match(source, /E_SHADER_NOT_FOUND/);
  assert.match(source, /E_TEXTURE_NOT_FOUND/);
  assert.match(source, /E_IMPORT_FAILED/);
  assert.match(source, /PlannedEffects/);
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
  assert.match(source, /TextureReferences/);
  assert.match(source, /Dependencies/);
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
  assert.ok((source.match(/Resources\.GetBuiltinResource<Font>\("LegacyRuntime\.ttf"\)/g)?.length ?? 0) >= 2);
  assert.doesNotMatch(source, /Arial\.ttf/);
  assert.match(source, /AddComponent<UiButton>\(\)/);
  assert.match(source, /AddComponent<UiImage>\(\)/);
  assert.match(source, /HierarchyPath\(/);
  assert.doesNotMatch(source, /HierarchyPath\(parent\)\)/);
  assert.match(source, /UIDocument/);
  assert.match(source, /VisualTreeAsset/);
  assert.match(source, /StyleSheet/);
  assert.match(source, /DocumentInfo/);
  assert.doesNotMatch(source, /SuccessJson\("\{\\"document\":\{" \+ UiAssetFieldsJson\(path, visualTree\) \+ ",\\"styleSheet\\":"/);
  assert.match(source, /PanelSettings/);
  assert.match(source, /E_UI_PARENT_NOT_FOUND/);
  assert.match(source, /E_TARGET_AMBIGUOUS/);
  assert.match(source, /TargetResolution/);
  assert.match(source, /ResolveGameObject|ResolveUniqueTarget/);
  assert.match(source, /E_UI_PANEL_SETTINGS_NOT_FOUND/);
  assert.match(source, /E_UI_UXML_NOT_FOUND/);
  assert.match(source, /E_UI_USS_NOT_FOUND/);
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
  assert.doesNotMatch(source, /RegisterCommand\(registry, "graphics\.pipeline", "graphics", false/);
  assert.doesNotMatch(source, /RegisterCommand\(registry, "graphics\.volume", "graphics", false/);
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
  assert.match(readme, /`tools run graphics\.quality` reads and updates `QualitySettings`/);
  assert.match(readme, /`tools run physics\.raycast` supports 3D and 2D ray queries/);
});

test("Unity Bridge cancellation prevents queued side effects and cancels started work", async () => {
  const source = await readUnityBridgeSources();

  assert.match(source, /CancelBeforeStart/);
  assert.match(source, /TryStart/);
  assert.match(source, /CancellationTokenSource/);
  assert.match(source, /public void Cancel\(\)[\s\S]*cancellation\.Cancel\(\)/);
  assert.match(source, /item\.CancelBeforeStart\(\)[\s\S]*E_UNITY_BUSY/);
  assert.match(source, /item\.Cancel\(\)[\s\S]*E_EXECUTION_TIMEOUT/);
  assert.match(source, /await Execute\(item\.Request, item\.Token\)/);
  assert.match(source, /request\.ContentLength64/);
  assert.match(source, /stream\.Read\(bytes, offset, bytes\.Length - offset\)/);
  assert.match(source, /bridge_request_enqueued requestId=/);
  assert.match(source, /bridge_request_dequeued requestId=/);
  assert.match(source, /bridge_request_skipped_expired/);
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
  // camera.create 已迁移为 IUnityCliTool(unitypkg/Editor/Tools/camera/create.cs)
  const createStart = source.indexOf("class CameraCreateTool");
  const createEnd = source.indexOf("class CameraTargetTool", createStart);
  const createBlock = source.slice(createStart, createEnd > createStart ? createEnd : source.length);
  const lensStart = source.indexOf("class CameraLensTool");
  const lensEnd = source.indexOf("class CameraMultiviewTool", lensStart);
  const lensBlock = source.slice(lensStart, lensEnd > lensStart ? lensEnd : source.length);

  assert.ok(createStart > -1, "CameraCreateTool should exist");
  assert.ok(createEnd > createStart, "CameraCreateTool block should be bounded");
  assert.match(source, /ParseCameraClearFlags/);
  assert.match(source, /E_CAMERA_CLEAR_FLAGS_INVALID/);
  assert.match(createBlock, /ResolveUniqueTarget\(request\.Parent, "camera\.create --parent <go> not found\."\)/);
  assert.match(createBlock, /ParseCameraClearFlags\(request\.ClearFlags\)/);
  assert.match(createBlock, /var gameObject = new GameObject\(name\)/);
  assert.ok(createBlock.indexOf("ResolveUniqueTarget(request.Parent") < createBlock.indexOf("new GameObject(name)"), "camera.create must validate parent before creating a GameObject");
  assert.ok(createBlock.indexOf("ParseCameraClearFlags") < createBlock.indexOf("new GameObject(name)"), "camera.create must validate clearFlags before creating a GameObject");
  assert.match(lensBlock, /ParseCameraClearFlags\(request\.ClearFlags\)/);
});

test("Unity Bridge editor commands expose state, play/pause/stop, menu execution and busy diagnostics", async () => {
  const source = await readUnityBridgeSources();
  const readme = await readFile(readmeUrl, "utf8");

  assert.match(source, /Name = "editor\.state"/);
  assert.match(source, /Name = "editor\.play"/);
  assert.match(source, /Name = "editor\.pause"/);
  assert.match(source, /Name = "editor\.stop"/);
  assert.match(source, /Name = "editor\.menu"/);
  assert.match(source, /Name = "console\.get"/);
  assert.match(source, /private static string BridgeBuildStamp\(\)/);
  assert.match(source, /private static string BridgeHealthJson\(\)/);
  assert.match(source, /private const string BridgeDiagnosticsFingerprint = "health-fingerprint-play-mode-guard-v3"/);
  assert.match(source, /private static string AssemblyMvid\(Assembly assembly\)/);
  assert.match(source, /private static string AssemblyLocation\(Assembly assembly\)/);
  assert.match(source, /private static string RuntimeDiagnosticsPath\(\)/);
  assert.match(source, /private static void WriteRuntimeLoadDiagnostics\(string phase\)/);
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
  assert.match(source, /E_MENU_NOT_FOUND/);
  assert.match(source, /E_SCHEMA_INVALID/);
  assert.match(source, /EnsureEditorNotBusy/);
  assert.doesNotMatch(source, /WaitForPlayModeState/);
  assert.match(source, /PrefabStage/);
  assert.match(readme, /`tools run editor\.state` returns playMode, pause, busy, activeScene and Prefab Stage status/);
  assert.match(readme, /`tools run editor\.play`, `tools run editor\.pause` and `tools run editor\.stop` control Editor PlayMode/);
  assert.match(readme, /`tools run editor\.menu --input '\{"path":"<path>"\}'` executes a Unity Editor menu item/);
  assert.match(readme, /`tools run console\.get --input '\{"count":<n>\}'` returns recent Unity Console entries/);
  assert.match(readme, /E_UNITY_BUSY/);
  assert.match(readme, /E_PLAYMODE_TIMEOUT/);
  assert.match(readme, /E_MENU_NOT_FOUND/);
});

test("Unity Bridge treats PlayMode switching as editor-command busy state", async () => {
  const source = await readUnityBridgeSources();
  assert.match(source, /EditorApplication\.isPlayingOrWillChangePlaymode/);
  assert.match(source, /E_UNITY_PLAY_MODE|E_UNITY_BUSY/);
});

test("Bridge execution uses the protocol request, linked cancellation, and one envelope boundary", async () => {
  const source = await readUnityBridgeSources();
  const executeStart = source.indexOf("private static async ValueTask<string> Execute(UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)");
  const executeEnd = source.indexOf("private static bool RequiresDestructiveSecurityProof", executeStart);
  const executeBlock = source.slice(executeStart, executeEnd);

  assert.ok(executeStart > -1, "Could not find typed Execute method");
  assert.ok(executeEnd > executeStart, "Could not find end of typed execution path");
  assert.match(executeBlock, /await ExecuteData\(request, cancellationToken\)/);
  assert.match(executeBlock, /ProtocolEnvelopeFactory\.Success\(request\.RequestId, data\)/);
  assert.match(executeBlock, /CommandRegistry\.ExecuteAsync\(request, cancellationToken\)/);
  assert.match(executeBlock, /!EditorApplication\.isPlaying && !allowedDuringPlayModeTransition/);
  assert.match(executeBlock, /request\.Command == "runtime-tool\.run"/);
  assert.doesNotMatch(executeBlock, /editor-domain write commands are not available/);

  const updateRegistrations = source.match(/EditorApplication\.update \+= ProcessQueuedWork/g) ?? [];
  assert.equal(updateRegistrations.length, 1, "Bridge must have exactly one Editor update scheduler");
  assert.match(source, /CancellationTokenSource\.CreateLinkedTokenSource\(cancellationToken/);
  assert.match(source, /UnityCliWaitScheduler\.Tick\(\)/);
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
  const busyStart = source.indexOf("internal static void EnsureEditorNotBusy");
  const busyEnd = source.indexOf("internal static string ResolveScenePath", busyStart);
  const busyBlock = source.slice(busyStart, busyEnd);

  assert.ok(busyStart > -1);
  assert.ok(busyEnd > busyStart);

  assert.match(busyBlock, /UnityCliBridge\.IsPlayModeChanging\(\)/, "EnsureEditorNotBusy must block both entering and exiting Play Mode transitions");
  assert.doesNotMatch(busyBlock, /this editor-domain command is not available/, "Stable Play Mode must not reject editor-domain commands");
});

test("editor.state and shared wait guards treat stable Play Mode as ready", async () => {
  // 回归保护：isPlayingOrWillChangePlaymode 在稳定 PlayMode 中仍为 true，不能直接作为 busy 标志。
  const stateSource = await readFile(new URL("Contexts/editor/state.cs", editorUrl), "utf8");
  const editorSupport = await readFile(new URL("Tools/editor/EditorToolSupport.cs", editorUrl), "utf8");
  const waiter = await readFile(new URL("Infrastructure/UnityCliWaiter.cs", editorUrl), "utf8");
  const allSource = await readUnityBridgeSources();

  assert.match(stateSource, /EditorApplication\.isPlayingOrWillChangePlaymode != EditorApplication\.isPlaying/);
  assert.match(stateSource, /IsEditorBusy\(\)[\s\S]*IsPlayModeChanging\(\)/);
  assert.match(stateSource, /PlayModeChanging = IsPlayModeChanging\(\)/);
  assert.match(editorSupport, /EnsureEditorNotBusy[\s\S]*UnityCliBridge\.IsPlayModeChanging\(\)/);
  assert.match(editorSupport, /IsBusy = [^\n]*UnityCliBridge\.IsPlayModeChanging\(\)/);
  assert.match(editorSupport, /PlayModeChanging = UnityCliBridge\.IsPlayModeChanging\(\)/);
  assert.match(waiter, /UntilEditorReady[\s\S]*!UnityCliBridge\.IsPlayModeChanging\(\)/);
  assert.doesNotMatch(allSource, /isPlayingOrWillChangePlaymode && !EditorApplication\.isPlaying/);
  assert.doesNotMatch(allSource, /!EditorApplication\.isPlayingOrWillChangePlaymode && !UnityCliBridge\.IsBuildingPlayer/);
});

test("HTTP and execution guards share the safe Play Mode transition allowlist", async () => {
  const source = await readUnityBridgeSources();
  const httpStart = source.indexOf("private static void HandleHttpCore");
  const httpEnd = source.indexOf("private static async ValueTask<string> Execute", httpStart);
  const executeStart = httpEnd;
  const executeEnd = source.indexOf("private static bool RequiresDestructiveSecurityProof", executeStart);
  const httpBlock = source.slice(httpStart, httpEnd);
  const executeBlock = source.slice(executeStart, executeEnd);

  assert.ok(httpStart > -1 && executeStart > -1 && executeEnd > executeStart);
  for (const command of ["editor.state", "runtime-tool.list", "runtime-tool.run", "context.read"]) {
    const pattern = new RegExp(`request\\.Command == "${command.replaceAll(".", "\\.")}"`);
    assert.match(httpBlock, pattern, `${command} must be allowed before queueing`);
    assert.match(executeBlock, pattern, `${command} must be allowed during execution`);
  }
  assert.match(httpBlock, /!cachedIsPlaying && !allowedDuringPlayModeTransition/);
  assert.match(executeBlock, /!EditorApplication\.isPlaying && !allowedDuringPlayModeTransition/);
});

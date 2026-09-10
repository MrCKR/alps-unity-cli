import assert from "node:assert/strict";
import { readdir, readFile } from "node:fs/promises";
import test from "node:test";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";

const editorUrl = new URL("../../unitypkg/Editor/", import.meta.url);
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
  const end = source.indexOf("private static void ScheduleBridgeStartup", start);
  const constructor = source.slice(start, end);
  assert.ok(start > -1 && end > start);
  assert.match(constructor, /AssetDatabase\.IsAssetImportWorkerProcess\(\)/);
  assert.ok(constructor.indexOf("IsAssetImportWorkerProcess") < constructor.indexOf("EditorApplication.update += ProcessQueuedWork"));
  assert.ok(constructor.indexOf("IsAssetImportWorkerProcess") < constructor.indexOf("ScheduleBridgeStartup()"));
  assert.doesNotMatch(constructor, /StartListener\(\)/);
});

test("one-capability-per-file camera tools route through catalog.editor.run with matching descriptors and contracts", async () => {
  const bridgeRegistry = await readFile(new URL("Bridge/BridgeCommandRegistry.cs", editorUrl), "utf8");
  const support = await readFile(new URL("Tools/camera/CameraToolSupport.cs", editorUrl), "utf8");
  const fromCamera = await readFile(new URL("Tools/camera/screenshot/from-camera.cs", editorUrl), "utf8");
  const fromCameraSpec = bridgeCommandRegistry.lookup("screenshot", "from-camera");
  assert.match(fromCamera, /Name = "screenshot\.from-camera"/);
  assert.equal(fromCameraSpec?.bridgeCommand, "catalog.editor.run");
  assert.equal(fromCameraSpec?.toolName, "screenshot.from-camera");
  assert.equal(findCommandContract("screenshot.from-camera")?.csharp_handler, "present");
  const expectedTools = new Map([
    ["list.cs", "camera.list"],
    ["info.cs", "camera.info"],
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
    "CameraToolSupport.cs", "create.cs", "info.cs", "lens.cs", "list.cs", "multiview.cs", "target.cs"
  ]);

  for (const [file, name] of expectedTools) {
    const source = await readFile(new URL(`Tools/camera/${file}`, editorUrl), "utf8");
    const dot = name.indexOf(".");
    const spec = bridgeCommandRegistry.lookup(name.slice(0, dot), name.slice(dot + 1));
    const contract = findCommandContract(name);
    assert.match(source, /IUnityCliTool/);
    assert.match(source, new RegExp(`Name = "${name.replace(".", "\\.")}"`));
    assert.match(source, /ToolDescriptor Describe\(\)/);
    assert.equal(spec?.bridgeCommand, "catalog.editor.run", `${name} must use the typed project-tool route`);
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

  assert.match(source, /RequireAnimationClipPath/);
  assert.match(source, /AnimationUtility\.GetCurveBindings/);
  assert.match(source, /Name = "animator\.get-state"/);
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
  assert.doesNotMatch(vfxSource, /AssetDatabase\.Refresh/);
  assert.match(vfxSource, /PrepareVfxGraphFactory/);
  assert.match(vfxSource, /E_VFX_GRAPH_FACTORY_UNAVAILABLE/);
  assert.doesNotMatch(vfxSource, /WriteMinimalVfxGraphYaml|VisualEffectResource|guid: [0-9a-f]{32}/);
  assert.doesNotMatch(vfxSource, /SetAssetPath/);
  assert.doesNotMatch(vfxSource, /graphProperty\.SetValue/);
  assert.match(vfxSource, /AssetDatabase\.LoadAssetAtPath\(path, assetType\)/);
  assert.match(source, /E_TARGET_AMBIGUOUS/);
  assert.match(source, /rawPath\.Replace\('\\\\', '\/'\)\.Trim\(\)/);
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
});

test("Unity Bridge prefab operations expose structured diagnostics", async () => {
  const source = await readUnityBridgeSources();

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
});


test("Unity Bridge shader and texture operations expose diagnostics", async () => {
  const source = await readUnityBridgeSources();

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
});

test("Unity Bridge material commands expose shader properties, assignment and diagnostics", async () => {
  const source = await readUnityBridgeSources();

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
});

test("Unity Bridge UI operations expose uGUI and UI Toolkit diagnostics", async () => {
  const source = await readUnityBridgeSources();

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
  assert.match(source, /E_GRAPHICS_RANGE_INVALID|E_GRAPHICS_ENUM_INVALID/);
  assert.match(source, /E_PHYSICS_SETTING_UNSUPPORTED/);
});

test("Unity Bridge cancellation prevents queued side effects without cancelling started work", async () => {
  const source = await readUnityBridgeSources();
  const httpServer = await readFile(new URL("Bridge/BridgeHttpServer.cs", editorUrl), "utf8");

  assert.match(source, /CancelBeforeStart/);
  assert.match(source, /TryStart/);
  assert.match(source, /CancellationTokenSource/);
  assert.match(source, /public bool CancelBeforeStart\(\)[\s\S]*cancellation\.Cancel\(\)/);
  assert.match(source, /item\.CancelBeforeStart\(\)[\s\S]*E_UNITY_BUSY/);
  assert.match(httpServer, /private static string ResolveStartedTimeoutResponse[\s\S]*if \(item\.Completed\.Wait\(0\) && item\.Response != null\) return item\.Response;[\s\S]*"E_EXECUTION_TIMEOUT"/);
  assert.doesNotMatch(httpServer, /ResolveStartedTimeoutResponse[\s\S]*CancelBeforeStart/);
  assert.match(source, /await ExecuteCoordinatedRequest\(item\.Request, item\.RawRequestBytes, item\.TryAccept, item\.Token\)/);
  assert.doesNotMatch(source, /CancelBeforeAcceptance\(\)/);
  assert.match(source, /request\.ContentLength64/);
  assert.match(source, /stream\.Read\(bytes, offset, bytes\.Length - offset\)/);
  assert.match(source, /"bridge_request_enqueued" : "bridge_request_attached"/);
  assert.match(source, /bridge_request_dequeued requestId=/);
  assert.match(source, /bridge_request_skipped_expired/);
});

test("Unity Bridge screenshot validates path, format, overwrite and busy diagnostics", async () => {
  const source = await readUnityBridgeSources();

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
  assert.match(source, /WriteScreenshotBytes\(outputPath, bytes, (?:captureBudget|budget)\)/);
  assert.match(source, /E_SCREENSHOT_ARTIFACT_EMPTY/);
  assert.match(source, /E_SCREENSHOT_ARTIFACT_MISSING/);
  assert.match(source, /E_SCREENSHOT_ARTIFACT_INVALID/);
  assert.match(source, /Path\.GetFullPath/);

  const multiviewSource = await readFile(new URL("Tools/camera/multiview.cs", editorUrl), "utf8");
  assert.match(multiviewSource, /CameraToolSupport\.NormalizeFormat\(request\.Format, Path\.Combine\(outputDir, "capture\.png"\)\)/);
  assert.match(multiviewSource, /CaptureCamera\(camera, width, height, false, 95, plan\.Value, format/);
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

  assert.doesNotMatch(source, /Name = "editor\.state"/);
  assert.match(source, /"internal\.editor\.state\.snapshot"/);
  assert.match(source, /Name = "editor\.play-mode\.start"/);
  assert.match(source, /Name = "editor\.pause"/);
  assert.match(source, /Name = "editor\.play-mode\.stop"/);
  assert.match(source, /Name = "editor\.menu"/);
  assert.match(source, /Name = "console\.read"/);
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
  assert.match(source, /Packages\/com\.alpsckr\.unitycli\/Editor\/UnityCliBridge\.cs/);
  assert.match(source, /private static void StopStaleBridgeListeners\(\)/);
  assert.match(source, /stale_bridge_listener_stopped/);
  assert.match(source, /diagnosticsFingerprint/);
  assert.match(source, /moduleVersionId/);
  assert.match(source, /"version", 2L/);
  assert.match(source, /"instanceId", InstanceId\(\)/);
  assert.match(source, /"canonicalProjectPath", ProjectIdentity\(\)\.CanonicalProjectPath/);
  assert.match(source, /"canonicalizationVersion"/);
  assert.match(source, /"editorSessionId", EditorSessionId\(\)/);
  assert.match(source, /"listenerId", ListenerId/);
  assert.match(source, /"actualPort", \(long\)port/);
  assert.match(source, /"buildStamp", BridgeBuildStamp\(\)/);
  assert.match(source, /EditorApplication\.ExecuteMenuItem\(request\.Path\)/);
  assert.match(source, /E_MENU_COMPLETION_UNSUPPORTED/);
  assert.match(source, /EditorApplication\.isPlaying/);
  assert.match(source, /EditorApplication\.isPaused/);
  assert.match(source, /EditorApplication\.isPlayingOrWillChangePlaymode/);
  assert.match(source, /E_UNITY_BUSY/);
  assert.match(source, /E_MENU_NOT_FOUND/);
  assert.match(source, /E_SCHEMA_INVALID/);
  assert.match(source, /EnsureEditorNotBusy/);
  assert.doesNotMatch(source, /WaitForPlayModeState/);
  assert.match(source, /PrefabStage/);
});

test("Unity Bridge treats PlayMode switching as editor-command busy state", async () => {
  const source = await readUnityBridgeSources();
  assert.match(source, /EditorApplication\.isPlayingOrWillChangePlaymode/);
  assert.match(source, /E_UNITY_PLAY_MODE|E_UNITY_BUSY/);
});

test("Bridge execution uses the protocol request, linked cancellation, and one envelope boundary", async () => {
  const source = await readUnityBridgeSources();
  const executeStart = source.indexOf("private static async ValueTask<string> Execute(UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)");
  const executeEnd = source.indexOf("private static void CancelPendingWaits", executeStart);
  const executeBlock = source.slice(executeStart, executeEnd);

  assert.ok(executeStart > -1, "Could not find typed Execute method");
  assert.ok(executeEnd > executeStart, "Could not find end of typed execution path");
  assert.match(executeBlock, /await ExecuteData\(request, cancellationToken\)/);
  assert.match(executeBlock, /ProtocolEnvelopeFactory\.Success\(request\.RequestId, data\)/);
  assert.match(executeBlock, /CommandRegistry\.ExecuteAsync\(request, cancellationToken\)/);
  assert.match(executeBlock, /if \(IsPlayModeChanging\(\)\)[\s\S]*if \(!allowedDuringPlayModeTransition\)/);
  assert.match(executeBlock, /request.Command == "catalog.player.run"/);
  assert.doesNotMatch(executeBlock, /editor-domain write commands are not available/);

  const updateRegistrations = source.match(/EditorApplication\.update \+= ProcessQueuedWork/g) ?? [];
  assert.equal(updateRegistrations.length, 1, "Bridge must have exactly one Editor update scheduler");
  assert.match(source, /CancellationTokenSource\.CreateLinkedTokenSource\(cancellationToken/);
  assert.match(source, /UnityCliWaitScheduler\.Tick\(\)/);
});

test("Unity Bridge source has no destructive security confirmation gate", async () => {
  const source = await readUnityBridgeSources();

  assert.doesNotMatch(source, /RequiresDestructiveSecurityProof/);
  assert.doesNotMatch(source, /SecurityConfirmed/);
  assert.doesNotMatch(source, /SecurityProof/);
  assert.doesNotMatch(source, /E_DESTRUCTIVE_CONFIRMATION_REQUIRED/);
  assert.doesNotMatch(source, /--yes/);
  assert.doesNotMatch(source, /confirmed security/i);
});

test("BridgeHttpServer Play Mode guard only blocks transitions", async () => {
  // 回归保护：HTTP 入队前 guard 不应在稳定 Play Mode 下提前拒绝工具调用。
  const source = await readUnityBridgeSources();
  const handleHttpCoreStart = source.indexOf("private static void HandleHttpCore");
  const handleHttpCoreEnd = source.indexOf("private sealed class BridgeWorkItem", handleHttpCoreStart);
  const httpBlock = source.slice(handleHttpCoreStart, handleHttpCoreEnd > handleHttpCoreStart ? handleHttpCoreEnd : source.length);

  assert.ok(handleHttpCoreStart > -1, "Could not find HandleHttpCore method");

  assert.match(httpBlock, /if \(cachedIsPlayModeChanging\)[\s\S]*if \(!allowedDuringPlayModeTransition\)/, "HandleHttpCore must only block transition-time commands");
  assert.match(httpBlock, /http-guard-v3/, "HandleHttpCore transition error must include the v3 marker");
  assert.doesNotMatch(httpBlock, /editor-domain write commands are not available/, "Stable Play Mode must not be rejected before queueing");

  // HandleHttpCore 的切换期 allowlist 需要与 Execute 保持一致。
  assert.match(httpBlock, /IsEditorStateSnapshotCommand\(request\.Command\)/, "HandleHttpCore must allow the private state snapshot during Play Mode transitions");
  assert.doesNotMatch(httpBlock, /request\.Command == "editor\.state"/, "The removed public editor.state command must not stay in the transition allowlist");
  assert.doesNotMatch(httpBlock, /request\.Command == "editor\.console"/, "removed Console bridge route must not stay in the transition allowlist");
  assert.match(httpBlock, /request\.Command == "editor\.pause"/, "HandleHttpCore must allow editor.pause during Play Mode transitions");
  assert.match(httpBlock, /request\.Command == "editor\.play-mode\.stop"/, "HandleHttpCore must allow editor.play-mode.stop during Play Mode transitions");
  assert.match(httpBlock, /IsProjectToolCatalogReadCommand\(request\.Command\)/, "HandleHttpCore must allow bounded catalog readers during Play Mode transitions");
  assert.match(httpBlock, /request.Command == "catalog.player.run"/, "HandleHttpCore must allow catalog.player.run during Play Mode transitions");
  assert.match(httpBlock, /IsDoctorAdmissionBypassCommand\(request\.Command\)/, "HandleHttpCore must allow bounded doctor catalog/compile reads during Play Mode transitions");
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

test("private state sampler and shared wait guards treat stable Play Mode as ready", async () => {
  // 回归保护：isPlayingOrWillChangePlaymode 在稳定 PlayMode 中仍为 true，不能直接作为 busy 标志。
  const stateSource = await readFile(new URL("Infrastructure/UnityCliEditorStateSnapshot.cs", editorUrl), "utf8");
  const editorSupport = await readFile(new URL("Tools/editor/EditorToolSupport.cs", editorUrl), "utf8");
  const waiter = await readFile(new URL("Infrastructure/UnityCliWaiter.cs", editorUrl), "utf8");
  const allSource = await readUnityBridgeSources();

  assert.match(stateSource, /IsPlayModeChanging\(\) \? "transitioning" : \(EditorApplication\.isPlaying \? "play" : "edit"\)/);
  assert.match(stateSource, /EditorApplication\.isPlaying && !IsPlayModeChanging\(\)/);
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
  const executeEnd = source.indexOf("private static void CancelPendingWaits", executeStart);
  const httpBlock = source.slice(httpStart, httpEnd);
  const executeBlock = source.slice(executeStart, executeEnd);

  assert.ok(httpStart > -1 && executeStart > -1 && executeEnd > executeStart);
  assert.match(httpBlock, /IsEditorStateSnapshotCommand\(request\.Command\)/);
  assert.match(executeBlock, /IsEditorStateSnapshotCommand\(request\.Command\)/);
  assert.doesNotMatch(httpBlock, /request\.Command == "editor\.state"/);
  assert.doesNotMatch(executeBlock, /request\.Command == "editor\.state"/);
  assert.match(httpBlock, /IsProjectToolCatalogReadCommand\(request\.Command\)/);
  assert.match(executeBlock, /IsProjectToolCatalogReadCommand\(request\.Command\)/);
  const runtimeRun = /request.Command == "catalog.player.run"/;
  assert.match(httpBlock, runtimeRun, "catalog.player.run must be allowed before queueing");
  assert.match(executeBlock, runtimeRun, "catalog.player.run must be allowed during execution");
  assert.match(httpBlock, /if \(cachedIsPlayModeChanging\)[\s\S]*if \(!allowedDuringPlayModeTransition\)/);
  assert.match(executeBlock, /if \(IsPlayModeChanging\(\)\)[\s\S]*if \(!allowedDuringPlayModeTransition\)/);
});

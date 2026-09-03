import { existsSync, mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { collectPublicToolInventory } from "./lib/public-tool-inventory.mjs";

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const args = process.argv.slice(2);
const strict = args.includes("--strict");
const outputPath = optionValue("--output");
const readOnlyProcessObservationBatchModeFilterAllowlist = new Set([
  "src/unity-project-processes.ts"
]);

/** 读取 UTF-8 文本，并统一路径分隔符便于跨平台审计。 */
function readText(path) {
  return readFileSync(path, "utf8");
}

/** 递归收集文件，调用方决定是否纳入审计。 */
function collectFiles(root, predicate, result = []) {
  if (!existsSync(root)) return result;
  for (const entry of readdirSync(root)) {
    const fullPath = join(root, entry);
    const info = statSync(fullPath);
    if (info.isDirectory()) collectFiles(fullPath, predicate, result);
    else if (predicate(fullPath)) result.push(fullPath);
  }
  return result;
}

/** 将绝对路径转换为仓库相对 POSIX 路径。 */
function repoPath(path) {
  return relative(ROOT, path).replaceAll("\\", "/");
}

/** 返回指定命令行选项后面的值。 */
function optionValue(name) {
  const index = args.indexOf(name);
  return index >= 0 ? args[index + 1] : undefined;
}

/** 统计正则匹配次数，避免依赖有状态 RegExp 的 lastIndex。 */
function countMatches(text, pattern) {
  const flags = pattern.flags.includes("g") ? pattern.flags : `${pattern.flags}g`;
  return [...text.matchAll(new RegExp(pattern.source, flags))].length;
}

function escapePattern(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/** 读取 extension manifest，并建立 capability ownership 真相表。 */
function loadExtensionOwnership() {
  const manifests = [];
  const toolOwners = new Map();
  const extensionRoot = join(ROOT, "extensions");
  for (const manifestPath of collectFiles(extensionRoot, (path) => path.endsWith("extension.json"))) {
    const manifest = JSON.parse(readText(manifestPath));
    const tools = (manifest.contributions?.tools ?? []).map((entry) => entry.name).filter(Boolean);
    if (Object.hasOwn(manifest.contributions ?? {}, "contexts")) throw new Error(`${repoPath(manifestPath)} contains forbidden legacy contexts contributions`);
    manifests.push({ id: manifest.id, path: repoPath(manifestPath), tools });
    for (const name of tools) addOwner(toolOwners, name, manifest.id);
  }
  manifests.sort((left, right) => left.id.localeCompare(right.id));
  return { manifests, toolOwners };
}

/** 保留全部 claim，后续可检测重复 owner。 */
function addOwner(map, name, owner) {
  const owners = map.get(name) ?? [];
  owners.push(owner);
  map.set(name, owners);
}

/** 从生成注入文件读取 TS-local capability 名单。 */
function generatedNames(path, functionName) {
  if (!existsSync(path)) return [];
  const pattern = new RegExp(`${functionName}\\(\"([^\"]+)\"`, "g");
  return [...readText(path).matchAll(pattern)].map((match) => match[1]).sort();
}

/** 扫描一个 C# 文件中的 Tool 接口实现数量。 */
function csharpCapabilityFile(path) {
  const text = readText(path);
  const toolCount = countMatches(text, /:\s*IUnityCliTool\s*</g);
  const descriptor = text.match(/ToolDescriptor\s+Describe\(\)\s*=>\s*new\(\)\s*\{([\s\S]*?)\};/);
  const descriptorText = descriptor?.[1] ?? "";
  const name = descriptorText.match(/\bName\s*=\s*"([a-z0-9][a-z0-9.-]*)"/)?.[1] ?? null;
  const group = descriptorText.match(/\bGroup\s*=\s*"([a-z0-9][a-z0-9.-]*)"/)?.[1] ?? null;
  return { path: repoPath(path), toolCount, names: name ? [name] : [], group };
}

/** 提取命名 class/interface 的平衡花括号类型体，避免跨到相邻 Result 类型。 */
function namedTypeBodies(text, suffix) {
  const bodies = [];
  const pattern = new RegExp(`\\b(?:class|interface)\\s+[A-Za-z0-9_]*${suffix}\\b[^\\{]*\\{`, "g");
  for (const match of text.matchAll(pattern)) {
    const open = match.index + match[0].lastIndexOf("{");
    let depth = 0;
    for (let index = open; index < text.length; index += 1) {
      if (text[index] === "{") depth += 1;
      else if (text[index] === "}") {
        depth -= 1;
        if (depth === 0) {
          bodies.push(text.slice(open + 1, index));
          break;
        }
      }
    }
  }
  return bodies;
}

/** JSON contract中只把descriptor entry自身的fingerprint视为top-level字段，业务schema同名字段不误报。 */
function countStaticCatalogJsonFields(value, forbiddenFields) {
  if (Array.isArray(value)) return value.reduce((total, item) => total + countStaticCatalogJsonFields(item, forbiddenFields), 0);
  if (!value || typeof value !== "object") return 0;
  const keys = Object.keys(value);
  const forbiddenLower = new Set([...forbiddenFields].map((field) => field.toLowerCase()));
  const descriptorEntry = (typeof value.name === "string" || typeof value.toolName === "string")
    && (Object.hasOwn(value, "summary") || Object.hasOwn(value, "group") || Object.hasOwn(value, "inputSchema"));
  const ownCount = keys.filter((key) => forbiddenLower.has(key.toLowerCase())
    && (key.toLowerCase() !== "fingerprint" || descriptorEntry)).length;
  return ownCount + Object.values(value).reduce((total, item) => total + countStaticCatalogJsonFields(item, forbiddenFields), 0);
}

/** 统计生产路径内禁止残留的符号或结构。 */
function residualChecks(allTextFiles, csharpFiles, multiCapabilityFiles) {
  const searchRoots = allTextFiles.map((path) => ({ path: repoPath(path), text: readText(path) }));
  const checks = [];
  const forbiddenPerToolIdentifiers = [
    ["Project", "Tool", "Handler", "Source"].join(""),
    ["internal", "Descriptor", "Fingerprint"].join(""),
    ["live", "Descriptor", "Fingerprint"].join(""),
    ["expected", "Descriptor", "Fingerprint"].join(""),
    ["Expected", "Descriptor", "Fingerprint"].join(""),
    ["ToolDescriptor", ".", "Fingerprint"].join(""),
    ["E", "_TOOL", "_DESCRIPTOR", "_CHANGED"].join(""),
    ["E", "_TOOL", "_FINGERPRINT", "_MISMATCH"].join(""),
  ];
  const forbiddenIdentifierPattern = new RegExp(forbiddenPerToolIdentifiers.map(escapePattern).join("|"), "g");
  const identifierResiduals = searchRoots
    .map((entry) => ({ path: entry.path, count: countMatches(entry.text, forbiddenIdentifierPattern) }))
    .filter((entry) => entry.count > 0);
  checks.push({
    id: "removedPerToolIdentifiers",
    expected: 0,
    actual: identifierResiduals.reduce((sum, entry) => sum + entry.count, 0),
    files: identifierResiduals,
  });

  const removedCatalogIdentities = [
    ["extension", "status"].join("."),
    ["tools", "describe"].join("."),
    ["tools", "group"].join("."),
    ["tools", "group-list"].join("."),
    ["tools", "search"].join("."),
  ];
  const removedIdentityPattern = new RegExp(`(?:"|')(?:${removedCatalogIdentities.map(escapePattern).join("|")})(?:"|')`, "g");
  const generatedOrContractResiduals = searchRoots
    .filter((entry) => entry.path.startsWith("contracts/")
      || entry.path.startsWith("schemas/")
      || entry.path.startsWith("scripts/")
      || entry.path.includes("/.generated-")
      || entry.path.includes("/Generated/")
      || entry.path.endsWith(".g.cs"))
    .map((entry) => ({ path: entry.path, count: countMatches(entry.text, removedIdentityPattern) }))
    .filter((entry) => entry.count > 0);
  checks.push({
    id: "removedCatalogIdentitiesInGeneratedContracts",
    expected: 0,
    actual: generatedOrContractResiduals.reduce((sum, entry) => sum + entry.count, 0),
    files: generatedOrContractResiduals,
  });

  const forbiddenStaticCatalogFields = [
    ["descriptor", "Fingerprint"].join(""),
    ["handler", "Fingerprint"].join(""),
    ["finger", "print"].join(""),
  ];
  const staticCatalogFieldPattern = new RegExp(`(?:["'](?:${forbiddenStaticCatalogFields.join("|")})["']\\s*:|\\b(?:${forbiddenStaticCatalogFields.join("|")})\\s*(?:[?]?:|\\{|=))`, "gi");
  const staticCatalogFieldResiduals = searchRoots
    .filter((entry) => entry.path !== "scripts/audit-final-architecture.mjs")
    .filter((entry) => entry.path.startsWith("contracts/")
      || entry.path.startsWith("schemas/")
      || entry.path.startsWith("scripts/")
      || entry.path.includes("/.generated-")
      || entry.path.includes("/Generated/")
      || entry.path.endsWith(".g.cs"))
    .map((entry) => {
      if (!entry.path.endsWith(".json")) return { path: entry.path, count: countMatches(entry.text, staticCatalogFieldPattern) };
      try { return { path: entry.path, count: countStaticCatalogJsonFields(JSON.parse(entry.text), new Set(forbiddenStaticCatalogFields)) }; }
      catch { return { path: entry.path, count: 0 }; }
    })
    .filter((entry) => entry.count > 0);
  checks.push({
    id: "staticCatalogForbiddenFields",
    expected: 0,
    actual: staticCatalogFieldResiduals.reduce((sum, entry) => sum + entry.count, 0),
    files: staticCatalogFieldResiduals,
  });

  const bridgeSource = searchRoots.find((entry) => entry.path === "unitypkg/Editor/UnityCliBridge.cs")?.text ?? "";
  const staticConstructorStart = bridgeSource.indexOf("static UnityCliBridge()");
  const staticConstructorEnd = bridgeSource.indexOf("private static", staticConstructorStart + 1);
  const staticConstructor = staticConstructorStart >= 0 && staticConstructorEnd > staticConstructorStart
    ? bridgeSource.slice(staticConstructorStart, staticConstructorEnd)
      .replace("AssetDatabase.IsAssetImportWorkerProcess()", "AllowedAssetImportWorkerGuard()")
    : bridgeSource;
  const catalogRegistry = searchRoots.find((entry) => entry.path === "unitypkg/Editor/ProjectTools/ProjectToolRegistry.cs")?.text ?? "";
  const constructionStart = catalogRegistry.indexOf("private static void InitializeProjectToolCatalog()");
  const constructionEnd = catalogRegistry.indexOf("private static bool ProjectToolCatalogMatches", constructionStart);
  const constructionGraph = constructionStart >= 0 && constructionEnd > constructionStart
    ? catalogRegistry.slice(constructionStart, constructionEnd)
    : catalogRegistry;
  const constructionOwnerPaths = [
    "unitypkg/Editor/ProjectTools/IUnityCliTool.cs",
    "unitypkg/Editor/ProjectTools/RegisteredTool.cs",
    "unitypkg/Editor/Infrastructure/RegistryOwnershipResolver.cs",
    "unitypkg/Editor/ProjectTools/OfficialAttributeToolRegistry.cs",
    "unitypkg/Editor/ProjectTools/ProjectToolSchemaValidator.cs",
    "unitypkg/Editor/ProjectTools/UnityCliSchemaDeriver.cs",
    "unitypkg/Editor/Infrastructure/UnityCliSerializer.cs",
  ];
  const constructionOwnerSources = constructionOwnerPaths
    .map((path) => searchRoots.find((entry) => entry.path === path)?.text ?? "")
    .join("\n");
  const observableConstructionGraph = `${staticConstructor}\n${constructionGraph}\n${constructionOwnerSources}`;
  const forbiddenConstructionIo = /\b(?:AssetDatabase|EditorPrefs|SessionState|PlayerPrefs)\b|\b(?:File|Directory)\s*\.|WriteLog\([^\r\n]*(?:completed|registered|success)/gi;
  const forbiddenConstructionCount = countMatches(observableConstructionGraph, forbiddenConstructionIo);
  checks.push({
    id: "catalogConstructionForbiddenIo",
    expected: 0,
    actual: forbiddenConstructionCount,
    files: forbiddenConstructionCount > 0 ? [{ path: "unitypkg/Editor/ProjectTools catalog construction graph", count: forbiddenConstructionCount }] : [],
  });
  const legacyPatternRoots = searchRoots.filter((entry) => entry.path !== "scripts/audit-final-architecture.mjs");
  const patternChecks = [
    ["legacyToolAttribute", /\[UnityCliTool\s*\(/g],
    ["legacyContextAttribute", /\[UnityCliContext\s*\(/g],
    ["rawContextInterface", /\bIUnityCliBuiltInRawBridgeContext\b/g],
    ["rawContextRead", /\bReadRawAsync\b/g],
    ["rawToolInput", /\bInputJson\b/g],
    ["deferredMarker", /\bDeferredResponseMarker\b/g],
    ["deferredWait", /\bStartDeferredWait\b/g],
    ["detailsJson", /\bDetailsJson\b/g],
    ["stringDetailsConstructor", /BridgeCommandException\(string code, string message, string detailsJson/g],
    ["bridgeNextAction", /\bBridgeCommandException\.NextAction\b/g],
    ["legacyRuntimeContext", /\blegacyRuntimeFromContext\b/g],
    ["legacyWireRequest", /\{\s*requestId\s*:[^{}\n]+,\s*command\s*:[^{}\n]+,\s*args\s*[,}]|bridgeExecute\??:\s*\([^\n]*\bargs\s*:/g],
    ["legacyCompatibilityFallback", /\blegacy(?:Method|Fallback|Adapter|Path|Implementation)\b|兼容[^\r\n]{0,80}(?:旧|legacy)[^\r\n]{0,80}(?:API|接口|路径|实现)?/gi],
    ["probuilderSemanticDegradation", /\bfallback\b|回退|近似|\blegacy(?:Method|Fallback|Adapter|Path|Implementation)\b|Mesh\.CombineMeshes|CreatePrimitive|InsertVertexOnEdge\([^\r\n]*null|hasColors|sharedMaterials\s*=(?!=)/gi],
    ["contextEmptySchemaFallback", /(?:inputSchema|outputSchema)\s*:\s*[^\n?]+\?\?\s*\{\s*type:\s*["']object["'],\s*properties:\s*\{\s*\}\s*\}/g],
    ["bypassDefineTool", /\bbypassDefineTool\b/g],
    ["duplicateToolJsonParser", /\bUnityCliToolJson\b/g],
    ["manualJsonParsers", /\bJsonRawProperty\s*\(|\bFindJson(?:String|Value)End\s*\(|\bJsonObjectPropertyNames\s*\(|\b(?:private|internal|public)\s+static\s+(?:bool|object|string|Dictionary[^\n]+|List[^\n]+)\s+Parse(?:Json)?(?:Value|Object|Array|String|Number|Literal|Bool)\s*\(/g],
    ["removedIdentifier", /\b[A-Za-z0-9_]*_Removed\b/g],
    ["removedJobHeadlessArchitecture", /\b(?:JobRunner|JobStore|UnityCliBatch|jobId|logsRef|supportsJob|job-deferred|directInvocationPolicy|frozenDeliveryPolicy|UNITY_EXE|discoverUnity)\b|-nographics\b|-runTests\b|-executeMethod\b|-quit\b/g],
    ["duplicateCliResultInterface", /(?:export\s+)?interface\s+CliResult\b/g],
    ["cliErrorIndexSignature", /interface\s+CliError\s*\{[\s\S]*?\[key:\s*string\]:\s*unknown;/g],
    ["curInstancesEnvelopeField", /\bcurInstances\b/g],
    ["failureDetailsFlatten", /(?:flatDetails|safeDetails|\.\.\.flatDetails)\b/g],
    ["emptyCSharpSchemaFallback", /builder\.Append\("\{\}"\)/g],
    ["staleWording", /(?:\bStage\s+[0-9]+\b|阶段\s*[0-9一二三四五六七八九十]+|迁移自|\bmigrated\b|\blegacy\s+(?:command|commands|context|output|payload|compatibility|adapter|fallback|path|implementation|form|api|schema|behavior)\b|旧行为|兼容入口|临时桥接|后续删除|\bdeprecated\b)/gi]
  ];
  for (const [id, pattern] of patternChecks) {
    const auditedRoots = id === "manualJsonParsers"
      ? legacyPatternRoots.filter((entry) => entry.path !== "unitypkg/Editor/Infrastructure/UnityCliSerializer.cs")
      : id === "removedJobHeadlessArchitecture"
        ? legacyPatternRoots.filter((entry) => /^(?:src|unitypkg|extensions|docs|skills)\//.test(entry.path))
      : id === "legacyCompatibilityFallback"
        ? legacyPatternRoots.filter((entry) => entry.path.endsWith(".cs"))
        : id === "probuilderSemanticDegradation"
          ? legacyPatternRoots.filter((entry) => entry.path.startsWith("extensions/probuilder/unitypkg/Editor/ProBuilder/") && entry.path.endsWith(".cs"))
          : legacyPatternRoots;
    const matches = auditedRoots
      .map((entry) => ({ path: entry.path, count: countMatches(entry.text, pattern) }))
      .filter((entry) => entry.count > 0);
    checks.push({ id, expected: 0, actual: matches.reduce((sum, entry) => sum + entry.count, 0), files: matches });
  }

  const batchModeResiduals = searchRoots
    .filter((entry) => /^(?:src|unitypkg|extensions|docs|skills)\//.test(entry.path))
    .filter((entry) => !readOnlyProcessObservationBatchModeFilterAllowlist.has(entry.path))
    .map((entry) => ({ path: entry.path, count: countMatches(entry.text, /-batchmode\b/g) }))
    .filter((entry) => entry.count > 0);
  checks.push({
    id: "unityBatchModeProductPath",
    expected: 0,
    actual: batchModeResiduals.reduce((sum, entry) => sum + entry.count, 0),
    files: batchModeResiduals
  });

  const removedHeadlessRuntime = searchRoots
    .filter((entry) => /^(?:src|unitypkg|extensions)\//.test(entry.path))
    .map((entry) => ({
      path: entry.path,
      count: countMatches(entry.text, /\b(?:JobRunner|JobStore|UnityCliBatch|discoverUnity|UNITY_EXE|__run-job)\b|-nographics\b|-executeMethod\b/g)
        + (readOnlyProcessObservationBatchModeFilterAllowlist.has(entry.path) ? 0 : countMatches(entry.text, /-batchmode\b/g))
    }))
    .filter((entry) => entry.count > 0);
  checks.push({
    id: "removedHeadlessRuntime",
    expected: 0,
    actual: removedHeadlessRuntime.reduce((sum, entry) => sum + entry.count, 0),
    files: removedHeadlessRuntime
  });

  const contractResidualSpecs = [
    {
      id: "materialSetFieldAliases",
      path: "unitypkg/Editor/Tools/material/set.cs",
      pattern: /public\s+string\s+(?:Material|Color|FloatValue|Vector|Texture)\s*\{/g
    },
    {
      id: "objectPatchDotCompatibility",
      path: "unitypkg/Editor/Infrastructure/UnityCliObjectPatch.cs",
      pattern: /向后兼容|同时兼容\s*dot|a\.b[^\r\n]*兼容|var\s+sep\s*=\s*path\.IndexOf/g
    },
    {
      id: "legacyDirectUiDocumentRoute",
      path: "src/bridge/index.ts",
      pattern: /command\s*===\s*["']ui\.document\.info["']/g
    }
  ];
  for (const spec of contractResidualSpecs) {
    const entry = searchRoots.find((candidate) => candidate.path === spec.path);
    const actual = entry ? countMatches(entry.text, spec.pattern) : 0;
    checks.push({ id: spec.id, expected: 0, actual, files: actual > 0 ? [{ path: spec.path, count: actual }] : [] });
  }

  const probuilderMissingSideEffects = searchRoots
    .filter((entry) => entry.path.startsWith("extensions/probuilder/unitypkg/Editor/ProBuilder/") && /:\s*IUnityCliTool\s*</.test(entry.text))
    .filter((entry) => /\bUndo\.|MarkSceneDirty\s*\(/.test(entry.text) && !/\bSideEffects\s*=/.test(entry.text))
    .map((entry) => ({ path: entry.path, count: 1 }));
  checks.push({
    id: "probuilderMutationSideEffects",
    expected: 0,
    actual: probuilderMissingSideEffects.length,
    files: probuilderMissingSideEffects
  });

  const csharpActionRouters = csharpFiles
    .map((path) => ({
      path: repoPath(path),
      count: namedTypeBodies(readText(path), "Request").filter((body) => /\bpublic\s+string\s+Action\s*\{\s*get\s*;\s*set\s*;\s*\}/.test(body)).length
    }))
    .filter((entry) => entry.count > 0);
  const tsActionRouters = allTextFiles
    .filter((path) => /[\\/]src[\\/]tools[\\/].*\.ts$/.test(path))
    .map((path) => ({
      path: repoPath(path),
      count: namedTypeBodies(readText(path), "Request").filter((body) => /\baction\??\s*:/.test(body)).length
    }))
    .filter((entry) => entry.count > 0);
  checks.push({
    id: "actionRouterRequest",
    expected: 0,
    actual: [...csharpActionRouters, ...tsActionRouters].reduce((sum, entry) => sum + entry.count, 0),
    files: [...csharpActionRouters, ...tsActionRouters]
  });

  const productionPlanReferences = searchRoots
    .filter((entry) => /^(?:src|unitypkg|extensions|docs|skills)\//.test(entry.path))
    .map((entry) => ({ path: entry.path, count: countMatches(entry.text, /\.plan[\\/]/g) }))
    .filter((entry) => entry.count > 0);
  checks.push({
    id: "productionPlanReference",
    expected: 0,
    actual: productionPlanReferences.reduce((sum, entry) => sum + entry.count, 0),
    files: productionPlanReferences
  });

  const machinePathReferences = searchRoots
    .filter((entry) => /^(?:src|docs|skills)\//.test(entry.path))
    .map((entry) => ({
      path: entry.path,
      count: countMatches(entry.text, /[A-Za-z]:[\\/](?:workspace[\\/]|Program Files[\\/]Unity[\\/])/g)
    }))
    .filter((entry) => entry.count > 0);
  checks.push({
    id: "hardcodedMachinePath",
    expected: 0,
    actual: machinePathReferences.reduce((sum, entry) => sum + entry.count, 0),
    files: machinePathReferences
  });

  const executionFiles = csharpFiles.filter((path) => /\/(ProjectTools|Infrastructure|Bridge|Tools|Samples)\//.test(`/${repoPath(path)}`));
  const cancellationMatches = executionFiles
    .map((path) => ({ path: repoPath(path), count: countMatches(readText(path), /\bCancellationToken\.None\b/g) }))
    .filter((entry) => entry.count > 0);
  checks.push({
    id: "executionCancellationTokenNone",
    expected: 0,
    actual: cancellationMatches.reduce((sum, entry) => sum + entry.count, 0),
    files: cancellationMatches
  });

  const updateMatches = csharpFiles
    .map((path) => ({ path: repoPath(path), count: countMatches(readText(path), /EditorApplication\.update\s*\+=/g) }))
    .filter((entry) => entry.count > 0);
  checks.push({
    id: "editorUpdateRegistrations",
    expected: 1,
    actual: updateMatches.reduce((sum, entry) => sum + entry.count, 0),
    files: updateMatches
  });

  const generatedSchemaPaths = [join(ROOT, "src", "tools", ".generated-schemas.ts")];
  const emptySchemaMatches = generatedSchemaPaths
    .filter(existsSync)
    .map((path) => ({
      path: repoPath(path),
      count: countMatches(readText(path), /(?:items":\{\}|inject(?:Tool|Context)Schema\("[^"]+",\s*\{\}\s*[,)]?)/g)
    }))
    .filter((entry) => entry.count > 0);
  checks.push({
    id: "implicitEmptySchemas",
    expected: 0,
    actual: emptySchemaMatches.reduce((sum, entry) => sum + entry.count, 0),
    files: emptySchemaMatches
  });

  checks.push({ id: "multiCapabilityFiles", expected: 0, actual: multiCapabilityFiles.length, files: multiCapabilityFiles });

  const legacyJsonWriterMatches = csharpFiles
    .map((path) => ({
      path: repoPath(path),
      count: countMatches(readText(path), /\bUnityCliJsonWriter\b|\.(?:Prop(?:Array|Object|Raw|Null)?|RawValue)\s*\(/g)
    }))
    .filter((entry) => entry.count > 0);
  checks.push({
    id: "legacyJsonWriter",
    expected: 0,
    actual: legacyJsonWriterMatches.reduce((sum, entry) => sum + entry.count, 0),
    files: legacyJsonWriterMatches
  });

  const businessCapabilityFiles = csharpFiles.filter((path) => /[\\/](?:Tools|Contexts)[\\/]/.test(path));
  const manualBusinessJsonMatches = businessCapabilityFiles
    .map((path) => ({
      path: repoPath(path),
      count: countMatches(
        readText(path),
        /\.(?:Prop(?:Array|Object|Raw|Null)?|RawJsonArg)\s*\(|\bJsonRawProperty\s*\(|\bFindJson(?:String|Value)End\s*\(|\bJsonObjectPropertyNames\s*\(|Regex\.Matches\([^\n]*json|\bUnityCliSerializer\.(?:Parse|Deserialize)\s*\(/gi
      )
    }))
    .filter((entry) => entry.count > 0);
  checks.push({
    id: "manualBusinessJson",
    expected: 0,
    actual: manualBusinessJsonMatches.reduce((sum, entry) => sum + entry.count, 0),
    files: manualBusinessJsonMatches
  });

  const businessJsonRoundTrips = businessCapabilityFiles
    .map((path) => ({
      path: repoPath(path),
      count: countMatches(readText(path), /UnityCliSerializer\.Deserialize\s*\([^;\n]*UnityCliSerializer\.Serialize\s*\(|UnityCliSerializer\.Deserialize\s*\(\s*[A-Za-z0-9_]+\s*,\s*typeof\s*\(/g)
    }))
    .filter((entry) => entry.count > 0);
  checks.push({
    id: "businessJsonRoundTrips",
    expected: 0,
    actual: businessJsonRoundTrips.reduce((sum, entry) => sum + entry.count, 0),
    files: businessJsonRoundTrips
  });

  const forbiddenDirectories = ["src/context"].filter((path) => existsSync(join(ROOT, path)));
  checks.push({ id: "forbiddenDirectories", expected: 0, actual: forbiddenDirectories.length, files: forbiddenDirectories.map((path) => ({ path, count: 1 })) });

  const forbiddenFiles = [
    "src/tools.ts",
    "src/docs-reflect.ts",
    "tmp-run-tests.ps1",
    "tools/clean-verification-contexts.mjs"
  ].filter((path) => existsSync(join(ROOT, path)));
  checks.push({ id: "forbiddenFiles", expected: 0, actual: forbiddenFiles.length, files: forbiddenFiles.map((path) => ({ path, count: 1 })) });

  const extensionMetadataFiles = collectFiles(join(ROOT, "extensions"), (path) => /[\\/]tools[\\/]metadata[\\/].+\.ts$/.test(path)).map(repoPath);
  checks.push({
    id: "extensionMetadataSources",
    expected: 0,
    actual: extensionMetadataFiles.length,
    files: extensionMetadataFiles.map((path) => ({ path, count: 1 }))
  });

  const extensionDomainCopies = ["cinemachine", "hdrp", "probuilder", "urp", "vfx-graph", "vfx"]
    .flatMap((name) => [join(ROOT, "src", "bridge", "domains", `${name}.ts`), join(ROOT, "src", "bridge", "domains", `${name}-tool.ts`)])
    .filter(existsSync)
    .map(repoPath);
  checks.push({
    id: "coreExtensionBridgeCopies",
    expected: 0,
    actual: extensionDomainCopies.length,
    files: extensionDomainCopies.map((path) => ({ path, count: 1 }))
  });

  return checks;
}

const contractPath = join(ROOT, "contracts", "command-contract.json");
if (!existsSync(contractPath)) {
  throw new Error("contracts/command-contract.json 不存在；请先运行 npm run build。");
}

const contract = JSON.parse(readText(contractPath));
const tsTools = generatedNames(join(ROOT, "src", "tools", ".generated-schemas.ts"), "injectToolSchema");
const tsToolSet = new Set(tsTools);
const { manifests, toolOwners } = loadExtensionOwnership();

const capabilities = contract.commands.map((entry) => {
  const name = entry.bridgeCommand;
  const extensionOwners = toolOwners.get(name) ?? [];
  let owner = "internal";
  if (tsToolSet.has(name)) owner = "ts-local";
  else if (extensionOwners.length === 1) owner = `extension:${extensionOwners[0]}`;
  else if (extensionOwners.length > 1) owner = "conflict";
  else if (entry.csharp_handler === "present") owner = "built-in";
  return { ...entry, name, owner };
});

const csharpFiles = [
  ...collectFiles(join(ROOT, "unitypkg"), (path) => path.endsWith(".cs")),
  ...collectFiles(join(ROOT, "extensions"), (path) => path.endsWith(".cs"))
];
const capabilityFiles = csharpFiles.map(csharpCapabilityFile).filter((entry) => entry.toolCount > 0);
const multiCapabilityFiles = capabilityFiles.filter((entry) => entry.toolCount > 1);

const textRoots = ["src", "unitypkg", "extensions", "tests", "docs", "skills", "contracts", "schemas", "scripts"];
const allTextFiles = textRoots.flatMap((root) => collectFiles(join(ROOT, root), (path) => /\.(?:cs|ts|js|mjs|cjs|json|md)$/.test(path)));
const checks = residualChecks(allTextFiles, csharpFiles, multiCapabilityFiles);

const expectedCompiledInventory = collectPublicToolInventory(ROOT)
  .filter((entry) => entry.descriptorOwner !== "ts-local")
  .map((entry) => [entry.name, entry.descriptorOwner, entry.assemblyName, entry.implementationType]);
const generatedInventoryPath = join(ROOT, "unitypkg", "Editor", "Generated", "UnityCliToolCompletionRegistry.g.cs");
const generatedInventoryRows = existsSync(generatedInventoryPath)
  ? [...readText(generatedInventoryPath).matchAll(/new UnityCliToolInventoryEntry\("([^"]+)", "([^"]+)", "([^"]+)", "([^"]+)"\)/g)]
      .map((match) => [match[1], match[2], match[3], match[4]])
  : [];
const compiledInventoryMismatch = JSON.stringify(expectedCompiledInventory) === JSON.stringify(generatedInventoryRows)
  ? []
  : [{ path: repoPath(generatedInventoryPath), count: 1, expected: `${expectedCompiledInventory.length} exact generated Tool owner rows` }];
checks.push({ id: "generatedCompiledToolInventory", expected: 0, actual: compiledInventoryMismatch.length, files: compiledInventoryMismatch });

const builtInToolLayoutMismatches = capabilityFiles
  .filter((entry) => entry.toolCount === 1 && entry.path.startsWith("unitypkg/Editor/Tools/"))
  .flatMap((entry) => {
    const name = entry.names[0];
    if (!name || !entry.group) return [{ path: entry.path, count: 1, expected: "descriptor Name and Group" }];
    const segments = name.split(".");
    const toolSegments = segments[0] === entry.group ? segments.slice(1) : segments;
    const expected = ["unitypkg", "Editor", "Tools", entry.group, ...toolSegments].join("/") + ".cs";
    return entry.path === expected ? [] : [{ path: entry.path, count: 1, expected }];
  });
checks.push({
  id: "builtInToolFileLayout",
  expected: 0,
  actual: builtInToolLayoutMismatches.length,
  files: builtInToolLayoutMismatches
});

const packageSamplesCapabilities = collectFiles(join(ROOT, "unitypkg", "Editor", "Samples"), (path) => path.endsWith(".cs"))
  .map(csharpCapabilityFile)
  .filter((entry) => entry.toolCount > 0);
checks.push({
  id: "packageSamplesCapabilities",
  expected: 0,
  actual: packageSamplesCapabilities.length,
  files: packageSamplesCapabilities.map((entry) => ({ path: entry.path, count: entry.toolCount }))
});

const internalMetaRoutes = new Set([
  "project-tool.groups", "project-tool.summary", "project-tool.describe", "project-tool.list", "project-tool.run",
  "runtime-tool.groups", "runtime-tool.summary", "runtime-tool.describe", "runtime-tool.list", "runtime-tool.run",
  "code.exec"
]);
const contractCsharpTools = new Set(contract.commands
  .filter((entry) => entry.csharp_handler === "present" && !internalMetaRoutes.has(entry.bridgeCommand))
  .map((entry) => entry.bridgeCommand));
const csharpDescriptorTools = new Set(capabilityFiles
  .filter((entry) => entry.toolCount === 1)
  .flatMap((entry) => entry.names));
const missingCsharpDescriptors = [...contractCsharpTools]
  .filter((name) => !csharpDescriptorTools.has(name))
  .sort()
  .map((name) => ({ path: "contracts/command-contract.json", count: 1, expected: `C# descriptor for ${name}` }));
const unknownCsharpDescriptors = [...csharpDescriptorTools]
  .filter((name) => !contractCsharpTools.has(name))
  .sort()
  .map((name) => ({ path: "unitypkg or extensions", count: 1, expected: `contract entry for ${name}` }));
checks.push({
  id: "csharpToolContractParity",
  expected: 0,
  actual: missingCsharpDescriptors.length + unknownCsharpDescriptors.length,
  files: [...missingCsharpDescriptors, ...unknownCsharpDescriptors]
});

const staleTestNarrativeAllowlist = new Map();
const staleTestNarrative = collectFiles(join(ROOT, "tests"), (path) => /\.(?:ts|js)$/.test(path))
  .flatMap((path) => {
    const text = readText(path);
    const count = countMatches(text, /\b(?:removed aliases|old tool names|no longer|legacy command form|migrated|new shape)\b/gi);
    return count > 0 && !staleTestNarrativeAllowlist.has(repoPath(path))
      ? [{ path: repoPath(path), count }]
      : [];
  });
checks.push({
  id: "staleTestNarrative",
  expected: 0,
  actual: staleTestNarrative.reduce((sum, entry) => sum + entry.count, 0),
  files: staleTestNarrative
});

const duplicateToolClaims = [...toolOwners.entries()]
  .filter(([, owners]) => owners.length > 1)
  .map(([name, owners]) => ({ name, owners }));
const csharpToolNames = capabilityFiles.filter((entry) => entry.toolCount === 1).flatMap((entry) => entry.names);
const publicToolOwners = new Map();
for (const name of tsTools) publicToolOwners.set(name, [...(publicToolOwners.get(name) ?? []), "ts-local"]);
for (const name of csharpToolNames) {
  const extensionOwners = toolOwners.get(name) ?? [];
  publicToolOwners.set(name, [...(publicToolOwners.get(name) ?? []), extensionOwners.length === 1 ? `extension:${extensionOwners[0]}` : "built-in"]);
}
const duplicatePublicToolOwners = [...publicToolOwners.entries()]
  .filter(([, owners]) => owners.length > 1)
  .map(([name, owners]) => ({ path: name, count: owners.length, expected: "one public Tool owner", owners }));
checks.push({ id: "duplicatePublicToolOwners", expected: 0, actual: duplicatePublicToolOwners.length, files: duplicatePublicToolOwners });

const controlFields = new Set(["dryRun", "wait", "waitTimeoutMs", "timeoutMs", "yes", "retry", "bridgeEndpoint", "instance"]);
const schemaControlFields = [];
for (const line of readText(join(ROOT, "src", "tools", ".generated-schemas.ts")).split(/\r?\n/)) {
  const match = line.match(/^injectToolSchema\("([^"]+)", /);
  if (!match) continue;
  const inputText = line.slice(match[0].length);
  let depth = 0;
  let end = -1;
  for (let index = 0; index < inputText.length; index += 1) {
    if (inputText[index] === "{") depth += 1;
    else if (inputText[index] === "}" && --depth === 0) { end = index + 1; break; }
  }
  if (end < 0) continue;
  const inputSchema = JSON.parse(inputText.slice(0, end));
  for (const field of Object.keys(inputSchema.properties ?? {})) {
    if (controlFields.has(field)) schemaControlFields.push({ path: match[1], count: 1, expected: `no ${field} in Request schema` });
  }
}
checks.push({ id: "tsRequestExecutionFields", expected: 0, actual: schemaControlFields.length, files: schemaControlFields });

/** 精确按花括号深度提取 Request DTO，避免 Result 的 dry-run 输出字段被误报。 */
function requestExecutionFields(path) {
  const text = readText(path);
  const matches = [];
  const declaration = /(?:public\s+)?(?:sealed\s+)?class\s+Request\s*\{/g;
  for (const match of text.matchAll(declaration)) {
    const start = match.index + match[0].length;
    let depth = 1;
    let end = start;
    for (; end < text.length && depth > 0; end += 1) {
      if (text[end] === "{") depth += 1;
      else if (text[end] === "}") depth -= 1;
    }
    if (depth !== 0) continue;
    const body = text.slice(start, end - 1);
    const count = countMatches(body, /\b(?:DryRun|TimeoutMs|Job|Yes)\s*\{\s*get;\s*set;\s*\}/g);
    if (count > 0) matches.push({ path: repoPath(path), count, expected: "Request DTO has no execution fields" });
  }
  return matches;
}

const csharpRequestExecutionFields = csharpFiles.flatMap(requestExecutionFields);
checks.push({ id: "csharpRequestExecutionFields", expected: 0, actual: csharpRequestExecutionFields.reduce((sum, entry) => sum + entry.count, 0), files: csharpRequestExecutionFields });

const csharpRequestExecutionAssignments = csharpFiles
  .map((path) => ({ path: repoPath(path), count: countMatches(readText(path), /new\s+Request\s*\{[^}]*\b(?:DryRun|TimeoutMs|Job|Yes)\s*=/g) }))
  .filter((entry) => entry.count > 0);
checks.push({ id: "csharpRequestExecutionAssignments", expected: 0, actual: csharpRequestExecutionAssignments.reduce((sum, entry) => sum + entry.count, 0), files: csharpRequestExecutionAssignments });

const legacyContextAuthoring = allTextFiles
  .map((path) => ({ path: repoPath(path), count: countMatches(readText(path), /\bdefineContext\s*(?:<|\()|\bIUnityCliContext\s*</g) }))
  .filter((entry) => entry.count > 0);
checks.push({ id: "legacyContextAuthoring", expected: 0, actual: legacyContextAuthoring.reduce((sum, entry) => sum + entry.count, 0), files: legacyContextAuthoring });

const recoveryParserMatches = collectFiles(join(ROOT, "src"), (path) => path.endsWith(".ts"))
  .map((path) => ({ path: repoPath(path), count: countMatches(readText(path), /recoverPowerShell|parseRecovered|splitRecovered|cleanRecovered/g) }))
  .filter((entry) => entry.count > 0);
checks.push({ id: "adHocRecoveryParsers", expected: 0, actual: recoveryParserMatches.reduce((sum, entry) => sum + entry.count, 0), files: recoveryParserMatches });

const report = {
  version: 1,
  generatedAt: new Date().toISOString(),
  baseline: {
    contractCommands: capabilities.length,
    tsLocalTools: tsTools.length,
    extensionCount: manifests.length,
    extensionTools: [...toolOwners.keys()].length,
    csharpCapabilityFiles: capabilityFiles.length,
    csharpToolImplementations: capabilityFiles.reduce((sum, entry) => sum + entry.toolCount, 0),
    multiCapabilityFiles: multiCapabilityFiles.length,
    failedChecks: checks.filter((check) => check.actual !== check.expected).length
  },
  extensionManifests: manifests,
  ownershipConflicts: { tools: duplicateToolClaims },
  capabilities,
  csharpCapabilityFiles: capabilityFiles,
  checks
};

const json = `${JSON.stringify(report, null, 2)}\n`;
if (outputPath) {
  const absoluteOutput = resolve(ROOT, outputPath);
  mkdirSync(dirname(absoluteOutput), { recursive: true });
  writeFileSync(absoluteOutput, json, "utf8");
}
console.log(JSON.stringify({ ok: checks.every((check) => check.actual === check.expected), output: outputPath ?? null, ...report.baseline }, null, 2));

if (strict && checks.some((check) => check.actual !== check.expected)) {
  process.exitCode = 1;
}

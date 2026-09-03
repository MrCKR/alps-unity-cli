import fs from "node:fs";
import path from "node:path";

/** 递归列出指定目录下的源码文件。 */
function filesUnder(root, relativePath, extension) {
  const base = path.join(root, relativePath);
  if (!fs.existsSync(base)) return [];
  const result = [];
  const visit = (directory) => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const fullPath = path.join(directory, entry.name);
      if (entry.isDirectory()) visit(fullPath);
      else if (entry.name.endsWith(extension)) result.push(fullPath);
    }
  };
  visit(base);
  return result.sort();
}

/** 将路径统一为仓库相对的正斜杠形式。 */
function relativeSource(root, file) {
  return path.relative(root, file).replaceAll("\\", "/");
}

/** 提取 C# Tool 的公开 descriptor 身份。 */
function collectCSharpTools(root) {
  const files = [
    ...filesUnder(root, "unitypkg/Editor/Tools", ".cs"),
    ...filesUnder(root, "extensions", ".cs"),
  ];
  const tools = [];
  for (const file of files) {
    const sourceText = fs.readFileSync(file, "utf8");
    if (!/\bIUnityCliTool\s*</.test(sourceText)) continue;
    const name = sourceText.match(/\bName\s*=\s*"([^"]+)"/)?.[1];
    if (!name) throw new Error(`${relativeSource(root, file)}: IUnityCliTool 缺少公开 Name`);
    const source = relativeSource(root, file);
    const extensionMatch = source.match(/^extensions\/([^/]+)\//);
    const implementationMatch = sourceText.match(/\b(?:class|struct)\s+([A-Za-z0-9_]+)\s*:\s*IUnityCliTool\s*</);
    if (!implementationMatch) throw new Error(`${source}: IUnityCliTool implementation type is missing`);
    const namespaceMatch = sourceText.match(/\bnamespace\s+([A-Za-z0-9_.]+)\s*\{/);
    const containingTypeMatch = implementationMatch.index !== undefined
      ? sourceText.slice(0, implementationMatch.index).match(/\b(?:class|struct)\s+([A-Za-z0-9_]+)\s*\{\s*$/m)
      : undefined;
    const namespaceName = namespaceMatch?.[1] ?? "";
    const containingType = containingTypeMatch?.[1];
    const implementationType = `${namespaceName ? `${namespaceName}.` : ""}${containingType ? `${containingType}+` : ""}${implementationMatch[1]}`;
    const assemblyName = extensionMatch ? readExtensionAssemblyName(root, extensionMatch[1]) : "UnityCli.Editor";
    tools.push({
      name,
      descriptorOwner: extensionMatch ? `extension:${extensionMatch[1]}` : "built-in",
      source,
      assemblyName,
      implementationType,
      handler: "bridge",
      asyncSignals: collectAsyncSignals(sourceText, "csharp"),
    });
  }
  return tools;
}

/** 读取 extension asmdef 的编译 assembly identity。 */
function readExtensionAssemblyName(root, extensionId) {
  const files = filesUnder(root, `extensions/${extensionId}/unitypkg`, ".asmdef");
  if (files.length !== 1) throw new Error(`extensions/${extensionId}: expected exactly one .asmdef, found ${files.length}`);
  const manifest = JSON.parse(fs.readFileSync(files[0], "utf8"));
  if (typeof manifest.name !== "string" || manifest.name.length === 0) throw new Error(`${relativeSource(root, files[0])}: asmdef name is required`);
  return manifest.name;
}

/** Extension manifest、asmdef 与源码 Tool/type 集合必须完全一致。 */
function assertExtensionInventory(root, csharpTools) {
  const extensionsDirectory = path.join(root, "extensions");
  if (!fs.existsSync(extensionsDirectory)) return;
  for (const entry of fs.readdirSync(extensionsDirectory, { withFileTypes: true }).filter((item) => item.isDirectory())) {
    const manifestPath = extensionManifestPath(root, entry.name);
    if (!fs.existsSync(manifestPath)) continue;
    const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
    const expectedTools = (manifest.contributions?.tools ?? []).map((item) => item.name).sort();
    const expectedTypes = (manifest.contributions?.csharpEntries ?? []).map((item) => item.type).sort();
    const actual = csharpTools.filter((tool) => tool.descriptorOwner === `extension:${manifest.id}`);
    compareExactSet(`extensions/${entry.name} Tool inventory`, expectedTools, actual.map((tool) => tool.name).sort());
    compareExactSet(`extensions/${entry.name} implementation inventory`, expectedTypes, actual.map((tool) => tool.implementationType).sort());
    const assemblyNames = [...new Set(actual.map((tool) => tool.assemblyName))];
    if (actual.length > 0 && (assemblyNames.length !== 1 || assemblyNames[0] !== readExtensionAssemblyName(root, entry.name))) {
      throw new Error(`extensions/${entry.name}: exact asmdef identity does not match Tool inventory`);
    }
  }
}

function extensionRoot(root) {
  return path.join(root, "extensions");
}

function extensionManifestPath(root, extensionId) {
  return path.join(extensionRoot(root), extensionId, "extension.json");
}

function compareExactSet(label, expected, actual) {
  if (JSON.stringify(expected) !== JSON.stringify(actual)) {
    throw new Error(`${label} mismatch: expected=${JSON.stringify(expected)} actual=${JSON.stringify(actual)}`);
  }
}

/** 提取 TS defineTool 的公开 descriptor 身份。 */
function collectTypeScriptTools(root) {
  const tools = [];
  for (const file of filesUnder(root, "src/tools", ".ts")) {
    const source = relativeSource(root, file);
    if (source.includes("/metadata/") || source.endsWith("/define.ts") || source.includes("/.generated-")) continue;
    const sourceText = fs.readFileSync(file, "utf8");
    if (!/\bdefineTool(?:<[^>]+>)?\s*\(/.test(sourceText)) continue;
    const name = sourceText.match(/\bname:\s*"([^"]+)"/)?.[1];
    if (!name) throw new Error(`${source}: defineTool 缺少公开 name`);
    const handler = sourceText.match(/\bhandler:\s*"([^"]+)"/)?.[1] ?? "offline";
    tools.push({
      name,
      descriptorOwner: "ts-local",
      source,
      handler,
      asyncSignals: collectAsyncSignals(sourceText, "typescript"),
    });
  }
  return tools;
}

/** 记录跨帧、callback 和外部 operation 信号，供 completion contract 审计。 */
function collectAsyncSignals(sourceText, language) {
  const patterns = language === "csharp"
    ? [
        ["value-task", /\bValueTask\s*</],
        ["await", /\bawait\b/],
        ["scheduler-wait", /\bctx\.Wait\b/],
        ["callback", /\b(callback|Callback|TakeSnapshot)\b/],
        ["package-manager", /\bPackageManager\.Client\b/],
        ["compilation-pipeline", /\bCompilationPipeline\b/],
        ["async-unity-operation", /\b(BakeAsync|RequestScriptCompilation)\b/],
      ]
    : [
        ["promise", /\bPromise\s*</],
        ["async", /\basync\b/],
        ["await", /\bawait\b/],
        ["callback", /\b(callback|Callback)\b/],
      ];
  return patterns.filter(([, pattern]) => pattern.test(sourceText)).map(([name]) => name);
}

/** 旧 Context authoring 重新出现时，所有 inventory/generator consumer 必须 fail closed。 */
function assertNoLegacyContextAuthoring(root) {
  const files = [
    ...filesUnder(root, "src", ".ts"),
    ...filesUnder(root, "unitypkg/Editor", ".cs"),
    ...filesUnder(root, "extensions", ".cs"),
  ];
  const forbidden = /\bdefineContext\s*(?:<|\()|\bIUnityCliContext\s*</;
  for (const file of files) {
    const sourceText = fs.readFileSync(file, "utf8");
    if (forbidden.test(sourceText)) throw new Error(`${relativeSource(root, file)}: legacy Context authoring is forbidden; define a Tool instead`);
  }
}

/** 返回全部产品 public Tool；同名 Tool 直接 fail closed。 */
export function collectPublicToolInventory(root) {
  assertNoLegacyContextAuthoring(root);
  const csharpTools = collectCSharpTools(root);
  assertExtensionInventory(root, csharpTools);
  const tools = [...csharpTools, ...collectTypeScriptTools(root)]
    .sort((left, right) => left.name.localeCompare(right.name) || left.source.localeCompare(right.source));
  const seen = new Map();
  for (const tool of tools) {
    const previous = seen.get(tool.name);
    if (previous) throw new Error(`duplicate public Tool ${tool.name}: ${previous} / ${tool.source}`);
    seen.set(tool.name, tool.source);
  }
  return tools;
}

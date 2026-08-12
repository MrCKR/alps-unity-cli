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
    tools.push({
      name,
      descriptorOwner: extensionMatch ? `extension:${extensionMatch[1]}` : "built-in",
      source,
      handler: "bridge",
      asyncSignals: collectAsyncSignals(sourceText, "csharp"),
    });
  }
  return tools;
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
  const tools = [...collectCSharpTools(root), ...collectTypeScriptTools(root)]
    .sort((left, right) => left.name.localeCompare(right.name) || left.source.localeCompare(right.source));
  const seen = new Map();
  for (const tool of tools) {
    const previous = seen.get(tool.name);
    if (previous) throw new Error(`duplicate public Tool ${tool.name}: ${previous} / ${tool.source}`);
    seen.set(tool.name, tool.source);
  }
  return tools;
}

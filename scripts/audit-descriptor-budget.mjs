// 离线的 Tool descriptor 文本预算扫描：只读源码，不依赖 Unity 实例。
// 预算口径见 .pi/skills/unity-cli-capability/SKILL.md《Descriptor 预算与归属》。
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

const root = process.cwd();
const BUDGET = { descriptionFail: 300, descriptionWarn: 150, notesFail: 2000 };
const TOKEN_RATIO = 3.9;

/** 估算 token：CJK 字符按 1 计，其余按固定字符比折算，与实测口径保持一致。 */
export function estimateTokens(text) {
  if (!text) return 0;
  const cjk = (text.match(/[\u3000-\u9fff\uff00-\uffef]/g) ?? []).length;
  return Math.round(cjk + (text.length - cjk) / TOKEN_RATIO);
}

/** 解开 C# 普通字符串字面量的转义序列。 */
function unescapeCStyle(raw) {
  return raw.replace(/\\(u[0-9a-fA-F]{4}|.)/g, (_match, escape) => {
    if (escape.startsWith("u")) return String.fromCharCode(parseInt(escape.slice(1), 16));
    switch (escape) {
      case "n": return "\n";
      case "r": return "\r";
      case "t": return "\t";
      case "0": return "\0";
      default: return escape;
    }
  });
}

/** 从字符串字面量集合中提取 C# 数组初始化内容；用字符扫描避免值里的括号误判。 */
function readArrayLiteral(source, openBraceIndex) {
  let depth = 0;
  let inString = false;
  let escaped = false;
  for (let index = openBraceIndex; index < source.length; index += 1) {
    const char = source[index];
    if (inString) {
      if (escaped) escaped = false;
      else if (char === "\\") escaped = true;
      else if (char === "\"") inString = false;
      continue;
    }
    if (char === "\"") { inString = true; continue; }
    if (char === "{") depth += 1;
    else if (char === "}") {
      depth -= 1;
      if (depth === 0) return source.slice(openBraceIndex + 1, index);
    }
  }
  return undefined;
}

/** 按顶层逗号切分 C# 数组体，保留元素原文；字符串与嵌套括号内的逗号不参与切分。 */
function splitTopLevelElements(body) {
  const elements = [];
  let current = "";
  let depth = 0;
  let inString = false;
  let escaped = false;
  for (const char of body) {
    if (inString) {
      current += char;
      if (escaped) escaped = false;
      else if (char === "\\") escaped = true;
      else if (char === "\"") inString = false;
      continue;
    }
    if (char === "\"") { inString = true; current += char; continue; }
    if (char === "(" || char === "{" || char === "[") { depth += 1; current += char; continue; }
    if (char === ")" || char === "}" || char === "]") { depth -= 1; current += char; continue; }
    if (char === "," && depth === 0) { elements.push(current); current = ""; continue; }
    current += char;
  }
  elements.push(current);
  return elements.filter((element) => element.trim().length > 0);
}

/** 取出一段 C# 源码里的全部字符串字面量；一个都没有就说明该元素无法离线核对。 */
function literalLines(source) {
  return [...source.matchAll(/"((?:[^"\\]|\\.)*)"/g)].map((match) => unescapeCStyle(match[1]));
}

/** 展开一段 notes 元素：收集内联字面量，并把零参数 static string 方法引用的文本按单一来源展开。 */
function resolveNoteElement(element, methods) {
  const lines = literalLines(element);
  const unresolvedCalls = [];
  const withoutStrings = element.replace(/"(?:[^"\\]|\\.)*"/g, '""');
  for (const match of withoutStrings.matchAll(/\b([A-Za-z_][\w.]*)\s*\(\s*\)/g)) {
    const name = match[1].split(".").pop();
    const resolved = methods?.get(name);
    if (resolved) lines.push(...resolved);
    else unresolvedCalls.push(match[1] + "()");
  }
  return { lines, unresolvedCalls };
}

/** 取出 C# descriptor 的 name/group/description/notes 文本字段；notes 支持内联数组或共享常量。 */
export function readCSharpDescriptor(source, noteIndex, methods) {
  const name = source.match(/\bName\s*=\s*"((?:[^"\\]|\\.)*)"/);
  if (!name) return undefined;
  const group = source.match(/\bGroup\s*=\s*"((?:[^"\\]|\\.)*)"/);
  const description = source.match(/\bDescription\s*=\s*"((?:[^"\\]|\\.)*)"/);
  const notes = readNotes(source, noteIndex, methods);
  return {
    name: unescapeCStyle(name[1]),
    group: group ? unescapeCStyle(group[1]) : undefined,
    description: description ? unescapeCStyle(description[1]) : "",
    notes: notes.lines,
    unresolvedNotes: notes.unresolved,
    unresolvedNotesElements: notes.unresolvedElements,
    unresolvedNotesCalls: notes.unresolvedCalls,
  };
}

/** 读取 Notes 字段：内联数组按元素解析，每个元素及其方法引用都必须可离线核对；`Class.Member` 从共享常量索引取文本。 */
function readNotes(source, noteIndex, methods) {
  const inline = source.match(/\bNotes\s*=\s*new\s*\[\s*\]\s*\{/);
  if (inline) {
    const body = readArrayLiteral(source, inline.index + inline[0].length - 1);
    if (body === undefined) throw new Error("unterminated Notes array literal");
    const lines = [];
    const unresolvedElements = [];
    const unresolvedCalls = [];
    for (const element of splitTopLevelElements(body)) {
      const resolved = resolveNoteElement(element, methods);
      if (resolved.lines.length === 0 && resolved.unresolvedCalls.length === 0) unresolvedElements.push(element.trim());
      lines.push(...resolved.lines);
      unresolvedCalls.push(...resolved.unresolvedCalls);
    }
    return { lines, unresolved: undefined, unresolvedElements, unresolvedCalls };
  }
  const reference = source.match(/\bNotes\s*=\s*([A-Za-z_][\w.]*)\s*[,;]/);
  if (!reference) return { lines: [], unresolved: undefined, unresolvedElements: [], unresolvedCalls: [] };
  const resolved = noteIndex?.get(reference[1]) ?? noteIndex?.get(reference[1].split(".").pop());
  return resolved
    ? { lines: resolved, unresolved: undefined, unresolvedElements: [], unresolvedCalls: [] }
    : { lines: [], unresolved: reference[1], unresolvedElements: [], unresolvedCalls: [] };
}

/** 建立共享 notes 常量与零参数文本方法的索引；成员名必须全局唯一，不可解析的元素必须上报。 */
export function buildNoteIndex(files) {
  const index = new Map();
  const expressions = new Map();
  const methods = new Map();
  const failures = [];
  const sources = files.map((file) => ({ file, source: readFileSync(file, "utf8") }));
  // 先索引零参数 static string 方法，notes 元素才能按单一来源展开它们的字面量。
  for (const { source } of sources) {
    for (const match of source.matchAll(/(?:internal|public|private)\s+static\s+string\s+(\w+)\s*\(\s*\)/g)) {
      const braceIndex = source.indexOf("{", match.index + match[0].length);
      if (braceIndex < 0) continue;
      const body = readArrayLiteral(source, braceIndex);
      if (body === undefined) continue;
      const lines = literalLines(body);
      // 重名视为不可解析：无法证明同一名字只有一个文本来源。
      methods.set(match[1], methods.has(match[1]) || lines.length === 0 ? null : lines);
    }
  }
  for (const { file, source } of sources) {
    for (const match of source.matchAll(/(?:static\s+readonly|static)\s+string\[\]\s+(\w+)\s*=\s*new\s*\[\s*\]\s*\{/g)) {
      const body = readArrayLiteral(source, match.index + match[0].length - 1);
      if (body === undefined) throw new Error(`unterminated notes constant ${match[1]} in ${file}`);
      const lines = [];
      for (const element of splitTopLevelElements(body)) {
        const resolved = resolveNoteElement(element, methods);
        const where = `notes constant ${match[1]} in ${path.relative(root, file).replaceAll("\\", "/")}`;
        if (resolved.lines.length === 0 && resolved.unresolvedCalls.length === 0) failures.push(`${where}: element "${element.trim().slice(0, 60)}" cannot be resolved offline; keep notes text as string literals`);
        for (const call of resolved.unresolvedCalls) failures.push(`${where}: reference ${call} cannot be resolved offline; keep notes text as string literals`);
        lines.push(...resolved.lines);
      }
      if (index.has(match[1])) throw new Error(`duplicate notes constant ${match[1]}`);
      index.set(match[1], lines);
    }
    for (const match of source.matchAll(/(?:static\s+readonly|static)\s+string\[\]\s+(\w+)\s*=\s*([^;\n]+);/g)) {
      if (!expressions.has(match[1])) expressions.set(match[1], match[2]);
    }
  }
  // 拼接型常量按引用顺序展开；循环与未知引用留给上层报错。
  for (let round = 0; round < 8; round += 1) {
    let progressed = false;
    for (const [name, expression] of expressions) {
      if (index.has(name)) continue;
      const references = [...expression.matchAll(/\b([A-Za-z_]\w*)\b/g)].map((match) => match[1]).filter((id) => index.has(id));
      if (references.length === 0) continue;
      index.set(name, references.flatMap((id) => index.get(id)));
      progressed = true;
    }
    if (!progressed) break;
  }
  return { index, methods, failures };
}

/** 递归列出目标目录下的 .cs 文件，跳过 Unity 生成的中间目录。 */
function listCSharpFiles(directory) {
  const files = [];
  const walk = (current) => {
    for (const entry of readdirSync(current)) {
      if (entry === "obj" || entry === "bin") continue;
      const full = path.join(current, entry);
      const stat = statSync(full);
      if (stat.isDirectory()) walk(full);
      else if (entry.endsWith(".cs")) files.push(full);
    }
  };
  if (statSync(directory, { throwIfNoEntry: false })) walk(directory);
  return files;
}

/** 收集 C# 侧（内置与 Extension）descriptor 文本，连同共享 notes 常量的不可解析元素。 */
export function collectCSharpDescriptors() {
  const directories = [path.join(root, "unitypkg", "Editor", "Tools")];
  const extensionsRoot = path.join(root, "extensions");
  for (const entry of readdirSync(extensionsRoot, { throwIfNoEntry: false }) ?? []) {
    const candidate = path.join(extensionsRoot, entry, "unitypkg", "Editor");
    if (statSync(candidate, { throwIfNoEntry: false })) directories.push(candidate);
  }
  const { index: noteIndex, methods, failures } = buildNoteIndex([
    ...listCSharpFiles(path.join(root, "unitypkg")),
    ...(readdirSync(extensionsRoot, { throwIfNoEntry: false }) ?? []).flatMap((entry) => {
      const candidate = path.join(extensionsRoot, entry, "unitypkg");
      return statSync(candidate, { throwIfNoEntry: false }) ? listCSharpFiles(candidate) : [];
    }),
  ]);
  const descriptors = [];
  for (const directory of directories) {
    for (const file of listCSharpFiles(directory)) {
      const parsed = readCSharpDescriptor(readFileSync(file, "utf8"), noteIndex, methods);
      if (!parsed) continue;
      descriptors.push({ ...parsed, source: path.relative(root, file).replaceAll("\\", "/") });
    }
  }
  return { descriptors, failures };
}

/** 收集 TypeScript 本地 Tool 的 descriptor 文本。 */
export async function collectTypeScriptDescriptors() {
  const candidates = ["dist/tools/metadata/index.js", "dist/src/tools/metadata/index.js"];
  const modulePath = candidates.map((candidate) => path.join(root, candidate)).find((candidate) => existsSync(candidate));
  if (!modulePath) throw new Error("compiled metadata is missing; run npm run build before scanning descriptor budget");
  const previous = process.env.UNITY_CLI_GENERATING_CONTRACT;
  process.env.UNITY_CLI_GENERATING_CONTRACT = "1";
  try {
    const module = await import(pathToFileURL(modulePath).href);
    return module.builtinToolMetadata().map((tool) => ({
      name: tool.name,
      group: tool.group,
      description: tool.description ?? "",
      notes: tool.notes ?? [],
      source: "src/tools",
    }));
  } finally {
    if (previous === undefined) delete process.env.UNITY_CLI_GENERATING_CONTRACT;
    else process.env.UNITY_CLI_GENERATING_CONTRACT = previous;
  }
}

/** 汇总预算结论；严格模式才把超限转为失败。 */
export async function auditDescriptorBudget() {
  const csharp = collectCSharpDescriptors();
  const tools = [...csharp.descriptors, ...(await collectTypeScriptDescriptors())].sort((left, right) => left.name.localeCompare(right.name));
  const failures = [...csharp.failures];
  const warnings = [];
  const rows = [];
  for (const tool of tools) {
    const descriptionTokens = estimateTokens(tool.description);
    const notesTokens = estimateTokens(tool.notes.join(" "));
    rows.push({ name: tool.name, group: tool.group, descriptionChars: tool.description.length, descriptionTokens, notesTokens, notesCount: tool.notes.length, source: tool.source });
    if (tool.unresolvedNotes) failures.push(`${tool.name}: notes reference ${tool.unresolvedNotes} cannot be resolved offline`);
    for (const element of tool.unresolvedNotesElements ?? []) failures.push(`${tool.name}: notes element "${element.slice(0, 60)}" cannot be resolved offline; keep notes text as string literals`);
    for (const call of tool.unresolvedNotesCalls ?? []) failures.push(`${tool.name}: notes reference ${call} cannot be resolved offline; keep notes text as string literals`);
    if (!tool.description) failures.push(`${tool.name}: descriptor is missing a description`);
    if (descriptionTokens > BUDGET.descriptionFail) failures.push(`${tool.name}: description ${descriptionTokens} tokens exceeds ${BUDGET.descriptionFail}`);
    if (notesTokens > BUDGET.notesFail) failures.push(`${tool.name}: notes ${notesTokens} tokens exceeds ${BUDGET.notesFail}`);
    if (descriptionTokens > BUDGET.descriptionWarn && descriptionTokens <= BUDGET.descriptionFail) warnings.push(`${tool.name}: description ${descriptionTokens} tokens exceeds the ${BUDGET.descriptionWarn}-token target`);
  }
  const total = rows.reduce((sum, row) => sum + row.descriptionTokens, 0);
  const sorted = [...rows].map((row) => row.descriptionTokens).sort((left, right) => left - right);
  return {
    budget: BUDGET,
    tools: rows,
    failures,
    warnings,
    summary: {
      toolCount: rows.length,
      descriptionTokens: total,
      descriptionMean: rows.length ? Math.round(total / rows.length) : 0,
      descriptionP90: sorted.length ? sorted[Math.floor(0.9 * (sorted.length - 1))] : 0,
      descriptionMax: sorted.length ? sorted[sorted.length - 1] : 0,
      notesTokens: rows.reduce((sum, row) => sum + row.notesTokens, 0),
    },
  };
}

const invokedDirectly = process.argv[1] && path.resolve(process.argv[1]) === path.resolve(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1"));
if (invokedDirectly) {
  const strict = process.argv.includes("--strict");
  const report = await auditDescriptorBudget();
  console.log(JSON.stringify(report, null, 2));
  if (strict && report.failures.length > 0) process.exitCode = 1;
}

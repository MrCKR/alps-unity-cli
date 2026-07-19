import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

interface StaticCapabilityCatalogFile {
  version: number;
  semantics: string;
  tools: Array<Record<string, unknown>>;
  contexts: Array<Record<string, unknown>>;
}

let cached: StaticCapabilityCatalogFile | undefined;

/**
 * 读取 package 自带的 built-in/Extension descriptor snapshot。
 * 该文件不包含 project-defined 能力，因此 offline、reload 与 blocked 状态均可安全读取。
 */
export function staticCapabilityCatalog(): StaticCapabilityCatalogFile {
  if (cached) return cached;
  const path = fileURLToPath(new URL("../contracts/static-capability-catalog.json", import.meta.url));
  const parsed = JSON.parse(readFileSync(path, "utf8")) as StaticCapabilityCatalogFile;
  validateCatalog(parsed);
  cached = parsed;
  return parsed;
}

/** 测试生成器幂等或替换 fixture 后可清除进程内 cache。 */
export function clearStaticCapabilityCatalogCache(): void {
  cached = undefined;
}

/** snapshot 结构异常必须在 discovery 边界 fail closed，禁止退回 live transport 补造 metadata。 */
function validateCatalog(catalog: StaticCapabilityCatalogFile): void {
  if (catalog.version !== 1 || !Array.isArray(catalog.tools) || !Array.isArray(catalog.contexts)) {
    throw new Error("Static capability catalog must be version 1 with tools and contexts arrays.");
  }
  validateEntries(catalog.tools, "Tool");
  validateEntries(catalog.contexts, "Context");
}

function validateEntries(entries: Array<Record<string, unknown>>, kind: string): void {
  const names = new Set<string>();
  for (const entry of entries) {
    const name = typeof entry?.name === "string" ? entry.name : "";
    const source = entry?.source;
    if (!name || (source !== "built-in" && source !== "extension")) {
      throw new Error(`Static ${kind} descriptor must have a name and built-in/extension source.`);
    }
    if (names.has(name)) throw new Error(`Duplicate static ${kind} descriptor: ${name}`);
    names.add(name);
  }
}

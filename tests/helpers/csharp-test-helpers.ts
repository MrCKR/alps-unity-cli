import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import { commandContracts } from "../../src/contracts/command-contract.js";
import { discoverProjectTools } from "../../src/project-tool-discovery.js";
import type { ResolvedToolMetadata, ToolMetadata } from "../../src/tools/metadata/index.js";

/** 递归列举目录下的所有 C# 文件。 */
export async function listCsFiles(directory: URL): Promise<URL[]> {
  const entries = await readdir(directory, { withFileTypes: true });
  const urls = await Promise.all(entries.map(async (entry) => {
    const child = new URL(`${entry.name}${entry.isDirectory() ? "/" : ""}`, directory);
    if (entry.isDirectory()) {
      return listCsFiles(child);
    }
    return entry.isFile() && entry.name.endsWith(".cs") ? [child] : [];
  }));
  return urls.flat().sort((left, right) => left.pathname.localeCompare(right.pathname));
}

/** 读取目录下所有 C# 文件并拼接成单个源码字符串。 */
export async function readCsSources(directory: URL): Promise<string> {
  const files = await listCsFiles(directory);
  return (await Promise.all(files.map((file) => readFile(file, "utf8")))).join("\n");
}

/** 读取 unitypkg/Editor 下全部 C# 源码，便于做全局注册断言。 */
export async function readEditorCsSources(): Promise<string> {
  return readCsSources(new URL("../../unitypkg/Editor/", import.meta.url));
}

/**
 * 将测试内构造的 descriptor 按真实 catalog discovery 路径归一化，
 * 避免重新依赖离线 metadata 注册表。
 */
export async function discoverLiveTools(...tools: ToolMetadata[]): Promise<Map<string, ResolvedToolMetadata>> {
  const result = await discoverProjectTools({}, { projectTools: tools }, "req_test_discover_tools");
  assert.ok(result.ok);
  return new Map(result.tools.map((tool) => [tool.name, tool]));
}

/** 读取某个 contract group 当前公开的工具名集合。 */
export function contractToolNames(group: string): string[] {
  return commandContracts()
    .filter((entry) => entry.group === group)
    .map((entry) => entry.bridgeCommand)
    .sort();
}

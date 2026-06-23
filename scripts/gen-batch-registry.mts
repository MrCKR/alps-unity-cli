// 基于 builtinToolMetadata 生成 batch commandRegistry，确保与 metadata 一致。
import { builtinToolMetadata } from "../src/tools/metadata/index.js";

const lines: string[] = [];
for (const tool of builtinToolMetadata()) {
  // 工具发现(tools.group/list/search/describe/run)与 init 不进 batch registry。
  if (tool.group === "tools") continue;
  const parallelSafe = tool.readOnlyHint && !tool.destructiveHint;
  lines.push(`  ["${tool.name}", { parallelSafe: ${parallelSafe} }],`);
}
console.log(lines.join("\n"));
console.error("count:", lines.length);

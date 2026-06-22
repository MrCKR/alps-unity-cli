/**
 * 通用工具函数：跨模块共享的小型纯函数。
 * 仅放真正无副作用的通用辅助，避免领域逻辑。
 */

/** 去除 UTF-8 BOM (\uFEFF) 前缀。读取用户/Unity 写入的 JSON 文件时统一处理。 */
export function stripBom(text: string): string {
  return text.charCodeAt(0) === 0xfeff ? text.slice(1) : text;
}

/**
 * 关键词搜索打分：完全匹配 > 前缀匹配 > 包含匹配 > 无。
 * 用于 tools/context 等发现类命令的排序。
 */
export function scoreSearch(text: string, query: string): number {
  if (text === query) return 8;
  if (text.startsWith(query)) return 4;
  if (text.includes(query)) return 2;
  return 0;
}

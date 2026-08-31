import { StructuredToolError } from "../errors.js";

export const DEFAULT_DISCOVERY_PAGE_SIZE = 20;
export const MAX_DISCOVERY_PAGE_SIZE = 100;

export interface DiscoveryPageRequest {
  pageSize?: number;
  cursor?: number;
}

export interface DiscoveryPageInfo {
  cursor: number;
  pageSize: number;
  total: number;
  nextCursor?: number;
}

/** 对稳定排序后的 discovery 结果应用统一数字 cursor 分页。 */
export function paginateDiscovery<T>(items: T[], request: DiscoveryPageRequest, operation: string): { items: T[]; pageInfo: DiscoveryPageInfo } {
  const pageSize = request.pageSize ?? DEFAULT_DISCOVERY_PAGE_SIZE;
  const cursor = request.cursor ?? 0;
  if (!Number.isSafeInteger(pageSize) || pageSize < 1 || pageSize > MAX_DISCOVERY_PAGE_SIZE) {
    throw pageError(operation, "pageSize", `[1,${MAX_DISCOVERY_PAGE_SIZE}] integer`, request.pageSize);
  }
  if (!Number.isSafeInteger(cursor) || cursor < 0) {
    throw pageError(operation, "cursor", "non-negative integer", request.cursor);
  }
  const end = Math.min(items.length, cursor + pageSize);
  return {
    items: cursor < items.length ? items.slice(cursor, end) : [],
    pageInfo: {
      cursor,
      pageSize,
      total: items.length,
      ...(end < items.length ? { nextCursor: end } : {}),
    },
  };
}

function pageError(operation: string, field: string, expected: string, actual: unknown): StructuredToolError {
  return new StructuredToolError(
    "E_SCHEMA_INVALID",
    `${operation} ${field} must be a ${expected}.`,
    [`Pass a valid ${field}; use the returned pageInfo.nextCursor unchanged to continue.`],
    { path: `$.${field}`, expected, actual: actual ?? null },
  );
}

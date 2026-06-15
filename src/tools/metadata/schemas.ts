/** Minimal JSON Schema helpers. */
export function objSchema(properties: Record<string, unknown>, required?: string[]): Record<string, unknown> {
  const schema: Record<string, unknown> = { type: "object", properties };
  if (required && required.length > 0) schema.required = required;
  return schema;
}

export function pageSchema(itemSchema: Record<string, unknown>): Record<string, unknown> {
  return objSchema({
    items: { type: "array", items: itemSchema },
    pageInfo: objSchema({
      pageSize: { type: "integer" },
      cursor: { type: "string" },
      nextCursor: { type: "string" },
      total: { type: "integer" }
    }, ["pageSize", "cursor", "total"])
  }, ["items", "pageInfo"]);
}

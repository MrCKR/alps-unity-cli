/** 明确表示可由 JSON 承载的任意动态值。schema 生成器会为其添加开放标记。 */
export type AnyJson = string | number | boolean | null | AnyJson[] | { [key: string]: AnyJson };

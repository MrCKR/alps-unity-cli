import type { Envelope, ToolError } from "./protocol/envelope.js";

/** CLI 内部名称仅作为唯一 wire envelope 的类型别名。 */
export type CliResult<T = unknown, TDetails = unknown> = Envelope<T, TDetails>;

/** CLI 内部名称仅作为唯一 wire error 的类型别名。 */
export type CliError<TDetails = unknown> = ToolError<TDetails>;

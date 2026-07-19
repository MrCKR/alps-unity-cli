import { AsyncLocalStorage } from "node:async_hooks";

export type InvocationOrigin = "batch-frozen";

const invocationOriginStorage = new AsyncLocalStorage<InvocationOrigin>();

/** 在不进入 argv、schema 或 envelope 的情况下绑定内部 delivery 来源。 */
export function withInvocationOrigin<T>(origin: InvocationOrigin, action: () => Promise<T>): Promise<T> {
  return invocationOriginStorage.run(origin, action);
}

/** 仅供 dispatcher 区分冻结 batch delivery 与普通 direct delivery。 */
export function currentInvocationOrigin(): InvocationOrigin | undefined {
  return invocationOriginStorage.getStore();
}

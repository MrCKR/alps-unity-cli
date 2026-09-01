/** CLI本地迟到清理只在现有输出宽限期内获得执行机会。 */
const pendingOwners = new Set<symbol>();
const idleWaiters = new Set<() => void>();

/** 注册一个不改变公开terminal的本地owner，并统一吞掉其迟到失败。 */
export function trackCliProcessOwner(operation: Promise<unknown>): void {
  const owner = Symbol("cli-process-owner");
  pendingOwners.add(owner);
  void operation.then(() => undefined, () => undefined).finally(() => {
    pendingOwners.delete(owner);
    if (pendingOwners.size !== 0) return;
    for (const resolve of idleWaiters) resolve();
    idleWaiters.clear();
  });
}

/** finish入口用同步快照避免无owner时增加退出延迟。 */
export function hasPendingCliProcessOwners(): boolean {
  return pendingOwners.size > 0;
}

/** 只等待当前CLI进程已登记的owner；退出上限仍由调用者的单一timer控制。 */
export function waitForCliProcessOwners(): Promise<void> {
  if (pendingOwners.size === 0) return Promise.resolve();
  return new Promise((resolve) => { idleWaiters.add(resolve); });
}

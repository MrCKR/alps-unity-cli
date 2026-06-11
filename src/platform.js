import os from 'node:os';
import path from 'node:path';

export function normalizePath(value) {
  return path.win32.normalize(String(value));
}

export function defaultLogDirectory(env = process.env) {
  const appData = env.LOCALAPPDATA || path.win32.join(env.USERPROFILE || os.homedir(), 'AppData', 'Local');
  return path.win32.join(appData, 'AlpsUnityCli', 'logs');
}

export function createRequestId() {
  if (globalThis.crypto?.randomUUID) {
    return globalThis.crypto.randomUUID();
  }

  return `req-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

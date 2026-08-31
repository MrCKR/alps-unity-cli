import { spawn } from "node:child_process";

/** 每个辅助进程必须在固定deadline内退出；超时先终止进程，再返回失败。 */
export function runProcess(executable, args, options = {}) {
  const timeoutMs = options.timeoutMs ?? 180_000;
  return new Promise((resolve, reject) => {
    const child = spawn(executable, args, {
      cwd: options.cwd,
      env: options.env,
      windowsHide: true,
    });
    let stdout = "";
    let stderr = "";
    let timedOut = false;
    const timer = setTimeout(() => {
      timedOut = true;
      child.kill();
    }, timeoutMs);
    child.stdout.on("data", (chunk) => { stdout += chunk; });
    child.stderr.on("data", (chunk) => { stderr += chunk; });
    child.once("error", (error) => { clearTimeout(timer); reject(error); });
    child.once("exit", (code) => {
      clearTimeout(timer);
      const result = { code, stdout: stdout.trim(), stderr: stderr.trim(), pid: child.pid };
      if (timedOut) reject(new Error(`Validation helper timed out after ${timeoutMs} ms: ${executable} ${args.join(" ")}\n${result.stderr}`));
      else resolve(result);
    });
  });
}

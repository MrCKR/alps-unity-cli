/**
 * PowerShell 子进程的统一输出编码契约。
 *
 * powershell.exe 在 stdout/stderr 被重定向时按 OEM 代码页（中文系统为 936）写字节，
 * 而 Node 侧一律按 UTF-8 解码，非 ASCII 文本会损坏成替换字符。
 * 因此每个 spawn 的脚本都必须以本行开头，否则窗口标题、按钮名、项目路径和本地化异常文本全部失真。
 */
export const POWERSHELL_UTF8_OUTPUT_LINE = "$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)";

/** 把 UTF-8 输出契约放到脚本首行；前缀必须位于脚本自身 try/catch 之外，否则异常会被脚本吞掉。 */
export function withPowerShellUtf8Output(script: string): string {
  return `${POWERSHELL_UTF8_OUTPUT_LINE}\n${script}`;
}

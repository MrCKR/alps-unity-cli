import { readFile } from "node:fs/promises";

export interface UnityTestSummary {
  total?: number;
  passed?: number;
  failed?: number;
  skipped?: number;
  duration?: number;
  failures: Array<{ name: string; message?: string }>;
}

export async function readUnityTestSummary(resultPath: string): Promise<UnityTestSummary | undefined> {
  try {
    const xml = await readFile(resultPath, "utf8");
    return parseUnityTestSummary(xml);
  } catch (error) {
    const nodeError = error as NodeJS.ErrnoException;
    if (nodeError.code === "ENOENT") {
      return undefined;
    }
    throw error;
  }
}

export function parseUnityTestSummary(xml: string): UnityTestSummary {
  const root = xml.match(/<test-run\b([^>]*)>/) ?? xml.match(/<test-suite\b([^>]*)>/);
  const attrs = root ? parseAttributes(root[1]) : {};
  const failures = [...xml.matchAll(/<test-case\b([^>]*)>[\s\S]*?<failure>[\s\S]*?<message>([\s\S]*?)<\/message>[\s\S]*?<\/failure>[\s\S]*?<\/test-case>/g)].map((match) => {
    const caseAttrs = parseAttributes(match[1]);
    return { name: caseAttrs.fullname ?? caseAttrs.name ?? "unknown", message: decodeXml(match[2].trim()) };
  });

  return {
    total: toNumber(attrs.total ?? attrs.testcasecount),
    passed: toNumber(attrs.passed),
    failed: toNumber(attrs.failed),
    skipped: toNumber(attrs.skipped),
    duration: toNumber(attrs.duration),
    failures
  };
}

function parseAttributes(raw: string): Record<string, string> {
  return Object.fromEntries([...raw.matchAll(/([a-zA-Z0-9_-]+)="([^"]*)"/g)].map((match) => [match[1], decodeXml(match[2])]));
}

function toNumber(value: string | undefined): number | undefined {
  if (value === undefined) {
    return undefined;
  }
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : undefined;
}

function decodeXml(value: string): string {
  return value.replaceAll("&quot;", "\"").replaceAll("&apos;", "'").replaceAll("&lt;", "<").replaceAll("&gt;", ">").replaceAll("&amp;", "&");
}

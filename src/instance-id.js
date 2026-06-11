import path from "node:path";
import { createHash } from "node:crypto";

export function createInstanceId(projectPath) {
  const projectName = path.basename(projectPath);
  const hash = createHash("sha1").update(path.resolve(projectPath).toLowerCase()).digest("hex").slice(0, 8);
  return `${projectName}@${hash}`;
}

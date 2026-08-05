import { isAbsolute, relative, resolve, sep } from "node:path";

/** 将内部绝对项目路径投影为稳定的项目相对业务路径，并拒绝越界值。 */
export function projectRelativePath(projectPath: string, value: string, field: string): string {
  const root = resolve(projectPath);
  const resolved = resolve(value);
  const projected = relative(root, resolved);
  if (!projected || projected === ".." || projected.startsWith(`..${sep}`) || isAbsolute(projected)) {
    throw new Error(`Package ${field} is outside the selected project or resolves to its root.`);
  }
  return projected.split("\\").join("/");
}

/** plannedEffects 只保留业务路径；外部 source 通过调用方提供的公开别名投影。 */
export function publicPackageEffects(
  projectPath: string,
  effects: Array<Record<string, unknown>>,
  pathAliases: ReadonlyMap<string, string> = new Map(),
): Array<Record<string, unknown>> {
  return effects.map((effect) => {
    const projected = { ...effect };
    for (const field of ["from", "to", "targetPath"] as const) {
      const value = projected[field];
      if (typeof value !== "string") continue;
      projected[field] = pathAliases.get(resolve(value)) ?? projectRelativePath(projectPath, value, `plannedEffects.${field}`);
    }
    return projected;
  });
}

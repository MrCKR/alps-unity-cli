# 安装、更新与扩展管理

用户明确要求安装、更新或管理扩展，或需要诊断扩展状态时读取本文件。确认当前任务确实需要且 CLI 或 Bridge 未安装时，可以说明缺失组件、用途及安装范围（CLI 或指定项目的 Bridge），询问用户是否安装；明确同意视为本次安装指令，已有明确指令不重复询问。

连接失败、Editor 未运行、编译中或工具暂不可用都不能作为未安装的依据。用户拒绝或尚未答复时不执行安装，可以继续不依赖该组件的工作。安装和更新须有用户明确要求或同意，不因 CLI、Bridge、Skill 或扩展缺失而自动安装、更新或联网检查版本。

## CLI 安装与更新

用户明确要求安装 `unitycli` 且 CLI 尚不可用，或明确要求更新 CLI 时，执行：

```powershell
npm install -g @alpsckr/unitycli@latest
```

等待 npm 进程成功退出，再执行 `unitycli init help`。CLI 已可用时，安装 Bridge 或 Skill 不要求先更新 CLI；用户只指定其中一项时只处理该项，不扩大到其他组件、项目或 Skill 目录。

## Bridge 与 Skill

CLI 可用后，安装或更新 Bridge、Skill 都先读取当前版本的协议：

```powershell
unitycli init help
```

按返回的 `aiInstructions` 和 `nextActions` 处理目标发现、安装和校验。更新 CLI 后必须重新读取新版协议；不要沿用旧版本的安装假设。安装目标和参数以该协议为准，下列入口仅用于区分安装对象：

- `init discover` 只读返回当前项目计划。用户已指定的目标直接沿用，不重复询问，也不扩大到其他项目或目录。
- Bridge 使用 `init package --project <UnityProject>`。准确目标 Unity 打开着是正常安装状态，直接就地安装：安装器用同级 staging 目录加 rename 替换包文件，不要求先关闭 Editor，也从不启动或重启 Editor。安装成功后，如果该项目 Editor 正在运行，用 `tools run editor.refresh` 让它导入替换后的包，再用 `init package --project <UnityProject> --check` 验证已安装字节。
- 只有发布阶段报告 `PACKAGE_DIRECTORY_LOCKED` 时才评估关闭：如实报告被占用的目录并说明原状态已回滚，询问一次是否可正常关闭该准确项目，同意后才对该项目加 `--close-editor` 重跑。不得强制终止，也不得替用户处理未保存场景。
- Skill 明确目录使用 `init skill --target <SkillDirectory>`；预设使用 `init skill --agent <.agents|claude> --scope <workspace|user>`，workspace scope 同时传 `--workspace <path>`。明确目录与预设选项不混用。
- 每个成功的安装或更新效果都用对应原命令加 `--check` 校验。Bridge 与 Skill 是独立效果，一个失败只阻断该效果及其校验，不阻断另一个已授权的效果。

项目文件安装成功不等于 Editor 已完成导入和编译。若用户任务还需要 Live Unity 效果，在已连接实例完成 readiness 检查后，回到主 Skill 的首次分组发现与业务验证流程。

## 扩展状态、安装、移除与诊断

```powershell
unitycli extensions list --project <path>
unitycli extensions install <extension> --project <path>
unitycli extensions remove <extension> --project <path>
unitycli extensions doctor --project <path>
```

- `list` 查看扩展浅状态和名称；使用当前返回的扩展标识，不猜名称。
- `install` 安装所选扩展；`remove` 移除所选扩展。只按用户明确要求执行，不因扩展缺失、不可用或目录未找到工具而自动执行。
- `doctor` 执行完整扩展检查。`list` 和 `doctor` 可用于用户任务相关的只读诊断，不需要另行安装授权；不要把它们当作普通 Tool 调用前的固定检查。
- `--project <path>` 指定目标 Unity 项目；当前工作目录已是目标 Unity 项目时可以省略，不把该参数用于选择 Live Tool 实例。

扩展 bundle 与它依赖的 Unity Package 是不同对象；不要假设 `extensions install` 会补装所需 Unity Package。依据返回状态、依赖和 `nextActions` 处理缺失项，新增安装仍需用户明确指令。

扩展安装或移除后，等待必要的 Unity 导入、编译和重载完成，再按主 Skill 重新执行 `tools group`。扩展提供的实际 Unity 操作仍通过同一 Tool catalog 发现、describe 和调用。

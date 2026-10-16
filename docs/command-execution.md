# Command执行

`command`是唯一Tool catalog的schema参数调用入口。它不复制descriptor、registry、dispatcher、security、completion或JSON envelope；同一Tool也可由`tools run <tool> --input <json>`调用。

## 发现与选择

省略name时读取选定实例的完整live catalog，并返回command-name投影：

```powershell
unitycli command --project-path C:\path\MyProject
unitycli command --instance <name|alias|projectPath>
```

名称选择是exact且确定的：

1. native Tool完整精确名优先。
2. 没有native精确名时，`command <name>`可选择实际注册的`UnityPipeline_<name>` official attribute Tool。
3. `command UnityPipeline_<name>`显式选择该attribute Tool。
4. 不做snake_case、camelCase、dotted name、相似能力或语义映射；未注册名称返回`E_TOOL_NOT_FOUND_FOR_TARGET`。

`[CliCommand("test")]`在`tools`目录中只显示为`UnityPipeline_test`，group固定为`UnityPipeline`。同名冲突时两个实现都不可调用；native Tool占用`UnityPipeline_`前缀/`UnityPipeline` group时只禁用该实现。两种情形都只隔离受影响的名称，其他工具和Bridge基础观察继续工作，`tools describe`与`doctor`返回有界冲突来源。

## Official Attribute Profile

registry按完整类型名识别Unity官方`[CliCommand]`、`[CliArg]`和`IStructuredCommandInput`，core package不建立对`com.unity.pipeline`的编译依赖。method声明assembly属于exact Package ID `com.unity.pipeline`时，该provider已由native能力吸收并在注册前整体抑制；项目与其他Package中的attribute方法不受影响。过滤不使用命令名、namespace、assembly名称或attribute定义assembly。attributed method必须是static、非泛型，并且只接受以下返回形态：

- 同步`void`或同步结果值。
- `Task`或`Task<T>`；`async void`和其他自定义awaitable使该命令被隔离为不可调用，其他命令继续发布。
- `RuntimeOnly=true`固定投影为runtime-only；其余attribute Tool投影为editor target。

每个method参数按`[CliArg]`名称、描述、`Required`和CLR默认值生成schema。schema default和生成example表示合法默认行为与调用形状，不代表调用方当前任务意图；任务明确指定业务值时，第一次执行必须使用任务值，不能先以default/example做连通性或probe调用。实现`IStructuredCommandInput`的具体DTO按public writable member递归生成封闭object schema，未知字段、缺少required member、递归类型和重复投影名在effect前失败。scalar、enum、date-time、UUID、dictionary和array保持typed schema；`object`、Json.NET `JToken`族与其他无法静态派生的值显式使用`x-unityCliAnyJson`，不会猜测字符串格式或静默套用默认值。

attribute Tool统一声明保守的`official-attribute` contract profile、`arbitrary-method-invocation` side effect、`execute-user-code` permission和`supportsDryRun:false`。调用在参数转换完成后只跨一次effect barrier；`Task`超时不声称取消底层method，只有可信method return才能形成成功terminal。

## 业务参数

command name确定后，CLI按该Tool当前`inputSchema.properties`逐字匹配字段并构造typed input：

```powershell
# named value
unitycli command UnityPipeline_test --mode EditMode --category Editor

# attached value与boolean裸开关
unitycli command UnityPipeline_project_reload_data --path Assets/GameData.json --force --timeout_ms=30000

# positional按live schema的声明顺序绑定
unitycli command eval "return 2 + 2;"
```

支持：

- `--field value`
- `--field=value`
- boolean裸开关`--field`，等价于该业务字段为`true`
- boolean显式`true`/`false`
- 按`properties`声明顺序绑定的positional
- string、boolean、integer、number、object、array、null和`x-unityCliAnyJson`

object、array和任意JSON值必须是有效JSON。字段大小写和下划线原样匹配；未知字段、缺值、重复字段、类型错误和positional溢出都在effect前失败。业务值本身以`--`开头时，使用attached形式或`--`结束option解析。

## Command级控制项

```powershell
# 省略name时只返回有界路由摘要；参数在单命令help中读取
unitycli command --project-path C:\path\MyProject --page-size 20
unitycli command --project-path C:\path\MyProject --page-size 20 --cursor <nextCursor>

unitycli command --project-path C:\path\MyProject <name> ...
unitycli command --timeout 120 <name> ...
unitycli command <name> ... --timeout-ms 120000 --dry-run
```

- `--project-path <path>`绑定现有canonical项目实例，不直连endpoint，也不写入active target。
- 省略name时，`--page-size`默认20、最大100；`pageInfo.nextCursor`绑定原查询与catalog generation，必须原值传回`--cursor`。列表项不展开parameters，使用`command <name> --help`读取参数。
- `--timeout <seconds>`位于name前；`--timeout-ms <milliseconds>`使用本项目执行控制语义。
- `--dry-run`、`--runtime`、`--artifact-dir`和`--instance`仍是执行/选择控制项，不进入Tool business input。
- stdout默认是单行紧凑JSON；`--pretty`只增加缩进，不改变envelope字段。
- catalog summary/group响应只含bounded routing fields；exact descriptor响应有界且仅在同一 generation 内有效。

## 等价性

以下两种调用在typed input相同的前提下选择同一实际Tool并返回同一业务结果：

```powershell
unitycli tools run UnityPipeline_test --input '{"mode":"EditMode"}'
unitycli command test --mode EditMode
```

两种语法共享instance选择、当前domain的immutable Tool table、availability、schema终检、risk metadata、dry-run、timeout、completion contract、Bridge执行和错误码。每次顶层调用的live discovery会冻结exact editor session、listener ID和generation provenance；同一 provenance 会贯穿 root admission，并在首次业务POST前校验，发生reload或listener交接时在 effect 前返回`E_TOOL_CATALOG_CHANGED`。这些字段是私有 transport 绑定，不进入业务input、公开describe或error details。command语法不承诺补齐当前catalog中不存在的官方命令，也不改变本项目JSON输出格式。

## Pipeline与Bridge启动

本package首次在项目中检测到`com.unity.pipeline`时，会把Pipeline Editor `AutoStart`持久设为false、停止其当前server并清理由该server启用的autotick。只有全部结果验证成功后才写入项目级一次性接管标记；此后reload、Editor重启、package更新以及用户手动重新开启Pipeline都不会再次覆盖或停服。

本Bridge使用`Project Settings > UnityCLI > Bridge Auto Start`项目级开关，默认开启。该值只在下一次Domain Reload或Editor启动时读取：关闭不会现场中断已接受请求，生效后不启动listener也不写instance registry。该设置不控制development Player runtime server。

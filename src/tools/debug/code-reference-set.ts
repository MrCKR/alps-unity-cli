/**
 * 编译用户代码的 Tool 共用的引用集披露。
 *
 * 引用集来自运行期 AppDomain 快照（DebugCompilerOwner.Snapshot），只能追加不能移除；
 * 同一类型名被其他已加载程序集声明时编译器报 CS0433。三个编译入口必须给出同一句事实，避免描述漂移。
 */
export const REFERENCE_SET_DISCLOSURE =
  "References: every locatable assembly already loaded in the Editor AppDomain is referenced for the whole domain generation, and refs only appends more; nothing can be removed, so a type name that another loaded assembly also declares is a CS0433 ambiguity (System.Tuple exists in both mscorlib and ExCSS.Unity); use object[] or a type you declare.";

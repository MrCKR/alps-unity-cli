#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Search;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// 统一封装支持范围内的 Unity 版本 API 差异。
    /// 其他 Tool 与 Infrastructure 代码必须通过这里访问存在版本差异的 API。
    /// </summary>
    internal static class UnityCliVersionApi
    {
        /// <summary>返回 Unity Search 当前启用且非空的 provider 列表。</summary>
        internal static List<SearchProvider> ActiveSearchProviders()
        {
#if UNITY_2022_2_OR_NEWER
            return SearchService.GetActiveProviders().Where(provider => provider != null && provider.active).ToList();
#else
            return SearchService.Providers.Where(provider => provider != null && provider.active).ToList();
#endif
        }

        /// <summary>读取或设置 3D 物理自动模拟状态。</summary>
        internal static bool AutoSimulation3D
        {
            get
            {
#if UNITY_2022_2_OR_NEWER
                return Physics.simulationMode != SimulationMode.Script;
#else
                return Physics.autoSimulation;
#endif
            }
            set
            {
#if UNITY_2022_2_OR_NEWER
                Physics.simulationMode = value ? SimulationMode.FixedUpdate : SimulationMode.Script;
#else
                Physics.autoSimulation = value;
#endif
            }
        }

        /// <summary>读取或设置全局纹理 mipmap 限制等级。</summary>
        internal static int MasterTextureLimit
        {
            get
            {
#if UNITY_2022_2_OR_NEWER
                return QualitySettings.globalTextureMipmapLimit;
#else
                return QualitySettings.masterTextureLimit;
#endif
            }
            set
            {
#if UNITY_2022_2_OR_NEWER
                QualitySettings.globalTextureMipmapLimit = value;
#else
                QualitySettings.masterTextureLimit = value;
#endif
            }
        }

        /// <summary>读取 GameObject 上挂载的组件数量。</summary>
        internal static int ComponentCount(GameObject gameObject)
        {
#if UNITY_2022_2_OR_NEWER
            return gameObject.GetComponentCount();
#else
            return gameObject.GetComponents<Component>().Length;
#endif
        }
    }
}
#endif

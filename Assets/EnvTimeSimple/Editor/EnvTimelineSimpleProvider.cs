// EnvTimelineSimpleProvider.cs
// 反射桥接器：让 EnvTimelineSimpleEditorWindow（壳）通过程序集反射调用 EnvTimelineSimpleCore（核心）
// 核心类可独立编译为 DLL，壳无需直接引用核心类型即可调用其方法
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Hotfix.Core.EnvTimelineSimple;

namespace UnityEditor.EnvTimelineSimple
{
    /// <summary>
    /// 通过反射调用 EnvTimelineSimpleCore 的桥接器。
    /// 壳类（EditorWindow）持有此桥接器实例，所有核心功能调用均经过反射，
    /// 使核心可独立打包为 DLL 而壳无需重新编译。
    /// </summary>
    public class EnvTimelineSimpleProvider
    {
        // 核心类型全名
        const string CORE_TYPE_FULL_NAME = "UnityEditor.EnvTimelineSimple.EnvTimelineSimpleCore";

        Type _coreType;
        object _coreInstance;
        readonly Dictionary<string, MethodInfo> _methodCache = new Dictionary<string, MethodInfo>();

        /// <summary>
        /// 反射获取到的核心实例（可能为 null，表示核心程序集未加载）
        /// </summary>
        public object CoreInstance => _coreInstance;

        /// <summary>
        /// 核心类型是否成功加载
        /// </summary>
        public bool IsValid => _coreType != null && _coreInstance != null;

        public EnvTimelineSimpleProvider()
        {
            _coreType = FindCoreType();
            if (_coreType != null)
            {
                try
                {
                    _coreInstance = Activator.CreateInstance(_coreType);
                    EnvTimeSimpleDebug.Log("[EnvTimelineSimpleProvider] 核心类已加载: " + _coreType.Assembly.GetName().Name);
                }
                catch (Exception e)
                {
                    EnvTimeSimpleDebug.LogError($"[EnvTimelineSimpleProvider] 创建核心实例失败: {e.InnerException?.Message ?? e.Message}");
                    _coreInstance = null;
                }
            }
            else
            {
                EnvTimeSimpleDebug.LogError("[EnvTimelineSimpleProvider] 未找到核心类 EnvTimelineSimpleCore，请确保核心程序集已加载");
            }
        }

        /// <summary>
        /// 扫描所有已加载程序集查找核心类型
        /// </summary>
        Type FindCoreType()
        {
            // 优先从当前程序集查找
            var type = Type.GetType(CORE_TYPE_FULL_NAME);
            if (type != null) return type;

            // 遍历所有已加载程序集
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = asm.GetType(CORE_TYPE_FULL_NAME);
                    if (type != null) return type;
                }
                catch (Exception e)
                {
                    // 某些程序集在 GetType 时可能抛出 ReflectionTypeLoadException，跳过即可
                    EnvTimeSimpleDebug.LogWarning($"[EnvTimelineSimpleProvider] 跳过程序集 '{asm.GetName().Name}' (GetType 异常): {e.Message}");
                    continue;
                }
            }
            return null;
        }

        /// <summary>
        /// 获取或缓存方法信息
        /// </summary>
        MethodInfo GetMethod(string methodName, Type[] paramTypes)
        {
            string key = methodName + "|" + (paramTypes?.Length ?? 0);
            if (_methodCache.TryGetValue(key, out var mi))
                return mi;

            if (_coreType == null) return null;

            // 尝试按参数类型精确匹配
            if (paramTypes != null && paramTypes.Length > 0)
            {
                mi = _coreType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, paramTypes, null);
            }

            // 回退到按名称匹配
            if (mi == null)
            {
                var methods = _coreType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                foreach (var m in methods)
                {
                    if (m.Name == methodName)
                    {
                        var mp = m.GetParameters();
                        if (paramTypes == null || mp.Length == paramTypes.Length)
                        {
                            mi = m;
                            break;
                        }
                    }
                }
            }

            if (mi != null)
                _methodCache[key] = mi;

            return mi;
        }

        // ============================================================
        // 通用反射调用
        // ============================================================

        /// <summary>
        /// 调用无返回值的核心方法
        /// </summary>
        public void CallVoid(string methodName, params object[] args)
        {
            if (!EnsureValid()) return;
            var method = GetMethod(methodName, GetArgTypes(args));
            if (method != null)
            {
                try { method.Invoke(_coreInstance, args); }
                catch (Exception e) { EnvTimeSimpleDebug.LogError($"[Reflector] 调用 {methodName} 失败: {e.InnerException?.Message ?? e.Message}"); }
            }
            else
            {
                EnvTimeSimpleDebug.LogError($"[Reflector] 未找到方法: {methodName}");
            }
        }

        /// <summary>
        /// 调用有返回值的核心方法
        /// </summary>
        public T Call<T>(string methodName, params object[] args)
        {
            if (!EnsureValid()) return default(T);
            var method = GetMethod(methodName, GetArgTypes(args));
            if (method != null)
            {
                try
                {
                    var result = method.Invoke(_coreInstance, args);
                    if (result is T) return (T)result;
                    if (result != null)
                    {
                        try { return (T)Convert.ChangeType(result, typeof(T)); }
                        catch { return default(T); }
                    }
                    return default(T);
                }
                catch (Exception e)
                {
                    EnvTimeSimpleDebug.LogError($"[Reflector] 调用 {methodName} 失败: {e.InnerException?.Message ?? e.Message}");
                    return default(T);
                }
            }
            EnvTimeSimpleDebug.LogError($"[Reflector] 未找到方法: {methodName}");
            return default(T);
        }

        /// <summary>
        /// 调用有返回值的核心方法（object 版本）
        /// </summary>
        public object CallObject(string methodName, params object[] args)
        {
            return Call<object>(methodName, args);
        }

        // ============================================================
        // 属性读写
        // ============================================================

        public void SetProperty(string propName, object value)
        {
            if (_coreType == null) return;
            var prop = _coreType.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
            {
                try { prop.SetValue(_coreInstance, value); }
                catch (Exception e) { EnvTimeSimpleDebug.LogError($"[Reflector] 设置属性 {propName} 失败: {e.InnerException?.Message ?? e.Message}"); }
            }
        }

        public T GetProperty<T>(string propName)
        {
            if (_coreType == null) return default(T);
            var prop = _coreType.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanRead)
            {
                try
                {
                    var val = prop.GetValue(_coreInstance);
                    if (val is T) return (T)val;
                }
                catch (Exception e)
                {
                    EnvTimeSimpleDebug.LogError($"[Reflector] 获取属性 {propName} 失败: {e.InnerException?.Message ?? e.Message}");
                }
            }
            return default(T);
        }

        public object GetProperty(string propName)
        {
            return GetProperty<object>(propName);
        }

        // ============================================================
        // 静态方法调用
        // ============================================================

        public static T CallStatic<T>(string typeName, string methodName, params object[] args)
        {
            var type = Type.GetType(typeName);
            if (type == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        type = asm.GetType(typeName);
                        if (type != null) break;
                    }
                    catch (Exception e)
                    {
                        // 某些程序集在 GetType 时可能抛出异常，跳过即可
                        EnvTimeSimpleDebug.LogWarning($"[EnvTimelineSimpleProvider] CallStatic 跳过程序集 '{asm.GetName().Name}' (GetType 异常): {e.Message}");
                        continue;
                    }
                }
            }
            if (type == null) return default(T);

            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, null, GetArgTypes(args), null);
            if (method != null)
            {
                try
                {
                    var result = method.Invoke(null, args);
                    if (result is T) return (T)result;
                    if (result != null)
                    {
                        try { return (T)Convert.ChangeType(result, typeof(T)); }
                        catch { return default(T); }
                    }
                    return default(T);
                }
                catch (Exception e)
                {
                    EnvTimeSimpleDebug.LogError($"[Reflector] 静态调用 {typeName}.{methodName} 失败: {e.InnerException?.Message ?? e.Message}");
                }
            }
            return default(T);
        }

        // ============================================================
        // 辅助
        // ============================================================

        bool EnsureValid()
        {
            if (_coreInstance == null)
            {
                EnvTimeSimpleDebug.LogError("[Reflector] 核心实例未初始化");
                return false;
            }
            return true;
        }

        static Type[] GetArgTypes(object[] args)
        {
            if (args == null || args.Length == 0) return Type.EmptyTypes;
            var types = new Type[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                types[i] = args[i]?.GetType() ?? typeof(object);
            }
            return types;
        }

        // ============================================================
        // 类型化的便捷封装方法（供 Shell 直接使用，内部走反射）
        // ============================================================

        // ---- 属性 ----
        public EnvironmentTimelineData Data
        {
            get => GetProperty<EnvironmentTimelineData>("Data");
            set => SetProperty("Data", value);
        }

        public int DefaultCubemapSize
        {
            get => GetProperty<int>("DefaultCubemapSize");
            set => SetProperty("DefaultCubemapSize", value);
        }

        public string CubemapPrefix
        {
            get => GetProperty<string>("CubemapPrefix");
            set => SetProperty("CubemapPrefix", value);
        }

        // ---- 数据操作 ----
        public EnvironmentTimelineData CreateNewTimelineInScene()
            => Call<EnvironmentTimelineData>("CreateNewTimelineInScene");

        public int AddNodeAtTime(float time)
            => Call<int>("AddNodeAtTime", time);

        public void ApplyPreview(float previewTime)
            => CallVoid("ApplyPreview", previewTime);

        // ---- Probe 验证 ----
        public bool IsProbeUsedByOtherNode(ReflectionProbe probe, int excludeIndex, out int usedByIndex)
        {
            usedByIndex = -1;
            if (!EnsureValid()) return false;
            var method = GetMethod("IsProbeUsedByOtherNode", new[] { typeof(ReflectionProbe), typeof(int), typeof(int).MakeByRefType() });
            if (method == null) return false;

            // out 参数需要特殊处理：通过反射传入后从数组中取回
            var args = new object[] { probe, excludeIndex, 0 };
            try
            {
                var result = method.Invoke(_coreInstance, args);
                usedByIndex = (int)args[2];
                return (bool)result;
            }
            catch (Exception e)
            {
                EnvTimeSimpleDebug.LogError($"[Reflector] IsProbeUsedByOtherNode 失败: {e.InnerException?.Message ?? e.Message}");
                return false;
            }
        }

        public HashSet<int> GetDuplicateProbeNodeIndices()
            => Call<HashSet<int>>("GetDuplicateProbeNodeIndices");

        public bool IsProbeSelfContained(ReflectionProbe probe)
            => Call<bool>("IsProbeSelfContained", probe);

        public bool ValidateNoDuplicateProbes()
            => Call<bool>("ValidateNoDuplicateProbes");

        // ---- 资产管理 ----
        public bool TryPickAssetsFolder(string title, out string assetRelativeFolder)
        {
            assetRelativeFolder = null;
            if (!EnsureValid()) return false;
            var method = GetMethod("TryPickAssetsFolder", new[] { typeof(string), typeof(string).MakeByRefType() });
            if (method == null) return false;

            var args = new object[] { title, null };
            try
            {
                var result = method.Invoke(_coreInstance, args);
                assetRelativeFolder = (string)args[1];
                return (bool)result;
            }
            catch (Exception e)
            {
                EnvTimeSimpleDebug.LogError($"[Reflector] TryPickAssetsFolder 失败: {e.InnerException?.Message ?? e.Message}");
                return false;
            }
        }

        // ---- 烘焙 ----
        public void BakeNodeSH(EnvTimeNode node)
            => CallVoid("BakeNodeSH", node);

        public void BakeReflectionProbe(EnvTimeNode node)
            => CallVoid("BakeReflectionProbe", node);

        public void BakeAllNodes()
            => CallVoid("BakeAllNodes");

        // ---- 半球映射 ----
        public void ProcessNodeHemisphereMirror(EnvTimeNode node)
            => CallVoid("ProcessNodeHemisphereMirror", node);

        // ---- 静态工具 ----
        public static string FormatV4(Vector4 v)
            => CallStatic<string>("UnityEditor.EnvTimelineSimple.EnvTimelineSimpleCore", "FormatV4", v);

        // ---- 镜面高光代理预览 ----
        public List<Light> CollectSpecularLights(EnvTimeNode node)
            => Call<List<Light>>("CollectSpecularLights", node);

        public GameObject CreateSpecularSphere(Vector3 pos, float radius,
            Color emissiveColor, string name)
            => Call<GameObject>("CreateSpecularSphere", pos, radius, emissiveColor, name);

        public GameObject CreateSpecularPanel(Vector3 pos, Quaternion rot,
            Vector3 scale, Color emissiveColor, string name, Texture cookie = null)
            => Call<GameObject>("CreateSpecularPanel", pos, rot, scale, emissiveColor, name, cookie);

        public GameObject CreateSpecularDisc(Vector3 pos, Quaternion rot,
            float radius, Color emissiveColor, string name)
            => Call<GameObject>("CreateSpecularDisc", pos, rot, radius, emissiveColor, name);
    }
}

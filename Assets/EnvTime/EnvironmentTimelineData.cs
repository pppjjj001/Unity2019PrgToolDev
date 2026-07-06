// EnvironmentTimelineData.cs（扩展版 - 增加 Light Probe 数据）
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BYTools.EnvTimeline
{
    [DisallowMultipleComponent]
    public class EnvironmentTimelineData : MonoBehaviour
    {
        [Tooltip("时间轴总长度（如 24 表示 24 小时）")]
        public float totalDuration = 24f;
        public bool loop = true;
        
        [SerializeField]
        public List<EnvTimeNode> nodes = new List<EnvTimeNode>();

        public void SortByTime()
        {
            nodes.Sort((a, b) => a.time.CompareTo(b.time));
        }

        public bool Sample(float currentTime, out EnvTimeNode from, out EnvTimeNode to, out float t)
        {
            from = to = null; t = 0f;
            if (nodes == null || nodes.Count == 0) return false;
            if (nodes.Count == 1) { from = to = nodes[0]; return true; }

            currentTime = loop ? Mathf.Repeat(currentTime, totalDuration)
                               : Mathf.Clamp(currentTime, 0f, totalDuration);

            for (int i = 0; i < nodes.Count - 1; i++)
            {
                if (currentTime >= nodes[i].time && currentTime <= nodes[i + 1].time)
                {
                    from = nodes[i]; to = nodes[i + 1];
                    float span = Mathf.Max(0.0001f, to.time - from.time);
                    t = (currentTime - from.time) / span;
                    return true;
                }
            }

            if (loop)
            {
                from = nodes[nodes.Count - 1];
                to = nodes[0];
                float span = (totalDuration - from.time) + to.time;
                if (span < 0.0001f) { t = 0f; return true; }
                if (currentTime >= from.time)
                    t = (currentTime - from.time) / span;
                else
                    t = (currentTime + (totalDuration - from.time)) / span;
                return true;
            }

            from = to = nodes[nodes.Count - 1];
            return true;
        }
    }

    [Serializable]
    public class SerializedSH
    {
        public Vector4 SHAr;
        public Vector4 SHAg;
        public Vector4 SHAb;
        public Vector4 SHBr;
        public Vector4 SHBg;
        public Vector4 SHBb;
        public Vector4 SHC;

        public bool IsValid =>
            SHAr != Vector4.zero || SHAg != Vector4.zero || SHAb != Vector4.zero;

        public static SerializedSH Lerp(SerializedSH a, SerializedSH b, float t)
        {
            return new SerializedSH
            {
                SHAr = Vector4.Lerp(a.SHAr, b.SHAr, t),
                SHAg = Vector4.Lerp(a.SHAg, b.SHAg, t),
                SHAb = Vector4.Lerp(a.SHAb, b.SHAb, t),
                SHBr = Vector4.Lerp(a.SHBr, b.SHBr, t),
                SHBg = Vector4.Lerp(a.SHBg, b.SHBg, t),
                SHBb = Vector4.Lerp(a.SHBb, b.SHBb, t),
                SHC  = Vector4.Lerp(a.SHC,  b.SHC,  t),
            };
        }

        /// <summary>
        /// 直接使用 Unity 原版 SH 属性名 (unity_SHAr / unity_SHAg / …) 写入 MPB。
        /// 需要配合 Renderer.lightProbeUsage = LightProbeUsage.CustomProvided 使用。
        /// 不再需要 _CustomSHBlend 参数，直接覆盖原版 SH 变量。
        /// </summary>
        public void ApplyToMPB(MaterialPropertyBlock mpb)
        {
            mpb.SetVector("unity_SHAr", SHAr);
            mpb.SetVector("unity_SHAg", SHAg);
            mpb.SetVector("unity_SHAb", SHAb);
            mpb.SetVector("unity_SHBr", SHBr);
            mpb.SetVector("unity_SHBg", SHBg);
            mpb.SetVector("unity_SHBb", SHBb);
            mpb.SetVector("unity_SHC",  SHC);
        }

        /// <summary>
        /// 将 SphericalHarmonicsL2 转换为 Unity 风格的 7 个 Vector4 并写入 MPB。
        /// 用于 LightProbe 采样结果直接写入 Renderer 的 unity_SHAr 等参数。
        /// </summary>
        public static void ApplySHL2ToMPB(MaterialPropertyBlock mpb, SphericalHarmonicsL2 sh)
        {
            mpb.SetVector("unity_SHAr", new Vector4(sh[0, 3], sh[0, 1], sh[0, 2], sh[0, 0]));
            mpb.SetVector("unity_SHAg", new Vector4(sh[1, 3], sh[1, 1], sh[1, 2], sh[1, 0]));
            mpb.SetVector("unity_SHAb", new Vector4(sh[2, 3], sh[2, 1], sh[2, 2], sh[2, 0]));
            mpb.SetVector("unity_SHBr", new Vector4(sh[0, 4], sh[0, 5], sh[0, 6], sh[0, 7]));
            mpb.SetVector("unity_SHBg", new Vector4(sh[1, 4], sh[1, 5], sh[1, 6], sh[1, 7]));
            mpb.SetVector("unity_SHBb", new Vector4(sh[2, 4], sh[2, 5], sh[2, 6], sh[2, 7]));
            mpb.SetVector("unity_SHC",  new Vector4(sh[0, 8], sh[1, 8], sh[2, 8], 1.0f));
        }

        public SphericalHarmonicsL2 ToSHL2()
        {
            var sh = new SphericalHarmonicsL2();
            sh[0, 0] = SHAr.w; sh[0, 1] = SHAr.y; sh[0, 2] = SHAr.z; sh[0, 3] = SHAr.x;
            sh[1, 0] = SHAg.w; sh[1, 1] = SHAg.y; sh[1, 2] = SHAg.z; sh[1, 3] = SHAg.x;
            sh[2, 0] = SHAb.w; sh[2, 1] = SHAb.y; sh[2, 2] = SHAb.z; sh[2, 3] = SHAb.x;

            sh[0, 4] = SHBr.x; sh[0, 5] = SHBr.y; sh[0, 6] = SHBr.z; sh[0, 7] = SHBr.w;
            sh[1, 4] = SHBg.x; sh[1, 5] = SHBg.y; sh[1, 6] = SHBg.z; sh[1, 7] = SHBg.w;
            sh[2, 4] = SHBb.x; sh[2, 5] = SHBb.y; sh[2, 6] = SHBb.z; sh[2, 7] = SHBb.w;

            sh[0, 8] = SHC.x;  sh[1, 8] = SHC.y;  sh[2, 8] = SHC.z;
            return sh;
        }
    }

    /// <summary>
    /// 🆕 单个 Light Probe 的 SH 数据（27 个 float = 3 通道 × 9 系数）
    /// 使用 float[] 而非 SphericalHarmonicsL2，方便序列化与无 GC 混合
    ///
    /// ⭐ Prefab 变换支持：
    /// - localPositions 存储 Probe 在 Prefab 局部空间下的位置（烘焙时换算）
    /// - bakeSpaceTransform 记录烘焙时 Prefab 根的世界变换
    /// - 运行时通过当前 Prefab 变换把 Renderer 世界位置逆变换到局部空间做近邻查找
    /// - 混合得到 SH 后，用 bakeSpace→currentSpace 的旋转差量旋转 SH 系数
    ///
    /// 向后兼容：旧数据只有 world-space positions，无 bakeSpaceTransform，
    /// 此时 usePrefabSpace=false，行为与旧版完全一致。
    /// </summary>
    [Serializable]
    public class LightProbeSnapshot
    {
        // ===== 位置数据 =====
        // 局部空间位置（相对 Prefab 根）。当 usePrefabSpace=true 时使用此数组做近邻查找。
        public Vector3[] localPositions;

        // 旧字段保留（世界空间）。仅当 usePrefabSpace=false 或旧数据迁移时使用。
        public Vector3[] positions;

        // 扁平化的 SH 系数：长度 = positions.Length * 27
        // 每个 Probe 占 27 个 float, 顺序为 [c, b]: sh[c, b]
        // 即 idx = probeIndex * 27 + c * 9 + b
        public float[] shCoefficients;

        // ===== 烘焙空间信息 =====
        [Tooltip("是否使用 Prefab 局部空间存储（支持 Prefab 旋转/缩放/位移）")]
        public bool usePrefabSpace = false;

        // 烘焙时 Prefab 根的世界空间变换
        public SpaceTransform bakeSpaceTransform;

        /// <summary>
        /// 返回用于近邻查找的位置数组。
        /// usePrefabSpace=true 时返回局部位置，否则返回世界位置。
        /// </summary>
        public Vector3[] SamplePositions
        {
            get
            {
                if (usePrefabSpace && localPositions != null)
                    return localPositions;
                return positions;
            }
        }

        public bool IsValid =>
            (localPositions != null && localPositions.Length > 0 ||
             positions != null && positions.Length > 0) &&
            shCoefficients != null &&
            shCoefficients.Length == ProbeCount * 27;

        public int ProbeCount =>
            usePrefabSpace && localPositions != null
                ? localPositions.Length
                : (positions != null ? positions.Length : 0);

        /// <summary>
        /// 从 LightmapSettings.lightProbes 当前烘焙数据捕获快照
        /// </summary>
        /// <param name="prefabRoot">
        /// Prefab 根节点的 Transform。传入非 null 时启用局部空间存储，
        /// Probe 位置会转换到该 Transform 的局部空间，并记录其世界变换。
        /// 传 null 时退化为旧版世界空间存储（向后兼容）。
        /// </param>
        public static LightProbeSnapshot CaptureCurrent(Transform prefabRoot = null)
        {
            var lp = LightmapSettings.lightProbes;
            if (lp == null || lp.count == 0) return null;

            var snap = new LightProbeSnapshot();
            var worldPositions = lp.positions;

            int n = worldPositions.Length;

            if (prefabRoot != null)
            {
                // ===== Prefab 局部空间存储 =====
                snap.usePrefabSpace = true;
                snap.localPositions = new Vector3[n];
                for (int i = 0; i < n; i++)
                    snap.localPositions[i] = prefabRoot.InverseTransformPoint(worldPositions[i]);

                // 同时保留世界位置（用于调试 / 旧路径兼容）
                snap.positions = (Vector3[])worldPositions.Clone();

                // 记录烘焙时的空间变换
                snap.bakeSpaceTransform = SpaceTransform.FromTransform(prefabRoot);
            }
            else
            {
                // ===== 旧版世界空间存储（向后兼容）=====
                snap.usePrefabSpace = false;
                snap.positions = (Vector3[])worldPositions.Clone();
            }

            // SH 系数存储（两种模式共用）
            var baked = lp.bakedProbes;
            snap.shCoefficients = new float[n * 27];

            for (int i = 0; i < n; i++)
            {
                var sh = baked[i];
                int baseIdx = i * 27;
                for (int c = 0; c < 3; c++)
                {
                    int rowBase = baseIdx + c * 9;
                    for (int b = 0; b < 9; b++)
                    {
                        snap.shCoefficients[rowBase + b] = sh[c, b];
                    }
                }
            }
            return snap;
        }

        /// <summary>
        /// 将局部空间 Probe 位置变换到当前世界空间（用于 SceneView 可视化）。
        /// 仅在 usePrefabSpace=true 时有效。
        /// </summary>
        public Vector3[] GetWorldPositions(Transform currentRoot)
        {
            if (!usePrefabSpace || localPositions == null)
                return positions;

            var result = new Vector3[localPositions.Length];
            for (int i = 0; i < localPositions.Length; i++)
                result[i] = currentRoot.TransformPoint(localPositions[i]);
            return result;
        }
    }

    /// <summary>
    /// 序列化的空间变换信息（位置+旋转+缩放），用于记录烘焙时 Prefab 根的世界状态。
    /// </summary>
    [Serializable]
    public struct SpaceTransform
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 lossyScale;

        public static SpaceTransform FromTransform(Transform t)
        {
            return new SpaceTransform
            {
                position = t.position,
                rotation = t.rotation,
                lossyScale = t.lossyScale,
            };
        }

        public Matrix4x4 ToMatrix()
        {
            return Matrix4x4.TRS(position, rotation, lossyScale);
        }

        /// <summary>
        /// 将世界空间点变换到此空间的局部坐标
        /// </summary>
        public Vector3 InverseTransformPoint(Vector3 worldPoint)
        {
            return ToMatrix().inverse.MultiplyPoint3x4(worldPoint);
        }
    }

    [Serializable]
    public class EnvTimeNode
    {
        public string nodeName = "Node";
        public float time = 0f;

        [Header("ReflectionProbe（直接引用，随预设保存）")]
        public ReflectionProbe mainProbe;
        public List<ReflectionProbe> additionalProbes = new List<ReflectionProbe>();

        [Header("影响目标")]
        public List<GameObject> affectedTargets = new List<GameObject>();
        public bool includeChildren = true;

        [Header("烘焙参数")]
        public int sampleResolution = 64;
        public float rotationY = 0f;
        public bool useHDRClamp = false;
        public float hdrClampMax = 5f;
        public float exposure = 1f;

        [Header("烘焙结果（自动写入）")]
        public SerializedSH customSH = new SerializedSH();

        // 🆕 Light Probe 快照
        [Header("Light Probe 数据")]
        public LightProbeSnapshot lightProbeData;

        public Cubemap GetMainCubemap()
        {
            if (mainProbe == null) return null;

            Texture t = mainProbe.texture;
            if (t == null)
            {
                // Custom 模式使用 customBakedTexture，Baked/Realtime 模式使用 bakedTexture
                t = (mainProbe.mode == ReflectionProbeMode.Custom)
                    ? mainProbe.customBakedTexture
                    : mainProbe.bakedTexture;
            }
            return t as Cubemap;
        }
    }
}
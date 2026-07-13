// EnvironmentTimelineData.cs
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Hotfix.Core.EnvTimelineSimple
{
    [DisallowMultipleComponent]
    public class EnvironmentTimelineData : MonoBehaviour
    {
        [Tooltip("时间轴总长度（如 24 表示 24 小时）")]
        public float totalDuration = 24f;
        public bool loop = true;

        [Tooltip("到达时间轴末尾时是否保持最后一个节点的环境。\n"
                 + "✅ 勾选：到达末尾后保持最后一个节点的环境（默认）。\n"
                 + "❌ 取消：循环回到第一个节点继续模拟。")]
        public bool holdAtEnd = true;

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

            // holdAtEnd 优先控制尾部行为：true=保持最后节点，false=循环回第一个
            bool shouldLoop = loop && !holdAtEnd;
            currentTime = shouldLoop ? Mathf.Repeat(currentTime, totalDuration)
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

            if (loop && !holdAtEnd)
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

            // holdAtEnd=true 或 loop=false：保持最后一个节点
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
        /// 将 SH 系数直接写入材质实例（而非 MPB）。
        /// 用于运行模式下对 SkinnedMeshRenderer 使用材质实例覆盖的场景。
        /// ⚠️ 会修改材质实例属性，需确保使用的是材质副本而非共享材质。
        /// 同样需要配合 Renderer.lightProbeUsage = LightProbeUsage.CustomProvided 使用。
        /// </summary>
        public void ApplyToMaterial(Material mat)
        {
            if (mat == null) return;
            mat.SetVector("unity_SHAr", SHAr);
            mat.SetVector("unity_SHAg", SHAg);
            mat.SetVector("unity_SHAb", SHAb);
            mat.SetVector("unity_SHBr", SHBr);
            mat.SetVector("unity_SHBg", SHBg);
            mat.SetVector("unity_SHBb", SHBb);
            mat.SetVector("unity_SHC",  SHC);
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
    /// 混合区域曲线类型
    /// </summary>
    public enum BlendCurveType
    {
        [Tooltip("线性插值")]
        Linear,
        [Tooltip("SmoothStep 平滑过渡（S形曲线）")]
        SmoothStep,
        [Tooltip("Ease In（先慢后快）")]
        EaseIn,
        [Tooltip("Ease Out（先快后慢）")]
        EaseOut,
        [Tooltip("Ease InOut（两端慢中间快）")]
        EaseInOut,
    }

    /// <summary>
    /// 混合区域（BlendZone）：定义从当前节点过渡到下一个节点时的混合行为。
    /// 
    /// 时间轴结构示意（两节点之间的间隙 [0,1]）：
    ///   NodeA ────[保持A]──── [start ── 混合区域 ── end] ────[保持B]──── NodeB
    ///                                    └ probeSwitchPoint ┘
    /// 
    /// - start/end：混合区域在两节点间隙中的归一化位置 [0,1]
    /// - probeSwitchPoint：ReflectionProbe 切换瞄点在混合区域内的归一化位置 [0,1]
    /// - shBlendCurve：SH/LightProbe 在混合区域内的插值曲线类型
    /// </summary>
    [Serializable]
    public class BlendZone
    {
        [Tooltip("启用自定义混合区域（关闭则使用全段线性混合，与旧版行为一致）")]
        public bool enabled = false;

        [Range(0f, 1f)]
        [Tooltip("混合区域起始位置（占两节点间距的百分比）\n" +
                 "此点之前完全使用当前节点（From）的环境数据。")]
        public float start = 0.3f;

        [Range(0f, 1f)]
        [Tooltip("混合区域结束位置（占两节点间距的百分比）\n" +
                 "此点之后完全使用目标节点（To）的环境数据。")]
        public float end = 0.7f;

        [Range(0f, 1f)]
        [Tooltip("ReflectionProbe 切换瞄点（在混合区域 [start,end] 内的归一化位置）\n" +
                 "0 = 混合区域起点切换，1 = 混合区域终点切换，0.5 = 中点切换。\n" +
                 "瞄点之前保持 From 的 Probe，瞄点之后切换为 To 的 Probe。")]
        public float probeSwitchPoint = 0.5f;

        [Range(0f, 0.5f)]
        [Tooltip("Probe 切换平滑宽度（在混合区域内的归一化半宽）\n" +
                 "0 = 硬切换（瞬切），>0 = 在瞄点两侧此宽度范围内平滑过渡。\n" +
                 "⚠️ 平滑过渡期间两个 Probe 同时启用，需要 Shader 支持反射球融合。")]
        public float probeSwitchSmoothWidth = 0f;

        [Tooltip("SH/LightProbe 在混合区域内的插值曲线类型")]
        public BlendCurveType shBlendCurve = BlendCurveType.SmoothStep;

        /// <summary>
        /// 根据 rawT（两节点间的原始归一化插值 0~1）计算 SH 混合权重。
        /// 返回值 0 = 完全使用 From，1 = 完全使用 To。
        /// </summary>
        public float EvaluateSHBlend(float rawT)
        {
            if (!enabled) return rawT; // 未启用时退化为全段线性

            float s = Mathf.Clamp01(start);
            float e = Mathf.Clamp01(end);
            if (e <= s) e = Mathf.Min(1f, s + 0.001f);

            if (rawT <= s) return 0f;
            if (rawT >= e) return 1f;

            float localT = (rawT - s) / (e - s); // 映射到 [0,1]
            return ApplyCurve(localT, shBlendCurve);
        }

        /// <summary>
        /// 根据 rawT 计算 ReflectionProbe 应该激活的节点标识。
        /// 返回 false = 激活 From 的 Probe，true = 激活 To 的 Probe。
        /// </summary>
        public bool ShouldSwitchProbe(float rawT)
        {
            if (!enabled) return rawT >= 0.5f; // 未启用时保持旧版行为（50% 切换）

            float s = Mathf.Clamp01(start);
            float e = Mathf.Clamp01(end);
            if (e <= s) e = Mathf.Min(1f, s + 0.001f);

            float switchRaw = Mathf.Lerp(s, e, Mathf.Clamp01(probeSwitchPoint));
            return rawT >= switchRaw;
        }

        /// <summary>
        /// 根据 rawT 计算 Probe 平滑过渡权重（用于 Shader 支持反射球融合时）。
        /// 返回值 0 = 完全使用 From 的 Probe，1 = 完全使用 To 的 Probe。
        /// probeSwitchSmoothWidth=0 时为硬切换（返回值仅在 0/1 之间跳变）。
        /// </summary>
        public float EvaluateProbeBlend(float rawT)
        {
            if (!enabled) return rawT >= 0.5f ? 1f : 0f;

            float s = Mathf.Clamp01(start);
            float e = Mathf.Clamp01(end);
            if (e <= s) e = Mathf.Min(1f, s + 0.001f);

            float switchRaw = Mathf.Lerp(s, e, Mathf.Clamp01(probeSwitchPoint));
            float smoothW = Mathf.Clamp01(probeSwitchSmoothWidth) * (e - s) * 0.5f;

            if (smoothW <= 0.001f)
                return rawT >= switchRaw ? 1f : 0f;

            return Mathf.Clamp01((rawT - (switchRaw - smoothW)) / (smoothW * 2f));
        }

        static float ApplyCurve(float t, BlendCurveType curve)
        {
            switch (curve)
            {
                case BlendCurveType.Linear:
                    return t;
                case BlendCurveType.SmoothStep:
                    return Mathf.SmoothStep(0f, 1f, t);
                case BlendCurveType.EaseIn:
                    return t * t;
                case BlendCurveType.EaseOut:
                    return 1f - (1f - t) * (1f - t);
                case BlendCurveType.EaseInOut:
                    return t * t * (3f - 2f * t);
                default:
                    return t;
            }
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

        [Header("ReflectionProbe 烘焙参与模型")]
        [Tooltip("烘焙此节点时，将这些 GameObject 及其递归所有子物体临时勾选 ReflectionProbeStatic 以参与烘焙，烘焙结束后自动还原。")]
        public List<GameObject> reflectionProbeBakeTargets = new List<GameObject>();

        [Header("混合区域（过渡到此节点时生效）")]
        [Tooltip("定义从前一个节点过渡到此节点时的混合区域。\n" +
                 "混合区域内才开始 SH/LightProbe 混合和 Probe 切换。")]
        public BlendZone blendZone = new BlendZone();

        [Header("烘焙参数")]
        public int sampleResolution = 64;
        public float rotationY = 0f;
        public bool useHDRClamp = false;
        public float hdrClampMax = 5f;
        public float exposure = 1f;

        [Header("半球映射")]
        [Tooltip("启用后，烘焙完 Cubemap 会自动将空半球用实景半球镜像填充。\n" +
                 "适用于场景只有一半有实景的情况。")]
        public bool enableHemisphereMirror = false;
        [Range(0f, 360f)]
        [Tooltip("与水平轴 Z 的夹角（度），定义实景半球的中心方向。\n" +
                 "0° = +Z 方向，90° = +X 方向，180° = -Z 方向，270° = -X 方向。\n" +
                 "镜面为过 Y 轴的垂直平面，法线指向实景半球。")]
        public float hemisphereAngle = 0f;

        [Header("烘焙结果（自动写入）")]
        public SerializedSH customSH = new SerializedSH();

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
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

        public void ApplyToMPB(MaterialPropertyBlock mpb, float blend = 1f)
        {
            mpb.SetVector("custom_SHAr", SHAr);
            mpb.SetVector("custom_SHAg", SHAg);
            mpb.SetVector("custom_SHAb", SHAb);
            mpb.SetVector("custom_SHBr", SHBr);
            mpb.SetVector("custom_SHBg", SHBg);
            mpb.SetVector("custom_SHBb", SHBb);
            mpb.SetVector("custom_SHC",  SHC);
            mpb.SetFloat("_CustomSHBlend", blend);
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
    /// </summary>
    [Serializable]
    public class LightProbeSnapshot
    {
        // 位置（世界空间）
        public Vector3[] positions;

        // 扁平化的 SH 系数：长度 = positions.Length * 27
        // 每个 Probe 占 27 个 float, 顺序为 [c, b]: sh[c, b]
        // 即 idx = probeIndex * 27 + c * 9 + b
        public float[] shCoefficients;

        public bool IsValid => positions != null && positions.Length > 0
            && shCoefficients != null && shCoefficients.Length == positions.Length * 27;

        public int ProbeCount => positions != null ? positions.Length : 0;

        /// <summary>
        /// 从 LightmapSettings.lightProbes 当前烘焙数据捕获快照
        /// </summary>
        public static LightProbeSnapshot CaptureCurrent()
        {
            var lp = LightmapSettings.lightProbes;
            if (lp == null || lp.count == 0) return null;

            var snap = new LightProbeSnapshot();
            snap.positions = (Vector3[])lp.positions.Clone();

            var baked = lp.bakedProbes;
            int n = baked.Length;
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
            Texture t = mainProbe.texture != null ? mainProbe.texture : mainProbe.bakedTexture;
            return t as Cubemap;
        }
    }
}
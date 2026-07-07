// EnvironmentTimelineData.cs
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BYTools.EnvTimelineSimple
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
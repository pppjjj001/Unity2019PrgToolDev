// CustomProbeSamplingDebugger.cs
// 编辑器侧独立的 LightProbe 4 近邻逆距离加权采样工具
// 复刻运行时 EnvironmentTimelineController.BuildProbeWeights 算法，
// 供 Editor 窗口 / SceneView Gizmo 查询某个 Renderer 的采样结果，不依赖运行时状态
#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.Rendering;

namespace BYTools.EnvTimeline
{
    /// <summary>
    /// 单次采样结果。iN=-1 表示该槽位无效。
    /// distances 为原始 sqrMagnitude（未开根），weights 已归一化。
    /// </summary>
    public struct CustomProbeSamplingResult
    {
        public Vector3 samplePosition;
        public int neighborCount;

        public int i0, i1, i2, i3;
        public float d0, d1, d2, d3;   // sqrMagnitude
        public float w0, w1, w2, w3;   // 归一化权重

        public SphericalHarmonicsL2 blendedSH;

        public bool IsValid => i0 >= 0;

        /// <summary>返回有效邻居数量（1~4）</summary>
        public int ActiveCount
        {
            get
            {
                int c = 0;
                if (i0 >= 0 && w0 > 0f) c++;
                if (i1 >= 0 && w1 > 0f) c++;
                if (i2 >= 0 && w2 > 0f) c++;
                if (i3 >= 0 && w3 > 0f) c++;
                return c;
            }
        }

        public int GetIndex(int slot)
        {
            switch (slot)
            {
                case 0: return i0;
                case 1: return i1;
                case 2: return i2;
                case 3: return i3;
            }
            return -1;
        }

        public float GetWeight(int slot)
        {
            switch (slot)
            {
                case 0: return w0;
                case 1: return w1;
                case 2: return w2;
                case 3: return w3;
            }
            return 0f;
        }

        public float GetDistanceSqr(int slot)
        {
            switch (slot)
            {
                case 0: return d0;
                case 1: return d1;
                case 2: return d2;
                case 3: return d3;
            }
            return float.MaxValue;
        }
    }

    /// <summary>
    /// 编辑器侧独立采样器。算法与运行时 BuildProbeWeights 完全一致，
    /// 可在编辑模式下不启动 Controller 直接查询采样结果。
    /// </summary>
    public static class CustomProbeSamplingDebugger
    {
        const float CoincidentEpsilon = 0.000001f;  // 与运行时一致
        const float WeightFloor = 0.000001f;

        // 四面体插值器缓存（编辑器侧独立使用，不依赖运行时 Controller）
        static readonly Dictionary<LightProbeSnapshot, TetrahedralInterpolator> _tetraCache
            = new Dictionary<LightProbeSnapshot, TetrahedralInterpolator>();

        /// <summary>
        /// 获取指定快照的四面体插值器（编辑器侧缓存）
        /// </summary>
        public static TetrahedralInterpolator GetTetraInterpolator(LightProbeSnapshot snap)
        {
            if (snap == null || !snap.IsValid) return null;

            TetrahedralInterpolator interp;
            if (!_tetraCache.TryGetValue(snap, out interp))
            {
                interp = new TetrahedralInterpolator();
                interp.Build(snap.SamplePositions, snap.ProbeCount);
                _tetraCache[snap] = interp;
            }
            return interp;
        }

        /// <summary>
        /// 清除四面体插值器缓存
        /// </summary>
        public static void InvalidateTetraCache()
        {
            _tetraCache.Clear();
        }

        /// <summary>
        /// 对指定位置做采样（自动选择 IDW 或四面体模式）。
        /// </summary>
        /// <param name="snap">LightProbe 快照</param>
        /// <param name="position">采样位置（世界空间）</param>
        /// <param name="neighborCount">邻居数 1~4（仅 IDW 模式使用）</param>
        /// <param name="mode">插值模式</param>
        public static CustomProbeSamplingResult Sample(
            LightProbeSnapshot snap, Vector3 position, int neighborCount,
            ProbeInterpolationMode mode = ProbeInterpolationMode.InverseDistance)
        {
            if (mode == ProbeInterpolationMode.Tetrahedral)
                return SampleTetrahedral(snap, position);
            return SampleIDW(snap, position, neighborCount);
        }

        /// <summary>
        /// 四面体插值采样
        /// </summary>
        public static CustomProbeSamplingResult SampleTetrahedral(
            LightProbeSnapshot snap, Vector3 position)
        {
            var result = new CustomProbeSamplingResult();
            result.samplePosition = position;
            result.neighborCount = 4;
            result.i0 = result.i1 = result.i2 = result.i3 = -1;
            result.w0 = result.w1 = result.w2 = result.w3 = 0f;
            result.d0 = result.d1 = result.d2 = result.d3 = float.MaxValue;

            if (snap == null || !snap.IsValid) return result;

            var interp = GetTetraInterpolator(snap);
            if (interp == null || !interp.IsBuilt)
            {
                // 四面体化失败，回退到 IDW
                return SampleIDW(snap, position, 4);
            }

            var tetraResult = interp.Sample(position);
            if (!tetraResult.valid)
            {
                return SampleIDW(snap, position, 4);
            }

            result.i0 = tetraResult.i0; result.w0 = tetraResult.w0;
            result.i1 = tetraResult.i1; result.w1 = tetraResult.w1;
            result.i2 = tetraResult.i2; result.w2 = tetraResult.w2;
            result.i3 = tetraResult.i3; result.w3 = tetraResult.w3;

            // 计算距离（用于可视化标签）
            var positions = snap.positions;
            if (positions != null)
            {
                if (result.i0 >= 0) result.d0 = (positions[result.i0] - position).sqrMagnitude;
                if (result.i1 >= 0) result.d1 = (positions[result.i1] - position).sqrMagnitude;
                if (result.i2 >= 0) result.d2 = (positions[result.i2] - position).sqrMagnitude;
                if (result.i3 >= 0) result.d3 = (positions[result.i3] - position).sqrMagnitude;
            }

            // 累加 SH
            result.blendedSH = AccumulateSH(snap, result.i0, result.w0, default);
            if (result.i1 >= 0 && result.w1 > 0f)
                result.blendedSH = AccumulateSH(snap, result.i1, result.w1, result.blendedSH);
            if (result.i2 >= 0 && result.w2 > 0f)
                result.blendedSH = AccumulateSH(snap, result.i2, result.w2, result.blendedSH);
            if (result.i3 >= 0 && result.w3 > 0f)
                result.blendedSH = AccumulateSH(snap, result.i3, result.w3, result.blendedSH);

            return result;
        }

        /// <summary>
        /// 对指定位置做 N 近邻逆距离加权采样。
        /// </summary>
        /// <param name="snap">LightProbe 快照</param>
        /// <param name="position">采样位置（世界空间）</param>
        /// <param name="neighborCount">邻居数 1~4</param>
        public static CustomProbeSamplingResult SampleIDW(
            LightProbeSnapshot snap, Vector3 position, int neighborCount)
        {
            var result = new CustomProbeSamplingResult();
            result.samplePosition = position;
            result.neighborCount = Mathf.Clamp(neighborCount, 1, 4);
            result.i0 = result.i1 = result.i2 = result.i3 = -1;
            result.w0 = result.w1 = result.w2 = result.w3 = 0f;
            result.d0 = result.d1 = result.d2 = result.d3 = float.MaxValue;

            if (snap == null || !snap.IsValid) return result;

            int count = snap.ProbeCount;
            int n = result.neighborCount;
            var positions = snap.positions;

            // ---- 4 近邻查找（与运行时 BuildProbeWeights 逐行一致）----
            for (int i = 0; i < count; i++)
            {
                float d = (positions[i] - position).sqrMagnitude;

                if (d <= CoincidentEpsilon)
                {
                    result.i0 = i;
                    result.d0 = 0f;
                    result.w0 = 1f;
                    result.blendedSH = ReadSH(snap, i);
                    return result;
                }

                if (d < result.d0)
                {
                    result.d3 = result.d2; result.i3 = result.i2;
                    result.d2 = result.d1; result.i2 = result.i1;
                    result.d1 = result.d0; result.i1 = result.i0;
                    result.d0 = d;          result.i0 = i;
                }
                else if (n > 1 && d < result.d1)
                {
                    result.d3 = result.d2; result.i3 = result.i2;
                    result.d2 = result.d1; result.i2 = result.i1;
                    result.d1 = d;          result.i1 = i;
                }
                else if (n > 2 && d < result.d2)
                {
                    result.d3 = result.d2; result.i3 = result.i2;
                    result.d2 = d;          result.i2 = i;
                }
                else if (n > 3 && d < result.d3)
                {
                    result.d3 = d;          result.i3 = i;
                }
            }

            // ---- 逆距离权重（与运行时一致）----
            float w0 = result.i0 >= 0 ? 1f / Mathf.Max(result.d0, WeightFloor) : 0f;
            float w1 = result.i1 >= 0 && n > 1 ? 1f / Mathf.Max(result.d1, WeightFloor) : 0f;
            float w2 = result.i2 >= 0 && n > 2 ? 1f / Mathf.Max(result.d2, WeightFloor) : 0f;
            float w3 = result.i3 >= 0 && n > 3 ? 1f / Mathf.Max(result.d3, WeightFloor) : 0f;
            float weightSum = w0 + w1 + w2 + w3;
            if (weightSum <= 0f) return result;

            result.w0 = w0 / weightSum;
            result.w1 = w1 / weightSum;
            result.w2 = w2 / weightSum;
            result.w3 = w3 / weightSum;

            // ---- 累加 SH ----
            result.blendedSH = AccumulateSH(snap, result.i0, result.w0, default);
            if (result.i1 >= 0 && result.w1 > 0f)
                result.blendedSH = AccumulateSH(snap, result.i1, result.w1, result.blendedSH);
            if (result.i2 >= 0 && result.w2 > 0f)
                result.blendedSH = AccumulateSH(snap, result.i2, result.w2, result.blendedSH);
            if (result.i3 >= 0 && result.w3 > 0f)
                result.blendedSH = AccumulateSH(snap, result.i3, result.w3, result.blendedSH);

            return result;
        }

        /// <summary>对两个快照做混合采样并 Lerp（模拟运行时 from→to 插值）</summary>
        public static SphericalHarmonicsL2 SampleAndLerp(
            LightProbeSnapshot from, LightProbeSnapshot to,
            Vector3 position, int neighborCount, float t,
            ProbeInterpolationMode mode = ProbeInterpolationMode.InverseDistance)
        {
            bool fromValid = from != null && from.IsValid;
            bool toValid = to != null && to.IsValid;

            if (!fromValid && !toValid) return default;

            if (fromValid && toValid)
            {
                var fromSH = Sample(from, position, neighborCount, mode);
                var toSH = Sample(to, position, neighborCount, mode);
                if (!fromSH.IsValid && !toSH.IsValid) return default;
                if (!fromSH.IsValid) return toSH.blendedSH;
                if (!toSH.IsValid) return fromSH.blendedSH;
                return LerpSH(fromSH.blendedSH, toSH.blendedSH, t);
            }

            var snap = fromValid ? from : to;
            var r = Sample(snap, position, neighborCount);
            return r.IsValid ? r.blendedSH : default;
        }

        // ---- 内部 SH 辅助（与运行时 AccumulateSnapshotSH / ReadSnapshotSH 一致）----

        static SphericalHarmonicsL2 ReadSH(LightProbeSnapshot snap, int probeIndex)
        {
            return AccumulateSH(snap, probeIndex, 1f, default);
        }

        static SphericalHarmonicsL2 AccumulateSH(
            LightProbeSnapshot snap, int probeIndex, float weight, SphericalHarmonicsL2 sh)
        {
            if (probeIndex < 0) return sh;

            var coeffs = snap.shCoefficients;
            int baseIdx = probeIndex * 27;
            for (int c = 0; c < 3; c++)
            {
                int rowBase = baseIdx + c * 9;
                sh[c, 0] += coeffs[rowBase + 0] * weight;
                sh[c, 1] += coeffs[rowBase + 1] * weight;
                sh[c, 2] += coeffs[rowBase + 2] * weight;
                sh[c, 3] += coeffs[rowBase + 3] * weight;
                sh[c, 4] += coeffs[rowBase + 4] * weight;
                sh[c, 5] += coeffs[rowBase + 5] * weight;
                sh[c, 6] += coeffs[rowBase + 6] * weight;
                sh[c, 7] += coeffs[rowBase + 7] * weight;
                sh[c, 8] += coeffs[rowBase + 8] * weight;
            }
            return sh;
        }

        static SphericalHarmonicsL2 LerpSH(SphericalHarmonicsL2 a, SphericalHarmonicsL2 b, float t)
        {
            SphericalHarmonicsL2 result = default;
            float invT = 1f - t;
            for (int c = 0; c < 3; c++)
            {
                for (int coeff = 0; coeff < 9; coeff++)
                    result[c, coeff] = a[c, coeff] * invT + b[c, coeff] * t;
            }
            return result;
        }

        /// <summary>把 SH 在球面方向上求值，返回 RGB 颜色（用于预览色块）</summary>
        public static Color EvaluateSHColor(SphericalHarmonicsL2 sh, Vector3 dir)
        {
            Vector3[] dirs = { dir };
            Color[] colors = new Color[1];
            sh.Evaluate(dirs, colors);
            return colors[0];
        }
    }
}
#endif

// TetrahedralInterpolator.cs
// Delaunay 四面体化 + 重心坐标插值
// 复刻 Unity 原生 LightProbe 的四面体插值算法，作为逆距离加权（IDW）的替代方案
//
// 核心原理：
// 1. 对所有 Probe 位置做 Delaunay 四面体化（Bowyer-Watson 增量插入算法）
// 2. 查询时找到包含采样点的四面体，计算 4 个顶点的重心坐标作为权重
// 3. 对凸包外的点回退到最近邻逆距离加权（与 Unity 行为一致）
//
// 优势（相比 4-NN 逆距离加权）：
// - C0 连续过渡，无最近邻跳变伪影
// - 与 Unity 原生 LightProbe 插值行为一致
// - 更适合大空间、探针分布不均匀的场景
using System.Collections.Generic;
using UnityEngine;

namespace BYTools.EnvTimeline
{
    /// <summary>
    /// Probe 插值模式
    /// </summary>
    public enum ProbeInterpolationMode
    {
        /// <summary>4 近邻逆距离加权（原有方案，简单快速）</summary>
        InverseDistance = 0,
        /// <summary>Delaunay 四面体重心坐标插值（Unity 原生方案，过渡更平滑）</summary>
        Tetrahedral = 1,
    }

    /// <summary>
    /// 单个四面体：4 个顶点在 positions 数组中的索引
    /// </summary>
    public struct Tetrahedron
    {
        public int a, b, c, d;
        public Vector3 circumCenter;
        public float circumRadiusSqr;

        public Tetrahedron(int a, int b, int c, int d)
        {
            this.a = a;
            this.b = b;
            this.c = c;
            this.d = d;
            circumCenter = Vector3.zero;
            circumRadiusSqr = 0f;
        }
    }

    /// <summary>
    /// 四面体插值查询结果
    /// </summary>
    public struct TetraSampleResult
    {
        public int i0, i1, i2, i3;
        public float w0, w1, w2, w3;
        public bool valid;
        public bool isInsideTetrahedron; // false=凸包外回退

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
    }

    /// <summary>
    /// Delaunay 四面体化器 + 重心坐标插值查询
    /// 
    /// 使用 Bowyer-Watson 增量插入算法构建 Delaunay 四面体网格。
    /// 查询时通过点-四面体包含测试找到包含四面体，计算重心坐标权重。
    /// 凸包外的查询点回退到 4 近邻逆距离加权。
    /// </summary>
    public class TetrahedralInterpolator
    {
        // 输入数据
        Vector3[] _positions;
        int _pointCount;

        // 四面体列表（仅有效四面体，不含超四面体）
        List<Tetrahedron> _tetrahedra;

        // 空间加速结构：对每个采样点缓存上次命中的四面体索引（时间一致性优化）
        int _lastHitTetra = -1;

        /// <summary>四面体数量</summary>
        public int TetrahedronCount => _tetrahedra != null ? _tetrahedra.Count : 0;

        /// <summary>探针数量</summary>
        public int ProbeCount => _pointCount;

        /// <summary>是否已构建</summary>
        public bool IsBuilt => _tetrahedra != null && _tetrahedra.Count > 0;

        /// <summary>获取所有四面体（用于可视化）</summary>
        public List<Tetrahedron> Tetrahedra => _tetrahedra;

        /// <summary>
        /// 构建 Delaunay 四面体化
        /// </summary>
        public void Build(Vector3[] positions, int count)
        {
            _positions = positions;
            _pointCount = count;
            _tetrahedra = new List<Tetrahedron>();
            _lastHitTetra = -1;

            if (count < 4)
            {
                // 少于 4 个点无法构成四面体，直接跳过
                // 查询时会回退到 IDW
                return;
            }

            // 1. 计算包围盒
            Vector3 min = positions[0], max = positions[0];
            for (int i = 1; i < count; i++)
            {
                if (positions[i].x < min.x) min.x = positions[i].x;
                if (positions[i].y < min.y) min.y = positions[i].y;
                if (positions[i].z < min.z) min.z = positions[i].z;
                if (positions[i].x > max.x) max.x = positions[i].x;
                if (positions[i].y > max.y) max.y = positions[i].y;
                if (positions[i].z > max.z) max.z = positions[i].z;
            }

            // 包围盒尺寸，向外扩展一个安全余量
            Vector3 center = (min + max) * 0.5f;
            Vector3 size = max - min;
            float maxDim = Mathf.Max(size.x, size.y, size.z);
            // 确保不为零
            if (maxDim < 0.0001f) maxDim = 1f;
            float superRadius = maxDim * 20f; // 足够大

            // 2. 创建超四面体的 4 个虚拟顶点
            // 用一个正四面体包裹所有点
            // 顶点索引：count, count+1, count+2, count+3 为超四面体顶点
            Vector3 s0 = center + new Vector3(0f, superRadius, 0f);
            Vector3 s1 = center + new Vector3(-superRadius * 0.943f, -superRadius * 0.333f, superRadius * 0.545f);
            Vector3 s2 = center + new Vector3(superRadius * 0.943f, -superRadius * 0.333f, superRadius * 0.545f);
            Vector3 s3 = center + new Vector3(0f, -superRadius * 0.333f, -superRadius * 1.091f);

            // 3. 初始化：超四面体
            var tetraList = new List<Tetrahedron>();
            var superTetra = new Tetrahedron(count, count + 1, count + 2, count + 3);
            ComputeCircumsphere(ref superTetra, s0, s1, s2, s3);
            tetraList.Add(superTetra);

            // 4. Bowyer-Watson 增量插入
            for (int i = 0; i < count; i++)
            {
                Vector3 p = positions[i];

                // 4a. 找到所有外接球包含 p 的四面体（"坏四面体"）
                var badTetras = new List<int>();
                for (int j = 0; j < tetraList.Count; j++)
                {
                    var t = tetraList[j];
                    float distSqr = (p - t.circumCenter).sqrMagnitude;
                    if (distSqr < t.circumRadiusSqr)
                        badTetras.Add(j);
                }

                if (badTetras.Count == 0)
                {
                    // 理论上不应发生（p 必在超四面体内），跳过
                    continue;
                }

                // 4b. 提取坏四面体的边界面（只被一个坏四面体引用的面）
                // 面 = (v0, v1, v2) 排序后的元组
                var boundaryFaces = new List<(int v0, int v1, int v2, int v3_opposite)>();
                var faceCount = new Dictionary<long, int>();
                var faceData = new Dictionary<long, (int v0, int v1, int v2, int v3_opposite)>();

                foreach (int bi in badTetras)
                {
                    var t = tetraList[bi];
                    // 四面体的 4 个面
                    AddFace(t.a, t.b, t.c, t.d, faceCount, faceData);
                    AddFace(t.a, t.b, t.d, t.c, faceCount, faceData);
                    AddFace(t.a, t.c, t.d, t.b, faceCount, faceData);
                    AddFace(t.b, t.c, t.d, t.a, faceCount, faceData);
                }

                // 边界面 = 只被一个坏四面体引用的面
                foreach (var kv in faceCount)
                {
                    if (kv.Value == 1)
                    {
                        var face = faceData[kv.Key];
                        boundaryFaces.Add(face);
                    }
                }

                // 4c. 删除坏四面体（从后往前删以保持索引）
                badTetras.Sort();
                badTetras.Reverse();
                foreach (int bi in badTetras)
                    tetraList.RemoveAt(bi);

                // 4d. 用 p 和每个边界面创建新四面体
                foreach (var face in boundaryFaces)
                {
                    var newTetra = new Tetrahedron(face.v0, face.v1, face.v2, i);

                    // 获取新四面体的 4 个顶点位置
                    Vector3 pa = GetPos(face.v0, s0, s1, s2, s3);
                    Vector3 pb = GetPos(face.v1, s0, s1, s2, s3);
                    Vector3 pc = GetPos(face.v2, s0, s1, s2, s3);
                    Vector3 pd = p;

                    ComputeCircumsphere(ref newTetra, pa, pb, pc, pd);
                    tetraList.Add(newTetra);
                }
            }

            // 5. 移除包含超四面体顶点的四面体
            var finalTetras = new List<Tetrahedron>();
            foreach (var t in tetraList)
            {
                if (t.a >= count || t.b >= count || t.c >= count || t.d >= count)
                    continue;
                finalTetras.Add(t);
            }

            _tetrahedra = finalTetras;
        }

        Vector3 GetPos(int index, Vector3 s0, Vector3 s1, Vector3 s2, Vector3 s3)
        {
            if (index < _pointCount) return _positions[index];
            switch (index - _pointCount)
            {
                case 0: return s0;
                case 1: return s1;
                case 2: return s2;
                case 3: return s3;
            }
            return Vector3.zero;
        }

        static void AddFace(int v0, int v1, int v2, int v3Opposite,
            Dictionary<long, int> faceCount,
            Dictionary<long, (int, int, int, int)> faceData)
        {
            // 排序 3 个顶点索引生成唯一 key
            int a = v0, b = v1, c = v2;
            if (a > b) { int t = a; a = b; b = t; }
            if (b > c) { int t = b; b = c; c = t; }
            if (a > b) { int t = a; a = b; b = t; }

            long key = (long)a * 100000 + (long)b * 1000 + c;

            if (!faceCount.ContainsKey(key))
            {
                faceCount[key] = 1;
                faceData[key] = (v0, v1, v2, v3Opposite);
            }
            else
            {
                faceCount[key]++;
            }
        }

        /// <summary>
        /// 计算四面体的外接球（圆心和半径平方）
        /// </summary>
        static void ComputeCircumsphere(ref Tetrahedron t, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            // 外接球圆心 = 线性方程组 (P - A)·(B - A) = 0.5|B-A|² 等的解
            // 参考标准算法：计算外心
            Vector3 ab = b - a;
            Vector3 ac = c - a;
            Vector3 ad = d - a;

            // 求解 |O - A|² = |O - B|² = |O - C|² = |O - D|²
            // 即 O·(B-A) = 0.5(|B|²-|A|²) 等
            // 设 O = A + s*AB + t*AC + u*AD，则有：
            // s*(AB·AB) + t*(AB·AC) + u*(AB·AD) = 0.5*AB·AB
            // s*(AC·AB) + t*(AC·AC) + u*(AC·AD) = 0.5*AC·AC
            // s*(AD·AB) + t*(AD·AC) + u*(AD·AD) = 0.5*AD·AD

            float ab_ab = Vector3.Dot(ab, ab);
            float ab_ac = Vector3.Dot(ab, ac);
            float ab_ad = Vector3.Dot(ab, ad);
            float ac_ac = Vector3.Dot(ac, ac);
            float ac_ad = Vector3.Dot(ac, ad);
            float ad_ad = Vector3.Dot(ad, ad);

            // 右侧
            float rhs0 = ab_ab * 0.5f;
            float rhs1 = ac_ac * 0.5f;
            float rhs2 = ad_ad * 0.5f;

            // 3x3 线性方程组求解（克莱姆法则）
            float det = ab_ab * (ac_ac * ad_ad - ac_ad * ac_ad)
                      - ab_ac * (ab_ac * ad_ad - ac_ad * ab_ad)
                      + ab_ad * (ab_ac * ac_ad - ac_ac * ab_ad);

            if (Mathf.Abs(det) < 1e-12f)
            {
                // 退化情况（共面），给一个大的外接球
                t.circumCenter = (a + b + c + d) * 0.25f;
                t.circumRadiusSqr = Mathf.Max(
                    (t.circumCenter - a).sqrMagnitude,
                    (t.circumCenter - b).sqrMagnitude,
                    (t.circumCenter - c).sqrMagnitude,
                    (t.circumCenter - d).sqrMagnitude) + 1f;
                return;
            }

            float invDet = 1f / det;

            float s = (rhs0 * (ac_ac * ad_ad - ac_ad * ac_ad)
                     - ab_ac * (rhs1 * ad_ad - ac_ad * rhs2)
                     + ab_ad * (rhs1 * ac_ad - ac_ac * rhs2)) * invDet;

            float tt = (ab_ab * (rhs1 * ad_ad - ac_ad * rhs2)
                      - rhs0 * (ab_ac * ad_ad - ac_ad * ab_ad)
                      + ab_ad * (ab_ac * rhs2 - rhs1 * ab_ad)) * invDet;

            float u = (ab_ab * (ac_ac * rhs2 - rhs1 * ac_ad)
                     - ab_ac * (ab_ac * rhs2 - rhs1 * ab_ad)
                     + rhs0 * (ab_ac * ac_ad - ac_ac * ab_ad)) * invDet;

            t.circumCenter = a + s * ab + tt * ac + u * ad;
            float rSqr = (t.circumCenter - a).sqrMagnitude;
            t.circumRadiusSqr = rSqr;
        }

        /// <summary>
        /// 在采样位置做四面体重心坐标插值
        /// </summary>
        public TetraSampleResult Sample(Vector3 position)
        {
            var result = new TetraSampleResult();
            result.i0 = result.i1 = result.i2 = result.i3 = -1;
            result.w0 = result.w1 = result.w2 = result.w3 = 0f;
            result.valid = false;
            result.isInsideTetrahedron = false;

            if (_positions == null || _pointCount == 0)
                return result;

            // 少于 4 个点直接用 IDW 回退
            if (_pointCount < 4 || _tetrahedra == null || _tetrahedra.Count == 0)
                return SampleIDW(position, result);

            // 1. 尝试上次命中的四面体（时间一致性优化）
            if (_lastHitTetra >= 0 && _lastHitTetra < _tetrahedra.Count)
            {
                var t = _tetrahedra[_lastHitTetra];
                if (TryBarycentric(position, t, ref result))
                {
                    result.valid = true;
                    result.isInsideTetrahedron = true;
                    return result;
                }
            }

            // 2. 遍历所有四面体，找到包含该点的
            for (int i = 0; i < _tetrahedra.Count; i++)
            {
                var t = _tetrahedra[i];
                if (TryBarycentric(position, t, ref result))
                {
                    _lastHitTetra = i;
                    result.valid = true;
                    result.isInsideTetrahedron = true;
                    return result;
                }
            }

            // 3. 凸包外：回退到 IDW
            return SampleIDW(position, result);
        }

        /// <summary>
        /// 计算点在四面体内的重心坐标。
        /// 如果 4 个坐标都在 [0,1] 范围内，则点在四面体内。
        /// </summary>
        bool TryBarycentric(Vector3 p, Tetrahedron t, ref TetraSampleResult result)
        {
            Vector3 a = _positions[t.a];
            Vector3 b = _positions[t.b];
            Vector3 c = _positions[t.c];
            Vector3 d = _positions[t.d];

            // 重心坐标计算
            // 设 P = w0*A + w1*B + w2*C + w3*D, w0+w1+w2+w3=1
            // 解 3x3 线性方程组：(B-A, C-A, D-A) * (w1, w2, w3)^T = (P-A)

            Vector3 ap = p - a;
            Vector3 ab = b - a;
            Vector3 ac = c - a;
            Vector3 ad = d - a;

            // 矩阵 M = [ab, ac, ad]（列向量）
            float m00 = ab.x, m01 = ac.x, m02 = ad.x;
            float m10 = ab.y, m11 = ac.y, m12 = ad.y;
            float m20 = ab.z, m21 = ac.z, m22 = ad.z;

            float det = m00 * (m11 * m22 - m12 * m21)
                      - m01 * (m10 * m22 - m12 * m20)
                      + m02 * (m10 * m21 - m11 * m20);

            if (Mathf.Abs(det) < 1e-12f)
                return false; // 退化四面体

            float invDet = 1f / det;

            // w1 = det(ap, ac, ad) / det
            float w1 = (ap.x * (m11 * m22 - m12 * m21)
                      - m01 * (ap.y * m22 - m12 * ap.z)
                      + m02 * (ap.y * m21 - m11 * ap.z)) * invDet;

            // w2 = det(ab, ap, ad) / det
            float w2 = (m00 * (ap.y * m22 - m12 * ap.z)
                      - ap.x * (m10 * m22 - m12 * m20)
                      + m02 * (m10 * ap.z - ap.y * m20)) * invDet;

            // w3 = det(ab, ac, ap) / det
            float w3 = (m00 * (m11 * ap.z - ap.y * m21)
                      - m01 * (m10 * ap.z - ap.y * m20)
                      + ap.x * (m10 * m21 - m11 * m20)) * invDet;

            float w0 = 1f - w1 - w2 - w3;

            // 容差：允许略微超出边界（处理数值误差）
            const float eps = -0.0001f;

            if (w0 < eps || w1 < eps || w2 < eps || w3 < eps)
                return false;

            // 钳制到 [0,1] 并归一化
            w0 = Mathf.Max(w0, 0f);
            w1 = Mathf.Max(w1, 0f);
            w2 = Mathf.Max(w2, 0f);
            w3 = Mathf.Max(w3, 0f);
            float sum = w0 + w1 + w2 + w3;
            if (sum <= 0f) return false;

            result.i0 = t.a; result.w0 = w0 / sum;
            result.i1 = t.b; result.w1 = w1 / sum;
            result.i2 = t.c; result.w2 = w2 / sum;
            result.i3 = t.d; result.w3 = w3 / sum;

            return true;
        }

        /// <summary>
        /// 4 近邻逆距离加权（凸包外回退）
        /// </summary>
        TetraSampleResult SampleIDW(Vector3 position, TetraSampleResult result)
        {
            int i0 = -1, i1 = -1, i2 = -1, i3 = -1;
            float d0 = float.MaxValue, d1 = float.MaxValue, d2 = float.MaxValue, d3 = float.MaxValue;

            for (int i = 0; i < _pointCount; i++)
            {
                float d = (_positions[i] - position).sqrMagnitude;
                if (d <= 0.000001f)
                {
                    result.i0 = i; result.w0 = 1f;
                    result.valid = true;
                    result.isInsideTetrahedron = false;
                    return result;
                }

                if (d < d0)
                {
                    d3 = d2; i3 = i2;
                    d2 = d1; i2 = i1;
                    d1 = d0; i1 = i0;
                    d0 = d; i0 = i;
                }
                else if (d < d1)
                {
                    d3 = d2; i3 = i2;
                    d2 = d1; i2 = i1;
                    d1 = d; i1 = i;
                }
                else if (d < d2)
                {
                    d3 = d2; i3 = i2;
                    d2 = d; i2 = i;
                }
                else if (d < d3)
                {
                    d3 = d; i3 = i;
                }
            }

            float w0 = i0 >= 0 ? 1f / Mathf.Max(d0, 0.000001f) : 0f;
            float w1 = i1 >= 0 ? 1f / Mathf.Max(d1, 0.000001f) : 0f;
            float w2 = i2 >= 0 ? 1f / Mathf.Max(d2, 0.000001f) : 0f;
            float w3 = i3 >= 0 ? 1f / Mathf.Max(d3, 0.000001f) : 0f;
            float sum = w0 + w1 + w2 + w3;
            if (sum <= 0f) return result;

            result.i0 = i0; result.w0 = w0 / sum;
            result.i1 = i1; result.w1 = w1 / sum;
            result.i2 = i2; result.w2 = w2 / sum;
            result.i3 = i3; result.w3 = w3 / sum;
            result.valid = true;
            result.isInsideTetrahedron = false;
            return result;
        }

        /// <summary>
        /// 找到采样点所在的四面体索引（用于可视化）
        /// </summary>
        public int FindContainingTetrahedron(Vector3 position)
        {
            if (_tetrahedra == null) return -1;

            // 尝试上次命中
            if (_lastHitTetra >= 0 && _lastHitTetra < _tetrahedra.Count)
            {
                var t = _tetrahedra[_lastHitTetra];
                var result = new TetraSampleResult();
                if (TryBarycentric(position, t, ref result))
                    return _lastHitTetra;
            }

            for (int i = 0; i < _tetrahedra.Count; i++)
            {
                var t = _tetrahedra[i];
                var result = new TetraSampleResult();
                if (TryBarycentric(position, t, ref result))
                    return i;
            }

            return -1;
        }
    }
}

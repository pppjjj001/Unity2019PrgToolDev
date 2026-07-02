// SHRotation.cs
// SH L2 (9 系数 × 3 通道) 旋转工具
// 用于支持 Prefab 旋转时正确变换 Light Probe 球谐系数
using UnityEngine;
using UnityEngine.Rendering;

namespace BYTools.EnvTimeline
{
    /// <summary>
    /// 球谐函数（SH）L2 旋转工具。
    ///
    /// 核心数学原理：
    /// - L0 (常数项): 不随旋转变化
    /// - L1 (3 系数): 本质是方向向量，可直接用旋转矩阵变换
    /// - L2 (5 系数): 本质是对称无迹张量(3×3)，用 T' = R·T·Rᵀ 变换
    ///
    /// SH L2 共 9 个系数，按 [L0, L1x, L1z, L1y, L2_0..L2_4] 排列，
    /// 与 Unity SphericalHarmonicsL2 的索引 [channel, band] 一致：
    ///   [c,0]=L0  [c,1]=L1y  [c,2]=L1z  [c,3]=L1x
    ///   [c,4]=L2_xy  [c,5]=L2_yz  [c,6]=L2_(3z²-1)  [c,7]=L2_xz  [c,8]=L2_(x²-y²)
    /// </summary>
    public static class SHRotation
    {
        // ---- L1 旋转：直接用 3×3 矩阵变换方向向量 ----
        // SH L1 的基函数为: Y₁₋₁ ∝ y, Y₁₀ ∝ z, Y₁₁ ∝ x
        // Unity 内部排列: [c,1]=L1y, [c,2]=L1z, [c,3]=L1x
        // 因此从 SH 取出 (x,y,z) = (sh[c,3], sh[c,1], sh[c,2])
        // 旋转后再写回同样位置

        /// <summary>
        /// 就地旋转 SphericalHarmonicsL2 的所有 3 个颜色通道。
        /// </summary>
        /// <param name="sh">要旋转的 SH（会被原地修改）</param>
        /// <param name="rotation">旋转矩阵（World←Local，即把旧方向变换到新方向）</param>
        public static void RotateSH(ref SphericalHarmonicsL2 sh, Matrix4x4 rotation)
        {
            // L0 不变

            // ---- L1: 旋转方向向量 ----
            for (int c = 0; c < 3; c++)
            {
                float x = sh[c, 3];
                float y = sh[c, 1];
                float z = sh[c, 2];

                sh[c, 3] = rotation.m00 * x + rotation.m01 * y + rotation.m02 * z;
                sh[c, 1] = rotation.m10 * x + rotation.m11 * y + rotation.m12 * z;
                sh[c, 2] = rotation.m20 * x + rotation.m21 * y + rotation.m22 * z;
            }

            // ---- L2: 旋转对称无迹张量 ----
            // 将 5 个 L2 系数还原为 3×3 对称无迹张量 T，做 T' = R·T·Rᵀ，
            // 再从 T' 提取 5 个新系数。
            //
            // SH L2 → 张量映射:
            //   T = | x²-y²     →  T11-T22  →  sh[c,8]
            //       xy          →  2·T12    →  sh[c,4]
            //       xz          →  2·T13    →  sh[c,7]
            //       yz          →  2·T23    →  sh[c,5]
            //       (3z²-1)/3   →  T33(无迹)→  sh[c,6]
            //
            // 注意比例因子需与 SH 基函数定义匹配。
            // 这里使用标准 SH L2 基函数到张量的转换。

            for (int c = 0; c < 3; c++)
            {
                // 从 SH 系数提取张量分量（带正确比例因子）
                float sh4 = sh[c, 4]; // xy
                float sh5 = sh[c, 5]; // yz
                float sh6 = sh[c, 6]; // 3z²-1
                float sh7 = sh[c, 7]; // xz
                float sh8 = sh[c, 8]; // x²-y²

                // 构建 3×3 对称无迹张量
                // 归一化因子: 基函数系数 → 张量分量
                float xx_yy = sh8;       // T11 - T22
                float xy = sh4;          // 2*T12  → T12 = sh4/2
                float xz = sh7;          // 2*T13  → T13 = sh7/2
                float yz = sh5;          // 2*T23  → T23 = sh5/2
                float zz = sh6 / 3f;     // 3z²-1 → 无迹化后 T33 = sh6/3

                // T11 + T22 + T33 = 0 (无迹)  =>  T11 + T22 = -T33
                // T11 - T22 = sh8
                // => T11 = (sh8 - T33) / 2,  T22 = (-sh8 - T33) / 2
                float t33 = zz;
                float t11 = (xx_yy - t33) * 0.5f;
                float t22 = (-xx_yy - t33) * 0.5f;
                float t12 = xy * 0.5f;
                float t13 = xz * 0.5f;
                float t23 = yz * 0.5f;

                // 旋转: T' = R · T · Rᵀ
                float r00 = rotation.m00, r01 = rotation.m01, r02 = rotation.m02;
                float r10 = rotation.m10, r11r = rotation.m11, r12 = rotation.m12;
                float r20 = rotation.m20, r21 = rotation.m21, r22 = rotation.m22;

                // T·Rᵀ 的各列 = T · (R 的行)
                // 第一列: T·[r00, r10, r20]ᵀ
                float tr_00 = t11 * r00 + t12 * r10 + t13 * r20;
                float tr_10 = t12 * r00 + t22 * r10 + t23 * r20;
                float tr_20 = t13 * r00 + t23 * r10 + t33 * r20;

                // 第二列: T·[r01, r11, r21]ᵀ
                float tr_01 = t11 * r01 + t12 * r11r + t13 * r21;
                float tr_11 = t12 * r01 + t22 * r11r + t23 * r21;
                float tr_21 = t13 * r01 + t23 * r11r + t33 * r21;

                // 第三列: T·[r02, r12, r22]ᵀ
                float tr_02 = t11 * r02 + t12 * r12 + t13 * r22;
                float tr_12 = t12 * r02 + t22 * r12 + t23 * r22;
                float tr_22 = t13 * r02 + t23 * r12 + t33 * r22;

                // R · (T·Rᵀ) → 取对称无迹部分
                float tp11 = r00 * tr_00 + r01 * tr_10 + r02 * tr_20;
                float tp12 = r00 * tr_01 + r01 * tr_11 + r02 * tr_21;
                float tp13 = r00 * tr_02 + r01 * tr_12 + r02 * tr_22;
                float tp21 = r10 * tr_00 + r11r * tr_10 + r12 * tr_20;
                float tp22 = r10 * tr_01 + r11r * tr_11 + r12 * tr_21;
                float tp23 = r10 * tr_02 + r11r * tr_12 + r12 * tr_22;
                float tp31 = r20 * tr_00 + r21 * tr_10 + r22 * tr_20;
                float tp32 = r20 * tr_01 + r21 * tr_11 + r22 * tr_21;
                float tp33 = r20 * tr_02 + r21 * tr_12 + r22 * tr_22;

                // 对称化（数值误差防护）
                float s11 = tp11;
                float s22 = tp22;
                float s33 = tp33;
                float s12 = (tp12 + tp21) * 0.5f;
                float s13 = (tp13 + tp31) * 0.5f;
                float s23 = (tp23 + tp32) * 0.5f;

                // 去除迹（强制无迹，消除数值漂移）
                float trace = (s11 + s22 + s33) / 3f;
                s11 -= trace;
                s22 -= trace;
                s33 -= trace;

                // 提取新 SH L2 系数（逆映射）
                sh[c, 8] = s11 - s22;       // x²-y²
                sh[c, 4] = 2f * s12;        // xy
                sh[c, 7] = 2f * s13;        // xz
                sh[c, 5] = 2f * s23;        // yz
                sh[c, 6] = 3f * s33;         // 3z²-1
            }
        }

        /// <summary>
        /// 旋转并返回新的 SH（不修改输入）
        /// </summary>
        public static SphericalHarmonicsL2 RotateSH(
            SphericalHarmonicsL2 sh, Matrix4x4 rotation)
        {
            var result = sh;
            RotateSH(ref result, rotation);
            return result;
        }

        /// <summary>
        /// 从世界→世界的旋转差量构建旋转矩阵。
        /// 给定烘焙时的旋转 Q_bake 和当前旋转 Q_current，
        /// 计算 delta = Q_current * Inverse(Q_bake)，
        /// 该矩阵将旧方向（烘焙时）变换到新方向（当前）。
        /// </summary>
        public static Matrix4x4 BuildDeltaRotation(Quaternion bakeRotation, Quaternion currentRotation)
        {
            Quaternion delta = currentRotation * Quaternion.Inverse(bakeRotation);
            return Matrix4x4.Rotate(delta);
        }
    }
}

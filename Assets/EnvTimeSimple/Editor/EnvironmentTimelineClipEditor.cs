//EnvironmentTimelineClipEditor.cs
// 在 Unity Timeline 编辑窗口中，于 Clip 背景上预览显示每个环境时间节点的位置。
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using Hotfix.Core.EnvTimelineSimple;

namespace UnityEditor.EnvTimelineSimple
{
    /// <summary>
    /// 自定义 Timeline Clip 编辑器：在 Clip 背景上绘制环境时间节点的位置标记。
    /// 通过 [CustomTimelineEditor] 特性自动关联到 EnvironmentTimelinePlayableAsset。
    /// </summary>
    [CustomTimelineEditor(typeof(EnvironmentTimelinePlayableAsset))]
    public class EnvironmentTimelineClipEditor : ClipEditor
    {
        // ---- 配色（与 EnvTimelineSimpleEditorWindow 保持一致）----
        static readonly Color CLR_NODE_BAKED   = new Color(0.4f, 1f, 0.6f);
        static readonly Color CLR_NODE_UNBAKED = new Color(0.3f, 0.7f, 1f);
        static readonly Color CLR_LINE         = new Color(1f, 1f, 1f, 0.3f);

        static GUIStyle s_NodeNameStyle;
        static GUIStyle s_TimeLabelStyle;
        static GUIStyle s_RangeStyle;

        static void EnsureStyles()
        {
            if (s_NodeNameStyle == null)
            {
                s_NodeNameStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    fontSize = 8,
                    alignment = TextAnchor.LowerCenter,
                    clipping = TextClipping.Clip,
                    normal = { textColor = new Color(1f, 1f, 1f, 0.9f) }
                };
            }
            if (s_TimeLabelStyle == null)
            {
                s_TimeLabelStyle = new GUIStyle(EditorStyles.whiteMiniLabel)
                {
                    fontSize = 8,
                    alignment = TextAnchor.UpperCenter,
                    clipping = TextClipping.Clip
                };
            }
            if (s_RangeStyle == null)
            {
                s_RangeStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    fontSize = 8,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.UpperLeft,
                    normal = { textColor = new Color(0.5f, 0.85f, 1f, 0.7f) }
                };
            }
        }

        public override ClipDrawOptions GetClipOptions(TimelineClip clip)
        {
            var options = base.GetClipOptions(clip);
            options.tooltip = "环境时间轴 Clip — 背景显示各节点位置";
            return options;
        }

        public override void DrawBackground(TimelineClip clip, ClipBackgroundRegion region)
        {
            EnsureStyles();

            var asset = clip.asset as EnvironmentTimelinePlayableAsset;
            if (asset == null) return;

            // 获取 Track 绑定的 Controller
            var director = TimelineEditor.inspectedDirector;
            if (director == null) return;

            var track = clip.GetParentTrack();
            if (track == null) return;

            var controller = director.GetGenericBinding(track) as EnvironmentTimelineController;
            if (controller == null || controller.timelineData == null) return;

            var data = controller.timelineData;
            if (data.nodes == null || data.nodes.Count == 0) return;

            // ---- 计算环境时间映射范围 ----
            float envStart = asset.startTime;
            float envEnd   = asset.endTime > 0 ? asset.endTime : data.totalDuration;
            float envSpan  = envEnd - envStart;

            double clipDuration = clip.duration;
            double regionSpan   = region.endTime - region.startTime;
            if (regionSpan <= 0.0001) return;

            Rect clipRect = region.position;

            // ---- 绘制时间范围标注 ----
            string rangeText = $"Env [{envStart:F1} → {envEnd:F1}]  ({asset.remapMode})";
            GUI.Label(new Rect(clipRect.x + 4, clipRect.y + 1, clipRect.width - 8, 12),
                rangeText, s_RangeStyle);

            // ---- 逐节点绘制 ----
            for (int i = 0; i < data.nodes.Count; i++)
            {
                var node = data.nodes[i];
                float nodeTime = node.time;

                // 将节点的环境时间映射为 Clip 内相对时间（0 ~ clipDuration）
                double clipTimeRel;
                switch (asset.remapMode)
                {
                    case TimeRemapMode.PercentageMap:
                    case TimeRemapMode.ScaledMap:
                        if (envSpan <= 0.0001f) continue;
                        clipTimeRel = clipDuration * (nodeTime - envStart) / envSpan;
                        break;

                    case TimeRemapMode.DirectMap:
                        // 直接映射：clip 相对时间 = 环境时间
                        clipTimeRel = nodeTime;
                        break;

                    default:
                        continue;
                }

                // 仅绘制可见区域内的节点
                if (clipTimeRel < region.startTime - 0.01 ||
                    clipTimeRel > region.endTime + 0.01)
                    continue;

                // 映射到像素坐标
                float visibleRatio = (float)((clipTimeRel - region.startTime) / regionSpan);
                float x = clipRect.x + clipRect.width * visibleRatio;

                // 节点颜色（与编辑器窗口配色一致）
                Color nodeColor = node.customSH.IsValid ? CLR_NODE_BAKED : CLR_NODE_UNBAKED;

                // ---- 绘制竖向参考线 ----
                EditorGUI.DrawRect(
                    new Rect(x - 0.5f, clipRect.y, 1f, clipRect.height),
                    CLR_LINE);

                // ---- 绘制节点标记（菱形 = 两个旋转的小方块）----
                float markerSize = 7f;
                float cy = clipRect.y + clipRect.height * 0.5f;
                Rect marker = new Rect(x - markerSize * 0.5f, cy - markerSize * 0.5f,
                                       markerSize, markerSize);

                // 深色底（增强对比）
                EditorGUI.DrawRect(new Rect(marker.x - 1, marker.y - 1,
                    marker.width + 2, marker.height + 2),
                    new Color(0, 0, 0, 0.6f));
                // 节点色
                EditorGUI.DrawRect(marker, nodeColor);

                // ---- 绘制节点名称（上方）----
                // 只在 Clip 宽度足够时显示名称，避免拥挤
                if (clipRect.width > 40f)
                {
                    float labelW = Mathf.Min(60f, clipRect.width * 0.3f);
                    GUI.Label(new Rect(x - labelW * 0.5f, clipRect.y + 12, labelW, 12),
                        node.nodeName, s_NodeNameStyle);
                }

                // ---- 绘制时间值（下方）----
                if (clipRect.width > 30f)
                {
                    float labelW = Mathf.Min(40f, clipRect.width * 0.25f);
                    GUI.Label(new Rect(x - labelW * 0.5f, clipRect.yMax - 11, labelW, 10),
                        nodeTime.ToString("F1"), s_TimeLabelStyle);
                }
            }
        }
    }
}
#endif

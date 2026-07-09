//EnvironmentTimelinePlayableAsset.cs
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Hotfix.Core.EnvTimelineSimple
{
    [System.Serializable]
    public class EnvironmentTimelinePlayableAsset : PlayableAsset, ITimelineClipAsset
    {
        [Header("时间映射")]
        [Tooltip("Timeline 区间映射模式")]
        public TimeRemapMode remapMode = TimeRemapMode.PercentageMap;

        [Tooltip("映射到时间轴的起始时间")]
        public float startTime = 0f;

        [Tooltip("映射到时间轴的结束时间（-1 表示使用 totalDuration）")]
        public float endTime = -1f;

        [Header("运行时设置")]
        [Tooltip("Timeline 播放时是否自动控制")]
        public bool autoControl = true;

        [Tooltip("停止时是否保持最后状态")]
        public bool holdOnStop = true;

        public ClipCaps clipCaps => ClipCaps.Extrapolation | ClipCaps.Blending;

        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            var playable = ScriptPlayable<EnvironmentTimelinePlayableBehaviour>.Create(graph);
            var behaviour = playable.GetBehaviour();

            behaviour.remapMode = remapMode;
            behaviour.startTime = startTime;
            behaviour.endTime = endTime;
            behaviour.autoControl = autoControl;
            behaviour.holdOnStop = holdOnStop;

            return playable;
        }
    }

    public enum TimeRemapMode
    {
        [Tooltip("按百分比映射：Timeline 的 0-100% 映射到环境时间轴的 startTime-endTime")]
        PercentageMap,

        [Tooltip("直接映射：Timeline 时间直接对应环境时间轴时间")]
        DirectMap,

        [Tooltip("缩放映射：Timeline 时长缩放后对应环境时间")]
        ScaledMap
    }
}
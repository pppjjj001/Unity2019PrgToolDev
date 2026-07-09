﻿//EnvironmentTimelinePlayableBehaviour.cs
using UnityEngine;
using UnityEngine.Playables;

namespace BYTools.EnvTimeline
{
    public class EnvironmentTimelineProPlayableBehaviour : PlayableBehaviour
    {
        public TimeRemapMode remapMode;
        public float startTime;
        public float endTime;
        public bool autoControl;
        public bool holdOnStop;

        private EnvironmentTimelineProController _controller;
        private bool _wasControlling = false;
        private float _lastTime = -1f;

        public override void OnPlayableCreate(Playable playable)
        {
            // 初始化
        }


        public override void ProcessFrame(Playable playable, UnityEngine.Playables.FrameData info, object playerData)
        {
            if (!autoControl) return;

            // 如果 TrackMixer 已经在处理，跳过（避免重复 Apply）
            if (playerData is EnvironmentTimelineProController)
                return;

            // 没有 TrackMixer 绑定时的兼容兜底
            if (_controller == null)
            {
                _controller = Object.FindObjectOfType<EnvironmentTimelineProController>();
            }

            if (_controller == null || _controller.timelineData == null)
                return;

            // endTime <= 0 时使用 timelineData.totalDuration 作为默认值
            float actualEndTime = endTime > 0 ? endTime : _controller.timelineData.totalDuration;

            // 计算映射后的环境时间
            float clipTime = (float)playable.GetTime();
            float clipDuration = (float)playable.GetDuration();
            float envTime = RemapTime(clipTime, clipDuration, actualEndTime);

            if (Mathf.Abs(envTime - _lastTime) > 0.001f)
            {
                _controller.currentTime = envTime;
                _controller.ApplyAtCurrentTime();
                _lastTime = envTime;
                _wasControlling = true;
            }
        }

        public override void OnBehaviourPlay(Playable playable, UnityEngine.Playables.FrameData info)
        {
            _wasControlling = false;
        }

        public override void OnBehaviourPause(Playable playable, UnityEngine.Playables.FrameData info)
        {
            if (_controller != null && _wasControlling && !holdOnStop)
            {
                // 可以选择重置或保持
            }
        }

        private float RemapTime(float clipTime, float clipDuration, float actualEndTime)
        {
            switch (remapMode)
            {
                case TimeRemapMode.PercentageMap:
                    // Timeline 的 0-100% 映射到 startTime-actualEndTime
                    float percent = clipDuration > 0 ? Mathf.Clamp01(clipTime / clipDuration) : 0f;
                    return Mathf.Lerp(startTime, actualEndTime, percent);

                case TimeRemapMode.DirectMap:
                    // 直接使用 Timeline 时间
                    return clipTime;

                case TimeRemapMode.ScaledMap:
                    // 缩放映射
                    float scale = clipDuration > 0 ? (actualEndTime - startTime) / clipDuration : 1f;
                    return startTime + clipTime * scale;

                default:
                    return clipTime;
            }
        }
    }
}

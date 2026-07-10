//EnvironmentTimelinePlayableAssetEditor.cs
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using UnityEditor.Timeline;
using Hotfix.Core.EnvTimelineSimple;

namespace BYTools.EnvTimelineSimple
{
    [CustomEditor(typeof(EnvironmentTimelinePlayableAsset))]
    public class EnvTimelineSimplePlayableAssetEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var asset = target as EnvironmentTimelinePlayableAsset;

            // ---- 快速打开编辑器窗口按钮 ----
            EditorGUILayout.Space(2);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("快捷操作", EditorStyles.boldLabel);

            var controller = FindBoundController();
            if (controller != null)
            {
                EditorGUILayout.LabelField(
                    $"绑定: {controller.gameObject.name}",
                    EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField(
                    "未检测到 Track 绑定的 Controller",
                    EditorStyles.miniLabel);
            }

            GUI.backgroundColor = new Color(0.4f, 0.9f, 1f);
            if (GUILayout.Button("🔓 Open Environment Timeline Simple 编辑器", GUILayout.Height(28)))
            {
                OpenEditorWindow(controller);
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);

            EditorGUILayout.HelpBox(
                "此 Clip 将 Unity Timeline 的时间映射到环境时间轴上。\n" +
                "可通过不同的映射模式控制时间关系。\n\n" +
                "⚠️ 环境数据通过 Track Binding 获取：\n" +
                "请在 Track 上绑定带有 EnvironmentTimelineController 的 GameObject。",
                MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("时间映射设置", EditorStyles.boldLabel);

            // 映射模式
            EditorGUILayout.PropertyField(serializedObject.FindProperty("remapMode"));

            var remapMode = (TimeRemapMode)serializedObject.FindProperty("remapMode").enumValueIndex;

            switch (remapMode)
            {
                case TimeRemapMode.PercentageMap:
                    EditorGUILayout.HelpBox(
                        "百分比映射：Timeline Clip 的 0%-100% 对应环境时间的 起始时间-结束时间\n" +
                        "例如：Clip 长度 10秒，起始=6.0，结束=18.0，则播放到 5秒 时环境时间为 12.0",
                        MessageType.None);
                    break;

                case TimeRemapMode.DirectMap:
                    EditorGUILayout.HelpBox(
                        "直接映射：Timeline 的时间值直接用作环境时间\n" +
                        "例如：Timeline 播放到 12.5 秒，环境时间就是 12.5",
                        MessageType.None);
                    break;

                case TimeRemapMode.ScaledMap:
                    EditorGUILayout.HelpBox(
                        "缩放映射：按比例缩放后映射\n" +
                        "适合需要精确控制时间跨度的情况",
                        MessageType.None);
                    break;
            }

            // 时间范围
            EditorGUILayout.PropertyField(serializedObject.FindProperty("startTime"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("endTime"));

            if (asset.endTime < 0)
            {
                EditorGUILayout.HelpBox(
                    "结束时间为 -1，运行时将自动使用 EnvironmentTimelineData.totalDuration",
                    MessageType.Info);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("运行时设置", EditorStyles.boldLabel);

            EditorGUILayout.PropertyField(serializedObject.FindProperty("autoControl"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("holdOnStop"));

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// 尝试从当前 Timeline 编辑器中查找此 Clip 所在 Track 绑定的 Controller。
        /// </summary>
        EnvironmentTimelineController FindBoundController()
        {
            var director = TimelineEditor.inspectedDirector;
            if (director == null) return null;

            var timelineAsset = director.playableAsset as TimelineAsset;
            if (timelineAsset == null) return null;

            foreach (var track in timelineAsset.GetOutputTracks())
            {
                foreach (var clip in track.GetClips())
                {
                    if (clip.asset == target)
                    {
                        return director.GetGenericBinding(track) as EnvironmentTimelineController;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 打开 EditorWindow 并尝试自动选中 Controller 的 GameObject。
        /// </summary>
        static void OpenEditorWindow(EnvironmentTimelineController controller)
        {
            if (controller != null)
            {
                Selection.activeGameObject = controller.gameObject;
            }

            var win = EditorWindow.GetWindow<EnvTimelineSimpleEditorWindow>("Env Timeline");
            win.Show();
            win.Repaint();
        }
    }
}
#endif

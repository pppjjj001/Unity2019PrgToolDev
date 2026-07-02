//EnvironmentTimelineControllerEditor.cs（更新版本）
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace BYTools.EnvTimelineSimple
{
    [CustomEditor(typeof(EnvironmentTimelineController))]
    public class EnvironmentTimelineControllerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var ctrl = (EnvironmentTimelineController)target;
            if (ctrl.timelineData == null) return;

            // Timeline 集成提示
            EditorGUILayout.Space(8);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("🎬 Unity Timeline 集成", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "在 Timeline 窗口中添加 'Environment Timeline Track'，\n" +
                "绑定此 Controller，即可通过 Timeline 控制环境时间。",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("快捷跳转", EditorStyles.boldLabel);

            int columns = 3;
            int n = ctrl.timelineData.nodes.Count;
            for (int i = 0; i < n; i += columns)
            {
                EditorGUILayout.BeginHorizontal();
                for (int j = 0; j < columns && i + j < n; j++)
                {
                    var node = ctrl.timelineData.nodes[i + j];
                    if (GUILayout.Button($"{node.nodeName}\n[{node.time:F2}]", GUILayout.Height(36)))
                    {
                        ctrl.JumpToNode(i + j);
                        SceneView.RepaintAll();
                    }
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(4);
            if (GUILayout.Button("立即应用当前时间", GUILayout.Height(26)))
            {
                ctrl.ApplyAtCurrentTime();
                SceneView.RepaintAll();
            }
            if (GUILayout.Button("清除所有 MPB", GUILayout.Height(22)))
            {
                ctrl.ClearAllMPB();
                SceneView.RepaintAll();
            }
        }
    }
}
#endif
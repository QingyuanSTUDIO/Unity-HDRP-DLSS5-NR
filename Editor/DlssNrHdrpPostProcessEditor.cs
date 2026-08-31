using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

namespace UnityRhi.DlssNr.Hdrp.Editor
{
    [VolumeComponentEditor(typeof(DlssNrHdrpPostProcess))]
    internal sealed class DlssNrHdrpPostProcessEditor : VolumeComponentEditor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("实际分辨率 / Actual Resolution", EditorStyles.boldLabel);
            if (Application.isPlaying && DlssNrHdrpPostProcess.LastInputWidth > 0)
            {
                string cameraName = string.IsNullOrEmpty(DlssNrHdrpPostProcess.LastCameraName)
                    ? "-" : DlssNrHdrpPostProcess.LastCameraName;
                EditorGUILayout.LabelField("Camera", cameraName);
                EditorGUILayout.LabelField(
                    "Input", $"{DlssNrHdrpPostProcess.LastInputWidth} x {DlssNrHdrpPostProcess.LastInputHeight}");
                EditorGUILayout.LabelField(
                    "DLSS-NR Output", $"{DlssNrHdrpPostProcess.LastOutputWidth} x {DlssNrHdrpPostProcess.LastOutputHeight}");
                EditorGUILayout.LabelField(
                    "Game Target", $"{DlssNrHdrpPostProcess.LastGameTargetWidth} x {DlssNrHdrpPostProcess.LastGameTargetHeight}");
                EditorGUILayout.LabelField(
                    "Scale", $"{(float)DlssNrHdrpPostProcess.LastOutputWidth / DlssNrHdrpPostProcess.LastInputWidth:0.###}x");
                Repaint();
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "进入 Play 并让 Game 相机渲染后，这里会显示最近一次实际使用的输入/输出尺寸。\n" +
                    "Enter Play mode and render with a Game camera to see the latest dimensions.",
                    MessageType.Info);
            }
        }
    }
}

#nullable enable
using NovaGames.Mobile.Ads;
using UnityEditor;
using UnityEngine;

namespace NovaGames.Mobile.Editor.Ads
{
    // Ad unit ID theo platform, không foldout:
    //   Interstitial   Android [..................]
    //                  iOS     [..................]
    [CustomPropertyDrawer(typeof(PlatformAdUnitId))]
    internal sealed class PlatformAdUnitIdDrawer : PropertyDrawer
    {
        const float PlatformLabelWidth = 52f;
        static readonly GUIContent Android = new GUIContent("Android");
        static readonly GUIContent Ios = new GUIContent("iOS");

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) =>
            EditorGUIUtility.singleLineHeight * 2 + EditorGUIUtility.standardVerticalSpacing;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            float lineHeight = EditorGUIUtility.singleLineHeight;
            var firstLine = new Rect(position.x, position.y, position.width, lineHeight);
            var content = EditorGUI.PrefixLabel(firstLine, GUIUtility.GetControlID(FocusType.Passive), label);

            int indent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            DrawPlatform(content, property.FindPropertyRelative("android"), Android);
            content.y += lineHeight + EditorGUIUtility.standardVerticalSpacing;
            DrawPlatform(content, property.FindPropertyRelative("ios"), Ios);
            EditorGUI.indentLevel = indent;
            EditorGUI.EndProperty();
        }

        static void DrawPlatform(Rect rect, SerializedProperty value, GUIContent platform)
        {
            EditorGUI.LabelField(new Rect(rect.x, rect.y, PlatformLabelWidth, rect.height), platform, EditorStyles.miniLabel);
            var field = new Rect(rect.x + PlatformLabelWidth, rect.y, rect.width - PlatformLabelWidth, rect.height);
            EditorGUI.PropertyField(field, value, GUIContent.none);
        }
    }
}

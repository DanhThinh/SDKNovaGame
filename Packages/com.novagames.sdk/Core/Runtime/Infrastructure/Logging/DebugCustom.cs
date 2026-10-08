using System.Diagnostics;
using System.Text;
using Unity.Serialization.Json;
using Debug = UnityEngine.Debug;

public class DebugCustom
{
    private static readonly StringBuilder sb = new(256);

    [Conditional("UNITY_EDITOR")]
    public static void LogWarning(params object[] content)
    {
        Debug.LogWarning(BuildString(content));
    }

    [Conditional("UNITY_EDITOR")]
    public static void Log(params object[] content)
    {
        sb.Clear();
        sb.Append("<color=\"#6DFF00\">");
        AppendContent(content);
        sb.Append("</color>");
        Debug.Log(sb.ToString());
    }

#if UNITY_EDITOR
    public static string ReturnLog(params object[] content)
    {
        string str = BuildString(content);
        Debug.Log(str);
        return str;
    }
#endif

    [Conditional("UNITY_EDITOR")]
    public static void LogError(params object[] content)
    {
        Debug.LogError(BuildString(content));
    }

    [Conditional("UNITY_EDITOR")]
    public static void LogException(System.Exception exception)
    {
        Debug.LogException(exception);
    }

    [Conditional("UNITY_EDITOR"), Conditional("GAME_ROCKET")]
    public static void LogColor(params object[] content)
    {
        sb.Clear();
        sb.Append("<color=\"#ffa500ff\">");
        AppendContent(content);
        sb.Append("</color>");
        Debug.Log(sb.ToString());
    }

    [Conditional("UNITY_EDITOR")]
    public static void LogColorJson(params object[] content)
    {
        sb.Clear();
        sb.Append("<color=\"#ffa500ff\">");
        AppendJsonContent(content);
        sb.Append("</color>");
        Debug.Log(sb.ToString());
    }

    [Conditional("UNITY_EDITOR")]
    public static void LogJson(params object[] content)
    {
        Debug.Log(BuildJsonString(content));
    }

    [Conditional("UNITY_EDITOR")]
    public static void LogErrorJson(params object[] content)
    {
        Debug.LogError(BuildJsonString(content));
    }

    private static string BuildString(params object[] content)
    {
        sb.Clear();
        AppendContent(content);
        return sb.ToString();
    }

    private static string BuildJsonString(params object[] content)
    {
        sb.Clear();
        AppendJsonContent(content);
        return sb.ToString();
    }

    private static void AppendContent(object[] content)
    {
        for (int i = 0; i < content.Length; i++)
        {
            if (i > 0) sb.Append("__");
            sb.Append(content[i]);
        }
    }

    private static void AppendJsonContent(object[] content)
    {
        for (int i = 0; i < content.Length; i++)
        {
            if (i > 0) sb.Append("__");
            sb.Append(JsonSerialization.ToJson(content[i]));
        }
    }
}

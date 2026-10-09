#nullable enable
using UnityEngine;

namespace NovaGames.Mobile.Bootstrap
{
    /// <summary>
    /// Chứa các module SDK dưới dạng GameObject con (mỗi vendor một con). Có sẵn trên prefab NovaSdk. Editor tự đồng bộ
    /// danh sách con với plugin vendor đang cài: thêm module còn thiếu, xóa module mất script; lúc build bỏ module không
    /// thuộc nền tảng đang build.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("NovaGames/Nova Modules")]
    public sealed class NovaModules : MonoBehaviour
    {
    }
}

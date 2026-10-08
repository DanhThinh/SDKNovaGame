# NovaGames Mobile SDK

Unity package `com.novagames.sdk` (Unity 6000.0+): Ads (MAX/AdMob), Firebase (Analytics, Remote Config, Crashlytics), Adjust, UMP/ATT, Unity IAP, local notifications, popup mất mạng và đánh giá app — tất cả qua API tĩnh `Nova*`.

## Cài đặt

### Cách 1 — Package Manager

*Window > Package Manager > + > Add package from git URL...* rồi dán:

```
https://github.com/DanhThinh/SDKNovaGame.git?path=/Packages/com.novagames.sdk#v1.0.0
```

### Cách 2 — Sửa `Packages/manifest.json`

```json
{
  "dependencies": {
    "com.novagames.sdk": "https://github.com/DanhThinh/SDKNovaGame.git?path=/Packages/com.novagames.sdk#v1.0.0"
  }
}
```

Mở lại Unity để resolve, sau đó commit cả `manifest.json` lẫn `packages-lock.json`.

> Luôn ghim tag (`#v1.0.0`). Không ghim tag thì game sẽ lấy commit mới nhất của nhánh mặc định.
>
> Repo private: máy cần đăng nhập Git có quyền đọc repo (Git Credential Manager hoặc SSH: `git@github.com:DanhThinh/SDKNovaGame.git?path=/Packages/com.novagames.sdk#v1.0.0`).

## Phiên bản

| Tag | Ghi chú |
|---|---|
| `v1.0.0` | Bản đầu tiên |

- **Nâng / lùi phiên bản:** đổi tag trong `manifest.json` của game (`#v1.0.0` → `#v1.0.1`), mở Unity, build thử rồi commit.
- Quy ước: **patch** sửa lỗi, **minor** thêm tính năng không phá API, **major** đổi/xóa API.

## Sau khi cài

Làm theo checklist **"Bắt đầu nhanh — import xong làm gì?"** ở đầu [`Docs/Guide.md`](Packages/com.novagames.sdk/Docs/Guide.md): tạo `NovaSdkSettings` + script Boot → chọn module → cài vendor → điền config → kiểm tra trước khi build.

Hướng dẫn chi tiết: [`Docs/Guide.md`](Packages/com.novagames.sdk/Docs/Guide.md) · Cấu trúc code: [`Docs/Overview.md`](Packages/com.novagames.sdk/Docs/Overview.md)

## Phát hành phiên bản mới (dành cho người phát triển SDK)

```powershell
./ci/release-sdk.ps1 -Version 1.0.1
git push
git push origin v1.0.1
```

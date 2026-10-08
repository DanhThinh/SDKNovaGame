// App Tracking Transparency cho NovaGames SDK (AppleAttPlatform.cs gọi qua DllImport "__Internal").
// Trạng thái trả về theo ATTrackingManagerAuthorizationStatus: 0 NotDetermined, 1 Restricted, 2 Denied, 3 Authorized;
// -1 = iOS dưới 14 (không có ATT).
#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <AppTrackingTransparency/AppTrackingTransparency.h>

typedef void (*NovaAttCallback)(int status);

// iOS chỉ hiện popup khi app đang active. Popup hệ thống khác (vd. xin quyền thông báo) làm app inactive và ATT trả
// NotDetermined ngay mà không hiện gì: chờ app active lại rồi hỏi lại, tối đa vài lần.
static const int NovaAttMaxAttempts = 3;

static void NovaAttRequest(NovaAttCallback callback, int attempt);

static void NovaAttWhenActive(NovaAttCallback callback, int attempt)
{
    if ([UIApplication sharedApplication].applicationState == UIApplicationStateActive)
    {
        NovaAttRequest(callback, attempt);
        return;
    }
    __block id observer = [[NSNotificationCenter defaultCenter]
        addObserverForName:UIApplicationDidBecomeActiveNotification
                    object:nil
                     queue:[NSOperationQueue mainQueue]
                usingBlock:^(NSNotification *note) {
                    [[NSNotificationCenter defaultCenter] removeObserver:observer];
                    observer = nil;
                    NovaAttRequest(callback, attempt);
                }];
}

static void NovaAttRequest(NovaAttCallback callback, int attempt)
{
    if (@available(iOS 14, *))
    {
        [ATTrackingManager requestTrackingAuthorizationWithCompletionHandler:^(ATTrackingManagerAuthorizationStatus status) {
            dispatch_async(dispatch_get_main_queue(), ^{
                if (status == ATTrackingManagerAuthorizationStatusNotDetermined && attempt + 1 < NovaAttMaxAttempts)
                {
                    NovaAttWhenActive(callback, attempt + 1);
                    return;
                }
                callback((int)status);
            });
        }];
    }
    else
    {
        callback(-1);
    }
}

extern "C"
{
    int NovaAtt_GetStatus()
    {
        if (@available(iOS 14, *)) return (int)[ATTrackingManager trackingAuthorizationStatus];
        return -1;
    }

    void NovaAtt_Request(NovaAttCallback callback)
    {
        dispatch_async(dispatch_get_main_queue(), ^{
            NovaAttWhenActive(callback, 0);
        });
    }
}

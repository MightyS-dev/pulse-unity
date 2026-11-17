#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <UserNotifications/UserNotifications.h>
#import <objc/message.h>
#import <dlfcn.h>

extern "C" const char* MightysPulseBuildNumber()
{
    id value = [[NSBundle mainBundle] objectForInfoDictionaryKey:@"CFBundleVersion"];
    return [value isKindOfClass:[NSString class]] ? [(NSString*)value UTF8String] : "";
}

// Pixels per point of the main screen.
extern "C" double MightysPulseScreenScale()
{
    return (double)[UIScreen mainScreen].scale;
}

// 1 when the App Store receipt is a sandbox receipt (TestFlight), 0 for a store receipt, -1 when there is no receipt URL.
extern "C" int MightysPulseSandboxReceipt()
{
    NSURL* url = [[NSBundle mainBundle] appStoreReceiptURL];
    if (url == nil) return -1;
    return [[url lastPathComponent] isEqualToString:@"sandboxReceipt"] ? 1 : 0;
}

extern "C" int MightysPulseIsSimulator()
{
#if TARGET_OS_SIMULATOR
    return 1;
#else
    return 0;
#endif
}

// 1 dark, 0 light, -1 unknown.
extern "C" int MightysPulseDarkMode()
{
    if (@available(iOS 13.0, *))
    {
        UIUserInterfaceStyle style = [UIScreen mainScreen].traitCollection.userInterfaceStyle;
        if (style == UIUserInterfaceStyleDark) return 1;
        if (style == UIUserInterfaceStyleLight) return 0;
    }
    return -1;
}

extern "C" int MightysPulseLowPower()
{
    return [[NSProcessInfo processInfo] isLowPowerModeEnabled] ? 1 : 0;
}

// 0.9.7 AE1 permissions, read without ever asking the user.
static volatile int mightysPulseNotificationStatus = -1;

// Starts an asynchronous read of the notification permission; the answer lands in MightysPulseNotificationStatus.
extern "C" void MightysPulseRefreshNotificationStatus()
{
    [[UNUserNotificationCenter currentNotificationCenter] getNotificationSettingsWithCompletionHandler:^(UNNotificationSettings* settings) {
        mightysPulseNotificationStatus = (int)settings.authorizationStatus;
    }];
}

// -1 while unknown, else UNAuthorizationStatus: 0 notDetermined, 1 denied, 2 authorized, 3 provisional, 4 ephemeral.
extern "C" int MightysPulseNotificationStatus()
{
    return mightysPulseNotificationStatus;
}

// App Tracking Transparency status only (never the advertising identifier), without linking the framework: it is
// opened at run time and the class is looked up by name, so an app without ATT, without the Info.plist usage key or on
// iOS 13 simply gets -1. Else ATTrackingManagerAuthorizationStatus: 0 notDetermined, 1 restricted, 2 denied, 3 authorized.
extern "C" int MightysPulseTrackingStatus()
{
    if (@available(iOS 14.0, *))
    {
        static dispatch_once_t once;
        dispatch_once(&once, ^{ dlopen("/System/Library/Frameworks/AppTrackingTransparency.framework/AppTrackingTransparency", RTLD_LAZY); });
        Class manager = NSClassFromString(@"ATTrackingManager");
        SEL selector = NSSelectorFromString(@"trackingAuthorizationStatus");
        if (manager == nil || ![manager respondsToSelector:selector]) return -1;
        return (int)((NSUInteger (*)(id, SEL))objc_msgSend)(manager, selector);
    }
    return -1;
}

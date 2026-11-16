#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>

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

#import <Foundation/Foundation.h>

extern "C" const char* MightysPulseBuildNumber()
{
    id value = [[NSBundle mainBundle] objectForInfoDictionaryKey:@"CFBundleVersion"];
    return [value isKindOfClass:[NSString class]] ? [(NSString*)value UTF8String] : "";
}

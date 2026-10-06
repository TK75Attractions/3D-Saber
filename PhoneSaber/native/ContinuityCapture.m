#import <AVFoundation/AVFoundation.h>
#import <Foundation/Foundation.h>
#import <CoreMedia/CoreMedia.h>
#import <CoreVideo/CoreVideo.h>
#include <math.h>
#include <float.h>
#include <limits.h>
#include <stdint.h>
#include <string.h>

double saber_clock(void) {
    return CMTimeGetSeconds(CMClockGetTime(CMClockGetHostTimeClock()));
}

static NSArray<AVCaptureDevice *> *cameras(void) {
    NSMutableArray *types = [NSMutableArray arrayWithObject:AVCaptureDeviceTypeBuiltInWideAngleCamera];
    if (@available(macOS 14.0, *)) {
        [types addObjectsFromArray:@[AVCaptureDeviceTypeExternal, AVCaptureDeviceTypeContinuityCamera]];
    } else {
        [types addObject:AVCaptureDeviceTypeExternalUnknown];
    }
    NSArray *devices = [AVCaptureDeviceDiscoverySession discoverySessionWithDeviceTypes:types
        mediaType:AVMediaTypeVideo position:AVCaptureDevicePositionUnspecified].devices;
    return [devices sortedArrayUsingComparator:^NSComparisonResult(AVCaptureDevice *a, AVCaptureDevice *b) {
        return [a.uniqueID compare:b.uniqueID];
    }];
}

static NSDictionary *formatInfo(AVCaptureDeviceFormat *format, NSInteger index) {
    CMVideoDimensions size = CMVideoFormatDescriptionGetDimensions(format.formatDescription);
    NSMutableArray *ranges = [NSMutableArray array];
    for (AVFrameRateRange *range in format.videoSupportedFrameRateRanges) {
        [ranges addObject:@{@"min": @(range.minFrameRate), @"max": @(range.maxFrameRate)}];
    }
    return @{@"index": @(index), @"width": @(size.width), @"height": @(size.height),
        @"subtype": @(CMFormatDescriptionGetMediaSubType(format.formatDescription)), @"fps_ranges": ranges};
}

static BOOL formatHasDimensions(AVCaptureDeviceFormat *format, int width, int height) {
    CMVideoDimensions size = CMVideoFormatDescriptionGetDimensions(format.formatDescription);
    return size.width == width && size.height == height;
}

static BOOL formatSupportsFPS(AVCaptureDeviceFormat *format, double fps) {
    if (!isfinite(fps) || fps <= 0) return NO;
    for (AVFrameRateRange *range in format.videoSupportedFrameRateRanges) {
        if (range.minFrameRate <= fps && fps <= range.maxFrameRate) return YES;
    }
    return NO;
}

static int64_t positiveGCD(int64_t a, int64_t b) {
    while (b != 0) {
        int64_t remainder = a % b;
        a = b;
        b = remainder;
    }
    return a;
}

// Convert a requested FPS to a deterministic CMTime frame period. Integer
// rates are kept exact; other rates use a bounded decimal rational whose
// numerator remains a valid CMTime timescale.
static BOOL frameDurationForFPS(double fps, CMTime *duration) {
    if (!duration || !isfinite(fps) || fps <= 0 || fps > (double)INT32_MAX ||
        fps < 1.0 / (double)INT32_MAX) return NO;

    double integral = 0;
    if (modf(fps, &integral) == 0.0) {
        *duration = CMTimeMake(1, (int32_t)integral);
        return CMTIME_IS_VALID(*duration);
    }

    const int64_t preferredDenominator = 1000000;
    double maximumDenominatorValue = floor((double)INT32_MAX / fps);
    int64_t maximumDenominator = maximumDenominatorValue >= (double)INT32_MAX
        ? INT32_MAX : (int64_t)maximumDenominatorValue;
    if (maximumDenominator < 1) return NO;
    int64_t denominator = maximumDenominator < preferredDenominator
        ? maximumDenominator : preferredDenominator;
    double scaled = fps * (double)denominator;
    if (!isfinite(scaled) || scaled > (double)INT32_MAX) return NO;
    int64_t numerator = (int64_t)floor(scaled + 0.5);

    // Very low rates need a larger denominator to avoid rounding to zero.
    if (numerator < 1) {
        double requiredDenominatorValue = ceil(1.0 / fps);
        if (!isfinite(requiredDenominatorValue) ||
            requiredDenominatorValue > (double)maximumDenominator) return NO;
        denominator = (int64_t)requiredDenominatorValue;
        scaled = fps * (double)denominator;
        if (!isfinite(scaled) || scaled < 0.5 || scaled > (double)INT32_MAX) return NO;
        numerator = (int64_t)floor(scaled + 0.5);
    }
    if (numerator < 1 || numerator > INT32_MAX) return NO;

    int64_t divisor = positiveGCD(numerator, denominator);
    int64_t reducedNumerator = numerator / divisor;
    int64_t reducedDenominator = denominator / divisor;
    if (reducedNumerator < 1 || reducedNumerator > INT32_MAX || reducedDenominator < 1)
        return NO;
    *duration = CMTimeMake(reducedDenominator, (int32_t)reducedNumerator);
    return CMTIME_IS_VALID(*duration);
}

static BOOL durationMatchesFPS(CMTime duration, double fps) {
    CMTime expected;
    if (!CMTIME_IS_VALID(duration) || !CMTIME_IS_NUMERIC(duration) ||
        duration.value <= 0 || duration.timescale <= 0 ||
        !frameDurationForFPS(fps, &expected)) return NO;
    if (CMTimeCompare(duration, expected) == 0) return YES;

    // AVFoundation can return the same period at a different timescale. Compare
    // against the exactly represented request after that scale conversion, with
    // one output tick allowed for device-side quantization.
    CMTime expectedAtScale = CMTimeConvertScale(expected, duration.timescale,
        kCMTimeRoundingMethod_RoundHalfAwayFromZero);
    if (!CMTIME_IS_VALID(expectedAtScale)) return NO;
    CMTime delta = CMTimeAbsoluteValue(CMTimeSubtract(duration, expectedAtScale));
    CMTime oneTick = CMTimeMake(1, duration.timescale);
    if (CMTimeCompare(delta, oneTick) <= 0) return YES;

    double actualSeconds = CMTimeGetSeconds(duration);
    double expectedSeconds = CMTimeGetSeconds(expected);
    double tickSeconds = CMTimeGetSeconds(oneTick);
    double floatingTolerance = 8.0 * DBL_EPSILON * fmax(1.0,
        fmax(fabs(actualSeconds), fabs(expectedSeconds)));
    return isfinite(actualSeconds) && isfinite(expectedSeconds) && isfinite(tickSeconds) &&
        fabs(actualSeconds - expectedSeconds) <= 0.5 * fabs(tickSeconds) + floatingTolerance;
}

static double formatCost(double w, double h, double width, double height) {
    double aspect = fabs(log((w / h) / (width / height)));
    double undersize = fmax(0, log(width / w)) + fmax(0, log(height / h));
    return aspect + undersize * 10 + log(w * h / (width * height));
}

static char *jsonString(id value) {
    NSData *data = [NSJSONSerialization dataWithJSONObject:value options:NSJSONWritingSortedKeys error:nil];
    return data ? strdup([[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding].UTF8String) : NULL;
}

// Exactly one retained frame. Consumers snapshot it without blocking the callback on a copy.
@interface FrameMailbox : NSObject {
    NSLock *_lock;
    CVPixelBufferRef _pixels;
    int64_t _sequence;
    double _received;
    double _ptsAge;
    double _frameInterval;
}
- (void)put:(CVPixelBufferRef)buffer received:(double)received ptsAge:(double)age;
- (int32_t)copyAfter:(int64_t)after into:(void *)destination capacity:(intptr_t)capacity metadata:(double *)metadata;
@end

@implementation FrameMailbox
- (instancetype)init {
    if ((self = [super init])) {
        _lock = [NSLock new];
        _received = NAN;
        _frameInterval = NAN;
    }
    return self;
}
- (void)dealloc { if (_pixels) CVPixelBufferRelease(_pixels); }
- (void)put:(CVPixelBufferRef)buffer received:(double)received ptsAge:(double)age {
    CVPixelBufferRetain(buffer);
    [_lock lock];
    CVPixelBufferRef old = _pixels;
    _pixels = buffer;
    _sequence++;
    _frameInterval = isfinite(_received) ? received - _received : NAN;
    _received = received;
    _ptsAge = age;
    [_lock unlock];
    if (old) CVPixelBufferRelease(old);
}
- (int32_t)copyAfter:(int64_t)after into:(void *)destination capacity:(intptr_t)capacity metadata:(double *)metadata {
    [_lock lock];
    if (!_pixels || _sequence == after) { [_lock unlock]; return 0; }
    CVPixelBufferRef buffer = CVPixelBufferRetain(_pixels);
    metadata[2] = (double)_sequence;
    metadata[3] = _received;
    metadata[4] = _ptsAge;
    metadata[5] = _frameInterval;
    [_lock unlock];
    size_t width = CVPixelBufferGetWidth(buffer), height = CVPixelBufferGetHeight(buffer);
    metadata[0] = width;
    metadata[1] = height;
    int32_t result = -1;
    if (destination && capacity >= 0 && (size_t)capacity >= width * height * 4) {
        result = -2;
        if (CVPixelBufferGetPixelFormatType(buffer) == kCVPixelFormatType_32BGRA &&
            CVPixelBufferLockBaseAddress(buffer, kCVPixelBufferLock_ReadOnly) == kCVReturnSuccess) {
            uint8_t *source = CVPixelBufferGetBaseAddress(buffer);
            size_t stride = CVPixelBufferGetBytesPerRow(buffer);
            if (source) {
                for (size_t row = 0; row < height; row++) {
                    memcpy((uint8_t *)destination + row * width * 4, source + row * stride, width * 4);
                }
                result = 1;
            }
            CVPixelBufferUnlockBaseAddress(buffer, kCVPixelBufferLock_ReadOnly);
        }
    }
    CVPixelBufferRelease(buffer);
    return result;
}
@end

@interface NativeCamera : NSObject <AVCaptureVideoDataOutputSampleBufferDelegate>
@property AVCaptureSession *session;
@property AVCaptureVideoDataOutput *output;
@property dispatch_queue_t queue;
@property FrameMailbox *mailbox;
@property NSDictionary *info;
- (NSString *)startID:(NSString *)deviceID width:(int)width height:(int)height fps:(double)fps format:(int)formatIndex;
- (void)stop;
@end

@implementation NativeCamera
- (instancetype)init {
    if ((self = [super init])) {
        _session = [AVCaptureSession new];
        _output = [AVCaptureVideoDataOutput new];
        _mailbox = [FrameMailbox new];
        _queue = dispatch_queue_create("saber.capture.latest",
            dispatch_queue_attr_make_with_qos_class(DISPATCH_QUEUE_SERIAL, QOS_CLASS_USER_INTERACTIVE, 0));
    }
    return self;
}
- (NSString *)startID:(NSString *)deviceID width:(int)width height:(int)height fps:(double)fps format:(int)formatIndex {
    if (width <= 0 || height <= 0 || !isfinite(fps) || fps <= 0) return @"Invalid dimensions/FPS";
    CMTime requestedDuration;
    if (!frameDurationForFPS(fps, &requestedDuration))
        return @"FPS cannot be represented as a valid CMTime frame period";
    AVAuthorizationStatus status = [AVCaptureDevice authorizationStatusForMediaType:AVMediaTypeVideo];
    if (status == AVAuthorizationStatusNotDetermined) {
        [AVCaptureDevice requestAccessForMediaType:AVMediaTypeVideo completionHandler:^(BOOL granted) {}];
        double deadline = saber_clock() + 30;
        while (status == AVAuthorizationStatusNotDetermined && saber_clock() < deadline) {
            [[NSRunLoop currentRunLoop] runUntilDate:[NSDate dateWithTimeIntervalSinceNow:0.05]];
            status = [AVCaptureDevice authorizationStatusForMediaType:AVMediaTypeVideo];
        }
    }
    if (status == AVAuthorizationStatusNotDetermined)
        return @"Camera permission is still pending after 30 seconds. Allow the launching app's camera request, then rerun.";
    if (status != AVAuthorizationStatusAuthorized)
        return @"Camera permission denied. Allow the launching app in System Settings > Privacy & Security > Camera.";
    NSMutableArray *matches = [NSMutableArray array];
    for (AVCaptureDevice *device in cameras()) {
        BOOL match = [device.uniqueID isEqualToString:deviceID];
        if ([deviceID isEqualToString:@"continuity"]) {
            if (@available(macOS 13.0, *)) match = device.isContinuityCamera;
        }
        if (match) [matches addObject:device];
    }
    if (matches.count != 1) return [NSString stringWithFormat:
        @"Expected one camera for '%@', found %lu. Use --list-cameras and --device-id. No built-in fallback.",
        deviceID, (unsigned long)matches.count];
    AVCaptureDevice *device = matches[0];
    AVCaptureDeviceFormat *choice = nil;
    NSInteger chosenIndex = -1;
    if (formatIndex < -1) return [NSString stringWithFormat:@"Invalid format index %d", formatIndex];
    if (formatIndex >= 0 && formatIndex >= (NSInteger)device.formats.count)
        return [NSString stringWithFormat:@"Format index %d is out of range (device has %lu formats)",
            formatIndex, (unsigned long)device.formats.count];
    for (NSInteger i = 0; i < (NSInteger)device.formats.count; i++) {
        if (formatIndex >= 0 && i != formatIndex) continue;
        AVCaptureDeviceFormat *format = device.formats[i];
        if (!formatHasDimensions(format, width, height) || !formatSupportsFPS(format, fps)) continue;
        choice = format;
        chosenIndex = i;
        break;
    }
    if (!choice) return [NSString stringWithFormat:
        @"No camera format matches the requested %dx%d at %.6g FPS%s. Inspect --list-cameras; no silent substitution.",
        width, height, fps, formatIndex >= 0 ? " for the requested format index" : ""];
    NSError *error = nil;
    AVCaptureDeviceInput *input = [AVCaptureDeviceInput deviceInputWithDevice:device error:&error];
    if (!input) return error.localizedDescription ?: @"Cannot create camera input";
    [_session beginConfiguration];
    if (![_session canAddInput:input] || ![_session canAddOutput:_output]) {
        [_session commitConfiguration];
        return @"Cannot attach camera input/output";
    }
    [_session addInput:input];
    [_session addOutput:_output];
    // On macOS activeFormat is configured directly; inputPriority is iOS-only.
    if (![device lockForConfiguration:&error]) {
        [_session commitConfiguration];
        return error.localizedDescription ?: @"Cannot configure camera";
    }
    NSString *configurationError = nil;
    @try {
        device.activeFormat = choice;
        device.activeVideoMinFrameDuration = requestedDuration;
        device.activeVideoMaxFrameDuration = requestedDuration;
    } @catch (NSException *exception) {
        configurationError = exception.reason;
    } @finally {
        [device unlockForConfiguration];
    }
    if (configurationError) { [_session commitConfiguration]; return configurationError; }
    AVCaptureDeviceFormat *actualFormat = device.activeFormat;
    CMVideoDimensions actualSize = CMVideoFormatDescriptionGetDimensions(actualFormat.formatDescription);
    if (actualFormat != choice || actualSize.width != width || actualSize.height != height ||
        !durationMatchesFPS(device.activeVideoMinFrameDuration, fps) ||
        !durationMatchesFPS(device.activeVideoMaxFrameDuration, fps) ||
        CMTimeCompare(device.activeVideoMinFrameDuration, device.activeVideoMaxFrameDuration) != 0) {
        double actualMinFPS = CMTimeGetSeconds(device.activeVideoMinFrameDuration) > 0
            ? 1.0 / CMTimeGetSeconds(device.activeVideoMinFrameDuration) : NAN;
        double actualMaxFPS = CMTimeGetSeconds(device.activeVideoMaxFrameDuration) > 0
            ? 1.0 / CMTimeGetSeconds(device.activeVideoMaxFrameDuration) : NAN;
        [_session commitConfiguration];
        return [NSString stringWithFormat:
            @"Camera rejected requested format %dx%d at %.6g FPS; active is %dx%d at %.6g-%.6g FPS",
            width, height, fps, actualSize.width, actualSize.height, actualMinFPS, actualMaxFPS];
    }
    // These are callback-output buffer requirements, not the wireless transport
    // resolution. FrameMailbox measures the CVPixelBuffer that actually arrives
    // in captureOutput and returns those dimensions to native_capture.py.
    _output.videoSettings = @{
        (id)kCVPixelBufferWidthKey: @(width),
        (id)kCVPixelBufferHeightKey: @(height),
        (id)kCVPixelBufferPixelFormatTypeKey: @(kCVPixelFormatType_32BGRA)
    };
    _output.alwaysDiscardsLateVideoFrames = YES;
    [_output setSampleBufferDelegate:self queue:_queue];
    AVCaptureConnection *connection = [_output connectionWithMediaType:AVMediaTypeVideo];
    if (connection.isVideoMirroringSupported) {
        connection.automaticallyAdjustsVideoMirroring = NO;
        connection.videoMirrored = NO;
    }
    [_session commitConfiguration];
    [_session startRunning];
    if (!_session.isRunning) return @"Capture session did not start";
    NSInteger actualIndex = [device.formats indexOfObject:actualFormat];
    double actualMinFPS = 1.0 / CMTimeGetSeconds(device.activeVideoMinFrameDuration);
    double actualMaxFPS = 1.0 / CMTimeGetSeconds(device.activeVideoMaxFrameDuration);
    _info = @{@"name": device.localizedName, @"id": device.uniqueID,
        @"requested": @{@"width": @(width), @"height": @(height), @"fps": @(fps)},
        @"selected": @{@"format_index": @(chosenIndex), @"format": formatInfo(choice, chosenIndex)},
        @"selected_format_index": @(chosenIndex),
        @"actual": @{@"format_index": @(actualIndex), @"width": @(actualSize.width),
            @"height": @(actualSize.height), @"min_fps": @(actualMinFPS),
            @"max_fps": @(actualMaxFPS),
            @"min_frame_duration": @(CMTimeGetSeconds(device.activeVideoMinFrameDuration)),
            @"max_frame_duration": @(CMTimeGetSeconds(device.activeVideoMaxFrameDuration))},
        @"active_format": formatInfo(actualFormat, actualIndex),
        @"frame_duration": @(CMTimeGetSeconds(device.activeVideoMinFrameDuration)),
        @"callback_output": @{
            @"requested_width": @(width), @"requested_height": @(height),
            @"pixel_format": @"BGRA",
            @"dimensions_source": @"FrameMailbox CVPixelBuffer callback output; not wireless transport resolution"
        },
        @"late_frames_discarded": @(_output.alwaysDiscardsLateVideoFrames),
        @"transport_bitrate": @"not exposed by Continuity Camera"};
    return nil;
}
- (void)captureOutput:(AVCaptureOutput *)output didOutputSampleBuffer:(CMSampleBufferRef)sample
        fromConnection:(AVCaptureConnection *)connection {
    CVPixelBufferRef pixels = CMSampleBufferGetImageBuffer(sample);
    if (!pixels) return;
    double now = saber_clock(), age = NAN;
    CMClockRef clock = _session.synchronizationClock;
    if (clock) {
        CMTime hostPTS = CMSyncConvertTime(CMSampleBufferGetPresentationTimeStamp(sample), clock, CMClockGetHostTimeClock());
        double pts = CMTimeGetSeconds(hostPTS);
        if (isfinite(pts)) age = now - pts;
    }
    // This PTS is NOT a verified iPhone exposure timestamp.
    // Measure the frame actually delivered to this callback, rather than only
    // trusting activeFormat. The mailbox records the callback period too.
    [_mailbox put:pixels received:now ptsAge:age];
}
- (void)stop {
    [_session stopRunning];
    [_output setSampleBufferDelegate:nil queue:NULL];
    dispatch_sync(_queue, ^{});
}
@end

char *saber_devices(void) {
    @autoreleasepool {
        NSMutableArray *result = [NSMutableArray array];
        for (AVCaptureDevice *device in cameras()) {
            NSMutableArray *formats = [NSMutableArray array];
            for (NSInteger i = 0; i < (NSInteger)device.formats.count; i++)
                [formats addObject:formatInfo(device.formats[i], i)];
            [result addObject:@{@"name": device.localizedName, @"id": device.uniqueID,
                @"type": device.deviceType, @"continuity": @(device.isContinuityCamera),
                @"authorization_status": @([AVCaptureDevice authorizationStatusForMediaType:AVMediaTypeVideo]),
                @"formats": formats}];
        }
        return jsonString(result);
    }
}
void saber_free(void *string) { free(string); }
void *saber_open(const char *deviceID, int32_t width, int32_t height, double fps, int32_t formatIndex, char **error) {
    @autoreleasepool {
        NativeCamera *camera = [NativeCamera new];
        NSString *message = [camera startID:@(deviceID) width:width height:height fps:fps format:formatIndex];
        if (message) { [camera stop]; *error = strdup(message.UTF8String); return NULL; }
        return (__bridge_retained void *)camera;
    }
}
char *saber_info(void *handle) {
    @autoreleasepool { return jsonString(((__bridge NativeCamera *)handle).info); }
}
int32_t saber_copy(void *handle, int64_t after, void *destination, intptr_t capacity, double *metadata) {
    return [((__bridge NativeCamera *)handle).mailbox copyAfter:after into:destination capacity:capacity metadata:metadata];
}
void saber_close(void *handle) {
    @autoreleasepool { NativeCamera *camera = (__bridge_transfer NativeCamera *)handle; [camera stop]; }
}

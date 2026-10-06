#import "ContinuityCapture.m"
#include <assert.h>

static CVPixelBufferRef frame(int width, int height, uint8_t value) {
    CVPixelBufferRef pixels = NULL;
    NSDictionary *attributes = @{(id)kCVPixelBufferBytesPerRowAlignmentKey: @64};
    assert(CVPixelBufferCreate(NULL, width, height, kCVPixelFormatType_32BGRA,
        (__bridge CFDictionaryRef)attributes, &pixels) == kCVReturnSuccess);
    assert(CVPixelBufferLockBaseAddress(pixels, 0) == kCVReturnSuccess);
    size_t stride = CVPixelBufferGetBytesPerRow(pixels);
    uint8_t *data = CVPixelBufferGetBaseAddress(pixels);
    for (int row = 0; row < height; row++) {
        memset(data + row * stride, value, width * 4);
        memset(data + row * stride + width * 4, 0xee, stride - width * 4);
    }
    CVPixelBufferUnlockBaseAddress(pixels, 0);
    return pixels;
}

int main(void) {
    @autoreleasepool {
        assert(formatCost(640, 480, 640, 360) < formatCost(1280, 720, 640, 360));
        assert(formatCost(640, 480, 640, 360) < formatCost(320, 240, 640, 360));
        CMTime period30, period60;
        assert(frameDurationForFPS(30.0, &period30));
        assert(period30.value == 1 && period30.timescale == 30);
        assert(durationMatchesFPS(period30, 30.0));
        assert(frameDurationForFPS(60.0, &period60));
        assert(period60.value == 1 && period60.timescale == 60);
        assert(durationMatchesFPS(period60, 60.0));
        FrameMailbox *mailbox = [FrameMailbox new];
        uint8_t bytes[24];
        // FrameMailbox writes the six-element ABI: width, height, sequence,
        // received time, PTS age, and frame interval.
        double metadata[6] = {0};
        assert([mailbox copyAfter:-1 into:bytes capacity:24 metadata:metadata] == 0);
        for (int i = 1; i <= 1000; i++) {
            CVPixelBufferRef pixels = frame(3, 2, i % 256);
            [mailbox put:pixels received:i ptsAge:0.01];
            CVPixelBufferRelease(pixels);
        }
        memset(bytes, 0xaa, sizeof(bytes));
        assert([mailbox copyAfter:0 into:bytes capacity:23 metadata:metadata] == -1);
        for (int i = 0; i < 24; i++) assert(bytes[i] == 0xaa);
        assert(metadata[0] == 3 && metadata[1] == 2 && metadata[2] == 1000);
        assert([mailbox copyAfter:0 into:bytes capacity:24 metadata:metadata] == 1);
        for (int i = 0; i < 24; i++) assert(bytes[i] == 1000 % 256);
        assert([mailbox copyAfter:1000 into:bytes capacity:24 metadata:metadata] == 0);
        dispatch_group_t group = dispatch_group_create();
        dispatch_group_async(group, dispatch_get_global_queue(QOS_CLASS_USER_INTERACTIVE, 0), ^{
            for (int i = 1001; i <= 5000; i++) {
                @autoreleasepool {
                    CVPixelBufferRef pixels = frame(3, 2, i % 256);
                    [mailbox put:pixels received:i ptsAge:0.01];
                    CVPixelBufferRelease(pixels);
                }
            }
        });
        int64_t last = 1000;
        for (int i = 0; i < 5000; i++) {
            if ([mailbox copyAfter:last into:bytes capacity:24 metadata:metadata] == 1) {
                assert(metadata[2] > last);
                assert(metadata[2] == metadata[3]);
                last = metadata[2];
                for (int j = 0; j < 24; j++) assert(bytes[j] == last % 256);
            }
        }
        dispatch_group_wait(group, DISPATCH_TIME_FOREVER);
        // Resizing must report capacity before writing into the consumer's old storage.
        CVPixelBufferRef bigger = frame(20, 10, 42);
        [mailbox put:bigger received:5001 ptsAge:0];
        CVPixelBufferRelease(bigger);
        assert([mailbox copyAfter:last into:bytes capacity:24 metadata:metadata] == -1);
        assert(metadata[0] == 20 && metadata[1] == 10);
        puts("PASS: native format cost, latest-only, duplicate suppression, padded rows, small buffers, concurrent snapshots, resizing");
    }
    return 0;
}

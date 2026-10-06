# IMG_5933 production-view fixtures

These PNG files are exact decoded frames from the user-provided
`IMG_5933.mp4`; the original video is not modified. `annotations.json` records
manually reviewed blade-axis endpoints in stored-image coordinates. An
`ambiguous` value is intentionally excluded from success-rate and endpoint
error assertions rather than guessing a hidden or unclear blade position.

The source is a 512 x 910, 30 fps, H.264/BT.709 video, not a raw camera
`CVPixelBuffer`. It is useful for deterministic regression and temporal
diagnostics, but passing these fixtures does not verify production exposure,
BGRA conversion, performance, or color response on an iPhone.

The selected frames cover simultaneous red/blue blades, horizontal/vertical/
diagonal poses, crossings, partial occlusion, foreshortening, motion, screen
edges, and the unrelated-bright-object line-bridging regression. This video
does not contain a clearly controlled no-saber frame, so negative-scene
coverage continues to come from the existing synthetic tests.

From the `PhoneSaber/` directory, regenerate the candidate JSON, annotated keyframes,
summary, and annotated video with:

```bash
python3 ios/PhoneSaberSenderTests/run_video_detection_diagnostic.py \
  /path/to/IMG_5933.mp4 \
  --output tests/debug_detection/IMG_5933 \
  --annotations ios/PhoneSaberSenderTests/Fixtures/production-video-IMG_5933/annotations.json
```

`tests/debug_detection/` is intentionally ignored: these are review artifacts,
not resources used by the production app.

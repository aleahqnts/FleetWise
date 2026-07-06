YOLO11n model — required for Phase 3+ detection
================================================

The app expects:  app/src/main/assets/yolo11n_float32.tflite
(app runs without it; the Camera screen just shows these instructions)

One-time export (any PC with Python 3.9+):

    pip install ultralytics
    yolo export model=yolo11n.pt format=tflite imgsz=320

This downloads yolo11n.pt automatically, then writes:
    yolo11n_saved_model/yolo11n_float32.tflite

Copy that file into this folder. Done.

Notes
- imgsz=320 keeps inference fast on mid-range phones (~320x320 input).
- float32 chosen for correctness first; float16 (yolo11n_float16.tflite) also works
  (rename to yolo11n_float32.tflite or update MODEL_ASSET in YoloDetector.kt).
- Output layout [1, 84, N] (or transposed) is auto-detected by YoloDetector.

"""
By Your Sight - Real-world object detection server (Opsi 2).

Reads the HoloLens 2 photo/video camera through the Windows Device Portal
Mixed Reality Capture live stream, runs YOLO on this laptop, and serves the
latest detections as JSON for Unity (Assets/Scripts/Perception/RealWorldObjectDetector.cs).

    python detection_server.py --portal https://192.168.1.25 --user NAME --password PASS
    python detection_server.py --source 0          # test with the laptop webcam, no HoloLens

Endpoints (http://127.0.0.1:8765):
    /             preview page with boxes drawn (open in a browser to check what the camera sees)
    /detections   JSON for Unity
    /status       JSON health info
"""

import argparse
import json
import os
import threading
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

# Low-latency FFmpeg options must be set before OpenCV opens the stream
os.environ.setdefault("OPENCV_FFMPEG_CAPTURE_OPTIONS", "fflags;nobuffer|flags;low_delay")

import cv2  # noqa: E402
from ultralytics import YOLO  # noqa: E402

# Default smart remappings for indoor room / assistive context.
# Standard COCO lacks 'shelf', so large vertical racks/shelves get classified as 'refrigerator'.
DEFAULT_LABEL_REMAPS = {
    "refrigerator": "shelf",
}


def parse_remaps(remap_str):
    remaps = dict(DEFAULT_LABEL_REMAPS)
    if remap_str:
        for pair in remap_str.split(","):
            if ":" in pair:
                src, dst = pair.split(":", 1)
                remaps[src.strip().lower()] = dst.strip().lower()
    return remaps

# Open-vocabulary classes used with a YOLO-World model (--model yolov8s-worldv2.pt).
# Spoken labels match the Unity voice vocabulary.
WORLD_CLASSES = [
    "person", "door", "stairs", "chair", "table", "desk", "sofa", "bed", "cabinet", "sink", "toilet",
    "trash bin", "pillar", "bag", "phone", "water bottle", "coffee mug", "laptop", "book", "television",
    "plant", "house keys", "small box", "window",
]


def build_source(args):
    if args.source is not None:
        return int(args.source) if args.source.isdigit() else args.source
    if not args.portal:
        raise SystemExit("Give --portal https://<hololens-ip> (with --user/--password) or --source 0 for a webcam.")
    parsed = urllib.parse.urlparse(args.portal if "://" in args.portal else "https://" + args.portal)
    netloc = parsed.netloc
    if args.user:
        cred = urllib.parse.quote(args.user, safe="") + ":" + urllib.parse.quote(args.password or "", safe="")
        netloc = cred + "@" + netloc
    # holo=false: camera only, no holograms drawn into the frames
    path = f"/api/holographic/stream/{args.quality}.mp4?holo=false&pv=true&mic=false&loopback=false"
    return f"{parsed.scheme}://{netloc}{path}"


def redact(source):
    if isinstance(source, int):
        return f"webcam {source}"
    p = urllib.parse.urlparse(source)
    host = p.hostname or ""
    if p.port:
        host += f":{p.port}"
    return f"{p.scheme}://{host}{p.path}"


class Shared:
    def __init__(self):
        self.lock = threading.Lock()
        self.frame = None
        self.frame_time = 0.0
        self.frames_read = 0
        self.stream_status = "starting"
        self.result = {"age": 0.0, "width": 0, "height": 0, "detections": []}
        self.result_frame_time = 0.0
        self.preview_jpeg = None
        self.inference_ms = 0.0
        self.inference_fps = 0.0
        self.stop = threading.Event()


def grab_frames(source, shared):
    """Reads the stream continuously so the newest frame is always available (no buffered lag)."""
    while not shared.stop.is_set():
        cap = cv2.VideoCapture(source) if isinstance(source, int) else cv2.VideoCapture(source, cv2.CAP_FFMPEG)
        if not cap.isOpened():
            with shared.lock:
                shared.stream_status = "cannot open camera stream (check IP, Device Portal login, Wi-Fi)"
            print("[stream] Cannot open", redact(source), "- retrying in 3 s")
            time.sleep(3)
            continue

        print("[stream] Connected to", redact(source))
        with shared.lock:
            shared.stream_status = "ok"
        while not shared.stop.is_set():
            ok, frame = cap.read()
            if not ok:
                break
            with shared.lock:
                shared.frame = frame
                shared.frame_time = time.time()
                shared.frames_read += 1

        cap.release()
        with shared.lock:
            shared.stream_status = "stream ended, reconnecting"
        print("[stream] Stream ended - reconnecting")
        time.sleep(1)


def run_detection(model, args, shared):
    last_processed = 0.0
    while not shared.stop.is_set():
        with shared.lock:
            frame = shared.frame
            frame_time = shared.frame_time
        if frame is None or frame_time == last_processed:
            time.sleep(0.01)
            continue
        last_processed = frame_time
        try:
            detect_frame(model, args, shared, frame, frame_time)
        except Exception as e:  # keep serving; one bad frame must not stop detection
            print("[detect] Error on frame:", e)
            time.sleep(0.2)


def detect_frame(model, args, shared, frame, frame_time):
    t0 = time.time()
    result = model.predict(frame, imgsz=args.imgsz, conf=args.conf, verbose=False, device=args.device)[0]
    elapsed = time.time() - t0

    h, w = frame.shape[:2]
    detections = []
    names = result.names
    for box in result.boxes:
        x1, y1, x2, y2 = box.xyxy[0].tolist()
        cls = int(box.cls[0])
        detections.append({
            "label": names[cls] if isinstance(names, (list, tuple)) else names.get(cls, str(cls)),
            "conf": round(float(box.conf[0]), 3),
            "cx": round(((x1 + x2) / 2) / w, 4),
            "cy": round(((y1 + y2) / 2) / h, 4),
            "w": round((x2 - x1) / w, 4),
            "h": round((y2 - y1) / h, 4),
        })

    ok, jpeg = cv2.imencode(".jpg", result.plot(), [cv2.IMWRITE_JPEG_QUALITY, 70])
    with shared.lock:
        shared.result = {"width": w, "height": h, "detections": detections}
        shared.result_frame_time = frame_time
        shared.inference_ms = elapsed * 1000
        shared.inference_fps = 0.8 * shared.inference_fps + 0.2 * (1.0 / max(elapsed, 1e-3))
        if ok:
            shared.preview_jpeg = jpeg.tobytes()


PREVIEW_HTML = b"""<!doctype html><meta charset="utf-8"><title>By Your Sight - Camera AI</title>
<style>body{background:#111;color:#eee;font:14px system-ui;margin:16px}img{max-width:100%;border:1px solid #444}</style>
<h3>By Your Sight - Camera AI preview</h3><p id="s">loading...</p><img id="i">
<script>
setInterval(()=>{document.getElementById('i').src='/preview.jpg?'+Date.now();
fetch('/status').then(r=>r.json()).then(s=>{document.getElementById('s').textContent=
'stream: '+s.stream+' | '+s.fps+' fps | '+s.inference_ms+' ms | sees: '+(s.labels.join(', ')||'-')})},400);
</script>"""


def make_handler(shared):
    class Handler(BaseHTTPRequestHandler):
        def _send(self, code, body, ctype):
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Cache-Control", "no-store")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            path = self.path.split("?")[0]
            if path == "/detections":
                with shared.lock:
                    payload = dict(shared.result)
                    payload["age"] = round(time.time() - shared.result_frame_time, 3) if shared.result_frame_time else 0.0
                self._send(200, json.dumps(payload).encode(), "application/json")
            elif path == "/status":
                with shared.lock:
                    status = {
                        "stream": shared.stream_status,
                        "frames_read": shared.frames_read,
                        "fps": round(shared.inference_fps, 1),
                        "inference_ms": round(shared.inference_ms),
                        "labels": sorted({d["label"] for d in shared.result["detections"]}),
                    }
                self._send(200, json.dumps(status).encode(), "application/json")
            elif path == "/preview.jpg":
                with shared.lock:
                    jpeg = shared.preview_jpeg
                if jpeg is None:
                    self._send(503, b"no frame yet", "text/plain")
                else:
                    self._send(200, jpeg, "image/jpeg")
            elif path == "/":
                self._send(200, PREVIEW_HTML, "text/html; charset=utf-8")
            else:
                self._send(404, b"not found", "text/plain")

        def log_message(self, *args):
            pass  # keep the console readable

    return Handler


def main():
    ap = argparse.ArgumentParser(description="By Your Sight object detection server")
    ap.add_argument("--portal", help="Device Portal address, e.g. https://192.168.1.25 (Wi-Fi) or http://127.0.0.1:10080 (USB)")
    ap.add_argument("--user", help="Device Portal user name")
    ap.add_argument("--password", help="Device Portal password")
    ap.add_argument("--source", help="Use another video source instead, e.g. 0 for the laptop webcam")
    ap.add_argument("--quality", default="live_low", choices=["live_low", "live_med", "live_high"],
                    help="Device Portal stream quality (lower = less delay)")
    ap.add_argument("--model", default="yolo11s.pt",
                    help="yolo11s.pt (accurate, 80 everyday classes), yolo11n.pt (faster), or yolov8s-worldv2.pt (also door/stairs)")
    ap.add_argument("--imgsz", type=int, default=640)
    ap.add_argument("--conf", type=float, default=0.25,
                    help="Confidence threshold (default 0.25 for better detection of books/smaller objects)")
    ap.add_argument("--remap", default=None,
                    help="Label remapping, e.g. 'refrigerator:shelf' (default: refrigerator -> shelf)")
    ap.add_argument("--device", default=None, help="cpu, 0 for the first NVIDIA GPU (default: auto)")
    ap.add_argument("--port", type=int, default=8765)
    args = ap.parse_args()

    source = build_source(args)
    script_dir = os.path.dirname(os.path.abspath(__file__))
    model_path = args.model
    if not os.path.exists(model_path):
        candidate = os.path.join(script_dir, args.model)
        if os.path.exists(candidate):
            model_path = candidate

    print(f"[model] Loading {model_path} (first run downloads it)...")
    model = YOLO(model_path)
    if "world" in args.model.lower():
        model.set_classes(WORLD_CLASSES)
        print("[model] YOLO-World classes:", ", ".join(WORLD_CLASSES))

    remaps = parse_remaps(args.remap)
    if remaps:
        names_dict = getattr(getattr(model, "model", None), "names", None)
        if names_dict is None and hasattr(model, "names") and isinstance(model.names, dict):
            names_dict = model.names
        if isinstance(names_dict, dict):
            remapped_names = []
            for k, v in list(names_dict.items()):
                low = str(v).lower()
                if low in remaps:
                    target = remaps[low]
                    names_dict[k] = target
                    remapped_names.append(f"{v} -> {target}")
            if remapped_names:
                print(f"[model] Applied label remapping: {', '.join(remapped_names)}")

    shared = Shared()
    threading.Thread(target=grab_frames, args=(source, shared), daemon=True).start()
    threading.Thread(target=run_detection, args=(model, args, shared), daemon=True).start()

    server = ThreadingHTTPServer(("127.0.0.1", args.port), make_handler(shared))
    print(f"[server] Ready. Preview: http://127.0.0.1:{args.port}/   (Ctrl+C to stop)")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        shared.stop.set()
        server.server_close()


if __name__ == "__main__":
    main()

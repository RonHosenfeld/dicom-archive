"""
Minimal DICOM SCP sink for testing — accepts any C-STORE and logs metadata.
No database, no file storage. Exposes an HTTP API for live tail viewing.
"""

import os
import json
import time
import logging
import threading
import collections
from http.server import HTTPServer, BaseHTTPRequestHandler
from urllib.parse import urlparse, parse_qs
from pynetdicom import AE, evt, AllStoragePresentationContexts, ALL_TRANSFER_SYNTAXES
from pynetdicom.sop_class import Verification

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s | %(levelname)-7s | %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S",
)
logger = logging.getLogger("test-pacs")

AE_TITLE = os.environ.get("AE_TITLE", "TEST_PACS")
LISTEN_PORT = int(os.environ.get("LISTEN_PORT", "104"))
HTTP_PORT = int(os.environ.get("HTTP_PORT", "8080"))

_lock = threading.Lock()
_buffer = collections.deque(maxlen=200)
_seq = 0
_start_time = time.monotonic()


def handle_store(event):
    global _seq
    ds = event.dataset
    ds.file_meta = event.file_meta

    patient_id = getattr(ds, "PatientID", "unknown")
    patient_name = str(getattr(ds, "PatientName", "unknown"))
    study_uid = getattr(ds, "StudyInstanceUID", "unknown")
    sop_uid = getattr(ds, "SOPInstanceUID", "unknown")
    modality = getattr(ds, "Modality", "unknown")

    with _lock:
        _seq += 1
        entry = {
            "seq": _seq,
            "timestamp": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "patient_name": patient_name,
            "patient_id": patient_id,
            "study_uid": study_uid,
            "sop_uid": sop_uid,
            "modality": modality,
        }
        _buffer.append(entry)

    logger.info(
        "C-STORE #%d | Patient: %s (%s) | Study: %s | SOP: %s | Modality: %s",
        entry["seq"],
        patient_name,
        patient_id,
        study_uid,
        sop_uid,
        modality,
    )
    return 0x0000


def handle_echo(event):
    logger.info("C-ECHO received")
    return 0x0000


class TailHandler(BaseHTTPRequestHandler):
    def log_message(self, format, *args):
        pass  # suppress default request logging

    def _send_json(self, data, status=200):
        body = json.dumps(data).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        parsed = urlparse(self.path)
        path = parsed.path.rstrip("/")
        params = parse_qs(parsed.query)

        if path == "/status":
            with _lock:
                total = _seq
                buf_size = len(_buffer)
            self._send_json({
                "ae_title": AE_TITLE,
                "uptime_seconds": round(time.monotonic() - _start_time, 1),
                "total_received": total,
                "buffer_size": buf_size,
            })
        elif path == "/received":
            since = int(params.get("since_seq", ["0"])[0])
            with _lock:
                snapshot = list(_buffer)
            filtered = [e for e in snapshot if e["seq"] > since]
            self._send_json(filtered)
        else:
            self._send_json({"error": "not found"}, 404)

    def do_POST(self):
        global _seq
        parsed = urlparse(self.path)
        path = parsed.path.rstrip("/")

        if path == "/clear":
            with _lock:
                _buffer.clear()
                _seq = 0
            self._send_json({"ok": True})
        else:
            self._send_json({"error": "not found"}, 404)

    def do_OPTIONS(self):
        self.send_response(204)
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type")
        self.end_headers()


def start_http_server():
    server = HTTPServer(("0.0.0.0", HTTP_PORT), TailHandler)
    logger.info("HTTP tail server listening on port %d", HTTP_PORT)
    server.serve_forever()


def main():
    # Start HTTP server on daemon thread
    http_thread = threading.Thread(target=start_http_server, daemon=True)
    http_thread.start()

    ae = AE(ae_title=AE_TITLE)

    ae.add_supported_context(Verification)

    for cx in AllStoragePresentationContexts:
        ae.add_supported_context(cx.abstract_syntax, ALL_TRANSFER_SYNTAXES)

    handlers = [
        (evt.EVT_C_STORE, handle_store),
        (evt.EVT_C_ECHO, handle_echo),
    ]

    logger.info("╔══════════════════════════════════════════╗")
    logger.info("║  Test PACS (DICOM Sink)                  ║")
    logger.info(f"║  AE Title : {AE_TITLE:<30}║")
    logger.info(f"║  DICOM    : 0.0.0.0:{LISTEN_PORT:<22}║")
    logger.info(f"║  HTTP     : 0.0.0.0:{HTTP_PORT:<22}║")
    logger.info("╚══════════════════════════════════════════╝")

    ae.start_server(("0.0.0.0", LISTEN_PORT), evt_handlers=handlers, block=True)


if __name__ == "__main__":
    main()

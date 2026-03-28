"""
Minimal DICOM SCP sink for testing — accepts any C-STORE and logs metadata.
No database, no file storage.
"""

import os
import logging
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

_received_count = 0


def handle_store(event):
    global _received_count
    ds = event.dataset
    ds.file_meta = event.file_meta
    _received_count += 1

    patient_id = getattr(ds, "PatientID", "unknown")
    patient_name = str(getattr(ds, "PatientName", "unknown"))
    study_uid = getattr(ds, "StudyInstanceUID", "unknown")
    sop_uid = getattr(ds, "SOPInstanceUID", "unknown")
    modality = getattr(ds, "Modality", "unknown")

    logger.info(
        "C-STORE #%d | Patient: %s (%s) | Study: %s | SOP: %s | Modality: %s",
        _received_count,
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


def main():
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
    logger.info(f"║  Listening: 0.0.0.0:{LISTEN_PORT:<22}║")
    logger.info("╚══════════════════════════════════════════╝")

    ae.start_server(("0.0.0.0", LISTEN_PORT), evt_handlers=handlers, block=True)


if __name__ == "__main__":
    main()

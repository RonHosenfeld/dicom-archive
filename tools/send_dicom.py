#!/usr/bin/env python3
"""Generate and send synthetic DICOM exams via C-STORE with configurable modality and test patterns."""

import argparse
import random
import sys
from datetime import date, timedelta

import numpy as np
import pydicom
from pydicom.dataset import Dataset
from pydicom.uid import generate_uid, ExplicitVRLittleEndian
from pynetdicom import AE

# ---------------------------------------------------------------------------
# Modality profiles
# ---------------------------------------------------------------------------

MODALITY_PROFILES = {
    "CT": {
        "sop_class_uid": "1.2.840.10008.5.1.4.1.1.2",
        "rows": 512,
        "cols": 512,
        "bits_allocated": 16,
        "bits_stored": 12,
        "default_slices": 20,
        "body_part": "CHEST",
        "study_description": "CT Chest",
        "window_center": 40,
        "window_width": 400,
        "rescale_intercept": -1024,
        "rescale_slope": 1,
        "slice_thickness": 2.5,
        "photometric_interpretation": "MONOCHROME2",
    },
    "MR": {
        "sop_class_uid": "1.2.840.10008.5.1.4.1.1.4",
        "rows": 256,
        "cols": 256,
        "bits_allocated": 16,
        "bits_stored": 16,
        "default_slices": 10,
        "body_part": "BRAIN",
        "study_description": "MR Brain",
        "window_center": 800,
        "window_width": 1600,
        "rescale_intercept": 0,
        "rescale_slope": 1,
        "slice_thickness": 5.0,
        "photometric_interpretation": "MONOCHROME2",
    },
    "CR": {
        "sop_class_uid": "1.2.840.10008.5.1.4.1.1.1",
        "rows": 2048,
        "cols": 2048,
        "bits_allocated": 16,
        "bits_stored": 14,
        "default_slices": 1,
        "body_part": "CHEST",
        "study_description": "CR Chest",
        "window_center": 8192,
        "window_width": 16384,
        "rescale_intercept": 0,
        "rescale_slope": 1,
        "slice_thickness": None,
        "photometric_interpretation": "MONOCHROME2",
    },
    "DX": {
        "sop_class_uid": "1.2.840.10008.5.1.4.1.1.1.1",
        "rows": 2560,
        "cols": 2048,
        "bits_allocated": 16,
        "bits_stored": 14,
        "default_slices": 1,
        "body_part": "CHEST",
        "study_description": "DX Chest",
        "window_center": 8192,
        "window_width": 16384,
        "rescale_intercept": 0,
        "rescale_slope": 1,
        "slice_thickness": None,
        "photometric_interpretation": "MONOCHROME2",
    },
    "MG": {
        "sop_class_uid": "1.2.840.10008.5.1.4.1.1.1.2.1",
        "rows": 4096,
        "cols": 3328,
        "bits_allocated": 16,
        "bits_stored": 12,
        "default_slices": 4,
        "body_part": "BREAST",
        "study_description": "Screening Mammography",
        "window_center": 2048,
        "window_width": 4096,
        "rescale_intercept": 0,
        "rescale_slope": 1,
        "slice_thickness": None,
        "photometric_interpretation": "MONOCHROME2",
    },
}

# ---------------------------------------------------------------------------
# Mammography views
# ---------------------------------------------------------------------------

VIEWS = [
    {"desc": "Left CC", "laterality": "L", "view": "CC", "series_num": 1},
    {"desc": "Left MLO", "laterality": "L", "view": "MLO", "series_num": 2},
    {"desc": "Right CC", "laterality": "R", "view": "CC", "series_num": 3},
    {"desc": "Right MLO", "laterality": "R", "view": "MLO", "series_num": 4},
]

# ---------------------------------------------------------------------------
# Name pools
# ---------------------------------------------------------------------------

LAST_NAMES = [
    "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller",
    "Davis", "Rodriguez", "Martinez", "Anderson", "Taylor", "Thomas", "Moore",
]
FIRST_NAMES = [
    "Maria", "Jennifer", "Linda", "Elizabeth", "Barbara", "Susan", "Jessica",
    "Sarah", "Karen", "Lisa", "Nancy", "Betty", "Dorothy", "Sandra",
]
REFERRING_PHYSICIANS = [
    "Lee^David", "Patel^Anita", "Chen^Wei", "Kim^Soo-Jin",
    "Garcia^Carlos", "Nguyen^Thi", "Williams^James", "Singh^Priya",
]

# ---------------------------------------------------------------------------
# Pattern generators
# ---------------------------------------------------------------------------


def generate_smpte_pattern(rows: int, cols: int, max_val: int) -> np.ndarray:
    """Generate an SMPTE RP 133 inspired test pattern."""
    img = np.full((rows, cols), max_val // 2, dtype=np.uint16)

    # --- Grayscale ramp: 11 patches (0% to 100%) centred vertically ----------
    num_patches = 11
    patch_w = cols // (num_patches + 2)
    patch_h = rows // 6
    y0 = (rows - patch_h) // 2
    x_start = (cols - num_patches * patch_w) // 2

    for i in range(num_patches):
        val = int(max_val * i / (num_patches - 1))
        px = x_start + i * patch_w
        img[y0 : y0 + patch_h, px : px + patch_w] = val

    # --- Low-contrast patches inside 0% and 100% patches --------------------
    lc_h = patch_h // 3
    lc_w = patch_w // 3
    lc_y = y0 + (patch_h - lc_h) // 2

    # 5% square inside the 0% patch
    lc_x0 = x_start + (patch_w - lc_w) // 2
    img[lc_y : lc_y + lc_h, lc_x0 : lc_x0 + lc_w] = int(max_val * 0.05)

    # 95% square inside the 100% patch
    last_patch_x = x_start + (num_patches - 1) * patch_w
    lc_x1 = last_patch_x + (patch_w - lc_w) // 2
    img[lc_y : lc_y + lc_h, lc_x1 : lc_x1 + lc_w] = int(max_val * 0.95)

    # --- Corner resolution targets -------------------------------------------
    corner_size = min(rows, cols) // 8
    line_widths = [1, 2, 3, 4]

    def _draw_horizontal_lines(region: np.ndarray):
        """Draw alternating horizontal line-pair groups."""
        y = 0
        for lw in line_widths:
            for _ in range(4):
                if y + lw > region.shape[0]:
                    return
                region[y : y + lw, :] = max_val
                y += lw
                if y + lw > region.shape[0]:
                    return
                y += lw  # gap

    def _draw_vertical_lines(region: np.ndarray):
        """Draw alternating vertical line-pair groups."""
        x = 0
        for lw in line_widths:
            for _ in range(4):
                if x + lw > region.shape[1]:
                    return
                region[:, x : x + lw] = max_val
                x += lw
                if x + lw > region.shape[1]:
                    return
                x += lw  # gap

    # Top-left / top-right: horizontal lines
    _draw_horizontal_lines(img[0:corner_size, 0:corner_size])
    _draw_horizontal_lines(img[0:corner_size, cols - corner_size : cols])
    # Bottom-left / bottom-right: vertical lines
    _draw_vertical_lines(img[rows - corner_size : rows, 0:corner_size])
    _draw_vertical_lines(img[rows - corner_size : rows, cols - corner_size : cols])

    # --- Centre crosshair ----------------------------------------------------
    cy, cx = rows // 2, cols // 2
    img[cy, :] = max_val
    img[:, cx] = max_val

    # --- White border (2 px) -------------------------------------------------
    img[0:2, :] = max_val
    img[rows - 2 : rows, :] = max_val
    img[:, 0:2] = max_val
    img[:, cols - 2 : cols] = max_val

    return img


def generate_gradient_pattern(rows: int, cols: int, max_val: int) -> np.ndarray:
    """Linear horizontal gradient from 0 to max_val."""
    gradient = np.linspace(0, max_val, cols, dtype=np.float64)
    img = np.tile(gradient, (rows, 1)).astype(np.uint16)
    return img


def generate_random_pattern(
    rows: int, cols: int, max_val: int, rng: random.Random
) -> np.ndarray:
    """Random noise using the provided RNG."""
    rs = np.random.RandomState(rng.randint(0, 2**31))
    return rs.randint(0, max_val + 1, (rows, cols), dtype=np.uint16)


PATTERN_GENERATORS = {
    "smpte": lambda r, c, mv, _rng: generate_smpte_pattern(r, c, mv),
    "gradient": lambda r, c, mv, _rng: generate_gradient_pattern(r, c, mv),
    "random": generate_random_pattern,
}

# ---------------------------------------------------------------------------
# Patient / study helpers
# ---------------------------------------------------------------------------


def generate_patient_info(index: int, rng: random.Random) -> dict:
    last = rng.choice(LAST_NAMES)
    first = rng.choice(FIRST_NAMES)
    birth_year = rng.randint(1950, 1990)
    birth_month = rng.randint(1, 12)
    birth_day = rng.randint(1, 28)
    return {
        "patient_id": f"SIM-{index:04d}",
        "patient_name": f"{last}^{first}",
        "patient_birth_date": f"{birth_year}{birth_month:02d}{birth_day:02d}",
        "patient_sex": "F",
    }


def generate_study_info(index: int, rng: random.Random, profile: dict, modality: str) -> dict:
    study_date = date.today() - timedelta(days=rng.randint(0, 30))
    study_date_str = study_date.strftime("%Y%m%d")
    hour = rng.randint(7, 17)
    minute = rng.randint(0, 59)
    second = rng.randint(0, 59)
    return {
        "study_instance_uid": generate_uid(),
        "study_date": study_date_str,
        "study_time": f"{hour:02d}{minute:02d}{second:02d}",
        "accession_number": f"ACC-{study_date_str}-{index:03d}",
        "study_description": profile["study_description"],
        "modality": modality,
        "referring_physician": rng.choice(REFERRING_PHYSICIANS),
    }


# ---------------------------------------------------------------------------
# Instance creation
# ---------------------------------------------------------------------------


def create_instance(
    patient: dict,
    study: dict,
    profile: dict,
    series_uid: str,
    instance_number: int,
    slice_index: int,
    pixel_data: np.ndarray,
    rng: random.Random,
    *,
    series_number: int = 1,
    series_description: str | None = None,
    laterality: str | None = None,
    view_position: str | None = None,
) -> Dataset:
    """Create a DICOM dataset for any modality."""
    ds = Dataset()

    # --- File meta -----------------------------------------------------------
    file_meta = pydicom.Dataset()
    file_meta.MediaStorageSOPClassUID = profile["sop_class_uid"]
    sop_instance_uid = generate_uid()
    file_meta.MediaStorageSOPInstanceUID = sop_instance_uid
    file_meta.TransferSyntaxUID = ExplicitVRLittleEndian
    ds.file_meta = file_meta
    ds.is_little_endian = True
    ds.is_implicit_VR = False

    # --- Patient -------------------------------------------------------------
    ds.PatientID = patient["patient_id"]
    ds.PatientName = patient["patient_name"]
    ds.PatientBirthDate = patient["patient_birth_date"]
    ds.PatientSex = patient["patient_sex"]

    # --- Study ---------------------------------------------------------------
    ds.StudyInstanceUID = study["study_instance_uid"]
    ds.StudyDate = study["study_date"]
    ds.StudyTime = study["study_time"]
    ds.AccessionNumber = study["accession_number"]
    ds.StudyDescription = study["study_description"]
    ds.Modality = study["modality"]
    ds.ReferringPhysicianName = study["referring_physician"]

    # --- Series --------------------------------------------------------------
    ds.SeriesInstanceUID = series_uid
    ds.SeriesNumber = series_number
    ds.SeriesDate = study["study_date"]
    ds.SeriesDescription = series_description or study["study_description"]
    ds.BodyPartExamined = profile["body_part"]

    if laterality is not None:
        ds.Laterality = laterality
    if view_position is not None:
        ds.ViewPosition = view_position

    # --- Instance ------------------------------------------------------------
    ds.SOPClassUID = profile["sop_class_uid"]
    ds.SOPInstanceUID = sop_instance_uid
    ds.InstanceNumber = instance_number

    # --- Pixel data ----------------------------------------------------------
    rows, cols = pixel_data.shape
    ds.Rows = rows
    ds.Columns = cols
    ds.BitsAllocated = profile["bits_allocated"]
    ds.BitsStored = profile["bits_stored"]
    ds.HighBit = profile["bits_stored"] - 1
    ds.PixelRepresentation = 0
    ds.SamplesPerPixel = 1
    ds.PhotometricInterpretation = profile["photometric_interpretation"]
    ds.PixelData = pixel_data.tobytes()

    # --- Windowing ------------------------------------------------------------
    ds.WindowCenter = str(profile["window_center"])
    ds.WindowWidth = str(profile["window_width"])

    # --- Rescale --------------------------------------------------------------
    ds.RescaleIntercept = str(profile["rescale_intercept"])
    ds.RescaleSlope = str(profile["rescale_slope"])

    # --- Slice geometry (CT / MR) --------------------------------------------
    if profile["slice_thickness"] is not None:
        thickness = profile["slice_thickness"]
        ds.SliceThickness = f"{thickness:.1f}"
        position = slice_index * thickness
        ds.ImagePositionPatient = [0.0, 0.0, position]
        ds.SliceLocation = f"{position:.1f}"

    return ds


# ---------------------------------------------------------------------------
# Exam generation
# ---------------------------------------------------------------------------


def generate_exam(
    exam_index: int,
    rng: random.Random,
    modality: str,
    pattern: str,
    size_override: tuple[int, int] | None,
    slices_override: int | None,
) -> tuple[dict, dict, list[Dataset]]:
    """Generate a complete exam for the given modality."""
    profile = MODALITY_PROFILES[modality]
    patient = generate_patient_info(exam_index, rng)
    study = generate_study_info(exam_index, rng, profile, modality)

    rows = size_override[1] if size_override else profile["rows"]
    cols = size_override[0] if size_override else profile["cols"]
    num_slices = slices_override if slices_override else profile["default_slices"]
    max_val = (1 << profile["bits_stored"]) - 1

    gen_fn = PATTERN_GENERATORS[pattern]
    datasets: list[Dataset] = []

    if modality == "MG":
        # Each view gets its own series and pixel data
        for i, view in enumerate(VIEWS, start=1):
            pixels = gen_fn(rows, cols, max_val, rng)
            ds = create_instance(
                patient,
                study,
                profile,
                series_uid=generate_uid(),
                instance_number=i,
                slice_index=0,
                pixel_data=pixels,
                rng=rng,
                series_number=view["series_num"],
                series_description=view["desc"],
                laterality=view["laterality"],
                view_position=view["view"],
            )
            datasets.append(ds)
    elif modality in ("CT", "MR"):
        # Multi-slice: one series, N instances
        series_uid = generate_uid()
        for s in range(num_slices):
            pixels = gen_fn(rows, cols, max_val, rng)
            ds = create_instance(
                patient,
                study,
                profile,
                series_uid=series_uid,
                instance_number=s + 1,
                slice_index=s,
                pixel_data=pixels,
                rng=rng,
                series_number=1,
                series_description=profile["study_description"],
            )
            datasets.append(ds)
    else:
        # CR / DX: single image
        series_uid = generate_uid()
        pixels = gen_fn(rows, cols, max_val, rng)
        ds = create_instance(
            patient,
            study,
            profile,
            series_uid=series_uid,
            instance_number=1,
            slice_index=0,
            pixel_data=pixels,
            rng=rng,
            series_number=1,
            series_description=profile["study_description"],
        )
        datasets.append(ds)

    return patient, study, datasets


# ---------------------------------------------------------------------------
# Sending
# ---------------------------------------------------------------------------


def _instance_label(ds: Dataset, index: int, total: int, modality: str) -> str:
    """Return a human-readable label for progress output."""
    if modality == "MG":
        lat = getattr(ds, "Laterality", "?")
        vp = getattr(ds, "ViewPosition", "?")
        return f"{lat}-{vp}".ljust(5)
    if modality in ("CT", "MR"):
        return f"Slice {index}/{total}"
    return f"Image {index}/{total}"


def send_exam(
    datasets: list[Dataset],
    host: str,
    port: int,
    ae_title: str,
    calling_ae: str,
    modality: str,
) -> tuple[int, int]:
    ae = AE(calling_ae)
    sop_class = MODALITY_PROFILES[modality]["sop_class_uid"]
    ae.add_requested_context(sop_class, ExplicitVRLittleEndian)

    try:
        assoc = ae.associate(host, port, ae_title=ae_title)
    except Exception as exc:
        print(f"  Connection failed: {exc}", file=sys.stderr)
        return 0, len(datasets)

    if not assoc.is_established:
        print(f"  Association rejected by {ae_title}@{host}:{port}", file=sys.stderr)
        return 0, len(datasets)

    sent = 0
    failed = 0
    for i, ds in enumerate(datasets, start=1):
        label = _instance_label(ds, i, len(datasets), modality)
        print(f"  Sending {label} ({i}/{len(datasets)}) ... ", end="", flush=True)
        status = assoc.send_c_store(ds)
        if status and status.Status == 0x0000:
            print("OK")
            sent += 1
        else:
            code = f"0x{status.Status:04X}" if status else "no response"
            print(f"FAILED ({code})")
            failed += 1

    assoc.release()
    return sent, failed


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------


def parse_size(value: str) -> tuple[int, int]:
    """Parse a WxH size string into (width, height)."""
    parts = value.lower().split("x")
    if len(parts) != 2:
        raise argparse.ArgumentTypeError(f"Invalid size format: {value!r}  (expected WxH)")
    try:
        w, h = int(parts[0]), int(parts[1])
    except ValueError:
        raise argparse.ArgumentTypeError(f"Invalid size format: {value!r}  (expected WxH)")
    if w <= 0 or h <= 0:
        raise argparse.ArgumentTypeError("Width and height must be positive integers")
    return w, h


def main():
    parser = argparse.ArgumentParser(
        description="Send synthetic DICOM exams via C-STORE"
    )
    parser.add_argument("ae_title", help="Target AE title")
    parser.add_argument("--host", default="localhost", help="Target host (default: localhost)")
    parser.add_argument("--port", type=int, default=11112, help="Target port (default: 11112)")
    parser.add_argument("--count", type=int, default=1, help="Number of exams to send (default: 1)")
    parser.add_argument("--calling-ae", default="TEST_SCU", help="Calling AE title (default: TEST_SCU)")
    parser.add_argument("--seed", type=int, default=None, help="Random seed for reproducible data")
    parser.add_argument(
        "--modality",
        choices=["CT", "MR", "CR", "DX", "MG"],
        default="MG",
        help="Modality to generate (default: MG)",
    )
    parser.add_argument(
        "--pattern",
        choices=["smpte", "gradient", "random"],
        default="smpte",
        help="Test pattern for pixel data (default: smpte)",
    )
    parser.add_argument(
        "--size",
        type=parse_size,
        default=None,
        help="Override image dimensions as WxH (e.g. 512x512)",
    )
    parser.add_argument(
        "--slices",
        type=int,
        default=None,
        help="Override number of slices (CT/MR) or views (MG)",
    )
    args = parser.parse_args()

    modality = args.modality
    pattern = args.pattern
    profile = MODALITY_PROFILES[modality]

    print(f"Modality: {modality} | Pattern: {pattern} | Target: {args.ae_title}@{args.host}:{args.port}")

    rng = random.Random(args.seed)
    total_sent = 0
    total_failed = 0
    total_images = 0

    for exam_idx in range(1, args.count + 1):
        patient, study, datasets = generate_exam(
            exam_idx, rng, modality, pattern, args.size, args.slices
        )
        total_images += len(datasets)
        print(
            f"\n[{exam_idx}/{args.count}] Patient: {patient['patient_id']} "
            f"({patient['patient_name']}) | {study['accession_number']}"
        )
        sent, failed = send_exam(
            datasets, args.host, args.port, args.ae_title, args.calling_ae, modality
        )
        total_sent += sent
        total_failed += failed

    print(f"\nDone. Sent {args.count} exam(s) ({total_sent}/{total_images} images), {total_failed} failure(s).")
    sys.exit(1 if total_failed > 0 else 0)


if __name__ == "__main__":
    main()

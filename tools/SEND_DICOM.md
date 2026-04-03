# send_dicom.py — Synthetic DICOM Test Data Generator

Generates and sends synthetic DICOM exams to a DICOM C-STORE SCP. Supports multiple modalities, realistic image dimensions, and standardized test patterns for viewer validation and performance testing.

## Prerequisites

```bash
pip install pydicom pynetdicom numpy
```

## Quick Start

```bash
# Send a default MG exam with SMPTE test pattern
python tools/send_dicom.py ARCHIVE_SCP

# Send 5 CT exams with gradient pattern
python tools/send_dicom.py ARCHIVE_SCP --modality CT --pattern gradient --count 5

# Send a small CR image for quick testing
python tools/send_dicom.py ARCHIVE_SCP --modality CR --size 512x512
```

## Command-Line Options

| Option | Default | Description |
|--------|---------|-------------|
| `ae_title` (positional) | *(required)* | Target AE title of the receiving SCP |
| `--host` | `localhost` | Target host or IP address |
| `--port` | `11112` | Target DICOM port |
| `--count N` | `1` | Number of exams to generate and send |
| `--calling-ae` | `TEST_SCU` | Calling AE title (identifies this sender) |
| `--seed N` | *(random)* | Random seed for reproducible patient data and pixel noise |
| `--modality` | `MG` | Modality to generate: `CT`, `MR`, `CR`, `DX`, or `MG` |
| `--pattern` | `smpte` | Pixel data pattern: `smpte`, `gradient`, or `random` |
| `--size WxH` | *(per modality)* | Override image dimensions (e.g., `512x512`) |
| `--slices N` | *(per modality)* | Override number of slices/views |

## Modality Profiles

Each modality generates clinically realistic DICOM metadata and image dimensions:

| Modality | SOP Class | Default Size | Bits Stored | Slices | Body Part | ~File Size |
|----------|-----------|-------------|-------------|--------|-----------|------------|
| **CT** | CT Image Storage | 512 x 512 | 12-bit | 20 | CHEST | ~0.5 MB |
| **MR** | MR Image Storage | 256 x 256 | 16-bit | 10 | BRAIN | ~130 KB |
| **CR** | CR Image Storage | 2048 x 2048 | 14-bit | 1 | CHEST | ~8 MB |
| **DX** | DX Image Storage | 2048 x 2560 | 14-bit | 1 | CHEST | ~10 MB |
| **MG** | Digital Mammography | 3328 x 4096 | 12-bit | 4 views | BREAST | ~27 MB |

### Modality-Specific Behavior

- **CT / MR** — Multi-slice: all slices share one series UID. Each slice has `ImagePositionPatient`, `SliceLocation`, and `SliceThickness` set for proper spatial ordering in viewers.
- **MG** — Four standard screening views (L-CC, L-MLO, R-CC, R-MLO), each in its own series with `Laterality` and `ViewPosition` tags.
- **CR / DX** — Single image, single series.

### DICOM Tags Set Per Modality

All modalities include: `WindowCenter`, `WindowWidth`, `RescaleIntercept`, `RescaleSlope`, `BodyPartExamined`, `PhotometricInterpretation` (MONOCHROME2).

CT additionally sets `RescaleIntercept = -1024` (Hounsfield units).

## Test Patterns

### SMPTE RP 133 (`--pattern smpte`)

A standardized display calibration pattern used in medical imaging:

- **50% gray background** — fills the entire image
- **Grayscale ramp** — 11 horizontal patches in the center band, stepping from 0% to 100% brightness in 10% increments
- **Low-contrast patches** — A 5% brightness square inside the 0% patch and a 95% brightness square inside the 100% patch (tests subtle contrast visibility)
- **Corner resolution targets** — Alternating line-pair groups at 1px, 2px, 3px, and 4px widths. Horizontal lines in top corners, vertical lines in bottom corners.
- **Center crosshair** — Single-pixel white lines through the exact center
- **White border** — 2px solid border around the entire image

Useful for verifying window/level behavior, display contrast range, and spatial resolution in DICOM viewers.

### Gradient (`--pattern gradient`)

A linear horizontal gradient from 0 (black, left) to max pixel value (white, right). Useful for verifying that window/level adjustments produce smooth transitions without banding artifacts.

### Random (`--pattern random`)

Uniform random noise across the full pixel value range. Produces the same output as the original `send_dicom.py`. When `--seed` is set, the noise is reproducible.

## Examples

```bash
# Reproduce the exact same exam data
python tools/send_dicom.py ARCHIVE_SCP --seed 42

# Quick viewer test with small CT
python tools/send_dicom.py ARCHIVE_SCP --modality CT --size 128x128 --slices 5

# Large DX image for performance testing
python tools/send_dicom.py ARCHIVE_SCP --modality DX

# Batch send: 10 MR exams with random noise
python tools/send_dicom.py ARCHIVE_SCP --modality MR --pattern random --count 10

# Full-size mammography with gradient for W/L testing
python tools/send_dicom.py ARCHIVE_SCP --modality MG --pattern gradient

# Send to a remote host
python tools/send_dicom.py PACS_SCP --host 192.168.1.50 --port 104 --modality CT
```

## Synthetic Patient Data

Each exam generates a random patient with:

- **Patient ID**: `SIM-0001`, `SIM-0002`, ...
- **Patient Name**: Random combination from built-in name pools
- **Birth Date**: Random date between 1950–1990
- **Sex**: F (fixed)
- **Accession Number**: `ACC-YYYYMMDD-NNN`
- **Study Date**: Random date within the last 30 days
- **Referring Physician**: Random from a pool of 8 names

Use `--seed` for reproducible patient demographics across runs.

## Exit Codes

- `0` — All images sent successfully
- `1` — One or more images failed to send

#!/bin/bash
set -e
cd "$(dirname "$0")"

# Use mkdir as an atomic lock — only one process wins the race
if mkdir .venv.lock 2>/dev/null; then
    python3 -m venv .venv
    source .venv/bin/activate
    pip install -q -r requirements.txt
    rmdir .venv.lock
else
    # Another process is setting up; wait for it to finish
    while [ -d .venv.lock ]; do sleep 0.5; done
fi

source .venv/bin/activate
exec python test_pacs.py

FROM python:3.11-slim

WORKDIR /app

# Install dependencies
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Copy agent source
COPY *.py ./

# DICOM port
EXPOSE 11112

# Volumes for local storage and quarantine
VOLUME ["/data/received", "/data/quarantine"]

ENV LOCAL_STORAGE_PATH=/data/received
ENV QUARANTINE_PATH=/data/quarantine

CMD ["python", "agent.py"]

"""
storage.py — pluggable blob storage backends
Supports: local filesystem, AWS S3, Azure Blob Storage
"""

import os
import logging
import hashlib
from pathlib import Path
from abc import ABC, abstractmethod

logger = logging.getLogger(__name__)


def sha256_of_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


class StorageBackend(ABC):
    @abstractmethod
    def store(self, local_path: str, blob_key: str) -> str:
        """Store file and return the blob key / URI."""

    @abstractmethod
    def exists(self, blob_key: str) -> bool:
        pass


# ── Local filesystem ──────────────────────────────────────────────────────────

class LocalStorage(StorageBackend):
    def __init__(self, base_path: str):
        self.base = Path(base_path)
        self.base.mkdir(parents=True, exist_ok=True)

    def store(self, local_path: str, blob_key: str) -> str:
        dest = self.base / blob_key
        dest.parent.mkdir(parents=True, exist_ok=True)
        import shutil
        shutil.copy2(local_path, dest)
        logger.info(f"[local] stored → {dest}")
        return str(dest)

    def exists(self, blob_key: str) -> bool:
        return (self.base / blob_key).exists()


# ── AWS S3 ────────────────────────────────────────────────────────────────────

class S3Storage(StorageBackend):
    def __init__(self, bucket: str, region: str,
                 access_key: str = None, secret_key: str = None):
        import boto3
        session = boto3.Session(
            aws_access_key_id=access_key,
            aws_secret_access_key=secret_key,
            region_name=region,
        )
        self.s3 = session.client("s3")
        self.bucket = bucket

    def store(self, local_path: str, blob_key: str) -> str:
        self.s3.upload_file(
            local_path, self.bucket, blob_key,
            ExtraArgs={"ContentType": "application/dicom"},
        )
        uri = f"s3://{self.bucket}/{blob_key}"
        logger.info(f"[s3] stored → {uri}")
        return uri

    def exists(self, blob_key: str) -> bool:
        from botocore.exceptions import ClientError
        try:
            self.s3.head_object(Bucket=self.bucket, Key=blob_key)
            return True
        except ClientError:
            return False


# ── Azure Blob ────────────────────────────────────────────────────────────────

class AzureStorage(StorageBackend):
    def __init__(self, connection_string: str, container: str):
        import time
        from azure.storage.blob import BlobServiceClient
        from azure.core.exceptions import ResourceExistsError
        # Strip ";ContainerName=..." appended by Aspire blob resource references
        connection_string = ";".join(
            p for p in connection_string.split(";")
            if not p.startswith("ContainerName=")
        )
        self.client = BlobServiceClient.from_connection_string(connection_string)
        self.container = container
        # Ensure container exists (retry — Azurite may still be starting)
        container_client = self.client.get_container_client(container)
        for attempt in range(1, 6):
            try:
                container_client.create_container()
                logger.info(f"[azure] created container '{container}'")
                break
            except ResourceExistsError:
                logger.info(f"[azure] container '{container}' already exists")
                break
            except Exception as exc:
                logger.warning(f"[azure] container create attempt {attempt}/5 failed: {exc}")
                if attempt < 5:
                    time.sleep(2)
                else:
                    raise

    def store(self, local_path: str, blob_key: str) -> str:
        from azure.storage.blob import ContentSettings
        blob = self.client.get_blob_client(container=self.container, blob=blob_key)
        with open(local_path, "rb") as f:
            blob.upload_blob(f, overwrite=True,
                             content_settings=ContentSettings(content_type="application/dicom"))
        uri = f"https://{self.client.account_name}.blob.core.windows.net/{self.container}/{blob_key}"
        logger.info(f"[azure] stored → {uri}")
        return uri

    def exists(self, blob_key: str) -> bool:
        blob = self.client.get_blob_client(container=self.container, blob=blob_key)
        return blob.exists()


# ── Factory ───────────────────────────────────────────────────────────────────

def get_storage_backend() -> StorageBackend:
    backend = os.getenv("STORAGE_BACKEND", "local").lower()

    if backend == "local":
        return LocalStorage(os.getenv("LOCAL_STORAGE_PATH", "./received"))

    elif backend == "s3":
        return S3Storage(
            bucket=os.getenv("S3_BUCKET", "dicom-archive"),
            region=os.getenv("AWS_REGION", "us-east-1"),
            access_key=os.getenv("AWS_ACCESS_KEY_ID"),
            secret_key=os.getenv("AWS_SECRET_ACCESS_KEY"),
        )

    elif backend == "azure":
        return AzureStorage(
            connection_string=os.getenv("AZURE_STORAGE_CONNECTION_STRING", ""),
            container=os.getenv("AZURE_CONTAINER", "dicom-archive"),
        )

    else:
        raise ValueError(f"Unknown STORAGE_BACKEND: {backend}")

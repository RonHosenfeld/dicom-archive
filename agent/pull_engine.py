"""
pull_engine.py — HTTP polling consumer for remote routing commands.

Polls the server for pending study routing commands, downloads DICOM instances
via HTTPS, and C-STOREs them to a local destination AE.

Follows the same start/stop/worker pattern as uploader.py.
"""

import os
import json
import asyncio
import logging
import tempfile
from pathlib import Path

import aiohttp
from pynetdicom import AE
from pynetdicom.sop_class import Verification
from pydicom import dcmread

logger = logging.getLogger("dicom-agent")

MAX_RETRIES = 3
RETRY_DELAY = 5  # seconds


class PullEngine:
    def __init__(
        self,
        server_url: str,
        api_key: str,
        ae_title: str = "ARCHIVE_SCU",
        workers: int = 2,
        poll_interval: int = 2,
    ):
        self.server_url = server_url.rstrip("/")
        self.api_key = api_key
        self.ae_title = ae_title
        self.workers = workers
        self.poll_interval = poll_interval
        self._tasks: list[asyncio.Task] = []
        self._session: aiohttp.ClientSession | None = None

    async def start(self):
        self._session = aiohttp.ClientSession(
            headers={"X-Api-Key": self.api_key},
            timeout=aiohttp.ClientTimeout(total=60),
        )
        for i in range(self.workers):
            task = asyncio.create_task(self._worker(i), name=f"pull-worker-{i}")
            self._tasks.append(task)
        logger.info("Pull engine started with %d workers (poll_interval=%ds)",
                     self.workers, self.poll_interval)

    async def stop(self):
        for task in self._tasks:
            task.cancel()
        if self._session:
            await self._session.close()

    async def _worker(self, worker_id: int):
        while True:
            try:
                commands = await self._poll_for_work()
                if not commands:
                    await asyncio.sleep(self.poll_interval)
                    continue

                for cmd in commands:
                    logger.info("[W%d] Received routing command: study=%s → %s@%s:%s",
                                worker_id, cmd.get("study_uid"),
                                cmd.get("destination_ae_title"),
                                cmd.get("destination_host"),
                                cmd.get("destination_port"))
                    try:
                        await self._process(cmd, worker_id)
                    except Exception as e:
                        logger.exception("[W%d] Error processing command: %s", worker_id, e)
            except asyncio.CancelledError:
                break
            except Exception as e:
                logger.exception("[W%d] Unexpected error in pull worker: %s", worker_id, e)
                await asyncio.sleep(RETRY_DELAY)

    async def _poll_for_work(self) -> list | None:
        assert self._session
        try:
            url = f"{self.server_url}/ingest/pending-routes?agent_ae={self.ae_title}"
            async with self._session.get(url) as resp:
                if resp.status != 200:
                    body = await resp.text()
                    logger.warning("Pending routes returned %d: %s", resp.status, body[:500])
                    return None
                data = await resp.json()
                return data if data else None
        except Exception as e:
            logger.warning("Pending routes request failed: %s", e)
            return None

    async def _process(self, command: dict, worker_id: int) -> bool:
        study_uid = command.get("study_uid", "")
        dest_ae = command.get("destination_ae_title", "")
        dest_host = command.get("destination_host", "")
        dest_port = int(command.get("destination_port", 104))
        routing_log_id = command.get("id")

        # 1. Get instance list for this study
        instances = await self._get_instances(study_uid)
        if instances is None:
            logger.error("[W%d] Could not retrieve instance list for study %s", worker_id, study_uid)
            return False

        logger.info("[W%d] Study %s has %d instances to deliver", worker_id, study_uid, len(instances))

        # 2. Download and C-STORE each instance
        success_count = 0
        for inst in instances:
            instance_uid = inst.get("instance_uid", "")
            for attempt in range(1, MAX_RETRIES + 1):
                try:
                    # Download the .dcm file
                    tmp_path = await self._download_instance(instance_uid)
                    if tmp_path is None:
                        logger.error("[W%d] Download failed for %s (attempt %d/%d)",
                                     worker_id, instance_uid, attempt, MAX_RETRIES)
                        await asyncio.sleep(RETRY_DELAY * attempt)
                        continue

                    # C-STORE to local destination (blocking call in executor)
                    loop = asyncio.get_event_loop()
                    ok = await loop.run_in_executor(
                        None, self._cstore, tmp_path, dest_ae, dest_host, dest_port
                    )

                    # Clean up temp file
                    try:
                        os.unlink(tmp_path)
                    except OSError:
                        pass

                    if ok:
                        success_count += 1
                        logger.info("[W%d] C-STORE success: %s → %s@%s:%d",
                                    worker_id, instance_uid, dest_ae, dest_host, dest_port)
                        break
                    else:
                        logger.error("[W%d] C-STORE failed for %s (attempt %d/%d)",
                                     worker_id, instance_uid, attempt, MAX_RETRIES)
                        await asyncio.sleep(RETRY_DELAY * attempt)

                except Exception as e:
                    logger.exception("[W%d] Error delivering %s (attempt %d/%d): %s",
                                     worker_id, instance_uid, attempt, MAX_RETRIES, e)
                    await asyncio.sleep(RETRY_DELAY * attempt)

        # 3. Acknowledge delivery to server
        if success_count == len(instances) and routing_log_id:
            await self._ack_delivery(routing_log_id)

        logger.info("[W%d] Study %s: delivered %d/%d instances",
                    worker_id, study_uid, success_count, len(instances))
        return success_count == len(instances)

    async def _get_instances(self, study_uid: str) -> list | None:
        assert self._session
        try:
            url = f"{self.server_url}/ingest/studies/{study_uid}/instances"
            async with self._session.get(url) as resp:
                if resp.status != 200:
                    body = await resp.text()
                    logger.warning("Instance list returned %d: %s", resp.status, body[:500])
                    return None
                return await resp.json()
        except Exception as e:
            logger.warning("Instance list request failed: %s", e)
            return None

    async def _download_instance(self, instance_uid: str) -> str | None:
        assert self._session
        try:
            url = f"{self.server_url}/ingest/instances/{instance_uid}/download"
            async with self._session.get(url) as resp:
                if resp.status != 200:
                    body = await resp.text()
                    logger.warning("Download %s returned %d: %s", instance_uid, resp.status, body[:500])
                    return None
                tmp_dir = tempfile.mkdtemp(prefix="pull_")
                tmp_path = os.path.join(tmp_dir, f"{instance_uid}.dcm")
                with open(tmp_path, "wb") as f:
                    async for chunk in resp.content.iter_chunked(65536):
                        f.write(chunk)
                return tmp_path
        except Exception as e:
            logger.warning("Download failed for %s: %s", instance_uid, e)
            return None

    def _cstore(self, dcm_path: str, ae_title: str, host: str, port: int) -> bool:
        """Synchronous C-STORE SCU — called via run_in_executor."""
        try:
            ds = dcmread(dcm_path)
            ae = AE(ae_title=self.ae_title)
            ae.add_requested_context(ds.SOPClassUID, ds.file_meta.TransferSyntaxUID)

            assoc = ae.associate(host, port, ae_title=ae_title)
            if not assoc.is_established:
                logger.error("Could not associate with %s@%s:%d", ae_title, host, port)
                return False

            status = assoc.send_c_store(ds)
            assoc.release()

            if status and status.Status == 0x0000:
                return True
            else:
                logger.error("C-STORE returned status: %s", status)
                return False
        except Exception as e:
            logger.exception("C-STORE exception: %s", e)
            return False

    async def _ack_delivery(self, routing_log_id: int):
        assert self._session
        try:
            url = f"{self.server_url}/ingest/remote-routing/{routing_log_id}/ack"
            async with self._session.post(url) as resp:
                if resp.status == 200:
                    logger.info("Acknowledged delivery for routing log %d", routing_log_id)
                else:
                    body = await resp.text()
                    logger.warning("Ack returned %d: %s", resp.status, body[:500])
        except Exception as e:
            logger.warning("Ack request failed: %s", e)

# Session Recordings — Live Runbook (DEFERRED, manual)

This runbook covers the parts of the recording feature (R-0…R-5) that can only be verified against
**live LiveKit egress and real S3** — things the offline mocked test suite deliberately does not
exercise. Run these manually against a deployed stack. Nothing here is automated by CI.

> The offline suites already prove the logic end-to-end with mocks: capture → webhook → store →
> list → download-url → delete, plus the failure branch, authorization, idempotency, logging
> privacy, and metrics. This document is only the **live** confirmation.

## Prerequisites
- StreamingService, ClassroomService, LiveKit server, RabbitMQ, and the S3/MinIO store all running
  and networked (see `backend/docker-compose.yml` and each service's `docker-compose.unit.yml`).
- LiveKit `egress.s3` and the service `Egress:S3` / `S3Settings` point at the **same** bucket.
- LiveKit `webhook.urls` includes `http://streaming-service:8080/api/webhooks/livekit` and
  `webhook.api_key` matches `LiveKit:ApiKey` (see `backend/StreamingService/livekit.yaml`).
- A teacher account, an enrolled student account, and a non-member account.

## Health check (before you start)
- `GET /health` on StreamingService → `recording_capture_config` should be **Healthy** (LiveKit
  key/secret/host + egress bucket present). Degraded means config is missing.
- `GET /health` on ClassroomService → `recordings_storage` should be **Healthy** (S3 bucket
  reachable with current credentials). Degraded means the bucket is missing/unreachable.

## 1. Capture — a real MP4 lands in S3
1. Start a session (teacher). Confirm StreamingService logs `Started room-composite egress {EgressId}`
   at INFO (note: the object key is **not** logged at INFO — by design).
2. Join and speak for ~30s, then end the session.
3. In S3/MinIO, confirm an object exists under the templated key
   `recordings/{room_name}/{time}.mp4` (the `Egress:KeyTemplate`).
   - Metric check: `recordings_started_total` incremented on StreamingService.

## 2. Webhook → Available
1. When egress finishes, LiveKit POSTs the egress-complete webhook to
   `/api/webhooks/livekit`. Confirm StreamingService logs
   `LiveKit egress webhook received: event egress_ended, egress {EgressId}, status EgressComplete`.
2. Confirm the `SessionRecordingReadyMessage` is published and ClassroomService's consumer logs
   `Session recording for session {SessionId} (egress {EgressId}) set to Available`.
3. `GET /api/classrooms/{classroomId}/recordings` (as a member) shows the recording `Available`
   with correct `durationSeconds` / `sizeBytes` and **no** `s3Key`/`url` field.
   - Metric check: `recordings_completed_total` and the `egress_to_available_seconds` histogram move
     on ClassroomService.

## 3. Download — direct from S3
1. As the **enrolled student**, `GET /api/classrooms/{classroomId}/recordings/{recordingId}/download-url`.
   Expect `200` with `{ url, expiresAt }`.
   - Metric check: `download_urls_issued_total` incremented. Confirm the URL is **not** in any log.
2. Download the MP4 by fetching the `url` directly (bytes do not pass through the backend). It
   should play.

## 4. Authorization
1. As a **non-member**, request the same `download-url`. Expect **403**.
   - Metric check: `download_authz_denied_total{reason="not_member"}` incremented.

## 5. Pre-signed URL expiry
1. Mint a download URL and record `expiresAt`.
2. Wait past `Recordings:DownloadUrlTtlSeconds` (default 600s).
3. Retry the **same** URL. Expect S3 to reject it (HTTP 403 `AccessDenied` / `Request has expired`).

## 6. Delete
1. As the **teacher/admin**, `DELETE /api/classrooms/{classroomId}/recordings/{recordingId}`.
   Expect **204**.
2. Confirm the S3 object is **gone** and the metadata row no longer lists.
   - Metric check: `recordings_deleted_total` incremented. Re-deleting is safe (idempotent).

## 7. Reconcile a stuck recording
1. Force a stuck state: start/stop a session but block or skip the egress webhook so a recording
   stays `Processing`.
2. Wait for the reconcile job (`Recordings:ReconcileIntervalMinutes`, default 15) — or set
   `Recordings:StuckProcessingMinutes` low for the test.
3. Confirm the recording flips to `Failed` with a "reconcile timeout" reason and ClassroomService
   logs `Reconciled {Count} stuck Processing recording(s) to Failed`.
   - Metric check: `recordings_reconciled_total{outcome="failed"}` incremented;
     `recordings_processing_current` returns toward 0.

## 8. Retention (only if enabled)
1. Set `Recordings:RetentionEnabled=true` and a small `Recordings:RetentionDays`.
2. Confirm recordings older than the cutoff are deleted (object + row) on the next maintenance
   cycle, and newer ones are kept. Leave retention **off** by default.

## Privacy checklist (spot-check logs)
Across both services, confirm at INFO level the logs contain **ids, statuses, sizes, durations,
counts, reasons** — and never pre-signed URLs, S3 secrets/credentials, JWTs, or raw `s3_key`
values.

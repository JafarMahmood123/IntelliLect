# Session Summaries — Live Runbook (DEFERRED, manual)

This runbook covers the parts of the session-summary feature (S-0…S-5) that can only be verified
against **live speech-to-text, a live model (Ollama), real S3, and a real broker** — things the
offline mocked suites deliberately do not exercise. Run these manually against a deployed stack.
Nothing here is automated by CI.

> The offline suites already prove the logic end-to-end with mocks: transcript persist → generate →
> render → store → ready event → metadata → list → download-url (PDF **and** MD), plus the failure
> branch, authorization, idempotency, logging privacy, and metrics. This document is only the
> **live** confirmation — and the one thing no test can do: **judging whether the summary is any
> good.**

## Prerequisites
- LiveAssistantService, RagService, ClassroomService, LiveKit, RabbitMQ, host Ollama, and the
  S3/MinIO store all running and networked (see `backend/docker-compose.yml` and each service's
  `docker-compose.unit.yml`).
- Ollama has the summary model pulled (`ollama pull qwen2.5:7b-instruct`) and the embedding model
  (`ollama pull qwen3-embedding`) for grounding.
- `LIVE_ASSISTANT_BASE_URL`, `INTERNAL_API_SECRET` (shared), and the `SUMMARY_S3_*` / `S3Settings`
  point at the same bucket the recordings use (or a dedicated summaries bucket).
- Classroom material is indexed for the classroom (so grounding has something to retrieve).
- A teacher account, an enrolled student account, and a non-member account.

## Health check (before you start)
- `GET /health` on **RagService** → `ollama` reachable + `generationModel` available;
  `pdfRenderer` **available** (WeasyPrint system libs present); `summaryStorage` **reachable**;
  `transcriptEndpoint` **reachable**. Any of these degraded ⇒ fix config before proceeding.
- `GET /health` on **ClassroomService** → `summaries-config` **Healthy** (shared S3 bucket
  configured + a sane download-URL TTL) and `recordings_storage` **Healthy** (bucket reachable).

## 1. Transcript persists during the session (S-0)
1. Start a session (teacher) and speak for ~1–2 minutes of real lecture content.
2. Confirm LiveAssistantService persists FINAL segments incrementally (not just at the end) — watch
   for the transcript rows growing during the session, and on end the `transcript_finalized` INFO
   log with a `segment_count` (never any transcript text).
3. `GET {LIVE_ASSISTANT_BASE_URL}/api/internal/sessions/{sessionId}/transcript` (with
   `X-Internal-Secret`) returns the assembled transcript with `status: Finalized`.

## 2. On session end, a summary generates (S-1 → S-3)
1. End the session. The session-end trigger calls
   `POST {RagService}/api/internal/sessions/{sessionId}/summarize` (body `{ classroomId }`,
   `X-Internal-Secret`) → **202**. Session end must succeed regardless of the summary outcome.
2. Confirm RagService logs the lifecycle at INFO (ids/model/duration/size/keys only — **never**
   transcript or Markdown text, **never** a pre-signed URL): `summary_generation_started` →
   `summary_generation_finished` (model, duration, grounded y/n) → `summary_pdf_rendered` (size) →
   `summary_artifacts_uploaded` (md/pdf **keys**) → `summary_ready_published`.
3. In S3, confirm **two** objects under `summaries/{classroomId}/{sessionId}.md` and `…/.pdf`.
   - Metric check (RagService `/metrics`): `summaries_generated_total`,
     `summary_generation_seconds`, `summary_render_seconds`, `summary_transcript_tokens`, and
     `summaries_grounded_total` (if grounding retrieved anything) all moved.
4. ClassroomService's consumer stores the `SessionSummary` as **Available** — confirm the
   `Session summary for session {SessionId} set to Available` INFO log.
   - Metric check: `summaries_available_current` gauge moved.

## 3. THE QUALITY CHECK — open the PDF and judge it (no test can do this)
This is the primary tuning signal for the S-1 prompt. Download and **open** the generated PDF, then
read it against what was actually taught:
- **Overview** — is the 2–4 sentence recap accurate and on-topic?
- **Key Points** — are the main things covered actually there, and correct?
- **Key Terms** — are the keywords/definitions right, and do they match the lecture's terminology
  (this is where grounding should help)?
- **Notable Moments** — are the emphasized points real ones from the lecture?
- **Fabrication check (most important):** is anything stated that was **not** taught? The model must
  summarize only the transcript; grounding material may sharpen terminology but must never introduce
  untaught content. If you see invented facts, that's a prompt-tuning signal (see
  `app/application/services/summary_prompts.py`).
- Also sanity-check the **styling**: header/subheader (classroom + date), the four sections render,
  bullets and emphasis look right, footer has the page number + generated-on date.

## 4. Download — direct from S3, both formats (S-4)
1. As the **enrolled student**,
   `GET /api/classrooms/{classroomId}/summaries/{summaryId}/download-url?format=pdf` → **200** with
   `{ url, expiresAt }`. Fetch the `url` directly (bytes never pass through the backend) — the PDF opens.
2. Repeat with `?format=md` → the URL serves the Markdown (`text/markdown`, attachment).
   - Metric check: `summary_download_urls_issued_total{format="pdf"}` and `{format="md"}` moved.
   Confirm the URL is **not** in any log; the response never contains an `s3Key`.
3. `GET …/summaries` (as a member) lists it `Available` with **no** `mdS3Key`/`pdfS3Key`/`url` field.

## 5. Authorization
1. As a **non-member**, request the same `download-url`. Expect **403**.
   - Metric check: `summary_authz_denied_total{reason="not_member"}` incremented.

## 6. Pre-signed URL expiry
1. Mint a download URL and record `expiresAt`.
2. Wait past `Summaries:DownloadUrlTtlSeconds` (default 600s).
3. Retry the **same** URL. Expect S3 to reject it (HTTP 403 `AccessDenied` / `Request has expired`).

## 7. Failure branch — Failed summary, download 409, session end unaffected
1. Force a failure: end a session with an **empty/near-empty transcript**, or take Ollama down and
   trigger `…/summarize`.
   - Empty transcript is **not** a hard failure: it still produces a valid minimal "insufficient
     content" summary (Available). To force a real failure, make the model unreachable.
2. Confirm session end still returned 202 and did not break.
3. Confirm RagService publishes a **failure** `SessionSummaryReadyMessage` (logged; metric
   `summaries_failed_total` moved) and ClassroomService marks the `SessionSummary` **Failed** with
   an error.
4. As a member, request the `download-url` → **409 Conflict** (not Available).

## Privacy checklist (spot-check logs across all three services)
At INFO level the logs must contain **ids, model names, counts, sizes, durations, statuses, reasons,
and internal object keys** — and **never** transcript text, summary text/Markdown, pre-signed URLs,
S3 secrets/credentials, or JWTs. The offline logging tests assert this for the mocked path; confirm
it holds with real content too.

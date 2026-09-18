# Conversation Sharing

## Contract

- Owners create a fixed preview, review its text and optional images, and explicitly confirm publication.
- Selected images must load, decode, and render before confirmation is enabled. Failed images require
  a new preview or removal from the selection; they cannot be silently published without review.
- A successful publish immediately exposes the link and revocation control without another status fetch.
  An uncertain response is explicitly flagged and requires a status check before a new preview.
- The standalone React component serves `#/share/{threadId}` anonymously. There is no token.
- Anyone who knows the thread ID can read an active published snapshot. Revoking and republishing
  reactivates the same old URL. Thread IDs are not an additional access-control credential.
- One current snapshot exists per thread. Later conversation changes do not alter it automatically.
- Default expiry is seven days from preview creation; the UI also offers one or thirty days.
  An unpublished preview expires after thirty minutes. Stale previews cannot overwrite newer sharing state.
- Only completed user/assistant text and selected UI screenshots are included. System/tool text,
  original attachments, package downloads, tool arguments, and original artifact URLs are excluded.
- Public routes support GET only. There is no anonymous continuation, deployment, or original download API.
- Revocation, expiry, and deletion of the original conversation block subsequent public reads.
  Already displayed or saved content cannot be retracted; an open page is cleared on expiry, not polled
  for revocation. Public content and screenshots can still be copied or captured.

## Privacy and Limits

Text redaction is heuristic, not a guarantee: it removes common secret formats, links, email addresses,
resource identifiers, and HTML. Review the exact preview for missed sensitive information. Images are
copied as-is with no automatic redaction. Only explicitly selected PNG UI screenshots are accepted.

Limits: 150 messages, 300,000 total text characters, 32,000 characters per text field, six screenshots,
and two MB per screenshot. Text fields over the per-field limit are rejected, not truncated. A thread
may have at most 50 unexpired pending previews. Expired previews and published snapshot history do not
consume that quota; capacity recovers after preview expiry even if physical cleanup is delayed.
Safe Markdown disables external links/images and raw HTML. Restricted Mermaid flowcharts render as
inert images. Share API responses carry no-store, no-referrer, noindex, and nosniff headers.

## Deployment

Deploy both the platform API and frontend. No generated-app or E2B image update is required.

- Cosmos: the existing database gains `Cosmos:SharesContainer` (default `conversationShares`),
  partitioned by `/id`. Startup uses the existing container initialization path. Provision it in advance
  if the platform identity cannot create containers, and grant the required data-plane CRUD/query access.
- Blob: the existing `Storage:Container` (default `artifacts`) must remain private, never `$web`.
  The platform identity needs create/read/write/list/delete rights. Publication refuses a public container.
  Snapshots and copied images use `conversation-shares/{previewId}/`; no public SAS URLs are issued.
- Authentication: outside Development, share management is denied when Entra is unconfigured.
  Configure a concrete tenant and the existing API application registration for owner management.
- Routing: the fragment URL needs no static-host rewrite. Build with the platform API's `VITE_API_BASE`
  and retain the existing exact-origin CORS configuration.
- Rate limits are per API process: 120 public requests per minute per remote IP and 30 management
  requests per minute per user. Reverse proxies may group visitors under one IP; tune at the trusted
  ingress before large-scale use. No new forwarded-header trust configuration is introduced.

Cleanup checks up to 50 eligible records every ten minutes. Revoked metadata is retained for at least
thirty minutes to invalidate existing previews. Backing snapshots from published/replaced shares may
remain private until their original expiry; drafts are eligible after thirty minutes. Backlogs and
storage failures delay physical deletion but do not extend public access.
Cleanup uses version-conditional deletion so an older task cannot delete a newly published record with
the same ID. Failure on one record does not stop processing other records. Deleting an original
conversation does not synchronously revoke or remove snapshot records: subsequent public reads fail
because the original thread no longer exists, and private snapshots remain until their normal expiry.

## Verification

ShareServiceTests cover immutable snapshots, image opt-in, ownership, expiry, revocation, concurrent
previews, interleaved cleanup workers, cleanup failure isolation, preview quota recovery, original
conversation deletion during stuck cleanup, authentication policy, and actual HTTP read/write boundaries. ShareSanitizerTests cover
representative redaction patterns. Browser checks use mocked API/auth responses, not live Azure data.
Cloud Cosmos/Blob permissions and scheduled cleanup still require deployment-environment verification.

Run frontend component regressions with `npm --prefix frontend run test:sharing` (Node.js 20.19+ or
22.12+ for the Vitest toolchain). These cover publication response loss, success without a follow-up
status fetch, revocation, failed image loading/decoding, and confirmation reset.
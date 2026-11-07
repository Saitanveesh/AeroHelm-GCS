# AeroHelm Kerberos enterprise integration — security design, not deployment

**Kerberos is NOT currently configured.** No KDC, AD domain, SPN, keytab,
ticket exchange or authentication gateway has been provisioned. Do not
claim Kerberos protects AeroHelm merely because a UI says so.

## Existing security boundary

- Windows GCS authenticates operator to Oracle over private Tailscale HTTPS.
- Oracle issues a short-lived bearer JWT held only in process memory.
- Pi uses its independently provisioned device token; Tailscale/WireGuard
  secures transport and limits reachability.
- The native M1 bridge is strictly Oracle WSS to Windows localhost TCP,
  with **no GCS-to-aircraft send path**. Video is direct Pi-to-Windows WebRTC
  over tailnet (not Oracle).
- Cloud operator authentication is NOT an aircraft command permission.
  The hardware/flight-control interlock remains independent.

## Where Kerberos *could* add real value

Optional enterprise **operator SSO**, not drone telemetry:
1. Provision an AD/FreeIPA Kerberos realm, time synchronization, domain-
   joined operator Windows machines and an appropriately protected KDC.
2. Register a dedicated HTTP service principal name (SPN) for a private
   authenticated gateway; store its keytab with strict filesystem rights.
3. Integrate SPNEGO / Negotiate at a TLS-terminating reverse proxy or
   dedicated identity service accessible **only inside the tailnet**.
4. Exchange a **verified** Kerberos identity for a short-lived, audience-
   bound AeroHelm API session. Validate service principal, replay windows,
   identity-to-role mapping, revocation and authorization in the backend.
5. Require explicit human-device enrollment and verify on a separate test
   instance before adding a Windows integrated-login option.
6. Retain a narrow independent offline maintenance/recovery path which
   never grants cloud operator tokens or flight authority.
7. Rotate keytabs, audit ticket use, enforce MFA via identity-provider
   policy where available, verify clock skew and logon failures.

Do **not** expose a KDC directly over public Internet, put a keytab or
password in the Pi image, reuse the Pi device token as a Kerberos principal,
or put Kerberos tickets in MAVLink. Kerberos would add setup and failure
modes but would not make the FC UART, WebRTC or MAVLink remotely safe.
Tailscale with per-device ACLs remains the better low-latency telemetry
transport; Kerberos is a conditional SSO control only.

## Additional current controls worth hardening first

- Tailscale ACLs: restrict Windows operator -> Oracle HTTPS and Pi video;
  deny unauthorized peers, disable Funnel and public port-forwarding.
- TLS certificate/hostname validation; no insecure verification bypass.
- Per-device identity/rotation, JWT TTL and refresh policy; RBAC enforcement
  at API endpoints rather than hiding only buttons.
- Failed-login rate limits and clear audit records; real persistent
  server-side audit store rather than RAM-only history.
- Read-only policy and operational guardrails independently on Oracle, Pi
  and GCS. A disabled UI button is never a permission boundary.
- Cold boot and disconnect tests: no replay of stale telemetry.
- Do not auto-apply updates to flight/ground-station software on boot.

**Acceptance gate:** no automatic Kerberos mode will be enabled without
verified domain/KDC/gateway configuration and an independent security review.

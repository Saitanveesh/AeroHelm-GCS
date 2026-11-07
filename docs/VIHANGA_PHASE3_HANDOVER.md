# VIHANGA GCS — Phase-3 engineering handover

**Source:** `Saitanveesh/AeroHelm-GCS`
**Reviewed branch:** `feat/vihanga-phase3-native-toolbar`
**Scope:** Native Windows GCS integration. Cloud service deployment is an
independent release track and is not implied by a successful GCS build.

## Product and UI consolidation

Vihanga (విహంగ) replaces AeroHelm as the operator-facing product name.
`logo.txt`, startup sign-in and the plugin descriptor use the new label.
AeroHelm source namespaces, PowerShell script paths, compiled plugin DLL,
JWT settings and cloud variable names remain for existing installations.
The logo gate accepts both old and new labels so a renamed build never
silently skips the cloud login gate. Mission Planner also uses the label
as the settings XML filename; the branding installer copies an existing
`AeroHelm GCS.xml` or `config.xml` into `Vihanga GCS.xml` when the new
profile is absent. Existing files are never overwritten, and a normal
repair/build applies Vihanga branding automatically. `--local-maintenance` continues to
require deliberate use and does not authorize cloud services.

The redundant nine-tile TOUCH WinForms shell was removed from the plugin
project. The native Mission Planner toolbar is the primary interface:
DATA, PLAN, SETUP, CONFIG, CLOUD, PATHS, SURVEY, VISION, LINK, OPS, INTEL
and MORE. MORE provides Fleet, Security, Analytics, Parameters and
Settings. TOUCH's shared vector icons, installed DLL SHA display and
settings shortcuts were retained in `plugins/VihangaSharedUi.cs`.
The previous launcher filename is a compatibility wrapper.

## Connection design

| Path | Operator entry | Endpoint / selection | Authority |
| --- | --- | --- | --- |
| Cloud account | CLOUD / startup gate | Private HTTPS cloud origin (`AEROHELM_CLOUD_URL`) | Authenticated inspection only |
| Cloud telemetry | PATHS | WSS stream via native loopback TCP `127.0.0.1:5760` | Receive-only MAVLink; outbound commands discarded |
| Local telemetry radio / USB | PATHS / native Mission Planner | Operator-selected COM port and baud | Native flight authority; manual pilot action only |
| Diagnostics | LINK / PATHS | Authenticated `api/v1/mavlink/health`, `api/v1/mavlink/paths` | Read-only status, heartbeat and attitude freshness |
| Video | VISION | Explicit private camera URL(s) | Camera observation; not vehicle control |
| Simulation and maintenance | OPS | `api/v1/operations/*` | Simulator actions; supervised calibration needs local approval |
| Fleet, parameters, assurance | INTEL / MORE | Authenticated `api/v2/*`, relevant `api/v1/vehicles/*/parameters` | View, compare or advisory validation; no physical parameter writes |

A cloud outage is an operator-visible warning, **not** an authorization to
switch aircraft links. A physically present pilot must select the local
telemetry route; autopilot RC and failsafe settings are independent of the
GCS. Cloud authentication, broker availability, session freshness and
Kerberos infrastructure are not substitutes for aircraft acceptance.

## OPS and INTEL functional scope

OPS provides authenticated simulator mission draft/start/pause/resume/abort,
lease renewal and status checks. Supervised calibration requires explicit
technician and propeller-removal attestations plus a separate local Pi
approval. Physical autonomous mission execution remains disabled.

INTEL exposes authenticated fleet status, advisory route validation,
passive mission shadow, read-only parameter snapshot freshness and drift
comparison, history and security posture. Route validation hashes are now
invalidated whenever route, geofence, altitude or speed changes; a changed
plan must be revalidated. Parameter comparisons are sorted by name and
duplicate keys are rejected. A missing vehicle identifier is shown as
missing, not replaced with a fabricated live aircraft ID.

The backend may not have every V2/V3 endpoint deployed. The interface
must show explicit unavailable/unknown state rather than imply a successful
operation. Kerberos SSO remains optional and needs a provisioned KDC,
SPNEGO gateway and exact-origin configuration.

## Verification and deployment

Static source audits and hardware-free tests are necessary but insufficient.
For a release candidate:

1. Review the exact commit, repository changes, license headers and branch.
2. Run `scripts/Test-AeroHelmWindowsPages.ps1`,
   `scripts/Audit-AeroHelmModules.ps1`, offline Python tests and native
   MAVLink/survey test projects.
3. Run the transactional Windows build/install and
   `Consolidate-AeroHelmWindows.ps1 -Stage Verify`; preserve previous binaries.
4. Smoke test the actual Windows GUI (toolbar, MORE tabs, 100/150/200% DPI,
   login, logout, errors, pilot-direct cancellation, duplicate plugin handling).
5. Test cloud origin/TLS/JWT expiry, actual Pi heartbeat and attitude,
   simulated backends, and cloud loss/reconnect without real vehicle commands.
6. On a propeller-free test rig, independently verify telemetry ports,
   parameter and failsafe behavior. Any flight test requires an authorized
   site-specific safety plan; source tests never certify flight readiness.

Release scripts reject unverified EXE/DLL hashes, dirty tracked files and
unknown bridge-port ownership; none performs automated flight tests or
uploads commands to a real aircraft.

## Backend API compatibility

Two distinct backend contracts coexist in this development stack:

| Backend contract | Supported examples | Native GCS availability |
| --- | --- | --- |
| Existing M1/M2 server | `/api/v1/auth/login`, `/api/v1/operations/*`, `/api/v2/fleet`, `/api/v2/vehicles/*/parameters` | GCS CLOUD, legacy OPS simulator and INTEL modules use these authenticated routes |
| New standalone cloud R2 | `/api/v1/capabilities`, `/api/v1/fleet/status`, `/api/v1/fleet/metrics`, `/api/v1/missions` | OPS and INTEL now expose **read-only** checks for these routes; mission dispatch is not wired |
| R2 production identity | OIDC bearer tokens with dedicated scopes | The existing M1 password-login dialog is **not** an R2 OIDC onboarding flow; an approved identity gateway or verified client integration is still required |

Do not claim that OPS or INTEL works end-to-end against R2 until the
identity origin, scope claims, routing, backend migration and
operation semantics have been tested together. R2 virtual mission state is
not evidence of a Pixhawk command. The cloud R2 feature stack is a
separate sequence of draft pull requests, not part of this Windows GCS PR.

## Release assessment

The fail-closed assessment script `scripts/Test-VihangaCertification.ps1`
checks exact-commit source tests, EXE/DLL SHA-256, matching GitHub Actions
results, fresh read-only cloud/Pi observations, and separate operator
acceptance evidence. Its report always sets
`operational_flight_certified=false`; external authorization is required.
See [certification procedure](VIHANGA_CERTIFICATION.md).

## Open integration gates

- Exact-commit Windows CI and hands-on GUI acceptance.
- Private cloud/API/Pi and optional Kerberos deployment verification.
- Physical autopilot radio/USB and failsafe bench acceptance.
- End-to-end mission, geofence, parameter and security review for real aircraft.
- Backend cloud stack reconciliation as a separate reviewed PR/release.

Status is **development candidate**, not production or flight acceptance.

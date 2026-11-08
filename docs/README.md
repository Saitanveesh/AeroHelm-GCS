# Vihanga GCS documentation

Start with the [repository README](../README.md). Use this page to choose the correct procedure. The older AeroHelm name remains in filenames to avoid breaking build and release scripts.

## Current operating documents

| Need | Document |
| --- | --- |
| What the system currently does | [Current capability and test status](AEROHELM_STATUS.md) |
| How the components communicate | [System architecture](AEROHELM_PROJECT.md) |
| Install and verify Raspberry Pi ↔ Oracle private telemetry | [Pi/Oracle live-link deployment](VIHANGA_ORACLE_PI_LIVE_LINK_DEPLOY.md) |
| Understand transport code and health fields | [Private MAVLink service README](../services/vihanga_link/README.md) |
| Prepare and rebuild Windows Vihanga | [Windows bench upgrade](VIHANGA_WINDOWS_BENCH_UPGRADE.md) |
| Set up a new Windows machine | [Clean Windows installation](VIHANGA_CLEAN_WINDOWS_INSTALL.md) |
| Run repeatable end-to-end checks | [Master test runbook](VIHANGA_MASTER_TEST_RUNBOOK.md) |
| Verify release artifacts and operator acceptance | [Vihanga certification checks](VIHANGA_CERTIFICATION.md) |
| Review repository setup rules | [Contributor instructions](../CONTRIBUTING.md) |
| Copyright and inherited source | [Provenance](AEROHELM_PROVENANCE.md) |

## Supervised cloud flight-control development

- [Flight-control architecture, roles, interlocks and release gates](VIHANGA_SUPERVISED_FLIGHT_CONTROL.md) — separate development branch, **not enabled on the aircraft**

## Planned or simulator-only work

Some directories describe future or independently tested features. They are **not** evidence of working remote flight commands or autonomous aircraft operation.

- [Phase 4 cloud parity design](VIHANGA_PHASE4_CLOUD_PARITY.md)
- [Phase 4 bench acceptance notes](VIHANGA_PHASE4_BENCH_ACCEPTANCE.md)
- [Kerberos security design](KERBEROS_SECURITY_DESIGN.md)
- [Cloud fleet experiment](../cloud/README.md)
- [Private telemetry architecture](AEROHELM_FULL_CLOUD_GCS_RFC.md)

## Historical reference

The remaining dated AeroHelm rollout, touch UI, build consolidation and M1 cutover notes are historical engineering records. They may describe earlier toolbar layouts, experiments or issues that were fixed before the private relay was installed. **Do not use them instead of the current install/acceptance documents above.**

The source repository includes thousands of upstream Mission Planner files and resources. Old filenames in upstream `ExtLibs/`, `Scripts/`, `GCSViews/`, `Drivers/` and `Resources/` do not prove those files are unneeded. Deleting them without compiling every affected target is unsafe.

## Public repository metadata

[GitHub About description and topics](AEROHELM_GITHUB_ABOUT.md) contains the reviewed text. GitHub repository settings are separate from Markdown and require an explicit About-field update.

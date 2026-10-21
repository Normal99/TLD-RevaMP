# Project status

This is the single entry point for project organization, status, and privacy. It describes the code and checked-in state; it does not claim that an unrecorded run passed.

## Repository and worktree state

- The canonical integration checkout is on the `dev` lineage at `d1ebfb7`; it is dirty and unpublished.
- The public dev checkout is separate. The dirty master checkout is also separate. All three are preserved.
- Do not reset, commit, push, or create branches while documenting or verifying this state.
- The evidence root is `${XDG_STATE_HOME:-$HOME/.local/state}/tld-revamp`.
- Under that root, `workers/` contains `results/` and `verify/`; `recovery/` is a sibling of `workers/`.
- The queued database is preserved outside the repository.
- Do not publish personal absolute home paths, user names, IP addresses, email addresses, or serial numbers.

## Current product and integration state

- The admission guard isolates test state and enforces resource limits; its suite covers 102 core checks and 11 safety mutations.
- VR covers lifecycle, retry, and read policy. One Quest 3 is sufficient for VR.
- Two-client coverage uses a desktop plus a laptop with distinct Steam accounts. Hardware interaction is external only when the actual hardware is unavailable.
- Settings use one path and expose errors and UI rows.
- Mod enable/disable changes take effect at the next game start. The preloader invalidates this copy’s type-loader caches before moving managed DLLs; deployments include the matching preloader.
- Camera has its own rig and strict comparison.
- Terrain has its own mesh transform, bounded work, and winding handling.
- The native Quest integration is a read-only overview.
- The five headless legacy tools are retired and exit explicitly with status 2.
- Nexus uploads remain owner-only. Website work and stable-release work are postponed and out of scope.

## Build and verification overview

- Unit projects are enumerated dynamically from `tools/unit/*.csproj`; every project targets .NET 10. This document intentionally does not state a project count.
- `tools/verify.sh` runs the units only. Each project has its own build outputs and logs, stored persistently outside the checkout.
- Full gates cover the main build, Python checks, the native guard and boundaries, the separate launcher and privacy checks, and a fresh package.
- The exact admitted unit example is:

  `tools/tld-admit run --profile test --name verify -- tools/verify.sh`

- Boundary and launcher real-job tests run outside the outer admission, so their child admission can operate correctly.
- The actual run manifest records each gate result. No final test `PASS` is inferred from this document.

## Live evidence

Live evidence is recorded separately from offline evidence in the run manifest. Each item must be marked `OBSERVED`, `PENDING`, or `BLOCKED` according to the actual run. It covers runtime behavior, screenshots, profiles, cgroups, Quest hardware, controllers, and two-client operation. Unavailable hardware is not described as permanently absent.

## Offline evidence

Offline evidence covers source, build, unit, Python, native, boundary, launcher, privacy, packaging, and repository-organization checks. The manifest, rather than this status document, is authoritative for whether a gate passed, failed, is pending, or is blocked.

## Quick start

Build with `make -C tools`, open `tools/overview` for the read-only terminal overview, then use the admitted command above for the units-only entry point. Use the repository's full-gate instructions for the complete gate set and consult the run manifest for results.

## Documentation

- [README](../README.md)
- [Developer guide](DEVELOPERS.md)
- [Contributing guide](CONTRIBUTING.md)
- [Tools guide](../tools/README.md)

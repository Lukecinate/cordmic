# Release dry run

Milestone M6 times the chain without publishing a release, pushing a tag, or
submitting a SignPath request. Fill the "actual" column when you run it on your
own machine. Steps that need SignPath acceptance or K1/K2 stay marked pending.

Offline manifest signing stays **manual** even when every other step is scripted.

| Step | Who / tool | Estimated | Actual (min) | Notes |
|---|---|---|---|---|
| Pin check `tools/release/check-action-pins.sh` | box / CI | < 1 min |  | Must exit 0 |
| `cordmic-keytool` generate + sign + verify (test key only) | box | ~1–2 min (measured **1.4 s** wall-clock on the 2026-11-21 box run for the three commands; first `dotnet` start can add a few seconds) |  | Use a throwaway `.p8`. Never use this for K1/K2 |
| `tools/release/make-manifest.py` + `hash-assets.sh` on existing dist files | box | < 1 min |  | Writes an unsigned manifest |
| Gate H script on an unticked checklist | box | < 1 min |  | Must **fail** until every box is ticked (checklist arrives in M8) |
| Windows tests + Android unit tests | box / CI | several minutes |  | Local M6 run is recorded in the implementing PR |
| SignPath inner / installer request | maintainer, after acceptance |  |  | Pending SignPath application. Do not submit a signing request from a dry run unless you mean to |
| APK sign `tools/release/sign-apk.sh` | maintainer machine |  |  | Keystore stays off the shared box |
| Offline `cordmic-keytool sign` with **K1** | maintainer, manual | a few minutes |  | Pending K1 generation. This step is never automated |
| `workflow_dispatch` verify-and-publish | maintainer |  |  | Not used in the dry run; would publish |

## What this dry run does not do

- It does not push, tag, or create a GitHub release.
- It does not submit the SignPath Foundation application.
- It does not generate or store K1/K2.
- It does not move or print the Android keystore.

## Reproducibility note

The target is: two unsigned `dotnet publish` runs of the same commit, with
`Deterministic=true` and `ContinuousIntegrationBuild=true`, produce identical
PE files. Confirm that on a GitHub-hosted `windows-2022` runner once the
workflow has been run; the shared box is not a Windows runner.

# Incident and rollback runbook

This is the short path when a shipped release is wrong, a key is in doubt, or
users need a way off a bad USB build. It does not ask anyone to disable
security features.

## 1. Pull a bad release off "latest"

If a GitHub release was published as the latest stable by mistake, or it is
broken:

1. On GitHub, edit the release and mark it as a **pre-release** (not latest).
   `/releases/latest` then points at the previous stable tag (for example 0.7.2).
2. Do not delete the tag or the assets (people who already downloaded them need
   a fixed checksum to compare). Do not move the tag.
3. In the release notes, state that the build should not be used, and point to
   the last good version and to Settings → Connection → **Turn off the USB engine**
   if the problem is in the USB path.
4. Ship a **higher** version (for example `0.8.1` after a bad `0.8.0`) through a
   reduced Gate H: the items that cover the failure, plus updater verification
   for installed and portable copies. Record what was skipped and why.

The in-app updater ignores pre-releases, so converting a bad "latest" into a
pre-release stops further automatic upgrades to it. Clients that already
installed it need the higher version, or a manual install of the last good
build.

## 2. Turn the USB engine off (tell users this)

Any one of these is enough on a given PC:

- Settings → Connection → **Turn off the USB engine**
- Start CordMic with `--no-usb-engine`
- Create the file `%ProgramData%\CordMic\disable-usb-engine`

CordMic then uses only USB tethering, as in 0.7.2. The setting stays on that PC.
Nothing is sent anywhere.

## 3. Collect diagnostics without recordings

Settings → Connection → **Copy sanitized diagnostics** puts a text report on the
clipboard. IP addresses and Windows user names are replaced. Voice recordings
are never included. Ask the person to review the text before they send it.

## 4. Key or token incidents

- **K1 lost or leaked:** ship N+1 signed with K2, put K1 on the embedded
  revocation list, and add a new backup key K3. Clients that have not yet
  updated still trust K1 until they install N+1; Authenticode through the
  SignPath project is still required for a Windows auto-update, so a K1 leak
  alone is not enough for a hostile update. Rotate SignPath credentials if they
  may have been exposed as well.
- **K2 also in doubt:** stop automatic signing, take the latest release back to
  pre-release if needed, and start a planned key ceremony (new K3/K4) before
  the next latest publish.
- **Android keystore lost:** existing installs cannot be updated with a new key.
  Treat this as a stop-ship for Play/sideload updates and say so honestly.
- **SignPath token leaked:** revoke it in SignPath, create a new token, update
  the `release` environment secret, and review recent signing requests.

## 5. What not to do

- Do not disable Secure Boot, Memory Integrity, Driver Signature Enforcement,
  or antivirus, and do not ask users to do that.
- Do not move or reuse a released tag.
- Do not print private keys, keystore passwords, or API tokens in tickets or logs.

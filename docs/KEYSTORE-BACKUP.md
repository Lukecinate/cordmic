# Android keystore backup

The existing Android upload keystore is as important as the offline release keys.
This document is the procedure. The scripts under `tools/keystore/` encrypt and
test a restore; they never move the original keystore and they never print a
password or key.

## Rules

- Keep the original keystore where you already store it. Do not copy it into this
  repository, the shared box, chat, cloud-synced folders, or email.
- Make **two encrypted copies**, on two different devices or two different offline
  media (A and B).
- Store the encryption passphrase in a different place from those copies.
- After each backup, run a restore test into a temporary folder, confirm the
  SHA-256 of the restored file matches the original, then delete the temporary
  file.
- Do not change the upload key. Future APKs must be signed with the same key as
  0.6.1–0.7.2 / 0.8.0-beta.1.

## Backup (on your own machine)

```bash
# You will be prompted for an encryption passphrase (not echoed).
# --in is your existing keystore. --out1 and --out2 are the two encrypted copies.
./tools/keystore/backup-keystore.sh \
  --in /path/to/your/keystore.jks \
  --out1 /media/stick-a/cordmic-upload-keystore.jks.enc \
  --out2 /media/stick-b/cordmic-upload-keystore.jks.enc
```

The script refuses to overwrite an output, refuses a path inside a git working
tree, and does not read `*.properties` files.

## Restore test

```bash
./tools/keystore/restore-keystore-test.sh \
  --enc /media/stick-a/cordmic-upload-keystore.jks.enc \
  --original /path/to/your/keystore.jks
```

This decrypts into a temporary directory, compares SHA-256 with `--original`,
prints only `ok` or `fail`, and deletes the temporary copy. It does not replace
the original.

Repeat the restore test for the second copy.

## If a copy cannot be decrypted

Use the other copy. Make a fresh pair of encrypted backups from the original as
soon as you can. If the original and both copies are lost, new APKs cannot update
existing installs; treat that as an incident and follow `docs/INCIDENT-ROLLBACK.md`.

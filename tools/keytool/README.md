# cordmic-keytool

Small tool that generates, checks and uses the offline CordMic release keys
(ECDSA P-256, SHA-256, encrypted PKCS#8). It never prints a private key or a
passphrase. Generate K1 and K2 on your own machine, not on a shared box. See
`RELEASING.md`.

```
dotnet run --project tools/keytool -- generate --out E:\cordmic-release-K1.p8
dotnet run --project tools/keytool -- check E:\cordmic-release-K1.p8
dotnet run --project tools/keytool -- sign release-manifest.json --key E:\cordmic-release-K1.p8
```

`CORDMIC_KEYTOOL_PASSPHRASE` exists only so automated tests can run the tool.
Do not use it for K1 or K2.

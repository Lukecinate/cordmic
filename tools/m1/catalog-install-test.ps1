# Route 2b catalog install *test plan* runner.
# Refuses to install an unsigned or untrusted catalog. Does not change Secure Boot
# or Memory Integrity. Prefer a Windows 11 VM with USB passthrough first.

param(
    [string]$InfPath = "",
    [switch]$Undo
)

$ErrorActionPreference = 'Stop'
Write-Output 'gate=PENDING-HARDWARE'
Write-Output 'script=catalog-install-test.ps1'

if (-not $InfPath) {
    $InfPath = Join-Path $PSScriptRoot '..\..\windows-app\driver\cordmic-accessory.inf'
}

if (-not (Test-Path $InfPath)) {
    Write-Output 'result=missing-inf'
    Write-Output 'note=The accessory INF is in the repo for review. A signed catalog does not exist yet (SignPath onboarding is still open).'
    exit 2
}

$cat = [IO.Path]::ChangeExtension($InfPath, '.cat')
if (-not (Test-Path $cat)) {
    Write-Output 'result=unsigned'
    Write-Output 'note=No .cat is present. CordMic will not install this package and will not use test-signing or a self-signed root.'
    exit 3
}

Write-Output 'result=signature-check-required'
Write-Output 'note=On the user PC, verify the catalog with Windows (Authenticode) before any pnputil add. If Windows refuses the SignPath signature, record the exact error, Windows build, and Secure Boot / Memory Integrity state. Do not work around a refusal.'
if ($Undo) {
    Write-Output 'note=Undo uses only a recorded oemNN.inf that was confirmed to be this package (provider and hardware IDs).'
}
exit 0

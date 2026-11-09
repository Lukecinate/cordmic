# Download the official VB-CABLE Basic pack, verify the pinned SHA-256
# (and Authenticode signer), then launch VB-Audio's own setup. CordMic
# never redistributes the driver.
param(
    [switch] $CheckOnly,
    [switch] $SkipIfPresent = $true
)

$ErrorActionPreference = "Stop"
$Url = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip"
$Sha256 = "bc44e8974191a5a2427d6c979e2dab08dbb09925f3f435dfa2f0ba3e45b6eb90"
$Page = "https://vb-audio.com/Cable/"
$SetupName = "VBCABLE_Setup_x64.exe"

function Test-VbCableInstalled {
    $sys = Join-Path $env:WINDIR "System32\drivers\vbaudio_cable64_win10.sys"
    if (Test-Path $sys) { return $true }
    $key = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VB-Audio Virtual Cable"
    if (Test-Path $key) { return $true }
    try {
        $devs = Get-CimInstance -ClassName Win32_SoundDevice -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match "VB-Audio|CABLE Input|CABLE Output" }
        if ($devs) { return $true }
    } catch {}
    return $false
}

if ($CheckOnly) {
    if (Test-VbCableInstalled) { exit 0 } else { exit 2 }
}

if ($SkipIfPresent -and (Test-VbCableInstalled)) {
    Write-Host "VB-CABLE is already installed."
    exit 0
}

$tmp = Join-Path $env:TEMP ("cordmic-vbcable-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $zip = Join-Path $tmp "VBCABLE_Driver_Pack46.zip"
    Write-Host "Downloading official VB-CABLE package..."
    Invoke-WebRequest -Uri $Url -OutFile $zip -UseBasicParsing
    $hash = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $Sha256) { throw "SHA-256 mismatch (got $hash)" }
    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    $setup = Join-Path $tmp $SetupName
    if (-not (Test-Path $setup)) { throw "Official zip did not contain $SetupName" }
    $sig = Get-AuthenticodeSignature -FilePath $setup
    $subject = $sig.SignerCertificate.Subject
    if ($sig.Status -ne "Valid" -or ($subject -notmatch "VB-Audio" -and $subject -notmatch "Vincent Burel" -and $subject -notmatch "V\.Burel")) {
        throw "Authenticode signer not recognized: $subject (status $($sig.Status))"
    }
    Write-Host "Launching official VB-CABLE setup (a reboot may be required)..."
    # The official package documents no silent switch.
    $p = Start-Process -FilePath $setup -Verb RunAs -Wait -PassThru
    Write-Host "VB-CABLE setup exit $($p.ExitCode)."
    exit 0
} catch {
    Write-Host $_
    Write-Host "Download it yourself from $Page"
    exit 1
} finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

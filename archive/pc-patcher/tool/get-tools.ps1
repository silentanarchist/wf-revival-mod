# Used by "1 - patch.bat".
# The patching program (LemonPatch.exe) and the loader package (melon_data.zip) are about 84 MB, so since the two mod
# repositories were merged (2026-10-17) they are no longer kept in this repository. This downloads any piece that is
# missing from the commit of the old Android repository (silentanarchist/wf-revival-mod-android) where they
# were last published, and checks each against the SHA-256 recorded here, so a changed or broken download is never used.
# Pieces already in this folder (for example from an older copy of the patcher) are checked the same way and kept.
$ErrorActionPreference = 'Stop'
$base = 'https://raw.githubusercontent.com/silentanarchist/wf-revival-mod-android/52b333dd265cbee4471db22bfe140d95e99754c5/patcher/tool/'
$pieces = [ordered]@{
    'LemonPatch.exe.part0'  = 'bc50bf789786693ccad9ef2d39830bac15be41c125e102234acb58d041372921'
    'LemonPatch.exe.part1'  = '4cf9296394c67de68aa777c05c6c81d502c545b524036235cdf0fb585064d2f2'
    'melon_data.zip.part0'  = '41f87945eca7f9c655bc02a065568e1de30df16af3b3244b49bb55f629c5407f'
    'melon_data.zip.part1'  = '81880117164d73e9841f8619f9d2c5ab24ef28c30ee12d561bc3dbf432727c9e'
    'melon_data.zip.part2'  = 'c6dd91c8931247ac7ff5d490699089122fc058128c57673108ee5892c68aa445'
}
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
# Windows PowerShell 5.1 does not offer TLS 1.2 by default; GitHub needs it.
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
$ProgressPreference = 'SilentlyContinue'   # the progress bar slows Invoke-WebRequest down a lot
$failed = $false
foreach ($name in $pieces.Keys) {
    $path = Join-Path $here $name
    $want = $pieces[$name]
    if (Test-Path $path) {
        if ((Get-FileHash -Algorithm SHA256 $path).Hash.ToLower() -eq $want) { continue }
        Write-Host "  $name is not the expected file; downloading it again."
        Remove-Item $path
    }
    Write-Host "  Downloading $name ..."
    $tmp = "$path.download"
    try {
        Invoke-WebRequest -UseBasicParsing -Uri ($base + $name) -OutFile $tmp
        if ((Get-FileHash -Algorithm SHA256 $tmp).Hash.ToLower() -ne $want) { throw "the downloaded file does not match its SHA-256" }
        Move-Item $tmp $path
    } catch {
        if (Test-Path $tmp) { Remove-Item $tmp }
        Write-Host "  Could not get $name ($($_.Exception.Message))."
        $failed = $true
    }
}
if ($failed) { exit 1 }
exit 0

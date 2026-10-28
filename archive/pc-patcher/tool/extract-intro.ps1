# Used by "7 - fix loading video.bat".
# Copies the loading-screen video out of the game's own APK, unchanged, so ffmpeg can convert it.
# The video is stored inside assets/bin/Data/sharedassets0.resource as a plain MP4; it is found by
# its first box ("ftyp") and its length is taken from the MP4's own boxes (ftyp, mdat, moov ...),
# so no fixed offsets are needed. Works with Windows PowerShell 5.1 and PowerShell 7.
param([Parameter(Mandatory = $true)][string]$Apk, [Parameter(Mandatory = $true)][string]$Out)
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Apk).Path)
try {
    $entry = $zip.GetEntry('assets/bin/Data/sharedassets0.resource')
    if ($null -eq $entry) { Write-Host '  The game file has no assets/bin/Data/sharedassets0.resource.'; exit 2 }
    $buffer = New-Object System.IO.MemoryStream
    $stream = $entry.Open()
    try { $stream.CopyTo($buffer) } finally { $stream.Dispose() }
} finally { $zip.Dispose() }
$data = $buffer.ToArray()

function Get-U32([byte[]]$a, [int64]$o) {
    return ([uint64]$a[$o] * 16777216) + ([uint64]$a[$o + 1] * 65536) + ([uint64]$a[$o + 2] * 256) + [uint64]$a[$o + 3]
}

# ISO-8859-1 maps every byte to one character, so a string search finds byte positions quickly.
$text = [System.Text.Encoding]::GetEncoding(28591).GetString($data)
$at = $text.IndexOf('ftyp', [System.StringComparison]::Ordinal)
if ($at -lt 4) { Write-Host '  No video found in the game file.'; exit 3 }
$start = [int64]($at - 4)

# Walk the MP4's top-level boxes until something that is not a box.
$pos = $start
$sawMoov = $false
while ($pos + 8 -le $data.Length) {
    $size = Get-U32 $data $pos
    $type = $text.Substring([int]$pos + 4, 4)
    if ($size -eq 1) { $size = (Get-U32 $data ($pos + 8)) * 4294967296 + (Get-U32 $data ($pos + 12)) }
    if ($size -lt 8 -or $type -notmatch '^[a-z]{4}$' -or $pos + $size -gt $data.Length) { break }
    if ($type -eq 'moov') { $sawMoov = $true }
    $pos += $size
}
$length = $pos - $start
if (-not $sawMoov -or $length -lt 100000) { Write-Host '  The video in the game file looks incomplete.'; exit 4 }

$clip = New-Object byte[] $length
[Array]::Copy($data, $start, $clip, 0, $length)
[System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($Out), $clip)
Write-Host ('  Video copied out of the game: {0:N1} MB.' -f ($length / 1MB))
exit 0

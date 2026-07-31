param(
    [Parameter(Mandatory = $true)]
    [string] $Archive
)

$ErrorActionPreference = 'Stop'
$archivePath = (Resolve-Path $Archive).Path
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("parley-release-smoke-" + [guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    Expand-Archive -Path $archivePath -DestinationPath $testRoot
    $parley = Get-ChildItem -Path $testRoot -Filter parley.exe -Recurse | Select-Object -First 1
    if (-not $parley) { throw 'Archive does not contain parley.exe.' }

    & $parley.FullName --version
    if ($LASTEXITCODE -ne 0) { throw 'parley --version failed.' }

    $env:PARLEY_HOME = Join-Path $testRoot 'state'
    & $parley.FullName join smoke --as sender --sid smoke-sender | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'sender join failed.' }
    & $parley.FullName join smoke --as receiver --sid smoke-receiver | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'receiver join failed.' }

    $sequence = & $parley.FullName send smoke --as sender --sid smoke-sender --to receiver --wake never -m 'release smoke'
    if ($LASTEXITCODE -ne 0 -or $sequence -ne '1') { throw 'send failed.' }
    $received = & $parley.FullName recv smoke --as receiver --sid smoke-receiver --last-seen 0
    if ($LASTEXITCODE -ne 0 -or -not ($received -match 'release smoke')) { throw 'receive failed.' }
    Write-Host "release smoke passed: $archivePath"
}
finally {
    Remove-Item -Path $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}

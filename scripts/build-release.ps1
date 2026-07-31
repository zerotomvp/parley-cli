param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64', 'win-x64', 'win-arm64')]
    [string] $Runtime,

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [string] $ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
$publish = Join-Path $output 'publish'
$stage = Join-Path $output 'stage'
$artifacts = Join-Path $output 'artifacts'

Remove-Item $publish, $stage, $artifacts -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $publish, $stage, $artifacts | Out-Null

& dotnet publish (Join-Path $repoRoot 'parley-cli.csproj') `
    -c Release -r $Runtime --self-contained true -o $publish `
    -p:AssemblyName=parley -p:PublishSingleFile=true -p:PublishTrimmed=false `
    -p:TreatWarningsAsErrors=true
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$executableName = if ($Runtime.StartsWith('win-')) { 'parley.exe' } else { 'parley' }
$executable = Join-Path $publish $executableName
$version = (& $executable --version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'published parley --version failed.' }
if ($ExpectedVersion -and $version -ne $ExpectedVersion) {
    throw "Published version '$version' does not match expected '$ExpectedVersion'."
}

Copy-Item $executable (Join-Path $stage $executableName)
Copy-Item (Join-Path $repoRoot 'LICENSE') $stage
Copy-Item (Join-Path $repoRoot 'README.md') $stage
Copy-Item (Join-Path $repoRoot 'CHANGELOG.md') $stage
Copy-Item (Join-Path $repoRoot 'SPEC.md') $stage

$baseName = "parley-$version-$Runtime"
if ($Runtime.StartsWith('win-')) {
    $archive = Join-Path $artifacts "$baseName.zip"
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive
}
else {
    $archive = Join-Path $artifacts "$baseName.tar.gz"
    & tar -C $stage -czf $archive .
    if ($LASTEXITCODE -ne 0) { throw 'tar archive creation failed.' }
}

Write-Host "Built $archive"
Write-Output $archive

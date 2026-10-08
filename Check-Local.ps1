$ErrorActionPreference = 'Stop'

$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Join-Path $projectRoot 'CodexUsageTray.cs'
$checks = Join-Path $projectRoot 'checks\SelfCheck.cs'
$notices = Join-Path $projectRoot 'THIRD-PARTY-NOTICES.txt'
$buildDir = [IO.Path]::GetFullPath((Join-Path $projectRoot 'build'))

foreach ($file in @($compiler, $source, $checks, $notices)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Required file not found: $file"
    }
}
if (-not $buildDir.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Build directory must be inside the project'
}
New-Item -ItemType Directory -Path $buildDir -Force | Out-Null

$common = @(
    '/nologo', '/langversion:5', '/platform:x64', '/optimize+',
    '/warn:4', '/warnaserror+', '/utf8output',
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll',
    '/r:System.Web.Extensions.dll',
    "/resource:$notices,CodexUsageTray.ThirdPartyNotices"
)

& $compiler @common /target:winexe /main:CodexUsageTray.Program `
    "/out:$buildDir\CodexUsageTray.exe" $source
if ($LASTEXITCODE -ne 0) { throw "Product build failed with exit code $LASTEXITCODE" }

& $compiler @common /target:exe /main:CodexUsageTray.SelfCheck `
    "/out:$buildDir\SelfCheck.exe" $source $checks
if ($LASTEXITCODE -ne 0) { throw "SelfCheck build failed with exit code $LASTEXITCODE" }

$selfCheck = Join-Path $buildDir 'SelfCheck.exe'
& $selfCheck
if ($LASTEXITCODE -ne 0) { throw "SelfCheck failed with exit code $LASTEXITCODE" }

& $selfCheck '--integration-check'
if ($LASTEXITCODE -ne 0) { throw "Offline integration check failed with exit code $LASTEXITCODE" }

Write-Host 'PASS: local build and offline checks'

$ErrorActionPreference = 'Stop'

$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Join-Path $projectRoot 'src\CodexUsageTray.cs'
$selfCheckSource = Join-Path $projectRoot 'tests\SelfCheck.cs'
$notices = Join-Path $projectRoot 'THIRD-PARTY-NOTICES.txt'
$buildDir = [IO.Path]::GetFullPath((Join-Path $projectRoot 'build\checks'))
$distDir = [IO.Path]::GetFullPath((Join-Path $projectRoot 'dist'))

foreach ($file in @($compiler, $source, $selfCheckSource, $notices)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Required file not found: $file"
    }
}
foreach ($outputDir in @($buildDir, $distDir)) {
    if (-not $outputDir.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Output directory must be inside the project'
    }
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

$common = @(
    '/nologo', '/langversion:5', '/platform:x64', '/optimize+',
    '/warn:4', '/warnaserror+', '/utf8output',
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll',
    '/r:System.Web.Extensions.dll',
    "/resource:$notices,CodexUsageTray.ThirdPartyNotices"
)

& $compiler @common /target:winexe /main:CodexUsageTray.Program `
    "/out:$distDir\CodexUsageTray.exe" $source
if ($LASTEXITCODE -ne 0) { throw "Product build failed with exit code $LASTEXITCODE" }

& $compiler @common /target:exe /main:CodexUsageTray.SelfCheck `
    "/out:$buildDir\SelfCheck.exe" $source $selfCheckSource
if ($LASTEXITCODE -ne 0) { throw "SelfCheck build failed with exit code $LASTEXITCODE" }

$selfCheck = Join-Path $buildDir 'SelfCheck.exe'
& $selfCheck
if ($LASTEXITCODE -ne 0) { throw "SelfCheck failed with exit code $LASTEXITCODE" }

& $selfCheck '--integration-check'
if ($LASTEXITCODE -ne 0) { throw "Offline integration check failed with exit code $LASTEXITCODE" }

Write-Host 'PASS: local build and offline checks'

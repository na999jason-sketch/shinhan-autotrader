param([string]$ReportPath = (Join-Path $PSScriptRoot '..\..\reports\uipi-tests.txt'))
$ErrorActionPreference = 'Stop'
$runnerProject = Join-Path $PSScriptRoot 'UipiTests.csproj'
msbuild $runnerProject /t:Rebuild /p:Configuration=Release /p:Platform=x86 /warnaserror
if ($LASTEXITCODE -ne 0) { throw "UIPI fixture build failed ($LASTEXITCODE)." }
$runner = Join-Path $PSScriptRoot 'bin\Release\UipiTests.exe'
& $runner ([System.IO.Path]::GetFullPath($ReportPath))
if ($LASTEXITCODE -ne 0) { throw "Windows UIPI assertions failed ($LASTEXITCODE). See $ReportPath." }

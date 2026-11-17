param([string]$ReportPath = (Join-Path $PSScriptRoot '..\..\reports\report-ui-tests.txt'))
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Report UI tests require a fresh GitHub Actions runner profile.' }
$dataPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ShinhanNativeTrader'
if (Test-Path -LiteralPath $dataPath) { throw 'Refusing to touch an existing application data path.' }
$runnerProject = Join-Path $PSScriptRoot 'ReportUiTests.csproj'
msbuild $runnerProject /t:Rebuild /p:Configuration=Release /p:Platform=x86 /warnaserror
if ($LASTEXITCODE -ne 0) { throw "Report UI runner build failed ($LASTEXITCODE)." }
$runner = Join-Path $PSScriptRoot 'bin\Release\ReportUiTests.exe'
& $runner ([System.IO.Path]::GetFullPath($ReportPath))
if ($LASTEXITCODE -ne 0) { throw "Report UI assertions failed ($LASTEXITCODE). See $ReportPath." }

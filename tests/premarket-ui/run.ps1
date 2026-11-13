param([string]$ReportPath = (Join-Path $PSScriptRoot '..\..\reports\premarket-ui-tests.txt'))
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Premarket UI tests require a fresh GitHub Actions runner profile.' }
$dataPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ShinhanNativeTrader'
if (Test-Path -LiteralPath $dataPath) { throw 'Refusing to touch an existing application data path.' }
$project = Join-Path $PSScriptRoot 'PremarketUiTests.csproj'
msbuild $project /t:Rebuild /p:Configuration=Release /p:Platform=x86 /warnaserror
if ($LASTEXITCODE -ne 0) { throw "Premarket UI runner build failed ($LASTEXITCODE)." }
$runner = Join-Path $PSScriptRoot 'bin\Release\PremarketUiTests.exe'
& $runner ([System.IO.Path]::GetFullPath($ReportPath))
if ($LASTEXITCODE -ne 0) { throw "Premarket UI assertions failed ($LASTEXITCODE). See $ReportPath." }

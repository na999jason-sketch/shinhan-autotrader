param([string]$ReportPath = (Join-Path $PSScriptRoot '..\..\reports\native-com-tests.txt'))
$ErrorActionPreference = 'Stop'
$fixtureProject = Join-Path $PSScriptRoot 'NativeComFixture.vcxproj'
$runnerProject = Join-Path $PSScriptRoot 'NativeComTests.csproj'
msbuild $fixtureProject /t:Rebuild /p:Configuration=Release /p:Platform=Win32 /warnaserror
if ($LASTEXITCODE -ne 0) { throw "Native fixture build failed ($LASTEXITCODE)." }
msbuild $runnerProject /t:Rebuild /p:Configuration=Release /p:Platform=x86 /warnaserror
if ($LASTEXITCODE -ne 0) { throw "Native COM runner build failed ($LASTEXITCODE)." }
$runner = Join-Path $PSScriptRoot 'bin\Release\NativeComTests.exe'
& $runner ([System.IO.Path]::GetFullPath($ReportPath))
if ($LASTEXITCODE -ne 0) { throw "Native COM assertions failed ($LASTEXITCODE)." }

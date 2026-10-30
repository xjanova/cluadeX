# Builds CluadeX and this harness, then runs it. The harness links the built CluadeX.dll, so a stale one tests old code.
$root = Resolve-Path "$PSScriptRoot\..\.."
dotnet build "$root\CluadeX\CluadeX.csproj" -c Debug --nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet build "$PSScriptRoot\chrometest.csproj" --nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& "$PSScriptRoot\bin\Debug\net8.0-windows\chrometest.exe"
exit $LASTEXITCODE

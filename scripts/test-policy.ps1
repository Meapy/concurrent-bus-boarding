$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'artifacts\checks'
New-Item -ItemType Directory -Force $output | Out-Null
$compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $compiler)) {
    throw 'The .NET Framework C# compiler was not found.'
}
& $compiler /nologo /out:"$output\BoardingPolicyCheck.exe" `
    "$root\ConcurrentBusBoarding\BoardingPolicy.cs" `
    "$root\tests\BoardingPolicyCheck.cs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& "$output\BoardingPolicyCheck.exe"
exit $LASTEXITCODE

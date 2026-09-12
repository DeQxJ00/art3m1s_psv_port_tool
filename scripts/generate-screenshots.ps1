param([string]$Output = (Join-Path $PSScriptRoot "..\docs\screenshots"))
$ErrorActionPreference = "Stop"
dotnet run --project (Join-Path $PSScriptRoot "..\tools\Art3m1s.PsvTool.Screenshots\Art3m1s.PsvTool.Screenshots.csproj") -c Release -- $Output

param([string]$Compiler = 'gcc')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$output = Join-Path $projectRoot 'src\v0.4\LightView.App\runtimes\win-x64\native\lcms2.dll'
New-Item -ItemType Directory -Path (Split-Path $output -Parent) -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.c' -File | ForEach-Object FullName)
if ($sources.Count -lt 20) { throw 'Little CMS source set is incomplete.' }
& $Compiler -O2 -std=c99 -shared -DCMS_DLL -DCMS_BUILD -static-libgcc '-I' (Join-Path $PSScriptRoot 'include') @sources '-o' $output
if ($LASTEXITCODE -ne 0) { throw 'Little CMS compilation failed.' }
[ordered]@{compiler=(& $Compiler --version | Select-Object -First 1);binary=$output;sha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant();bytes=(Get-Item -LiteralPath $output).Length;builtAt=[DateTimeOffset]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'build-receipt.json') -Encoding utf8
Write-Output ('Built project-local Little CMS: ' + $output)

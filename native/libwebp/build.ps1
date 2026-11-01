param([string]$Compiler='gcc')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$sources=@(Get-ChildItem (Join-Path $PSScriptRoot 'src/dec') -Filter '*.c' -File | ForEach-Object FullName)
foreach($group in @('dsp','utils')){
  $make=Get-Content (Join-Path $PSScriptRoot "src/$group/Makefile.am")
  $names=@($make | Where-Object { $_ -match '^(COMMON_SOURCES|libwebpdspdecode_\w+_la_SOURCES) \+=' } | ForEach-Object { [regex]::Matches($_,'[\w]+\.c') | ForEach-Object Value } | Sort-Object -Unique)
  $sources+=@($names | ForEach-Object { Join-Path $PSScriptRoot "src/$group/$_" })
}
$sources+=@('anim_decode.c','demux.c') | ForEach-Object { Join-Path $PSScriptRoot "src/demux/$_" }
$output=Join-Path $projectRoot 'src/v0.4/LightView.App/runtimes/win-x64/native/ivwebp.dll'
New-Item -ItemType Directory -Path (Split-Path $output -Parent) -Force | Out-Null
& $Compiler -O2 -std=c99 -shared -DWEBP_DLL -static-libgcc '-I' $PSScriptRoot @sources '-o' $output
if($LASTEXITCODE -ne 0){throw 'WebP decoder compilation failed'}
& objdump -p $output | Select-String 'DLL Name|WebPGetDecoderVersion' | Set-Content (Join-Path $PSScriptRoot 'binary-dependencies.txt')
[ordered]@{version='1.6.0';compiler=(& $Compiler --version | Select-Object -First 1);sha256=(Get-FileHash $output).Hash.ToLowerInvariant();sources=$sources.Count;bytes=(Get-Item $output).Length;builtAt=[DateTimeOffset]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content (Join-Path $PSScriptRoot 'build-receipt.json')

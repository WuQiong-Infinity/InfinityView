param([ValidateSet('Debug','Release')][string]$Configuration='Release', [switch]$IncludeTests)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$project=if($IncludeTests){'tests/v0.4/LightView.Tests.csproj'}else{'src/v0.4/LightView.App/LightView.App.csproj'}
Push-Location $root
try{
  & dotnet restore $project --locked-mode --configfile (Join-Path $root 'NuGet.config')
  if($LASTEXITCODE -ne 0){throw 'Locked dependency restore failed.'}
  & dotnet build $project -c $Configuration --no-restore
  if($LASTEXITCODE -ne 0){throw 'Source build failed.'}
  Write-Output (Join-Path $root "src/v0.4/LightView.App/bin/InfinityView/$Configuration/net48/InfinityView.exe")
}finally{Pop-Location}

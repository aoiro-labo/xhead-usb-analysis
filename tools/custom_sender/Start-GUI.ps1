param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $projectDir 'XHeadSender.csproj'

& dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "XHeadSenderのビルドに失敗しました: exit=$LASTEXITCODE"
}

$executable = Join-Path $projectDir "bin\$Configuration\net472\XHeadSender.exe"
Write-Host "起動: $executable"
Start-Process -FilePath $executable -ArgumentList '--gui' -WorkingDirectory $projectDir

param(
    [string]$DotnetPath = 'dotnet',
    [switch]$Standalone
)

$ErrorActionPreference = 'Stop'
$projectFile = Join-Path $PSScriptRoot 'WpeRemix.csproj'
$publishDirectory = Join-Path $PSScriptRoot 'release\v1.4-win-x64'
$publishArguments = @('publish', $projectFile, '-c', 'Release', '-r', 'win-x64', '-o', $publishDirectory, '--nologo')
if ($Standalone) {
    $publishArguments += @('--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')
} else {
    $publishArguments += @('--self-contained', 'false')
}
& $DotnetPath @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw 'WpeRemix 发布失败，请检查 .NET 10 SDK 和构建输出'
}
Write-Output (Join-Path $publishDirectory 'WpeRemix.exe')

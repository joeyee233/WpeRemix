param(
    [string]$DotnetPath = 'dotnet',
    [switch]$Standalone
)

$ErrorActionPreference = 'Stop'
$projectFile = Join-Path $PSScriptRoot 'WpeRemix.csproj'
[xml]$project = Get-Content -LiteralPath $projectFile
$version = [string]$project.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw '项目未设置版本号'
}
$publishDirectory = Join-Path $PSScriptRoot "release\v$version-win-x64"
$publishArguments = @('publish', $projectFile, '-c', 'Release', '-r', 'win-x64', '-o', $publishDirectory, '--nologo', '-p:DebugType=None', '-p:DebugSymbols=false')
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

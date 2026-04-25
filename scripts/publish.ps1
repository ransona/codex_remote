param(
    [ValidateSet("win-x64", "win-arm64")]
    [string[]]$Runtime = @("win-x64"),

    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [switch]$Zip
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$publishRoot = Join-Path $projectRoot "dist"

New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null

foreach ($rid in $Runtime) {
    $outputDir = Join-Path $publishRoot $rid

    dotnet publish `
        (Join-Path $projectRoot "CodexRemote.csproj") `
        -c $Configuration `
        -r $rid `
        --self-contained true `
        /p:PublishSingleFile=true `
        /p:IncludeNativeLibrariesForSelfExtract=true `
        /p:EnableCompressionInSingleFile=true `
        -o $outputDir

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for runtime $rid."
    }

    if ($Zip) {
        $zipPath = Join-Path $publishRoot ("codex_remote-" + $rid + ".zip")
        if (Test-Path $zipPath) {
            Remove-Item $zipPath -Force
        }

        Compress-Archive -Path (Join-Path $outputDir "*") -DestinationPath $zipPath
    }
}

Write-Host ""
Write-Host "Published builds:"
foreach ($rid in $Runtime) {
    Write-Host (" - " + (Join-Path $publishRoot $rid))
}

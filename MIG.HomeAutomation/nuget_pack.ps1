$ErrorActionPreference = 'Stop'
$tag = $env:APPVEYOR_REPO_TAG_NAME
if (-not $tag) {
    Write-Host 'Skipping package creation on an untagged build.'
    return
}

$project = Join-Path $PSScriptRoot 'MIG.HomeAutomation.csproj'
$version = ([xml](Get-Content -Raw -LiteralPath $project)).Project.PropertyGroup.Version
if ($tag -cne $version) {
    throw "Release tag '$tag' must match the project version '$version'."
}

dotnet pack $project -c Release --no-build -o .
if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed with exit code $LASTEXITCODE." }

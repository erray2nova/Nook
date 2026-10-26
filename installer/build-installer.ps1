$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot "FastPush.csproj"
$issPath = Join-Path $PSScriptRoot "Nook.iss"
$publishDirectory = Join-Path $repositoryRoot "artifacts\installer\publish"
$outputDirectory = Join-Path $repositoryRoot "artifacts\installer\output"

$compiler = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if ($null -ne $compiler) {
    $compilerPath = $compiler.Source
}
else {
    $programFilesRoots = @(
        ${env:ProgramFiles(x86)},
        $env:ProgramFiles,
        $env:ProgramW6432
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

    $knownCompilerPaths = @(
        foreach ($root in $programFilesRoots) {
            (Join-Path $root "Inno Setup 7\ISCC.exe")
            (Join-Path $root "Inno Setup 6\ISCC.exe")
        }
    )
    $compilerPath = $knownCompilerPaths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($compilerPath)) {
    throw "Inno Setup 7 or 6 was not found. Install Inno Setup and run this script again."
}

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Project file was not found: $projectPath"
}

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

Write-Host "Publishing Nook for Windows x64..."
& dotnet publish $projectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDirectory `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Write-Host "Building the Inno Setup installer..."
& $compilerPath `
    "/DPublishDir=$publishDirectory" `
    "/DOutputDir=$outputDirectory" `
    $issPath

if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

Remove-Item -LiteralPath $publishDirectory -Recurse -Force

Write-Host "Installer created: $outputDirectory\Nook-Setup-v1.0-x64.exe"

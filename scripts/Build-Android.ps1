[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$AndroidSdkDirectory,
    [string]$JavaSdkDirectory,
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'apps/Nexora.Mobile/Nexora.Mobile.csproj'
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "Projeto Android não encontrado: $project"
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Instale o .NET SDK indicado em global.json antes de compilar.'
}

function Test-AndroidSdk([string]$Directory) {
    return $Directory -and
        (Test-Path -LiteralPath (Join-Path $Directory 'platforms/android-36/android.jar') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $Directory 'build-tools/36.0.0/aapt2.exe') -PathType Leaf)
}

function Test-JavaSdk([string]$Directory) {
    if (-not $Directory -or
        -not (Test-Path -LiteralPath (Join-Path $Directory 'bin/javac.exe') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $Directory 'release') -PathType Leaf)) {
        return $false
    }

    return [bool](Get-Content -LiteralPath (Join-Path $Directory 'release') |
        Where-Object { $_ -match '^JAVA_VERSION="21(?:\.|\")' })
}

if (-not $AndroidSdkDirectory) {
    $androidCandidates = @(
        $env:ANDROID_HOME,
        $env:ANDROID_SDK_ROOT,
        (Join-Path ${env:ProgramFiles(x86)} 'Android/android-sdk'),
        (Join-Path $env:LOCALAPPDATA 'Android/Sdk')
    )
    $AndroidSdkDirectory = $androidCandidates |
        Where-Object { Test-AndroidSdk $_ } |
        Select-Object -First 1
}
if (-not (Test-AndroidSdk $AndroidSdkDirectory)) {
    throw 'SDK Android 36 e build-tools 36.0.0 não encontrados. Informe -AndroidSdkDirectory com uma instalação existente.'
}

if (-not $JavaSdkDirectory) {
    $javaCandidates = @($env:JAVA_HOME, $env:JDK_HOME)
    $javaRoots = @(
        (Join-Path $env:ProgramFiles 'Android/openjdk'),
        (Join-Path $env:ProgramFiles 'Microsoft'),
        (Join-Path $env:ProgramFiles 'Eclipse Adoptium')
    )
    foreach ($javaRoot in $javaRoots) {
        if (Test-Path -LiteralPath $javaRoot -PathType Container) {
            $javaCandidates += Get-ChildItem -LiteralPath $javaRoot -Directory |
                Where-Object { $_.Name -like '*jdk*' } |
                Sort-Object Name -Descending |
                Select-Object -ExpandProperty FullName
        }
    }
    $JavaSdkDirectory = $javaCandidates |
        Where-Object { Test-JavaSdk $_ } |
        Select-Object -First 1
}
if (-not (Test-JavaSdk $JavaSdkDirectory)) {
    throw 'JDK 21 não encontrado. Informe -JavaSdkDirectory com uma instalação existente.'
}

$AndroidSdkDirectory = (Resolve-Path -LiteralPath $AndroidSdkDirectory).Path
$JavaSdkDirectory = (Resolve-Path -LiteralPath $JavaSdkDirectory).Path
Write-Host "Android SDK: $AndroidSdkDirectory"
Write-Host "JDK 21: $JavaSdkDirectory"

$buildArguments = @(
    'build', $project,
    '-f', 'net10.0-android',
    '-c', $Configuration,
    '-p:AndroidPackageFormats=apk',
    '-p:EmbedAssembliesIntoApk=true',
    "-p:AndroidSdkDirectory=$AndroidSdkDirectory",
    "-p:JavaSdkDirectory=$JavaSdkDirectory",
    '--nologo'
)
if ($NoRestore) {
    $buildArguments += '--no-restore'
}

Push-Location -LiteralPath $repositoryRoot
try {
    & dotnet @buildArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Build Android falhou (código $LASTEXITCODE)."
    }
}
finally {
    Pop-Location
}

$outputDirectory = Join-Path (Split-Path -Parent $project) "bin/$Configuration/net10.0-android"
$packages = @(Get-ChildItem -LiteralPath $outputDirectory -Filter '*-Signed.apk' -File -Recurse)
if ($packages.Count -eq 0) {
    throw "Build terminou sem APK assinado em $outputDirectory."
}
foreach ($package in $packages) {
    Write-Host "APK: $($package.FullName)"
}

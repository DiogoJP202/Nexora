#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string]$ReleaseId,
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = Join-Path $repository 'artifacts/arch'
$releasePath = Join-Path $outputRoot $ReleaseId
$utf8 = [Text.UTF8Encoding]::new($false)

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command falhou (código $LASTEXITCODE). Os arquivos parciais foram preservados." }
}

function Assert-NoLinks([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -Force -LiteralPath $current).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Caminho com link/junction recusado: $current"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function Write-Utf8([string]$Path, [string]$Content) {
    [IO.File]::WriteAllText($Path, $Content.Replace("`r`n", "`n"), $utf8)
}

function Assert-LinuxExecutable([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $header = [byte[]]::new(20)
        if ($stream.Read($header, 0, $header.Length) -ne $header.Length -or
            $header[0] -ne 0x7f -or $header[1] -ne 0x45 -or $header[2] -ne 0x4c -or $header[3] -ne 0x46 -or
            $header[4] -ne 2 -or $header[5] -ne 1 -or $header[18] -ne 0x3e -or $header[19] -ne 0) {
            throw "O artefato não é ELF Linux x64: $Path"
        }
    } finally { $stream.Dispose() }
}

function Read-BundleRuntimeConfig([string]$Path) {
    # Formato do bundle .NET: dotnet/runtime, Bundle/Manifest.cs e Bundle/Bundler.cs.
    $stream = [IO.File]::OpenRead($Path)
    try {
        [long]$offset = 0
        if (-not [Microsoft.NET.HostModel.Bundle.Bundler]::IsBundle($Path, [ref]$offset)) { throw 'Assinatura de bundle .NET ausente.' }
        if ($offset -le 0 -or $offset -ge $stream.Length - 52) { throw 'Offset de bundle inválido.' }
        $stream.Position = $offset
        $reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::UTF8, $true)
        if ($reader.ReadUInt32() -ne 6 -or $reader.ReadUInt32() -ne 0) { throw 'Formato de bundle não suportado.' }
        $null = $reader.ReadInt32(); $null = $reader.ReadString()
        $null = $reader.ReadInt64(); $null = $reader.ReadInt64()
        $configOffset = $reader.ReadInt64(); $configSize = $reader.ReadInt64()
        if ($configSize -le 0 -or $configSize -gt 16384 -or $configOffset -le 0 -or $configOffset -gt $stream.Length - $configSize) {
            throw 'Configuração embutida do bundle inválida.'
        }
        $stream.Position = $configOffset
        return [Text.Encoding]::UTF8.GetString($reader.ReadBytes([int]$configSize))
    } finally { $stream.Dispose() }
}

function Assert-RuntimeVersion($Config) {
    if (-not $Config.runtimeOptions.PSObject.Properties['includedFrameworks']) { throw 'O artefato deve incluir o runtime.' }
    $frameworks = @($Config.runtimeOptions.includedFrameworks)
    if ($frameworks.Count -lt 1) { throw 'Runtime incluído ausente.' }
    foreach ($framework in $frameworks) {
        if ($framework.version -ne '10.0.12') { throw "Runtime inesperado no artefato: $($framework.version)" }
    }
}

if ($ReleaseId -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)' -or $ReleaseId.EndsWith('.')) {
    throw 'ReleaseId reservado pelo filesystem Windows.'
}
Assert-NoLinks $releasePath
if (Test-Path -LiteralPath $releasePath) { throw "A release já existe: $releasePath. Use outro ReleaseId." }

Push-Location $repository
$previousRuntime = [Environment]::GetEnvironmentVariable('RuntimeFrameworkVersion', 'Process')
$previousAspNetEnvironment = [Environment]::GetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Process')
$previousDotNetEnvironment = [Environment]::GetEnvironmentVariable('DOTNET_ENVIRONMENT', 'Process')
try {
    $sourceCommit = (& git rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') { throw 'Não foi possível identificar o commit.' }
    $initialStatus = (& git status --porcelain=v1 --untracked-files=all | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao verificar o Git.' }
    $sourceDirty = -not [string]::IsNullOrWhiteSpace($initialStatus)
    if ($sourceDirty -and -not $AllowDirty) { throw 'O Git contém alterações. Faça commit antes de publicar; -AllowDirty serve somente para verificação local.' }

    # EF precisa construir o modelo, mas geração de bundle/SQL não acessa o banco.
    # Development utiliza a configuração local; nenhuma credencial é copiada para o pacote.
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:DOTNET_ENVIRONMENT = 'Development'
    [Environment]::SetEnvironmentVariable('RuntimeFrameworkVersion', $null, 'Process')
    $sdkVersion = (& dotnet --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.0\.4\d\d$') { throw 'Use o SDK selecionado por global.json, da feature band 10.0.4xx.' }
    $sdkInventory = @(& dotnet --list-sdks)
    if ($LASTEXITCODE -ne 0) { throw 'Não foi possível localizar o SDK.' }
    $sdkRoot = $null
    foreach ($sdkLine in $sdkInventory) {
        if ($sdkLine -match ('^' + [regex]::Escape($sdkVersion) + ' \[(.+)\]$')) { $sdkRoot = $Matches[1]; break }
    }
    if (-not $sdkRoot) { throw 'O SDK selecionado não está no inventário local.' }
    $null = [Reflection.Assembly]::LoadFrom((Join-Path $sdkRoot "$sdkVersion/Microsoft.NET.HostModel.dll"))
    Invoke-Checked dotnet @('tool', 'restore')
    Invoke-Checked dotnet @('restore', 'Nexora.sln', '--locked-mode')
    Invoke-Checked dotnet @('build', 'src/Nexora.Api/Nexora.Api.csproj', '-c', 'Release', '--no-restore')

    [IO.Directory]::CreateDirectory($releasePath) | Out-Null
    foreach ($project in @('Api', 'Worker')) {
        $destination = Join-Path $releasePath $project.ToLowerInvariant()
        Invoke-Checked dotnet @('publish', "src/Nexora.$project/Nexora.$project.csproj", '-c', 'Release', '-r', 'linux-x64', '--self-contained', 'true',
            '-p:RuntimeFrameworkVersion=10.0.12', '-p:RestoreLockedMode=true', '-o', $destination)
        Assert-LinuxExecutable (Join-Path $destination "Nexora.$project")
        Assert-LinuxExecutable (Join-Path $destination 'libcoreclr.so')
        Assert-LinuxExecutable (Join-Path $destination 'libSkiaSharp.so')
        $runtime = Get-Content -Raw (Join-Path $destination "Nexora.$project.runtimeconfig.json") | ConvertFrom-Json
        Assert-RuntimeVersion $runtime
        foreach ($publishedFile in Get-ChildItem -LiteralPath $destination -File -Recurse -Force) {
            if ($publishedFile.Extension -in @('.pfx', '.p12', '.key', '.env', '.config') -or $publishedFile.Name -eq 'secrets.json' -or
                ($publishedFile.Extension -eq '.json' -and $publishedFile.Name -notin @('appsettings.json', 'appsettings.Production.json', "Nexora.$project.staticwebassets.endpoints.json") -and $publishedFile.Name -notmatch '\.(deps|runtimeconfig)\.json$')) {
                throw 'Conteúdo privado ou configuração adicional encontrado na publicação. Revise os itens de publicação do projeto.'
            }
        }
        # Configuração e segredos são externos; configuração Development não integra releases.
        $developmentSettings = Join-Path $destination 'appsettings.Development.json'
        if (Test-Path -LiteralPath $developmentSettings) { Remove-Item -LiteralPath $developmentSettings }
    }

    $migrationsPath = Join-Path $releasePath 'migrations'
    [IO.Directory]::CreateDirectory($migrationsPath) | Out-Null
    $efProject = @('--project', 'src/Nexora.Infrastructure', '--startup-project', 'src/Nexora.Api', '--configuration', 'Release', '--no-build')
    # O projeto temporário criado pelo EF está fora do repositório; repassar a versão do runtime por MSBuild.
    $env:RuntimeFrameworkVersion = '10.0.12'
    Invoke-Checked dotnet (@('ef', 'migrations', 'bundle', '--self-contained', '--target-runtime', 'linux-x64', '--output', (Join-Path $migrationsPath 'efbundle')) + $efProject)
    Assert-LinuxExecutable (Join-Path $migrationsPath 'efbundle')
    $bundleRuntime = Read-BundleRuntimeConfig (Join-Path $migrationsPath 'efbundle')
    Assert-RuntimeVersion ($bundleRuntime | ConvertFrom-Json)
    Write-Utf8 (Join-Path $migrationsPath 'embedded-runtimeconfig.json') ($bundleRuntime + "`n")
    Invoke-Checked dotnet (@('ef', 'migrations', 'script', '--idempotent', '--output', (Join-Path $migrationsPath 'migrations.sql')) + $efProject)
    Copy-Item -LiteralPath 'src/Nexora.Api/appsettings.json' -Destination (Join-Path $migrationsPath 'appsettings.json')

    $opsPath = Join-Path $releasePath 'ops'
    [IO.Directory]::CreateDirectory($opsPath) | Out-Null
    $sourceOps = Join-Path $repository 'ops/arch'
    Assert-NoLinks $sourceOps
    foreach ($entry in Get-ChildItem -LiteralPath $sourceOps -Recurse -Force) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Links em ops/arch são recusados.' }
        $relative = [IO.Path]::GetRelativePath($sourceOps, $entry.FullName).Replace('\', '/')
        if ($entry.PSIsContainer) {
            if ($relative -notin @('systemd', 'config')) { throw "Diretório inesperado em ops/arch: $relative" }
        } elseif ($relative -notmatch '^(?:[a-z-]+\.sh|systemd/nexora-(?:api|worker)\.service|config/[a-z-]+\.env\.example|config/provision-database\.sql)$') {
            throw "Arquivo inesperado em ops/arch: $relative. Credenciais locais não devem ficar nessa pasta."
        }
    }
    Copy-Item -LiteralPath $sourceOps -Destination $opsPath -Recurse
    # Git no Windows pode materializar scripts com CRLF. SHA será calculado após normalização.
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $opsPath 'arch') -File -Recurse -Force) {
        if ($file.Extension -in @('.sh', '.service', '.example', '.sql')) {
            Write-Utf8 $file.FullName ([IO.File]::ReadAllText($file.FullName))
        }
    }
    $lastMigration = Get-ChildItem -LiteralPath 'src/Nexora.Infrastructure/Persistence/Migrations' -Filter '*.Designer.cs' |
        ForEach-Object { [regex]::Match([IO.File]::ReadAllText($_.FullName), '\[Migration\("([^"]+)"\)\]').Groups[1].Value } |
        Sort-Object | Select-Object -Last 1
    if (-not $lastMigration) { throw 'Nenhuma migration identificada.' }
    Write-Utf8 (Join-Path $releasePath 'release-id.txt') "$ReleaseId`n"
    $metadata = [ordered]@{
        schemaVersion = 1; releaseId = $ReleaseId; sourceCommit = $sourceCommit; sourceDirty = $sourceDirty
        runtimeIdentifier = 'linux-x64'; sdkVersion = $sdkVersion; runtimeFrameworkVersion = '10.0.12'
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); lastMigration = $lastMigration
    }
    Write-Utf8 (Join-Path $releasePath 'release.json') (($metadata | ConvertTo-Json) + "`n")

    $manifest = [Collections.Generic.List[string]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $releasePath -File -Recurse -Force | Sort-Object FullName -CaseSensitive) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'A release contém link.' }
        $relative = [IO.Path]::GetRelativePath($releasePath, $file.FullName).Replace('\', '/')
        if ($relative -notmatch '^[A-Za-z0-9][A-Za-z0-9._/-]*$' -or ($relative -split '/') -contains '..') { throw "Nome de artefato recusado: $relative" }
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $manifest.Add("$hash  $relative")
    }
    Write-Utf8 (Join-Path $releasePath 'SHA256SUMS') (($manifest -join "`n") + "`n")
    $finalStatus = (& git status --porcelain=v1 --untracked-files=all | Out-String).Trim()
    $finalCommit = (& git rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $sourceCommit -ne $finalCommit -or $initialStatus -ne $finalStatus) {
        throw 'O código mudou durante a publicação. A release parcial não deve ser instalada.'
    }
    $manifestHash = (Get-FileHash -LiteralPath (Join-Path $releasePath 'SHA256SUMS') -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "Release: $releasePath"
    Write-Host "SHA-256 do manifesto (guardar fora do pacote): $manifestHash"
    if ($sourceDirty) { Write-Warning 'Release de verificação com sourceDirty=true. Não use em produção.' }
} finally {
    [Environment]::SetEnvironmentVariable('RuntimeFrameworkVersion', $previousRuntime, 'Process')
    [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', $previousAspNetEnvironment, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ENVIRONMENT', $previousDotNetEnvironment, 'Process')
    Pop-Location
}

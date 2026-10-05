[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Este script prepara somente o PostgreSQL de desenvolvimento no Windows (PowerShell 7).' }

$nexoraPgRoot = Join-Path $env:LOCALAPPDATA 'Nexora\postgresql\18'
$nexoraPgBin = Join-Path $nexoraPgRoot 'pgsql\bin'
$nexoraPgData = Join-Path $nexoraPgRoot 'data'
$nexoraPgPassword = Join-Path $nexoraPgRoot 'admin.pw'
$nexoraPgPort = 55432

function Assert-NativeSuccess([string] $operation) {
    if ($LASTEXITCODE -ne 0) { throw "Falha em $operation (exit code $LASTEXITCODE)." }
}

function New-LocalPassword {
    return [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
}

function Set-LocalUserSecrets([string] $id, [hashtable] $values) {
    $nexoraSecretsDir = Join-Path $env:APPDATA "Microsoft\UserSecrets\$id"
    New-Item -ItemType Directory -Path $nexoraSecretsDir -Force | Out-Null
    $nexoraSecretsFile = Join-Path $nexoraSecretsDir 'secrets.json'
    $nexoraExistingSecrets = @{}
    if (Test-Path -LiteralPath $nexoraSecretsFile) {
        try {
            $nexoraExistingSecrets = Get-Content -LiteralPath $nexoraSecretsFile -Raw | ConvertFrom-Json -AsHashtable
            if ($nexoraExistingSecrets -isnot [System.Collections.IDictionary]) { throw 'Invalid JSON object.' }
        }
        catch {
            throw 'User Secrets existentes não puderam ser lidos. Nenhuma configuração foi substituída.'
        }
    }
    foreach ($nexoraSecretKey in $values.Keys) {
        $nexoraExistingSecrets[$nexoraSecretKey] = $values[$nexoraSecretKey]
    }
    $nexoraTemporarySecrets = Join-Path $nexoraSecretsDir ('secrets.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $nexoraExistingSecrets | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $nexoraTemporarySecrets -Encoding utf8
        [IO.File]::Move($nexoraTemporarySecrets, $nexoraSecretsFile, $true)
    }
    finally {
        if (Test-Path -LiteralPath $nexoraTemporarySecrets) {
            Remove-Item -LiteralPath $nexoraTemporarySecrets
        }
    }
}

function Assert-LocalUserSecrets([string] $id, [string[]] $keys) {
    $nexoraSecretsFile = Join-Path $env:APPDATA "Microsoft\UserSecrets\$id\secrets.json"
    if (-not (Test-Path -LiteralPath $nexoraSecretsFile)) {
        throw 'User Secrets ausentes. Configure as credenciais existentes manualmente; o setup não redefine senhas.'
    }
    try {
        $nexoraExistingSecrets = Get-Content -LiteralPath $nexoraSecretsFile -Raw | ConvertFrom-Json -AsHashtable
        if ($nexoraExistingSecrets -isnot [System.Collections.IDictionary]) { throw 'Invalid JSON object.' }
    }
    catch {
        throw 'User Secrets não puderam ser lidos. Verifique o arquivo local sem compartilhar seu conteúdo.'
    }
    foreach ($nexoraSecretKey in $keys) {
        if ([string]::IsNullOrWhiteSpace($nexoraExistingSecrets[$nexoraSecretKey])) {
            throw 'Configuração local incompleta. Registre as credenciais existentes em User Secrets conforme docs/development.md.'
        }
    }
}

New-Item -ItemType Directory -Path $nexoraPgRoot -Force | Out-Null
# The cluster and bootstrap credential belong only to this Windows user and SYSTEM.
$nexoraIdentity = [Security.Principal.WindowsIdentity]::GetCurrent().User
$nexoraDirectory = [IO.DirectoryInfo]::new($nexoraPgRoot)
$nexoraAcl = [IO.FileSystemAclExtensions]::GetAccessControl($nexoraDirectory, [Security.AccessControl.AccessControlSections]::Access)
$nexoraAcl.SetAccessRuleProtection($true, $false)
foreach ($nexoraExistingRule in $nexoraAcl.GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier])) {
    $nexoraAcl.RemoveAccessRuleSpecific($nexoraExistingRule)
}
foreach ($nexoraPrincipal in @($nexoraIdentity, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
    $nexoraRule = [Security.AccessControl.FileSystemAccessRule]::new(
        $nexoraPrincipal, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $nexoraAcl.AddAccessRule($nexoraRule)
}
[IO.FileSystemAclExtensions]::SetAccessControl($nexoraDirectory, $nexoraAcl)

if (-not (Test-Path -LiteralPath (Join-Path $nexoraPgBin 'postgres.exe'))) {
    $nexoraPgArchive = Join-Path $nexoraPgRoot 'postgresql-18.6-5-windows-x64-binaries.zip'
    Write-Host 'Baixando binários nativos PostgreSQL 18.6 do distribuidor EDB...'
    Invoke-WebRequest -Uri 'https://get.enterprisedb.com/postgresql/postgresql-18.6-5-windows-x64-binaries.zip' -OutFile $nexoraPgArchive
    & tar.exe -xf $nexoraPgArchive -C $nexoraPgRoot pgsql/bin pgsql/lib pgsql/share
    Assert-NativeSuccess 'extração do PostgreSQL'
}

if (-not (Test-Path -LiteralPath (Join-Path $nexoraPgData 'PG_VERSION'))) {
    if (Test-Path -LiteralPath $nexoraPgData) {
        throw 'Diretório data existente sem PG_VERSION. Verifique-o manualmente antes de inicializar.'
    }
    New-LocalPassword | Set-Content -LiteralPath $nexoraPgPassword -NoNewline -Encoding utf8
    & (Join-Path $nexoraPgBin 'initdb.exe') -D $nexoraPgData -U nexora_bootstrap `
        --pwfile=$nexoraPgPassword --auth-host=scram-sha-256 --auth-local=scram-sha-256 --encoding=UTF8 --locale=C
    Assert-NativeSuccess 'inicialização do cluster'
    @"
listen_addresses = '127.0.0.1'
port = $nexoraPgPort
timezone = 'UTC'
log_timezone = 'UTC'
log_min_error_statement = 'PANIC'
log_parameter_max_length_on_error = 0
"@ | Add-Content -LiteralPath (Join-Path $nexoraPgData 'postgresql.conf') -Encoding utf8
}

if (-not (Test-Path -LiteralPath $nexoraPgPassword)) {
    throw 'Credencial bootstrap ausente. O script não altera um cluster cuja credencial não conhece.'
}
$nexoraBootstrapPassword = Get-Content -LiteralPath $nexoraPgPassword -Raw
if ([string]::IsNullOrWhiteSpace($nexoraBootstrapPassword)) { throw 'Credencial bootstrap vazia. Nenhum cluster foi iniciado pelo setup.' }

& (Join-Path $nexoraPgBin 'pg_ctl.exe') status -D $nexoraPgData 2>$null | Out-Null
$nexoraPgStatus = $LASTEXITCODE
if ($nexoraPgStatus -notin @(0, 3)) { throw "Não foi possível verificar o cluster local (exit code $nexoraPgStatus)." }
if ($nexoraPgStatus -eq 3) {
    & (Join-Path $nexoraPgBin 'pg_ctl.exe') start -D $nexoraPgData -l (Join-Path $nexoraPgRoot 'postgresql.log') -w
    Assert-NativeSuccess 'início do PostgreSQL'
}

$nexoraOriginalPgPassword = $env:PGPASSWORD
try {
    $env:PGPASSWORD = $nexoraBootstrapPassword
    $nexoraPsqlArguments = @('-h', '127.0.0.1', '-p', "$nexoraPgPort", '-U', 'nexora_bootstrap', '-d', 'postgres', '-v', 'ON_ERROR_STOP=1', '-X', '-q', '-w')
    # Refuse to overwrite existing role credentials or touch an existing development database.
    $nexoraRoleExists = & (Join-Path $nexoraPgBin 'psql.exe') @nexoraPsqlArguments -tAc "SELECT count(*) FROM pg_roles WHERE rolname IN ('nexora_dev','nexora_test');"
    Assert-NativeSuccess 'verificação das contas locais'
    if ([int]$nexoraRoleExists -eq 2) {
        $nexoraExistingDatabases = & (Join-Path $nexoraPgBin 'psql.exe') @nexoraPsqlArguments -tAc "SELECT count(*) FROM pg_database d JOIN pg_roles r ON r.oid = d.datdba WHERE (d.datname = 'nexora_dev' AND r.rolname = 'nexora_dev') OR (d.datname = 'nexora_test' AND r.rolname = 'nexora_test');"
        Assert-NativeSuccess 'verificação dos bancos existentes'
        if ([int]$nexoraExistingDatabases -ne 2) { throw 'Bancos locais ausentes ou com proprietário inesperado; verifique o provisionamento parcial manualmente.' }
        Assert-LocalUserSecrets 'Nexora.Api.Development' @('ConnectionStrings:Nexora', 'Storage:RootPath')
        Assert-LocalUserSecrets 'Nexora.IntegrationTests.Development' @('ConnectionStrings:NexoraTests')
        Write-Host 'Contas locais já existem; credenciais e bancos foram preservados.'
        return
    }
    if ([int]$nexoraRoleExists -ne 0) { throw 'Provisionamento parcial encontrado; verifique-o manualmente. Nenhuma credencial foi alterada.' }
    $nexoraDatabaseExists = & (Join-Path $nexoraPgBin 'psql.exe') @nexoraPsqlArguments -tAc "SELECT count(*) FROM pg_database WHERE datname IN ('nexora_dev','nexora_test');"
    Assert-NativeSuccess 'verificação dos bancos locais'
    if ([int]$nexoraDatabaseExists -ne 0) { throw 'Bancos Nexora existentes sem contas esperadas; nenhuma alteração realizada.' }

    $nexoraDevPassword = New-LocalPassword
    $nexoraTestPassword = New-LocalPassword
    @"
CREATE ROLE nexora_dev LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD '$nexoraDevPassword';
CREATE ROLE nexora_test LOGIN NOSUPERUSER CREATEDB NOCREATEROLE NOREPLICATION PASSWORD '$nexoraTestPassword';
CREATE DATABASE nexora_dev OWNER nexora_dev;
CREATE DATABASE nexora_test OWNER nexora_test;
"@ | & (Join-Path $nexoraPgBin 'psql.exe') @nexoraPsqlArguments
    Assert-NativeSuccess 'provisionamento dos bancos locais'

    $nexoraDevConnection = "Host=127.0.0.1;Port=$nexoraPgPort;Database=nexora_dev;Username=nexora_dev;Password=$nexoraDevPassword;Timeout=5;Command Timeout=5"
    $nexoraTestConnection = "Host=127.0.0.1;Port=$nexoraPgPort;Database=nexora_test;Username=nexora_test;Password=$nexoraTestPassword;Timeout=5;Command Timeout=5"
    Set-LocalUserSecrets 'Nexora.Api.Development' @{
        'ConnectionStrings:Nexora' = $nexoraDevConnection
        'Storage:RootPath' = (Join-Path $env:LOCALAPPDATA 'Nexora\storage')
    }
    Set-LocalUserSecrets 'Nexora.IntegrationTests.Development' @{
        'ConnectionStrings:NexoraTests' = $nexoraTestConnection
    }
    Write-Host 'PostgreSQL local configurado em 127.0.0.1:55432. Credenciais gravadas somente em User Secrets.'
}
finally {
    $env:PGPASSWORD = $nexoraOriginalPgPassword
}

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Start', 'Stop', 'Status')]
    [string] $Action
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Este script controla somente o cluster Nexora local no Windows (PowerShell 7).' }
$nexoraPgRoot = Join-Path $env:LOCALAPPDATA 'Nexora\postgresql\18'
$nexoraPgCtl = Join-Path $nexoraPgRoot 'pgsql\bin\pg_ctl.exe'
$nexoraPgData = Join-Path $nexoraPgRoot 'data'
if (-not (Test-Path -LiteralPath (Join-Path $nexoraPgData 'PG_VERSION'))) {
    throw 'Inicialize primeiro com scripts/Initialize-LocalPostgres.ps1.'
}

& $nexoraPgCtl status -D $nexoraPgData 2>$null | Out-Null
$nexoraStatus = $LASTEXITCODE
if ($nexoraStatus -notin @(0, 3)) { throw "Não foi possível verificar o cluster local (exit code $nexoraStatus)." }
if ($Action -eq 'Status') {
    Write-Host $(if ($nexoraStatus -eq 0) { 'PostgreSQL Nexora em execução.' } else { 'PostgreSQL Nexora parado.' })
    return
}
if (($Action -eq 'Start' -and $nexoraStatus -eq 0) -or ($Action -eq 'Stop' -and $nexoraStatus -eq 3)) {
    Write-Host 'O cluster já está no estado solicitado.'
    return
}
switch ($Action) {
    'Start' { & $nexoraPgCtl start -D $nexoraPgData -l (Join-Path $nexoraPgRoot 'postgresql.log') -w }
    'Stop' { & $nexoraPgCtl stop -D $nexoraPgData -m fast -w }
}
if ($LASTEXITCODE -ne 0) { throw "pg_ctl retornou exit code $LASTEXITCODE." }

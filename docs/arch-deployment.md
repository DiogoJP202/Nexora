# Nexora no Arch Linux — implantação privada

Este roteiro prepara a operação da Fase 7 em **Arch Linux x86_64**, com PostgreSQL 18 local, API/Worker self-contained .NET 10.0.12 e HTTPS por Tailscale Serve. Os scripts são entregáveis operacionais: sua execução no Arch, o codec Linux e a recuperação real ainda precisam ser validados. Os 229 testes atuais foram executados no Windows; isso não comprova a aceitação do servidor Arch.

Os comandos deste documento são executados deliberadamente pelo operador no host identificado. Não há instalação remota, provisionamento de tailnet ou início automático de produção. Para o ensaio de recuperação, use [backup-and-restore.md](backup-and-restore.md). A Fase 7 disponibiliza a API; os clientes mobile e web permanecem em fases futuras.

## 1. Contrato de diretórios e identidade

| Caminho | Dono e modo | Uso |
| --- | --- | --- |
| `/opt/nexora/releases/<id>` | `root:root`, diretórios `0755`, arquivos `0644`, executáveis `0755` | Release imutável. |
| `/opt/nexora/current` | Symlink absoluto gerenciado por root | Release escolhida explicitamente pela ativação. |
| `/etc/nexora` | `root:nexora`, `0750` | Configuração externa à release. |
| `/etc/nexora/{common,api,worker,migrations}.env` | `root:root`, `0600` | Segredos lidos pelo systemd; o migrador recebe seu arquivo apenas no job explícito. |
| `/etc/nexora/data-protection.pfx` | `root:nexora`, `0640` | Certificado RSA com chave privada para Data Protection. |
| `/var/lib/nexora/keys` | `nexora:nexora`, `0700` | Keyring persistente da autenticação. |
| `/srv/nexora` e subdiretórios de conteúdo | `nexora:nexora`, `0700` | Originais, derivados e temporários. |

O instalador cria o usuário/grupo de sistema `nexora`, com senha bloqueada, shell `/usr/bin/nologin` e home `/var/lib/nexora`. Uma identidade existente incompatível interrompe a instalação para inspeção. Ele verifica os diretórios existentes, sem corrigir recursivamente ownership nem substituir dados. Os caminhos gerenciados devem ser diretórios reais; mounts dedicados são aceitos nesses diretórios, symlinks/junctions não.

API e Worker usam a mesma identidade de SO e o mesmo papel PostgreSQL de execução. O filho de imagens limpa seu ambiente e recebe bytes/parâmetros por pipes, mas conserva a identidade do Worker; essa separação **não é sandbox de SO**. O sandbox das units protege caminhos e privilégios, sem prometer isolamento entre processos do mesmo usuário.

## 2. Preflight do sistema e PostgreSQL

Inspecione o host, armazenamento e unidade PostgreSQL antes de alterar qualquer coisa:

```bash
uname -m
cat /etc/arch-release
df -h /srv /var/lib /opt
findmnt /srv
systemctl cat postgresql.service
pacman -Q postgresql tailscale
postgres --version
```

Confirme capacidade livre, mounts persistentes e **cgroup v2 com controlador memory**. Os defaults da aplicação mantêm 50 GiB livres, permitem reservas de até 50 GiB e arquivos de até 20 GiB; ajuste o planejamento ao disco real. API, Worker, PostgreSQL e SO precisam caber na RAM do servidor.

O Arch é rolling release. Planeje a manutenção completa, leia os avisos de atualização e faça backup antes de atualizar um host com dados. Não use `pacman -Sy` seguido de instalação seletiva, `IgnorePkg` permanente para PostgreSQL ou downgrade de major como estratégia de implantação. Em 8/10/2026, o pacote oficial `postgresql` de `extra` era `18.6-2`; a compatibilidade deste projeto é com **major 18**, não com qualquer versão futura do pacote. Se a atualização exigir outra major, interrompa a implantação e planeje o upgrade do cluster separadamente. [Manutenção Arch](https://wiki.archlinux.org/title/System_maintenance), [pacote PostgreSQL](https://archlinux.org/packages/extra/x86_64/postgresql/).

Após essa revisão, a instalação de dependências usa uma atualização completa:

```bash
sudo pacman -Syu --needed postgresql tailscale glibc icu krb5 libgcc libstdc++ libunwind openssl zlib python file jq
```

Self-contained dispensa a instalação de .NET no servidor, mas ainda exige bibliotecas do SO. Os nomes acima seguem as [dependências do pacote .NET no Arch](https://archlinux.org/packages/extra/x86_64/dotnet-runtime/). O pacote Skia usado pelo projeto inclui `libSkiaSharp.so` para Linux e sua variante `NoDependencies` depende somente das bibliotecas básicas C; confirme o carregamento e a decodificação reais na aceitação. [SkiaSharp](https://github.com/mono/SkiaSharp/blob/main/documentation/dev/packages.md).

### Cluster existente

Verifique o `PGDATA` efetivo da unit e, para o caminho padrão, `/var/lib/postgres/data/PG_VERSION`. Se existir conteúdo ou cluster, **não execute initdb, não apague diretórios e não recrie o banco**. Confira a major e conecte como administrador local:

```bash
sudo -u postgres psql -X --no-password --dbname=postgres --command='SHOW server_version_num;'
```

O resultado deve indicar major 18. Dados de outra major exigem upgrade/restauração planejados; um binário novo não converte o cluster automaticamente.

### Cluster novo, somente diretório vazio

Use apenas após confirmar o caminho padrão da unit, ausência de dados e serviço parado. O bloco recusa diretório não vazio ou link e nunca remove conteúdo:

```bash
sudo systemctl is-active postgresql.service
sudo -u postgres bash <<'BASH'
set -euo pipefail
data=/var/lib/postgres/data
[[ ! -L "$data" ]]
if [[ -e "$data" ]]; then
    [[ -d "$data" ]]
    [[ -z $(find "$data" -mindepth 1 -maxdepth 1 -print -quit) ]]
else
    install -d -m 0700 -- "$data"
fi
initdb --locale=C.UTF-8 --encoding=UTF8 --data-checksums \
    --auth-local=peer --auth-host=scram-sha-256 -D "$data"
BASH
```

Se `is-active` responder `active`, não execute o bloco. A [documentação do Arch](https://wiki.archlinux.org/title/PostgreSQL) detalha initdb e upgrades. Mantenha `listen_addresses='127.0.0.1'`, `port=5432` e `unix_socket_directories='/run/postgresql'` em `postgresql.conf`: preflight e backup usam esse socket explicitamente. Preserve administração local do usuário `postgres` por `peer` e autenticação `scram-sha-256` para `nexora`/`nexora_migrator` em `127.0.0.1/32` no `pg_hba.conf`. Revise a ordem das regras para não deixar uma regra ampla anterior liberar esses logins. PostgreSQL não deve ouvir na LAN/tailnet. Inicie-o explicitamente somente depois da revisão:

```bash
sudo systemctl enable --now postgresql.service
sudo -u postgres psql -X --no-password --dbname=postgres --command='SHOW server_version_num; SHOW listen_addresses; SHOW password_encryption;'
```

## 3. Gerar, transportar e instalar uma release

Na máquina de build, use o SDK da feature band `10.0.4xx` selecionado por `global.json`. Prepare um commit revisado e rode em PowerShell 7:

```powershell
./scripts/Publish-ArchRelease.ps1 -ReleaseId <id>
```

A saída fica em `artifacts/arch/<id>`: `api/`, `worker/`, `migrations/efbundle`, `migrations/migrations.sql`, configuração pública de migrations, `ops/arch/`, `release.json`, `release-id.txt` e `SHA256SUMS`. API/Worker incluem runtime **10.0.12**, apphosts ELF Linux x64 e Skia Linux. O script verifica os formatos e registra commit, estado do source, SDK e última migration. `-AllowDirty` existe para verificação local; uma release marcada `sourceDirty=true` não deve ser usada em produção.

O SDK `10.0.400` foi distribuído originalmente com runtime `10.0.11`; a versão de runtime é fixada explicitamente na publicação. [Metadata .NET](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json). O bundle de migrations é self-contained e não exige SDK/EF Tool no servidor. [EF Core](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying).

Guarde o **SHA-256 de SHA256SUMS** mostrado pelo script por um canal confiável separado do pacote. Transporte toda a pasta por um meio autorizado e preserve os bytes. Integridade do manifesto não prova origem quando pacote e hash vêm do mesmo canal adulterado.

No Arch, substitua os placeholders e execute o instalador da pasta recebida:

```bash
sudo bash /caminho/absoluto/recebido/ops/arch/install.sh \
    --bundle /caminho/absoluto/recebido \
    --release-id <id> \
    --manifest-sha256 <hash-confiavel-de-SHA256SUMS>
```

Ele instala `/opt/nexora/releases/<id>`, diretórios faltantes e units. **Não cria current, inicia/habilita serviços, inicializa PostgreSQL nem aplica migrations.** Recusa release existente e unit instalada que difere da recebida: revise a alteração e substitua a unit explicitamente antes de repetir a instalação. Depois da revisão das units:

```bash
sudo systemctl daemon-reload
```

## 4. Provisionar banco e configuração privada

Execute o SQL público da release com `psql -X`, como administrador local. As duas senhas são solicitadas por `\password`, sem texto claro em argumentos ou histórico. [psql](https://www.postgresql.org/docs/18/app-psql.html).

```bash
sudo -u postgres psql -X --dbname=postgres \
    --file=/opt/nexora/releases/<id>/ops/arch/config/provision-database.sql
```

O SQL exige PostgreSQL 18, recusa papéis existentes privilegiados/com memberships e banco com owner inesperado. `nexora_migrator` é LOGIN e dono do banco/schema, sem superuser, CREATEDB ou CREATEROLE. `nexora` é LOGIN sem privilégios administrativos, ownership ou memberships: recebe CONNECT, USAGE no schema, SELECT/INSERT/UPDATE/DELETE nas tabelas e USAGE/SELECT em sequences. PUBLIC perde privilégios do banco e schema; objetos futuros criados pelo migrador recebem os mesmos privilégios DML por default privileges. **Nenhuma credencial de migrador é usada pelos serviços.** [Privilégios PostgreSQL](https://www.postgresql.org/docs/18/ddl-priv.html).

Copie os quatro `.env.example` da release para `/etc/nexora/{common,api,worker,migrations}.env`, como arquivos regulares novos `root:root 0600`; recuse sobrescrever configuração existente. Edite com `sudoedit`. `common.env` contém a conexão de execução compartilhada e `/srv/nexora`; `api.env` contém o FQDN e Data Protection; `worker.env` contém o orçamento de imagens; `migrations.env` sobrescreve somente a conexão para o papel migrador. Remova todos os valores `REPLACE_*`.

São arquivos **EnvironmentFile do systemd**, UTF-8 sem BOM, com atribuições em uma linha, valor simples ou integralmente entre aspas. O preflight recusa escapes/backslashes e quoting ambíguo. Se a senha contiver delimitadores, use uma connection string válida para Npgsql e um valor compatível com esse formato; não imprima a connection string para diagnosticar. Nunca use `source`, `eval`, `export $(cat ...)`, argumentos `--connection`, `--verbose` de migrations ou versionamento de arquivos reais. O provider `NEXORA_` é adicionado após os providers padrão, e `__` representa os níveis da configuração.

### PFX RSA local, sem sobrescrita

O certificado protege o **keyring Data Protection**, não o HTTPS do Tailscale. O Linux Production exige PFX com chave privada RSA. Crie-o no próprio Arch, usando temporário privado fora do Git e senha interativa de exportação:

```bash
sudo bash <<'BASH'
set -euo pipefail
umask 077
destination=/etc/nexora/data-protection.pfx
[[ -d /etc/nexora && ! -L /etc/nexora ]]
[[ ! -e "$destination" && ! -L "$destination" ]]
stage=$(mktemp -d /etc/nexora/.data-protection-XXXXXXXX)
cleanup() {
    if [[ $stage == /etc/nexora/.data-protection-* && -d $stage && ! -L $stage ]]; then
        rm -rf -- "$stage"
    fi
}
trap cleanup EXIT
openssl req -x509 -newkey rsa:3072 -sha256 -days 3650 -noenc \
    -subj /CN=Nexora-DataProtection \
    -keyout "$stage/private-key.pem" -out "$stage/certificate.pem"
openssl pkcs12 -export -name Nexora.DataProtection \
    -inkey "$stage/private-key.pem" -in "$stage/certificate.pem" \
    -out "$stage/data-protection.pfx"
chown root:nexora -- "$stage/data-protection.pfx"
chmod 0640 -- "$stage/data-protection.pfx"
# Same filesystem: atomic creation fails if another operator created the PFX.
ln -- "$stage/data-protection.pfx" "$destination"
BASH
```

Informe a mesma senha em `NEXORA_DataProtection__CertificatePassword` de `api.env` via editor protegido. Preserve **esse PFX e o mesmo keyring** entre releases e backups. A implementação atual não oferece rotação com certificados antigos; substituí-lo faz perder a leitura de chaves anteriores e compromete sessões. A renovação automática do certificado TLS do Serve é independente. [Data Protection e rotação](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0#unprotect-keys-with-any-certificate-unprotectkeyswithanycertificate).

## 5. Migrations e bootstrap explícitos

Antes de atualizar uma instalação com dados, faça e valide backup, revise `migrations.sql` e pare **ambos** os serviços. Numa primeira instalação eles ainda não foram iniciados. Execute o bundle da release pretendida em job transitório, com os arquivos em ordem: common, api, migrations. O último fornece a conexão do migrador:

```bash
sudo systemctl stop nexora-api.service nexora-worker.service
sudo systemd-run --unit=nexora-migrate-<id> --wait --collect --pipe \
    --property=Type=exec --property=User=nexora --property=Group=nexora \
    --property=WorkingDirectory=/opt/nexora/releases/<id>/migrations \
    --property=RuntimeDirectory=nexora-migrate-<id> --property=RuntimeDirectoryMode=0700 \
    --property='EnvironmentFile=/etc/nexora/common.env /etc/nexora/api.env /etc/nexora/migrations.env' \
    --property='Environment=ASPNETCORE_ENVIRONMENT=Production DOTNET_ENVIRONMENT=Production DOTNET_EnableDiagnostics=0 DOTNET_BUNDLE_EXTRACT_BASE_DIR=/run/nexora-migrate-<id>' \
    --property=UMask=0077 --property=NoNewPrivileges=yes --property=LimitCORE=0 \
    /opt/nexora/releases/<id>/migrations/efbundle
```

O bundle pode precisar extrair bibliotecas nativas do executável single-file. `RuntimeDirectory` cria um diretório privado gravável pelo usuário do job; `DOTNET_BUNDLE_EXTRACT_BASE_DIR` direciona a extração para esse diretório em `/run`, sem depender do home `/var/lib/nexora`, que não é gravável pelo serviço. Esse caminho de execução Linux ainda requer ensaio no Arch.

Somente após sucesso, restrinja a tabela de histórico a leitura pelo papel de execução. Repita a conferência após cada migração:

```bash
sudo -u postgres psql -X --dbname=nexora --set=ON_ERROR_STOP=on \
    --command='REVOKE ALL ON TABLE public."__EFMigrationsHistory" FROM nexora; GRANT SELECT ON TABLE public."__EFMigrationsHistory" TO nexora;'
sudo -u postgres psql -X --dbname=nexora --set=ON_ERROR_STOP=on \
    --command="SELECT has_database_privilege('nexora','nexora','CREATE') AS runtime_database_create, has_schema_privilege('nexora','public','CREATE') AS runtime_schema_create, has_table_privilege('nexora','public.\"__EFMigrationsHistory\"','INSERT') AS runtime_history_insert;"
```

Os três resultados devem ser `f`. Readiness consulta histórico/migrations; o papel runtime conserva SELECT, sem capacidade de registrar uma migration.

Na primeira instalação, crie a conta administrativa em terminal interativo usando **apenas common/api**, com a conexão runtime. O comando recebe login/senha pelo terminal e não os aceita como argumentos:

```bash
sudo systemd-run --unit=nexora-bootstrap --wait --collect --pty \
    --property=User=nexora --property=Group=nexora \
    --property=WorkingDirectory=/opt/nexora/releases/<id>/api \
    --property='EnvironmentFile=/etc/nexora/common.env /etc/nexora/api.env' \
    --property='Environment=ASPNETCORE_ENVIRONMENT=Production DOTNET_ENVIRONMENT=Production DOTNET_EnableDiagnostics=0' \
    --property=UMask=0077 --property=NoNewPrivileges=yes --property=LimitCORE=0 \
    /opt/nexora/releases/<id>/api/Nexora.Api bootstrap-admin
```

O reset usa a mesma forma e `reset-admin-password`, quando explicitamente necessário. Não recrie o administrador para atualizar uma release.

## 6. Tailscale Serve, Host e acesso privado

Cadastre/autentique o servidor pelo fluxo Tailscale autorizado e habilite MagicDNS/HTTPS no tailnet. Use o FQDN completo exibido pelo Tailscale, como `nexora.<tailnet-real>.ts.net`, em `NEXORA_AllowedHosts`; não use IP, hostname curto, wildcard, esquema ou porta. Serve pode conduzir o consentimento para HTTPS e provisiona os certificados TLS pelo daemon. Não é necessário exportar `tailscale cert` para Kestrel. [HTTPS Tailscale](https://tailscale.com/docs/how-to/set-up-https-certificates), [Serve](https://tailscale.com/docs/features/tailscale-serve).

Uma política mínima usa usuários reais em um grupo, tag atribuída por administradores e somente HTTPS no servidor. Este é um **trecho a integrar/revisar**, não uma substituição automática da política:

```json
{
  "groups": { "group:nexora-users": ["LOGIN_REAL_DO_USUARIO"] },
  "tagOwners": { "tag:nexora": ["autogroup:admin"] },
  "grants": [
    { "src": ["group:nexora-users"], "dst": ["tag:nexora"], "ip": ["tcp:443"] }
  ]
}
```

Grants são aditivos: uma regra existente ampla continua liberando acesso. Revise/remova permissões amplas que alcancem o servidor e teste também um dispositivo/usuário sem permissão. Não adicione Funnel capabilities para esse servidor. [Sintaxe grants](https://tailscale.com/docs/reference/syntax/grants).

Inspecione configurações existentes antes de alterá-las. Para a porta 443 dedicada ao Nexora, desligue eventual Funnel e configure **Serve por último**:

```bash
sudo tailscale serve status --json
sudo tailscale funnel status --json
sudo tailscale funnel --https=443 off
sudo tailscale serve --bg --https=443 http://127.0.0.1:5100
sudo tailscale serve status --json
sudo tailscale funnel status --json
```

Confirme acesso limitado ao tailnet e ausência de qualquer `AllowFunnel: true`, inclusive em outras portas/configurações existentes. O Serve e Funnel compartilham configuração por porta; o último comando define sua exposição. `--bg` conserva Serve após reinício. [Serve CLI](https://tailscale.com/docs/reference/tailscale-cli/serve), [Funnel CLI](https://tailscale.com/docs/reference/tailscale-cli/funnel).

Serve preserva Host no proxy HTTP e fornece XFF/HTTPS. A API aceita headers encaminhados somente de `127.0.0.1`/`::1`, com um salto, e exige HTTPS para `/api/*` em Production. Kestrel fica em `http://127.0.0.1:5100`; não exponha essa porta nem PostgreSQL em grants/firewall. Os headers de identidade Tailscale não substituem login, token e validação de sessão Nexora. [Implementação Serve](https://github.com/tailscale/tailscale/blob/main/ipn/ipnlocal/serve.go#L910-L929).

## 7. Ativação e verificação operacional

Rode o preflight explícito e depois escolha a release. Na primeira ativação, a validação de caminhos das units é concluída após a criação de `current`:

```bash
sudo bash /opt/nexora/releases/<id>/ops/arch/verify-host.sh --release-id <id>
sudo bash /opt/nexora/releases/<id>/ops/arch/activate.sh --release-id <id>
sudo systemctl enable --now nexora-api.service nexora-worker.service
curl --fail --silent --show-error http://127.0.0.1:5100/health/live
curl --fail --silent --show-error http://127.0.0.1:5100/health/ready
```

Ativação interrompe os dois serviços, verifica que seus processos/filhos pararam e troca `current` atomicamente. Retoma somente os serviços que estavam ativos antes da execução; o start/enable acima é a decisão explícita da primeira instalação. Em atualizações, registre o estado anterior antes da parada para migrations e reinicie os serviços pretendidos depois da ativação. Ela nunca migra banco. Se falhar após a parada, restaura apenas o ponteiro anterior e deixa os serviços parados para revisão; banco migrado pode ser incompatível com código anterior. Não trate troca de symlink como rollback de dados.

As units fornecem logs JSON no journal, `UMask=0077`, usuário sem privilégios, `PrivateTmp`, proteção de sistema/home/kernel e caminhos graváveis delimitados. O orçamento total é API `MemoryHigh=384M`/`MemoryMax=512M`, Worker `MemoryHigh=1024M`/`MemoryMax=1536M`, sem swap. Os pais usam `DOTNET_GCHeapHardLimit=10000000` hexadecimal = 256 MiB. O filho tem 256 MiB de heap gerenciado e working set amostrado de 512 MiB; o cgroup contém pai e filho. Memória nativa não é toda coberta pelo GC. Não habilite `MemoryDenyWriteExecute=yes` ou filtros de syscall não ensaiados: JIT/codec nativo precisam de compatibilidade real. [systemd recursos](https://github.com/systemd/systemd/blob/main/man/systemd.resource-control.xml#L352-L359), [JIT](https://github.com/systemd/systemd/blob/main/man/systemd.exec.xml#L2464-L2478), [GC .NET](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector#heap-hard-limit).

```bash
sudo systemctl status nexora-api.service nexora-worker.service --no-pager
sudo journalctl -u nexora-api.service -u nexora-worker.service --since '10 minutes ago' --no-pager
sudo systemctl show nexora-api.service nexora-worker.service -p User -p MemoryCurrent -p MemoryHigh -p MemoryMax -p TasksCurrent
```

Não ative logging de bodies/headers, SQL sensível, parâmetros Npgsql ou exception details. Os exemplos mantêm categorias Microsoft em Warning e os erros da aplicação são sanitizados. Health anônimo divulga somente status, sem connection string ou paths.

Confira capacidade do volume e consumo por categoria sem listar nomes de conteúdo:

```bash
df -h /srv/nexora /var/lib/nexora
sudo du -sh /srv/nexora/blobs /srv/nexora/thumbnails /srv/nexora/previews /srv/nexora/temp
```

O cliente autenticado pode consultar `/api/storage` para biblioteca ativa, lixeira, Blobs/derivados, temporários, reservas e espaço livre. Reserve a margem antes de novos uploads e investigue temporários/reservas persistentes pelo estado dos uploads/jobs; não exclua arquivos físicos à mão para liberar capacidade.

Para diagnóstico da fila, abra `sudo -u postgres psql -X --dbname=nexora` no terminal administrativo e execute somente consultas:

```sql
SELECT "Kind", "State", count(*) FROM public."BackgroundJobs"
GROUP BY "Kind", "State" ORDER BY "Kind", "State";
SELECT "Id", "Kind", "Attempts", "MaximumAttempts", "FailureCode", "NextAttemptAt"
FROM public."BackgroundJobs" WHERE "State" = 'Failed'
ORDER BY "NextAttemptAt", "Id" LIMIT 100;
SELECT "Id", "Kind", "LeaseExpiresAt"
FROM public."BackgroundJobs" WHERE "State" = 'Running' AND "LeaseExpiresAt" <= now()
ORDER BY "LeaseExpiresAt", "Id" LIMIT 100;
```

Uma tarefa esgotada permanece visível para investigação. Confira código de falha, espaço, Worker e journal antes de intervir; lease expirado permite recuperação pelo Worker. Não altere estado, lease ou tentativas por SQL como procedimento automático de retry. Não há alertas, painel nem endpoint de administração de jobs nesta entrega.

## 8. Aceitação no Arch ainda pendente

Antes de registrar a Fase 7 como concluída, guarde evidências da release/commit, versões do host e resultados destes fluxos:

- Preflight, PostgreSQL major 18, papéis/permissions, PFX RSA, ownership/modes e `/health/ready` saudável após migrations.
- HTTPS com FQDN exato em outro dispositivo autorizado; Host desconhecido recusado; API HTTP recusada; `/api/assets` sem token responde 401; usuário/dispositivo sem grant não acessa o servidor; Funnel permanece desligado.
- Login, refresh, upload em chunks, finalização pelo Worker, download com SHA-256 e JPEG/PNG/WebP com thumbnail/preview. Isso comprova o codec Linux e execução do filho sob a unit, além de bytes originais preservados.
- Restart de API/Worker e reboot do host; keyring/PFX e sessões persistem, leases/jobs se recuperam, Serve volta privado e grants continuam efetivos.
- Biblioteca, lixeira/restauração e manutenção de dados coerentes, sem paths/segredos nos logs e com orçamento de memória observado.
- Backup completo e **restauração real em ambiente isolado**, seguida de comparação de originais/derivados/metadados, autenticação e retomada dos jobs, conforme [backup-and-restore.md](backup-and-restore.md).

Execução Linux, acesso móvel via tailnet e restauração não foram comprovados pelo ambiente Windows. Até essas verificações, a Fase 7 permanece preparada e pendente de aceitação operacional.

# Backup consistente e ensaio de restauração

Esta fase entrega scripts e um procedimento manual para Arch Linux. A preparação e a verificação de sintaxe no Windows não comprovam um backup recuperável: executar o ensaio completo abaixo em um host Arch isolado continua pendente. Não há backup agendado, timer, retenção automática, replicação, PITR nem restauração automática em produção.

O banco, os arquivos e as chaves formam uma unidade de recuperação. Um [dump PostgreSQL é internamente consistente](https://www.postgresql.org/docs/18/backup-dump.html), mas não congela os arquivos da aplicação. Por isso `backup.sh` para API e Worker antes de capturar ambos e inclui conteúdo publicado, tentativas intermediárias e temporários. Não copie somente `blobs/` ou somente o banco. A chave privada RSA, sua senha e o keyring são necessários para ler access tokens protegidos anteriormente; sua perda não pode ser corrigida pelo dump. Refresh tokens são aleatórios e persistidos como hash: seu sucesso isolado não demonstra recuperação de Data Protection.

## Contrato e pré-requisitos

Use o layout e as units de [arch-deployment.md](arch-deployment.md). Os scripts ficam na própria release, por exemplo `/opt/nexora/current/ops/arch/backup.sh`; `common.sh` deve permanecer junto deles. São scripts Bash para GNU coreutils, findutils, grep, tar, gzip, jq, util-linux, systemd com cgroup v2 e clientes PostgreSQL 18. Não execute estes comandos no Windows, em Nextcloud ou no banco pessoal de desenvolvimento.

| Componente | Caminho / requisito |
| --- | --- |
| Release | `/opt/nexora/releases/<release-id>`, com `api/`, `worker/`, `migrations/efbundle`, `release-id.txt`, `release.json` e `SHA256SUMS`. `/opt/nexora/current` é o único symlink previsto neste contrato. |
| Banco de produção | `nexora`, PostgreSQL 18; dono, schema e relações da aplicação pertencem à role `nexora_migrator`, sem privilégios de superuser, criação de bancos/roles, replicação ou bypass de RLS, e sem memberships. A role de runtime `nexora` tem apenas CRUD/USAGE e SELECT do histórico EF. A aplicação usa TCP local/SCRAM conforme o guia de implantação. |
| Administração do banco | `runuser -u postgres`, socket explícito `/run/postgresql:5432`, usuário PostgreSQL `postgres` com peer. Os scripts descartam variáveis `PG*` herdadas e não recebem senhas em argumentos. |
| Storage completo | `/srv/nexora`, `nexora:nexora 0700`: inclui `blobs`, `thumbnails`, `previews`, `temp`, chunks, `.part` e `.publishing`. |
| Key ring | `/var/lib/nexora/keys`, `nexora:nexora 0700`. Preservar todos os XML, inclusive chaves antigas. |
| Configuração | `/etc/nexora`, `root:nexora 0750`; `common.env`, `api.env`, `worker.env` são `root:root 0600`. O certificado RSA com chave privada é `/etc/nexora/data-protection.pfx`, `root:nexora 0640`, legível pela API. Todos os arquivos desta árvore entram no snapshot. |
| Destino do backup | Diretório já provisionado, absoluto, `root:root 0700`, fora de storage, key ring, configuração, releases, ensaios e Nextcloud; exemplo `/mnt/nexora-backups`, em outro volume. Todos os ancestrais devem pertencer a root e não permitir escrita de grupo/outros. |

Os scripts recusam componentes de caminho que sejam symlinks, árvores com symlinks, hardlinks ou inodes especiais e nomes fora do contrato ASCII. Não existe fallback para seguir links. O operador e root são confiáveis: reserve uma janela sem deploy, migrations, comandos administrativos, instâncias avulsas, alterações de certificados ou outros escritores. As units e o banco não impedem um administrador de alterar os arquivos durante o backup.

## Criar o snapshot manual

Monte e confira o volume externo antes de provisionar o destino. `install -d` não monta o disco e não deve ser usado para ocultar um mount ausente. Verifique capacidade para dump, arquivo comprimido e metadados; um volume de aproximadamente 500 GB exige tempo e espaço proporcionais aos dados reais.

```bash
findmnt /mnt/nexora-backups
sudo install -d -o root -g root -m 0700 /mnt/nexora-backups
sudo bash /opt/nexora/current/ops/arch/backup.sh \
  --backup-root /mnt/nexora-backups
```

O script adquire o mesmo lock exclusivo `/opt/nexora/.deploy.lock` usado por instalação, ativação e restore; uma segunda operação concorrente falha antes de alterar serviços. Confere manifest e hashes da release atual, permissões e ownership; captura quais units estavam ativas e para ambas. Aguarda até 90 segundos depois do stop para confirmar `MainPID=0`, `ControlPID=0`, cgroups vazios, inclusive o renderer filho, e nenhuma outra conexão a `nexora`. O próprio stop respeita os limites das units. Somente então grava o histórico EF, os parâmetros do banco, `pg_dump --format=custom` e um `tar.gz` completo. Dump e diagnóstico são redirecionados para arquivos privados; connection strings, tokens, senhas e conteúdo não são enviados ao terminal. Nenhum `.env` é executado com `source`.

O script verifica o histórico do banco contra `lastMigration` da release. Backup não aplica migration nem cria roles. Todos os processos precisam estar encerrados para que as publicações em andamento e os jobs persistidos representem o mesmo ponto de recuperação. Quando um processo precisou terminar, seus intermediários permanecem no snapshot e o Worker retomará a decisão durável ao iniciar posteriormente.

O resultado tem esta estrutura, sempre root e privado:

```text
/mnt/nexora-backups/<UTC>_<release-id>/
  database.dump
  database-settings.txt
  payload.tar.gz       # raízes relativas storage/, keys/, config/
  migrations.txt       # MigrationId + ProductVersion, ordenados
  release-id.txt
  release.json
  release-SHA256SUMS   # conteúdo exato do manifest da release
  manifest.txt
  backup.log
  SHA256SUMS           # cobre todos os arquivos acima
```

Até concluir checksums e validação, o diretório termina em `.incomplete`. Uma falha preserva esse diretório para inspeção; ele não é um snapshot aceito pelo restore. Na saída, inclusive por erro, SIGINT ou SIGTERM, o trap tenta iniciar somente as units que estavam originalmente ativas. Falhas de restart são informadas e fazem o comando retornar erro, mesmo quando o snapshot já foi finalizado. Verifique `systemctl status nexora-api nexora-worker` e `/health/ready` após a operação. SIGKILL, falha de energia ou falha do próprio systemd podem impedir o trap; nesse caso o operador precisa verificar e recuperar o estado das units manualmente.

O tar e o gzip trabalham em streaming, sem carregar um arquivo de 500 GB em RAM. A aplicação fica parada durante dump, compressão e cálculo dos hashes; dimensione a janela com medidas do próprio volume. A restauração também faz passes de leitura para checksums, validação do tar e extração. Há custo de I/O além da cópia inicial.

Guarde também o bundle publicado exato daquela release fora do host. O snapshot contém sua identidade e hashes, mas não contém os executáveis. Conserve uma segunda cópia protegida e offline do snapshot e do `SHA256SUMS`; não publique estes arquivos em Git, logs ou Nextcloud. Os checksums detectam corrupção e troca de conteúdo quando comparados a uma cópia confiável; não autenticam um backup cujo manifest tenha sido adulterado junto com os dados. O `tar.gz` não fornece criptografia: use um volume/cópia com proteção apropriada e mantenha o acesso restrito. Não há exclusão automática de snapshots.

## Restaurar dados em um host isolado

Use uma VM ou máquina Arch separada, sem acesso aos volumes de produção e sem encaminhamento de tráfego. Instale PostgreSQL 18 com encoding, provider e locale iguais aos registrados em `database-settings.txt`; o restore compara esses parâmetros e recusa diferenças. Para o contrato de implantação atual, preserve o `initdb` UTF-8 / `C.UTF-8`. Instale o bundle exato por seu manifest confiável conforme o guia de implantação, sem ativar `current` ou serviços. Não atualize código, migration ou schema antes de validar a recuperação daquela release.

Provisionamento administrativo no host de ensaio:

```bash
sudo install -d -o root -g root -m 0700 /var/lib/nexora-restore
sudo runuser -u postgres -- psql -X --dbname=postgres
```

No `psql`, crie uma role dona dedicada, sem login nem privilégios globais; a role deve não ter memberships em outras roles:

```sql
CREATE ROLE nexora_restore NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
```

Copie o snapshot com suas permissões privadas para o volume de backup do host isolado. Confirme o hash do `SHA256SUMS` contra a cópia confiável e confira que não está usando um diretório `.incomplete`. Então execute, usando identificadores novos:

```bash
sudo bash /opt/nexora/releases/<release-id>/ops/arch/restore.sh \
  --isolated \
  --backup /mnt/nexora-backups/<UTC>_<release-id> \
  --release-dir /opt/nexora/releases/<release-id> \
  --database nexora_restore_exercicio_20261008 \
  --target-root /var/lib/nexora-restore/exercicio_20261008
```

`--isolated` afirma que o operador já isolou o host. O script recusa API/Worker ativos ou processos remanescentes nas units; essa checagem sozinha não transforma um host de produção parado em um ambiente isolado. O banco precisa ser inexistente e usar `nexora_restore_[a-z0-9_]+`. O destino precisa ser inexistente e um filho direto, literal, de `/var/lib/nexora-restore`. Os scripts nunca executam `DROP DATABASE`, `--clean`, `--create` do pg_restore, sobrescrita de produção ou inicialização de serviços.

Antes de extrair, `restore.sh` confere cobertura e SHA-256 de todos os arquivos, manifest e identidade/conteúdo exatos da release, lista integralmente o tar e valida cada membro. Rejeita nomes absolutos, `..`, controles, links simbólicos/hardlinks, dispositivos, FIFO, modos especiais, entradas duplicadas e membros fora de `storage/`, `keys/`, `config/`. A extração ocorre em diretório novo, privado, sem preservar ownership do arquivo e sem substituir arquivos existentes. Estas restrições seguem a [orientação de segurança do GNU tar](https://www.gnu.org/software/tar/manual/html_section/Security.html).

Depois da extração, todos os diretórios são root `0700` e arquivos root `0600`. A configuração está apenas arquivada em `<target>/config`; não foi instalada em `/etc/nexora`, carregada pelo shell ou entregue aos serviços. O script cria o banco com owner `nexora_restore`, revoga acesso de `PUBLIC` e executa `pg_restore --role=nexora_restore --no-owner --no-acl --single-transaction --exit-on-error`. A conexão administrativa usa peer, mas o SQL do dump é executado sob a role limitada. O restore compara todo o `__EFMigrationsHistory` com o snapshot e preserva os metadados e `restore.log` privados em `<target>/metadata`.

Restaure somente dumps do seu banco e de administradores confiáveis: [pg_restore executa SQL contido no dump](https://www.postgresql.org/docs/18/app-pgrestore.html). Checksum não inspeciona o significado desse SQL. O script não faz restore de roles globais nem pede a senha de produção. Uma falha deixa arquivos e, quando já criado, banco para inspeção; não há rollback de filesystem nem exclusão automática. O banco é restaurado em uma transação, mas pode permanecer vazio quando uma etapa anterior falha. Corrija a causa e escolha novo identificador para outro ensaio; remoções posteriores são uma operação administrativa explícita sobre o ambiente isolado.

## Configurar a instância recuperada para o exercício

Prepare arquivos `.env` novos e privados para o ensaio a partir dos exemplos de implantação. **Não execute nem copie automaticamente os `.env` de `config/`**: eles contêm credenciais e caminhos de produção. Use uma credencial nova do host de ensaio e substitua explicitamente `NEXORA_ConnectionStrings__Nexora`, `NEXORA_Storage__RootPath`, `NEXORA_DataProtection__KeyDirectory`, `NEXORA_DataProtection__CertificatePath` e endereço de escuta. O certificado e os XML antigos devem permanecer os mesmos para validar chaves antigas; informe manualmente a senha do PFX existente em `NEXORA_DataProtection__CertificatePassword`, sem imprimi-la ou incluí-la em argumentos. Mantenha `SetApplicationName("Nexora.Auth")` da mesma release.

No `psql` administrativo do host isolado, crie uma role de login só para o exercício e defina sua senha no prompt. Não use SQL com senha literal, argumentos de processo ou um arquivo gerado automaticamente:

```sql
CREATE ROLE nexora_restore_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
\password nexora_restore_app
GRANT nexora_restore TO nexora_restore_app;
GRANT CONNECT ON DATABASE nexora_restore_exercicio_20261008 TO nexora_restore_app;
```

Restrinja SCRAM/TCP a `127.0.0.1` e ao banco/role do ensaio. Escreva a nova connection string em um `.env` `root:root 0600` usando editor local. A role de login recebe a ownership restrita por membership em `nexora_restore`; não recebe privilégios globais. Não altere a role `nexora` nem suas credenciais.

Antes de executar API/Worker manualmente ou em units próprias do ensaio, adapte as permissões apenas nos caminhos literais que acabou de restaurar. Para o exemplo acima, storage e keys precisam ficar `nexora:nexora`, diretórios `0700` e arquivos `0600`; os ancestrais até o destino devem permitir travessia ao usuário `nexora`, mantendo o metadata e os `.env` privados. Use paths completos revisados em cada `chown`/`chmod`, sem glob de identificadores. O PFX copiado para um diretório próprio de configuração do ensaio precisa de `root:nexora 0640` e ancestral `root:nexora 0750`, como no contrato da API. Esta preparação manual não muda os arquivos de produção e não deve tornar toda a configuração arquivada legível pela aplicação.

API e Worker do ensaio devem receber o mesmo banco e storage isolados, chaves recuperadas apenas na API e uma URL local diferente, por exemplo `http://127.0.0.1:5180`. Crie units de ensaio com caminhos e EnvironmentFiles próprios, revisadas antes de iniciar; as units padrão continuam apontando à produção. Não use `source` de `.env` para iniciar o processo: preserve a semântica de `EnvironmentFile` do systemd. O script de restore encerra antes desta configuração e não inicia a aplicação.

## Evidência de recuperação no Arch

Prepare dados controlados antes de tirar o snapshot e registre IDs, contagens, tamanhos e hashes em uma folha de evidência privada. Não registre senhas, bearer tokens ou refresh tokens. Exercite:

1. Preparar a conta administrativa do ensaio, um arquivo binário e imagens JPEG/PNG/WebP, incluindo uma imagem com EXIF e uma sem captura UTC confiável. Guarde SHA-256 dos originais e dos PNG derivados, tamanho e IDs de Asset/Blob em evidência protegida. Conclua pelo menos um job de imagem antes do snapshot e deixe um upload finalizando/job pendente para testar retomada. A instalação pessoal não oferece cadastro público nem segundo bootstrap; testes com duas contas pertencem a bancos isolados da suíte de integração.
2. Reenvie os mesmos bytes pela mesma conta e registre o mesmo Asset e um único original físico. Coloque um item na lixeira, mantenha um favorito e confira timeline e paginação antes da cópia. Se o ambiente tiver contas adicionais preparadas por fixtures, confira também o Blob compartilhado e Assets separados; não acrescente contas ao banco pessoal para esse teste.
3. Faça login antes do snapshot e guarde um par de tokens somente em um cliente local protegido. Para verificar leitura de access token antigo, faça um ensaio pequeno que permita snapshot, restore e requisição antes de seus cinco minutos de validade. Em backup longo ele pode expirar; não interprete 401 de token expirado como perda de chaves. Conserve também um refresh token não consumido, dentro dos 30 dias da sessão, para verificar o estado recuperado da sessão. Não use esse par em paralelo com produção depois do snapshot.
4. Após restaurar, compare `__EFMigrationsHistory`, usuários, sessões/dispositivos, Assets/Blobs, uploads, BlobImages, jobs e tentativas com o estado registrado. Confira hashes dos XML do keyring e do PFX antes de iniciar a instância. Faça login e confira `/health/live` e `/health/ready`; no ensaio curto, use o access token antigo ainda válido para comprovar a leitura da chave anterior. Baixe originais e derivados por `/api/assets/{id}/content`, `/thumbnail` e `/preview`; confira SHA-256, tamanho, Range e recusa sem autenticação. Se houver duas contas de fixture, confira isolamento de proprietários. Os bytes devem coincidir com os mesmos objetos no snapshot.
5. Confira biblioteca, favorito, nome, `/api/assets?imagesOnly=true&sort=timeline`, cursores e `/api/trash`. Originais/derivados do item na lixeira devem retornar `404`; restaure esse item por `POST /api/trash/{id}/restore` e verifique ID e conteúdo preservados. Faça purge somente de um item controlado, no ensaio, para verificar que um Blob ainda referenciado por outra conta não é removido.
6. Inicie o Worker isolado e aguarde o job pendente alcançar seu estado terminal. Leases/tentativas antigas devem se resolver sem perda de chunks/originais, sem nova duplicata e sem publicação indevida de intermediários. Confira novos derivados e os registros de tentativas. Nenhum comando deve apontar para o volume ou banco originais.
7. Teste o refresh antigo recuperado **uma única vez e em sequência** pelo cliente protegido, sem Authorization antigo no endpoint de refresh. Troque o par local pelo novo recebido. Isso valida hashes e estado da sessão recuperados, sem substituir a verificação de chave do passo 4. Valide sessão/dispositivo e logout/revogação; refresh concorrente ou reutilização de token consumido revoga a família e não constitui um teste de backup válido. Um access token expirado deve continuar expirado, mesmo com as chaves corretas. Registre sucesso/estado e IDs, nunca os valores dos tokens.
8. Reinicie apenas a instância de ensaio e repita login, download, leitura de derivados e uso do par renovado enquanto válido. Isso verifica persistência do key ring/certificado, sessões e jobs além da primeira inicialização. Registre release, versão Arch/PostgreSQL, comandos, horário, checksums, resultados e limitações; vincule a evidência em [status-and-roadmap.md](status-and-roadmap.md).

O ensaio só é aceito quando banco, originais, derivados, deduplicação, biblioteca/lixeira, jobs e chaves/sessões forem recuperados juntos. A existência de `database.dump`, um checksum válido ou um comando de restore que terminou não comprova a recuperação da aplicação. Restauração sobre produção, plano de corte de tráfego, perda máxima admissível e procedimentos de recuperação operacional continuam sendo trabalho futuro; este script não oferece essa operação.

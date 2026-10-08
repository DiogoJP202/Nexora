# Desenvolvimento local

Execute os comandos deste guia na raiz do repositório. As fases 1 a 4 entregam fundação, autenticação, armazenamento com deduplicação e arquivos por API. API e Worker são processos separados que compartilham PostgreSQL e a raiz de storage.

## SDK e dependências

`global.json` fixa a família de SDK 10.0.400 com atualização de patch permitida. As versões de pacotes são centralizadas e os arquivos de lock são versionados. A ferramenta EF Core é local ao repositório, fixada na linha 10; não depende de instalação global de `dotnet-ef`.

```powershell
dotnet --version
dotnet tool restore
dotnet restore Nexora.sln
dotnet build Nexora.sln
```

Para reproduzir exatamente as dependências já registradas, use `dotnet restore Nexora.sln --locked-mode`. Atualizações de dependências exigem revisão das versões e dos arquivos de lock.

## PostgreSQL 18 fora do Nextcloud

O ambiente Windows usa PostgreSQL nativo/portable, sem Docker. Binários, cluster, logs e demais arquivos locais ficam sob `%LOCALAPPDATA%\Nexora\postgresql`; não devem ficar no diretório do projeto sincronizado pelo Nextcloud. Uma instalação PostgreSQL 18 existente pode ser usada se cumprir a separação de credenciais e bancos abaixo.

O setup automatizado exige PowerShell 7 (`pwsh`) e `tar.exe` no Windows:

```powershell
pwsh -File scripts/Initialize-LocalPostgres.ps1
```

O script baixa os binários PostgreSQL 18.6 distribuídos pela EDB, extrai `bin`, `lib` e `share`, inicializa o cluster e o inicia. A instalação fica sob `%LOCALAPPDATA%\Nexora\postgresql\18`; o cluster fica em `data`, os binários em `pgsql\bin` e o log em `postgresql.log`. Escuta somente em `127.0.0.1:55432`, com timezone UTC e autenticação SCRAM.

O bootstrap gera senhas aleatórias sem exibi-las, cria roles/bancos separados e registra as configurações nos User Secrets da API e dos testes. A credencial administrativa do bootstrap também fica fora do repositório, em arquivo protegido no diretório local do PostgreSQL; essa conta não é usada pela aplicação. O diretório do cluster recebe ACL restrita ao usuário Windows atual e SYSTEM.

Não há serviço Windows instalado. Controle o processo explicitamente:

```powershell
pwsh -File scripts/LocalPostgres.ps1 -Action Status
pwsh -File scripts/LocalPostgres.ps1 -Action Start
pwsh -File scripts/LocalPostgres.ps1 -Action Stop
```

O setup preserva roles e bancos existentes, sem redefinir credenciais. Ele interrompe o provisionamento diante de estado parcial ou cluster desconhecido. Se os User Secrets forem perdidos, executar o setup novamente não recupera as credenciais das roles existentes; examine a situação antes de alterar contas ou dados. Não reinicialize nem apague um cluster para contornar um erro de configuração.

Como alternativa ao script, use uma instalação PostgreSQL 18 existente ou os [binários Windows indicados pelo projeto PostgreSQL](https://www.postgresql.org/download/windows/). Configure loopback, SCRAM e senhas próprias; a porta deve corresponder às connection strings. O provisionamento manual equivalente, somente na primeira configuração, é:

```sql
CREATE ROLE nexora_dev LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
\password nexora_dev
CREATE DATABASE nexora_dev OWNER nexora_dev;

CREATE ROLE nexora_test LOGIN NOSUPERUSER CREATEDB NOCREATEROLE NOREPLICATION;
\password nexora_test
CREATE DATABASE nexora_test OWNER nexora_test;
GRANT CONNECT ON DATABASE postgres TO nexora_test;
```

As linhas `\password` são comandos do `psql` e solicitam a senha interativamente. Este bloco é de provisionamento inicial; não deve ser executado repetidamente contra roles/bancos já existentes. Nunca usar a role superuser na aplicação ou nos testes. `CREATEDB` é necessário apenas para a fixture de integração criar seus bancos isolados; não conceder esse privilégio à aplicação nem à conta de produção.

O banco `nexora_dev` pertence à aplicação local. `nexora_test` e a role correspondente ficam separados; os testes efetivos criam bancos transitórios próprios com prefixo `nexora_it_`. A role de teste precisa conectar ao banco de manutenção `postgres`. A fixture não remove um banco externo passado na configuração.

## Configuração e segredos

| Chave | Configuração local | Produção |
| --- | --- | --- |
| `ConnectionStrings:Nexora` | User Secrets da API ou variável de ambiente. | Segredo fornecido pelo operador, fora do Git. |
| `Storage:RootPath` | Caminho absoluto local, preferencialmente `%LOCALAPPDATA%\Nexora\storage`. | `/srv/nexora` como padrão versionado. |
| `ConnectionStrings:NexoraTests` | User Secrets dos testes ou variável exclusiva da fixture. | Não usada pela aplicação. |
| `DataProtection:KeyDirectory` | Opcional; por padrão, diretório `Nexora/keys` em LocalApplicationData do usuário do sistema. | Diretório permanente e restrito, fora da instalação e do storage de conteúdo. |
| `DataProtection:CertificatePath` | Não necessário no Windows; desenvolvimento Linux permite key ring sem certificado. | Obrigatório em Linux fora de Development: PFX RSA protegido com chave privada. |
| `DataProtection:CertificatePassword` | Segredo opcional para o PFX. | Fornecido fora do Git junto aos demais segredos. |

A API e o Worker utilizam os providers nativos de configuração e acrescentam variáveis com prefixo `NEXORA_`. O provider adicional é carregado por último e pode sobrescrever os demais valores. Use dois underscores para hierarquia: `NEXORA_Storage__RootPath`, `NEXORA_ConnectionStrings__Nexora` e `NEXORA_Uploads__ChunkSizeBytes`. `NEXORA_STORAGE_PATH` não corresponde à configuração atual. Ambos validam opções de banco, storage e uploads na inicialização; mantenha os mesmos limites nos dois processos.

User Secrets da API e do Worker usam o identificador `Nexora.Api.Development`, carregado em Development. Para registrar a connection string, substitua a senha do exemplo localmente:

```powershell
dotnet user-secrets set "ConnectionStrings:Nexora" "Host=127.0.0.1;Port=55432;Database=nexora_dev;Username=nexora_dev;Password=<senha-local>" --project src/Nexora.Api

$nexoraStorageRoot = Join-Path $env:LOCALAPPDATA 'Nexora\storage'
dotnet user-secrets set "Storage:RootPath" $nexoraStorageRoot --project src/Nexora.Api
```

Esses comandos manuais são necessários apenas para uma configuração alternativa; o bootstrap já registra esses valores. `55432` é a porta do cluster provisionado pelos scripts; ajuste se usar outra instância. User Secrets são armazenamento local de desenvolvimento, não um cofre criptografado. Não usar credenciais de produção e não compartilhar sua saída. Evite deixar senhas reais no histórico do terminal ou em arquivos versionados.

Alternativamente, configure as variáveis apenas na sessão do processo. Um exemplo sem connection string literal:

```powershell
$env:NEXORA_Storage__RootPath = Join-Path $env:LOCALAPPDATA 'Nexora\storage'
# NEXORA_ConnectionStrings__Nexora deve ser fornecida por seu ambiente local de segredos.
```

A validação de `Storage:RootPath` não cria diretórios: o caminho deve ser absoluto para o sistema operacional atual e não pode ser a raiz de um volume. Os adaptadores da Fase 3 criam diretórios privados ao executar uma escrita e recusam symlinks/junctions. A connection string deve informar Host, Database e Username. Não ativar `Log Parameters`, `Include Error Detail` nem `Persist Security Info`; a validação rejeita essas opções por exporem dados sensíveis.

As chaves de autenticação usam Data Protection com ApplicationName estável `Nexora.Auth`. No Windows, o padrão é `%LOCALAPPDATA%\Nexora\keys`, com ACL restrita ao usuário atual e SYSTEM e proteção DPAPI do usuário. Um certificado configurado substitui a proteção DPAPI. O provider inicializa o diretório de chaves quando necessário; ele fica fora da instalação e do storage. Um override utiliza `NEXORA_DataProtection__KeyDirectory`, sempre com caminho absoluto e diferente da raiz de um volume.

No desenvolvimento Linux, o diretório é restrito a modo `0700`, mas o key ring pode ficar sem criptografia de arquivo; isso é permitido apenas em Development. Em Linux fora de Development, configurar `NEXORA_DataProtection__CertificatePath` para um PFX RSA com chave privada e, se necessário, `NEXORA_DataProtection__CertificatePassword` por segredo. Proteja os arquivos do certificado e faça backup do certificado/chaves; não basta copiar o executável. Os [detalhes de autenticação](authentication.md) explicam recuperação e revogação.

Um servidor PostgreSQL temporariamente indisponível deixa readiness em falha; configuração ausente ou inválida impede a aplicação de iniciar. Não imprimir connection strings em logs, issues ou comandos de diagnóstico compartilhados.

## Migrations explícitas

A migration inicial configura Identity; a Fase 2 acrescenta dispositivos, sessões e refresh tokens; `20261006114248_ContentBlobsAssets`, da Fase 3, acrescenta Blobs e Assets. `20261008112515_UploadsDurableJobs` acrescenta sessões/chunks de upload, jobs e suas tentativas rastreadas. Aplicar migrations não cria administrador. Toda alteração de schema passa por migration versionada e revisão.

Com a configuração da API de desenvolvimento disponível:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet ef migrations list --project src/Nexora.Infrastructure --startup-project src/Nexora.Api
dotnet ef database update --project src/Nexora.Infrastructure --startup-project src/Nexora.Api
```

Para gerar uma migration de uma alteração futura de modelo:

```powershell
dotnet ef migrations add NomeDaAlteracao --project src/Nexora.Infrastructure --startup-project src/Nexora.Api
```

Revise o SQL e o impacto de cada migration antes de aplicá-la. API, Worker e health checks nunca executam `Migrate` na inicialização. Em produção, migrations serão uma operação explícita do deploy com credencial apropriada, antes de disponibilizar a nova versão; não embutir uma senha de produção no comando ou no repositório.

## Criar ou recuperar a conta administrativa

Com PostgreSQL iniciado e migrations aplicadas, execute em um terminal interativo:

```powershell
dotnet run --project src/Nexora.Api -- bootstrap-admin
```

O comando pede email, senha e confirmação. A senha fica oculta, não é argumento do comando e não deve entrar em logs. A conta única não é criada automaticamente; um segundo bootstrap é recusado.

Para recuperar localmente a senha da conta já existente:

```powershell
dotnet run --project src/Nexora.Api -- reset-admin-password
```

O reset pede a nova senha e sua confirmação sem eco. Ele revoga todas as sessões existentes. Não há endpoint HTTP de registro ou recuperação de senha. A passphrase deve ter entre 14 e 256 caracteres; não há exigência artificial de dígitos, maiúsculas ou símbolos.

## Executar e verificar a API

```powershell
dotnet run --project src/Nexora.Api
```

O launch profile define Development e `http://127.0.0.1:5100`, sem abrir navegador. Em outro terminal:

```powershell
Invoke-RestMethod http://127.0.0.1:5100/health/live
Invoke-RestMethod http://127.0.0.1:5100/health/ready
Invoke-RestMethod http://127.0.0.1:5100/openapi/v1.json
```

Live não depende de PostgreSQL. Ready verifica conexão e migrations pendentes com timeout de cinco segundos, retornando status genérico e `503` em falha. OpenAPI existe somente em Development; não há Swagger UI instalada. Respostas de erro utilizam Problem Details com código e identificador de rastreamento, sem detalhes internos.

As rotas JSON mantêm o limite de 16 KiB. A rota `PUT /api/uploads/{id}/chunks/{number}` usa o tamanho configurado de chunk como limite e recebe apenas `application/octet-stream`; também verifica o tamanho real exigido para aquele índice. Requisições de login e refresh não devem incluir um bearer antigo de sessão revogada; enviar somente seus corpos JSON.

O HTTP em loopback serve ao desenvolvimento local. Fora de Development, `/api/*` exige HTTPS. O uso previsto é Tailscale Serve terminando TLS e encaminhando para loopback; Forwarded Headers só são aceitos de proxies loopback confiáveis. Funnel permanece desativado e grants restringem a tailnet. Deploy systemd e operação com dados pessoais ainda pertencem à Fase 7.

## Executar o Worker

Com as migrations aplicadas e a API em execução, abra outro terminal na raiz do repositório:

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
dotnet run --project src/Nexora.Worker
```

O launch profile também define Development. O Worker carrega `appsettings.json` a partir da pasta do executável, lê os mesmos User Secrets da API em Development e aceita overrides `NEXORA_`. Ele não escuta HTTP e não requer o diretório de chaves de autenticação da API. Configure a mesma conexão, raiz e opções de uploads nos dois processos. Variáveis de ambiente são locais a cada terminal: um override na janela da API não se propaga ao Worker.

O processo executa uma montagem por vez, renova leases e mantém expiração/limpeza em paralelo. Sem Worker, uma conclusão permanece `Finalizing`. Para interromper localmente, use Ctrl+C; um job cujo lease ficou ativo pode ser recuperado quando ele expirar. O contrato e os estados consultáveis estão em [uploads.md](uploads.md).

## Testes unitários e de integração

As regras de domínio, chaves e hashing podem ser verificadas sem PostgreSQL:

```powershell
dotnet test tests/Nexora.UnitTests -c Release
```

User Secrets dos testes usam `Nexora.IntegrationTests.Development`:

```powershell
dotnet user-secrets set "ConnectionStrings:NexoraTests" "Host=127.0.0.1;Port=55432;Database=nexora_test;Username=nexora_test;Password=<senha-local-de-testes>" --project tests/Nexora.IntegrationTests
dotnet test Nexora.sln
```

O bootstrap já registra o segredo de testes. Para executar após o setup, basta iniciar o cluster e rodar `dotnet test Nexora.sln`. A alternativa é `NEXORA_TEST_CONNECTION_STRING`, específica da fixture, com a connection string da role de testes. Esta variável não é a conexão usada pela API.

Os testes unitários, HTTP, de configuração e filesystem executam sem PostgreSQL. Quando nenhuma conexão de testes é fornecida, testes PostgreSQL aparecem como skipped com motivo explícito. Se a conexão foi fornecida, falhas de autenticação, banco indisponível, privilégio ausente ou migration incorreta resultam em testes falhos.

Testes de autenticação usam Data Protection efêmero por padrão para não tocar as chaves pessoais do desenvolvedor. Contas/dispositivos criados por fixtures existem somente em seus bancos isolados; os testes não executam bootstrap no banco de desenvolvimento.

Cada fixture cria um banco `nexora_it_<guid>` e aplica migrations nesse banco. A limpeza é restrita ao identificador criado pela própria fixture e a seu prefixo; nunca apontar testes à produção. Bancos que sobrarem após uma interrupção devem ser examinados pelo administrador e removidos somente quando for confirmado que pertencem à execução interrompida, sem comandos de exclusão por wildcard.

Testes de conteúdo e uploads usam diretórios próprios sob a pasta temporária do sistema, com limpeza restrita ao caminho gerado pela fixture. Verificam streams sem seek, publicação sem sobrescrita, limites reais de bytes, cancelamento, junctions, deduplicação concorrente, chunks, leases, retomada, limpeza, capacidade e downloads. Não usam o storage pessoal configurado na API.

O [guia de armazenamento](storage.md) descreve os contratos internos; [uploads.md](uploads.md) descreve os limites de admissão, reservas e recuperação durável da Fase 4. Não há quota fixa de 500 GB: capacidade e espaço livre vêm do volume. Os caminhos nativos de publicação, sincronização e locks Linux precisam de validação no Arch; os resultados locais desta fase são de Windows. Testes de falha injetada e recuperação de lease não comprovam persistência após queda de energia.

Esses comandos descrevem a validação disponível; a documentação não representa um relatório de testes de uma execução específica.

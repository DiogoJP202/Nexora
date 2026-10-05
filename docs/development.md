# Desenvolvimento local

Execute os comandos deste guia na raiz do repositório. A Fase 1 disponibiliza infraestrutura e health checks; não oferece endpoints de autenticação ou de arquivos.

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

A API utiliza os providers padrão do ASP.NET Core e acrescenta variáveis com prefixo `NEXORA_`. O provider adicional é carregado por último e pode sobrescrever os demais valores. Use dois underscores para hierarquia: `NEXORA_Storage__RootPath` e `NEXORA_ConnectionStrings__Nexora`. `NEXORA_STORAGE_PATH` não corresponde à configuração atual.

User Secrets da API usam o identificador `Nexora.Api.Development`. Para registrar a connection string, substitua a senha do exemplo localmente:

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

A inicialização valida configuração sem abrir conexão de banco e sem criar diretórios. `Storage:RootPath` deve ser absoluto para o sistema operacional atual e não pode ser a raiz de um volume. A connection string deve informar Host, Database e Username. Não ativar `Log Parameters`, `Include Error Detail` nem `Persist Security Info`; a validação rejeita essas opções por exporem dados sensíveis.

Um servidor PostgreSQL temporariamente indisponível deixa readiness em falha; configuração ausente ou inválida impede a aplicação de iniciar. Não imprimir connection strings em logs, issues ou comandos de diagnóstico compartilhados.

## Migrations explícitas

A migration inicial configura o schema do Identity. Isso não cria um administrador nem implementa autenticação. Toda alteração de schema passa por migration versionada e revisão.

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

Revise o SQL e o impacto de cada migration antes de aplicá-la. API e health checks nunca executam `Migrate` na inicialização. Em produção, migrations serão uma operação explícita do deploy com credencial apropriada, antes de disponibilizar a nova versão; não embutir uma senha de produção no comando ou no repositório.

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

O HTTP em loopback serve somente ao desenvolvimento desta fundação. Antes de usar dados pessoais remotamente, as fases de autenticação, autorização, HTTPS/Tailscale e hardening devem estar concluídas. Deploy systemd de produção ainda não faz parte da Fase 1.

## Testes de integração

User Secrets dos testes usam `Nexora.IntegrationTests.Development`:

```powershell
dotnet user-secrets set "ConnectionStrings:NexoraTests" "Host=127.0.0.1;Port=55432;Database=nexora_test;Username=nexora_test;Password=<senha-local-de-testes>" --project tests/Nexora.IntegrationTests
dotnet test Nexora.sln
```

O bootstrap já registra o segredo de testes. Para executar após o setup, basta iniciar o cluster e rodar `dotnet test Nexora.sln`. A alternativa é `NEXORA_TEST_CONNECTION_STRING`, específica da fixture, com a connection string da role de testes. Esta variável não é a conexão usada pela API.

Os testes HTTP e de configuração executam sem PostgreSQL. Quando nenhuma conexão de testes é fornecida, testes PostgreSQL aparecem como skipped com motivo explícito. Se a conexão foi fornecida, falhas de autenticação, banco indisponível, privilégio ausente ou migration incorreta resultam em testes falhos.

Cada fixture cria um banco `nexora_it_<guid>` e aplica migrations nesse banco. A limpeza é restrita ao identificador criado pela própria fixture e a seu prefixo; nunca apontar testes à produção. Bancos que sobrarem após uma interrupção devem ser examinados pelo administrador e removidos somente quando for confirmado que pertencem à execução interrompida, sem comandos de exclusão por wildcard.

Esses comandos descrevem a validação disponível; a documentação não representa um relatório de testes de uma execução específica.

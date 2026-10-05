# Nexora

Nuvem privada para fotos, vídeos e arquivos pessoais, construída com .NET 10, ASP.NET Core, EF Core e PostgreSQL 18. O destino de produção é um servidor Arch Linux, com systemd e acesso privado por Tailscale.

## Estado atual: Fase 2

A API inclui autenticação com Identity, access tokens opacos, refresh com rotação, sessões e revogação de dispositivos, além da fundação de configuração, PostgreSQL, migrations e health checks. A conta administrativa é criada somente por comando local. Upload, download e processamento de mídia pertencem às próximas fases.

Os projetos atuais são `Nexora.Api`, `Nexora.Application`, `Nexora.Domain`, `Nexora.Infrastructure` e `Nexora.IntegrationTests`. Worker, testes específicos de domínio e cliente MAUI serão introduzidos quando tiverem uma função real.

## Desenvolvimento

Requisitos: SDK definido em `global.json` e PostgreSQL 18. Na raiz do repositório:

```powershell
dotnet tool restore
dotnet restore Nexora.sln
dotnet build Nexora.sln
pwsh -File scripts/Initialize-LocalPostgres.ps1
```

No Windows, o script provisiona PostgreSQL nativo fora do Nextcloud, escutando em `127.0.0.1:55432`, e configura User Secrets da API e dos testes. Não instala serviço Windows. Depois da primeira configuração, use `pwsh -File scripts/LocalPostgres.ps1 -Action Start` quando precisar iniciar o cluster novamente.

Uma instância PostgreSQL existente também pode ser utilizada configurando `ConnectionStrings:Nexora` e um `Storage:RootPath` absoluto por User Secrets ou variáveis de ambiente. O [guia de desenvolvimento](docs/development.md) explica credenciais, operação do cluster e os comandos completos.

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet ef database update --project src/Nexora.Infrastructure --startup-project src/Nexora.Api
dotnet run --project src/Nexora.Api -- bootstrap-admin
dotnet run --project src/Nexora.Api
```

O bootstrap solicita email e senha interativamente, sem exibir a senha. Execute-o uma vez após aplicar migrations; a API não cria administrador automaticamente nem oferece registro público. Consulte [autenticação](docs/authentication.md) para login, dispositivos e recuperação local da conta.

A API de desenvolvimento escuta em `http://127.0.0.1:5100`:

| Rota | Comportamento |
| --- | --- |
| `/health/live` | Confirma que o processo responde, sem consultar o banco. |
| `/health/ready` | Confere conexão PostgreSQL e ausência de migrations pendentes; responde `503` se indisponível. |
| `/openapi/v1.json` | Documento OpenAPI disponível somente em Development. |
| `/api/auth/login`, `/api/auth/refresh`, `/api/auth/logout` | Login, rotação de refresh e encerramento da sessão. |
| `/api/devices` | Listagem autenticada; `DELETE /api/devices/{id}` revoga o dispositivo. |

Health checks retornam apenas um status sanitizado. A aplicação não aplica migrations automaticamente.

## Testes

```powershell
dotnet test Nexora.sln
```

Testes HTTP/configuração executam sem PostgreSQL. Testes de PostgreSQL exigem uma conexão separada configurada para a role de testes; sem ela são marcados como ignorados com motivo explícito. Conexão configurada e inválida resulta em falha. A fixture cria e remove somente bancos próprios `nexora_it_<id>`; não limpa o banco de desenvolvimento.

## Documentação

- [Estado e próximas etapas](docs/status-and-roadmap.md): objetivo do projeto, entregas concluídas, pendências por fase e critérios de aceite.
- [Arquitetura e decisões](docs/architecture.md): modelo, uploads, segurança, recuperação e roadmap.
- [Desenvolvimento](docs/development.md): configuração, PostgreSQL, migrations e validação local.
- [Autenticação](docs/authentication.md): conta única, contratos HTTP, tokens, revogação e chaves de proteção.

Segredos, binários PostgreSQL, dados de banco e arquivos pessoais ficam fora do repositório. Nexora é armazenamento; backups do banco e do conteúdo continuam necessários.

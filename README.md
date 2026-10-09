# Nexora

Nuvem privada para fotos, vídeos e arquivos pessoais, construída com .NET 10, ASP.NET Core, EF Core e PostgreSQL 18. O destino de produção é um servidor Arch Linux, com systemd e acesso privado por Tailscale.

## Estado atual: cliente Android e envios fora da tela na Fase 8

A API inclui autenticação com Identity, tokens opacos, sessões e revogação de dispositivos, além de configuração validada, PostgreSQL, migrations e health checks. Arquivos podem ser enviados em chunks retomáveis, finalizados pelo Worker, listados e baixados com HTTP Range. JPEG, PNG e WebP estáticos recebem metadados, orientação corrigida, thumbnail e preview autenticados. A biblioteca oferece timeline, filtros, favoritos, renomeação, lixeira e restauração. O Worker remove itens após a retenção e coleta somente Blobs sem referências. Blob/Asset mantêm armazenamento imutável, SHA-256 e deduplicação; falha de imagem preserva o original. A conta administrativa é criada somente por comando local.

As fases 1 a 6 foram verificadas no Windows. A Fase 7 acrescenta publicação para Linux x64, unidades systemd, instalação e ativação explícitas, configuração privada e procedimentos de backup/restauração. A execução real no Arch, o HTTPS privado e o exercício de recuperação permanecem pendentes. Consulte [instalação no Arch](docs/arch-deployment.md), [backup e restauração](docs/backup-and-restore.md) e o [registro de validação](docs/arch-validation.md).

O cliente MAUI Android inclui login, sessão protegida, biblioteca com cache offline, favoritos/lixeira, previews e fila de uploads retomáveis. Um envio iniciado pelo usuário pode continuar ao navegar ou colocar o app em segundo plano, por um serviço Android com notificação e pausa; após perda do processo, a retomada é manual. O backend oferece snapshot e alterações incrementais, incluindo exclusões definitivas. O núcleo mobile e seus testes fazem parte de `Nexora.sln`; o app Android tem uma solução separada, `apps/Nexora.Mobile.sln`, para manter o backend compilável sem workloads mobile. O aceite visual e dos fluxos no dispositivo permanece pendente.

Para gerar o APK com os SDKs Android/JDK existentes, execute `pwsh -File scripts/Build-Android.ps1`. Requisitos, instalação e roteiro de aceite estão em [cliente Android](docs/mobile.md). A sincronização e a recuperação após backup estão em [contrato de sincronização](docs/synchronization.md).

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

Execute o Worker em outro terminal, com a mesma conexão PostgreSQL e raiz de storage da API:

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
dotnet run --project src/Nexora.Worker
```

Em Development, o Worker compartilha o identificador de User Secrets da API. Sem o Worker, chunks podem ser recebidos, mas conclusões e processamento de imagens ficam pendentes. Consulte [uploads e Worker](docs/uploads.md) para o fluxo de arquivos e [imagens](docs/images.md) para metadados, limites e recuperação de derivados. A decodificação usa processo filho com watchdog; a validação nativa no Arch permanece prevista para a Fase 7.

O bootstrap solicita email e senha interativamente, sem exibir a senha. Execute-o uma vez após aplicar migrations; a API não cria administrador automaticamente nem oferece registro público. Consulte [autenticação](docs/authentication.md) para login, dispositivos e recuperação local da conta.

A API de desenvolvimento escuta em `http://127.0.0.1:5100`:

| Rota | Comportamento |
| --- | --- |
| `/health/live` | Confirma que o processo responde, sem consultar o banco. |
| `/health/ready` | Confere conexão PostgreSQL e ausência de migrations pendentes; responde `503` se indisponível. |
| `/openapi/v1.json` | Documento OpenAPI disponível somente em Development. |
| `/api/auth/login`, `/api/auth/refresh`, `/api/auth/logout` | Login, rotação de refresh e encerramento da sessão. |
| `/api/devices` | Listagem autenticada; `DELETE /api/devices/{id}` revoga o dispositivo. |
| `/api/uploads` | Criação autenticada de sessão; consulta, chunks, conclusão e cancelamento por ID. |
| `/api/assets` | Biblioteca paginada; detalhes e download em `/api/assets/{id}/content`. |
| `/api/assets?imagesOnly=true&sort=timeline` | Timeline por captura UTC confiável ou upload; filtro `isFavorite` opcional. |
| `/api/assets/{id}` | `PATCH` edita nome/favorito; `DELETE` move para a lixeira. |
| `/api/trash`, `/api/trash/{id}/restore` | Listagem da lixeira e restauração por `POST`. |
| `/api/assets/{id}/thumbnail`, `/api/assets/{id}/preview` | Derivados PNG autenticados por GET/HEAD, com Range e ETag, somente quando prontos. |
| `/api/storage` | Tamanhos lógicos, consumo físico, temporários, reservas e espaço do volume. |
| `/api/sync`, `/api/sync/changes` | Snapshot consistente e alterações duráveis por conta, com cursores protegidos. |

Health checks retornam apenas um status sanitizado. A aplicação não aplica migrations automaticamente.

## Testes

```powershell
dotnet test Nexora.sln
```

Testes unitários, HTTP/configuração e filesystem executam sem PostgreSQL. Testes de PostgreSQL exigem uma conexão separada configurada para a role de testes; sem ela são marcados como ignorados com motivo explícito. Conexão configurada e inválida resulta em falha. As fixtures usam bancos próprios `nexora_it_<id>` e diretórios temporários isolados; não limpam o banco nem os arquivos de desenvolvimento.

## Documentação

- [Estado e próximas etapas](docs/status-and-roadmap.md): objetivo do projeto, entregas concluídas, pendências por fase e critérios de aceite.
- [Arquitetura e decisões](docs/architecture.md): modelo, uploads, segurança, recuperação e roadmap.
- [Desenvolvimento](docs/development.md): configuração, PostgreSQL, migrations e validação local.
- [Autenticação](docs/authentication.md): conta única, contratos HTTP, tokens, revogação e chaves de proteção.
- [Armazenamento](docs/storage.md): identidade de conteúdo, contratos internos, publicação, deduplicação e recuperação por reenvio.
- [Uploads e Worker](docs/uploads.md): contratos HTTP, retomada, finalização durável, download e capacidade.
- [Imagens e derivados](docs/images.md): metadados, estados, configuração, processo nativo, publicação e recuperação.
- [Biblioteca e lixeira](docs/library.md): timeline, filtros, edição, restauração, retenção e coleta segura.
- [Cliente Android](docs/mobile.md): APK, sessão segura, cache offline, uploads e aceite no dispositivo.
- [Teste pelo Windows](docs/windows-mobile-test.md): conectar o celular ao servidor local por HTTPS privado, sem exigir Arch.
- [Uploads fora da tela](docs/android-background-uploads.md): serviço Android, notificação/pausa, restrições e testes manuais pendentes.
- [Validação mobile](docs/mobile-validation.md): testes locais, APK verificado e evidências que ainda dependem do dispositivo.
- [Sincronização](docs/synchronization.md): snapshot, journal, cursores, exclusões e recuperação após backup.
- [Instalação e operação no Arch](docs/arch-deployment.md): pacote Linux, PostgreSQL, systemd, Tailscale, atualização e diagnóstico.
- [Backup e restauração](docs/backup-and-restore.md): snapshot consistente e recuperação em destino isolado.
- [Validação no Arch](docs/arch-validation.md): evidências locais e verificações necessárias para concluir a Fase 7.

Segredos, binários PostgreSQL, dados de banco e arquivos pessoais ficam fora do repositório. Nexora é armazenamento; backups do banco e do conteúdo continuam necessários.

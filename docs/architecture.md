# Arquitetura do Nexora

Nexora é uma nuvem privada para fotos, vídeos e arquivos pessoais, executada em um único servidor Arch Linux com aproximadamente 500 GB disponíveis. O MVP terá uma conta administrativa, acesso privado por Tailscale e uma API ASP.NET Core. O cliente mobile será uma etapa posterior.

Este documento registra o desenho aprovado e sua implementação incremental. As fases 1 a 6 entregam fundação, autenticação, conteúdo imutável, uploads retomáveis, downloads, imagens e biblioteca com timeline, favoritos e lixeira. A base foi validada localmente no Windows. A Fase 7 já prepara publicação Linux, systemd e procedimentos operacionais; o aceite de produção depende da execução e da restauração real no Arch.

Consulte [estado e próximas etapas](status-and-roadmap.md) para o inventário das entregas atuais, as pendências de cada fase e seus critérios de aceite.

## Decisões e limites do MVP

- .NET 10, ASP.NET Core Web API, Entity Framework Core e PostgreSQL 18.
- Armazenamento de conteúdo no filesystem; PostgreSQL guarda metadados e estado de operações.
- Execução de produção com systemd, usuário de serviço sem privilégios administrativos e diretórios privados.
- Uma conta, vários dispositivos autenticados e autorização por proprietário em todas as operações de conteúdo.
- A API não inclui interface web nesta etapa.
- Thumbnails e previews inicialmente para JPEG, PNG e WebP. Outros arquivos continuam sendo conteúdo armazenável conforme a política de upload, sem processamento de imagem obrigatório.
- Servidor confiável, com acesso ao conteúdo em claro para processamento. Criptografia de ponta a ponta não faz parte do MVP.
- Não há compartilhamento público, reconhecimento facial, IA, edição, colaboração ou multi-tenancy complexo.

## Separação de responsabilidades

A árvore abaixo é a estrutura alvo. Os quatro projetos centrais, Worker e os dois projetos de testes já existem; o cliente mobile entra na Fase 8.

```text
Nexora.sln
src/
  Nexora.Api
  Nexora.Application
  Nexora.Domain
  Nexora.Infrastructure
  Nexora.Worker
tests/
  Nexora.UnitTests
  Nexora.IntegrationTests
apps/
  Nexora.Mobile
```

`Domain` contém entidades e invariantes sem dependência de ASP.NET Core, EF Core ou filesystem. `Application` coordena casos de uso e define os contratos necessários. `Infrastructure` implementa persistência, storage e integrações. `Api` concentra HTTP, autenticação, validação de entrada e composição de dependências. `Worker` executa processamento e manutenção em background, compartilhando os mesmos casos de uso e infraestrutura.

Não introduzir repositório genérico, mediator, event bus ou camadas adicionais sem uma necessidade concreta. Manter dependências direcionadas para Domain/Application e reutilizar recursos nativos do framework.

## Modelo atual e evolução

| Modelo | Responsabilidade |
| --- | --- |
| Usuário Identity | Conta administrativa e credenciais verificadas pelo ASP.NET Core Identity. |
| Device | Identidade do dispositivo, proprietário, plataforma, atividade e revogação. |
| AuthSession | Sessão autenticada vinculada ao usuário e dispositivo, com expiração e revogação. |
| RefreshToken | Hash do token, sessão, expiração, consumo e sucessor para rotação e detecção de reutilização; revogação controlada pela sessão. |
| Blob | Conteúdo imutável: SHA-256, tamanho, chave interna, MIME detectado e estado de publicação. |
| Asset | Referência do proprietário ao Blob: nome original, upload, favorito e lixeira. |
| BlobImage | Metadados compartilhados da imagem: estado, dimensões orientadas, captura local/UTC, geração, hashes/tamanhos dos derivados e reserva. |
| UploadSession | Sessão retomável, tamanho esperado, chunks, estado, atividade e resultado. |
| UploadChunk | Índice, tamanho, SHA-256 e localização interna de um chunk confirmado. |
| BackgroundJob | Trabalho durável de upload ou imagem, exatamente um alvo, tentativas, próxima execução e lease. |
| BackgroundJobAttempt | UUID de tentativa persistido antes de criar montagem/publicação ou derivados. |

Identity, Device, AuthSession e RefreshToken estão implementados na Fase 2. Blob e Asset foram introduzidos na Fase 3. A Fase 4 acrescentou uploads/jobs/tentativas. A Fase 5 introduz BlobImage e jobs `ProcessImage`, compartilhados por todos os Assets que apontam ao mesmo Blob.

Blob e Asset separados permitem deduplicar bytes sem acoplar nome, favorito ou exclusão à cópia física. SHA-256 é calculado pelo servidor e tem unicidade no Blob. Um Asset por `(OwnerId, BlobId)` evita duplicatas lógicas mesmo em conclusões concorrentes.

A importação de conteúdo idêntico retorna o Asset ativo existente, sem alterar seus metadados. Se estiver na lixeira, retorna `AssetInTrash`, exigindo restauração explícita; uploads HTTP registram essa decisão como falha terminal `asset_in_trash`. Concluir novamente a mesma UploadSession retorna a mesma operação ou seu resultado anterior.

Não manter `ReferenceCount` persistido inicialmente: consultar referências incluindo a lixeira evita divergência de contadores. BlobImage mantém dimensões após orientação e captura extraída do EXIF. Câmera e geolocalização ficam para um incremento posterior. Datas EXIF sem offset válido ficam como horário local de origem, sem conversão UTC inventada; timestamps operacionais são UTC. A timeline usa captura UTC confiável ou upload e mantém uma fronteira de processamento entre páginas, conforme [library.md](library.md).

## Autenticação e acesso

Criar o administrador por comando local `bootstrap-admin`, com email, senha e confirmação solicitados interativamente, sem eco da senha nem registro em logs. A criação é serializada no banco para preservar a conta única. `reset-admin-password` solicita nova senha e confirmação sem eco e revoga todas as sessões. Não há registro público ou reset de senha por HTTP.

O login verifica credenciais pelo Identity e emite access token opaco do mecanismo nativo de bearer authentication do ASP.NET Core, válido por cinco minutos. AuthSession tem duração absoluta de 30 dias. O refresh token é aleatório, com 256 bits de entropia, vinculado à sessão/dispositivo; guardar somente SHA-256, rotacionar a cada uso e revogar a família diante de reutilização. Os clientes serializam refresh: uma resposta perdida após consumo do token pode exigir novo login.

Cada requisição autenticada verifica no banco security stamp, sessão ativa, expiração, lockout e dispositivo pertencente ao usuário e não revogado. Falha do banco fecha o acesso com `503`. Revogar o dispositivo revoga suas sessões, bloqueando inclusive access tokens ainda não expirados. Device representa o cadastro vinculado à autenticação; nome, plataforma e modelo informados pelo cliente não constituem atestação de hardware. Atualizar LastSeen no máximo a cada cinco minutos.

As chaves de ASP.NET Core Data Protection persistem fora da instalação e do storage, com ApplicationName `Nexora.Auth`, DPAPI do usuário no Windows e certificado obrigatório em Linux fora de Development. Desenvolvimento Linux admite key ring sem criptografia de arquivo em diretório `0700`; isso não é a configuração de produção. Backup deve incluir chaves e material necessário para recuperá-las. Reinstalar a aplicação não deve gerar outro conjunto de chaves inadvertidamente.

Passphrases têm de 14 a 256 caracteres, sem regras artificiais de composição; email também é username. Identity bloqueia a conta por 15 minutos após cinco falhas. Login tem limite de cinco chamadas por IP/minuto e refresh de 30 por IP/minuto. Os [contratos e fluxos implementados](authentication.md) detalham login, refresh e revogação.

Os endpoints de Assets, uploads, conteúdo e lixeira verificam o proprietário. UUID, StorageKey ou SHA-256 não concedem acesso. Consultas internas por hash resolvem deduplicação sem expor conteúdo de outra conta.

Originais autenticados usam streaming, nomes de resposta tratados pelo framework, `application/octet-stream`, attachment e `nosniff`. Derivados confirmados e verificados são PNG reencodificados, servidos inline com CSP restrita e sem cache. MIME e extensão informados pelo cliente não são prova de conteúdo; arquivos arbitrários não devem ser executados nem renderizados como HTML no domínio da API.

## Contratos HTTP atuais e previstos

Os endpoints das fases 2, 4, 5 e 6 estão implementados. Recursos de usuário sempre exigem autenticação e autorização; login/refresh têm validação e limitação próprias.

| Fase | Contrato | Responsabilidade |
| --- | --- | --- |
| 2 | `POST /api/auth/login`, `POST /api/auth/refresh`, `POST /api/auth/logout` | Abrir, renovar e encerrar a sessão. |
| 2 | `GET /api/devices`, `DELETE /api/devices/{id}` | Listar dispositivos e revogar o selecionado. |
| 4 | `POST /api/uploads`, `GET /api/uploads/{id}`, `DELETE /api/uploads/{id}` | Criar, consultar e cancelar uma sessão de upload. |
| 4 | `PUT /api/uploads/{id}/chunks/{number}`, `POST /api/uploads/{id}/complete` | Receber chunk idempotente e solicitar conclusão em background. |
| 4 | `GET /api/assets`, `GET /api/assets/{id}`, `GET`/`HEAD /api/assets/{id}/content` | Listagem básica, metadados e download com HTTP Range/ETag. |
| 4 | `GET /api/storage` | Espaço físico, tamanho lógico e reservas. |
| 5 | `GET`/`HEAD /api/assets/{id}/thumbnail`, `/api/assets/{id}/preview` | Servir PNGs autorizados, verificados por hash/tamanho, com Range/ETag. |
| 6 | `PATCH /api/assets/{id}` | Renomear e/ou alterar favorito, preservando identidade e upload. |
| 6 | `DELETE /api/assets/{id}`, `GET /api/trash`, `POST /api/trash/{id}/restore` | Soft delete, listagem da lixeira e restauração explícita. |

Usar Problem Details para erros e DTOs sem caminhos físicos ou entidades EF expostas. A conclusão do upload retorna `202` durante processamento e `200` ao repetir uma conclusão terminal; consultar a sessão informa o resultado ou a falha. Downloads completos/parciais trabalham com Streams e respeitam Range válido, inclusive `206` e `416` quando aplicável. Os [contratos da Fase 4](uploads.md) descrevem índices, estados, erros, paginação e capacidade.

## Upload e publicação

A interface HTTP cria a sessão, recebe chunks numerados a partir de zero, informa o progresso e solicita a conclusão. Upload simples, quando introduzido, deverá usar as mesmas invariantes de publicação e deduplicação.

1. Criar a UploadSession autenticada, validar tamanho e reservar capacidade. O servidor determina o tamanho dos chunks e a quantidade esperada.
2. Receber cada chunk binário em streaming limitado, escrevendo uma tentativa temporária independente. Validar índice, quantidade de bytes e hash antes de confirmar o chunk no banco; `X-Chunk-SHA256` é uma expectativa opcional do cliente.
3. Um reenvio com mesmo índice e conteúdo é idempotente. Conteúdo diferente para um índice confirmado é conflito. O progresso enumera somente chunks confirmados.
4. A conclusão verifica todos os chunks e muda a sessão de `Open` para `Finalizing`, gravando um job na mesma transação. Retornar `202 Accepted`, sem montar um arquivo grande dentro da requisição HTTP.
5. O Worker limpa tentativas anteriores, monta o arquivo em streaming e valida tamanho/hash final. O UUID da tentativa já está persistido antes da escrita. Registrar a montagem antes de remover os chunks torna essas remoções recuperáveis e limita as cópias completas a duas no fluxo planejado.
6. Resolver a deduplicação: um Blob `Ready` só é reutilizado após verificar seu conteúdo; `Staging` permite retomar publicação da mesma geração; `Deleting` impede nova referência. Para conteúdo novo, persistir intenção e chave antes de publicar. Só depois da publicação, confirmar Blob, Asset, upload e job em uma transação, exigindo lease válido. Para JPEG/PNG/WebP, registrar BlobImage e job de imagem nessa mesma transação, quando ausentes.
7. Remover montagem e tentativas terminais antes de liberar a reserva. A limpeza é idempotente e pode ser repetida após uma interrupção; os registros de referências permanecem enquanto uma exclusão/sincronização estiver pendente.

Estados de UploadSession: `Open`, `Finalizing`, `Completed`, `Cancelled`, `Expired` e `Failed`. Estados de Blob: `Staging`, `Ready` e `Deleting`. Transições são verificadas no banco; API e Worker executam como processos separados e coordenam as operações por locks no PostgreSQL.

Reservas globais limitam espaço de uploads e processamento de imagens. A montagem considera chunks e arquivo final, chegando a duas vezes o tamanho declarado; cada imagem reserva espaço para ambos os derivados. Um limite de sessões concorrentes não garante que todas possam receber arquivos máximos ao mesmo tempo: o orçamento global decide a admissão.

Defaults atuais de upload e parâmetros das fases seguintes:

| Limite | Valor inicial |
| --- | --- |
| Chunk | 8 MiB. |
| Arquivo | 20 GiB. |
| Orçamento global de temporários/reservas | 50 GiB. |
| Margem mínima de espaço livre | 50 GiB. |
| Sessões simultâneas por usuário | 2, condicionadas à capacidade global. |
| Expiração de upload inativo | 7 dias sem atividade. |
| Retenção na lixeira | 30 dias, configurável em Library. |
| Concorrência de processamento | Uma montagem e uma imagem por Worker, em rotinas separadas. |

Reservar `2 × tamanho declarado` na criação da sessão, serializando a admissão no banco e conferindo espaço real. A contagem conservadora exige reservas + temporários físicos + nova reserva dentro do orçamento, além de espaço livre para margem + todas as reservas. O orçamento de 50 GiB admite somente um upload de 20 GiB, mesmo que o teto de sessões seja dois. O corpo não pode exceder o tamanho esperado; outros usos do volume afetam o espaço livre. A referência de 500 GB não constitui quota fixa.

## Storage e recuperação

`Storage:RootPath` é um caminho absoluto definido pelo operador. Em produção, a raiz prevista é `/srv/nexora`, com diretórios `blobs`, `thumbnails`, `previews` e `temp`. O nome original nunca é usado como caminho físico.

Chaves internas são opacas e imutáveis, com identificador distinto por geração, por exemplo `blobs/ab/cd/<blob-id>`. SHA-256 identifica conteúdo no banco; uma chave física por geração impede uma limpeza antiga de remover um novo upload do mesmo hash.

`IBlobStorage` recebe/devolve Streams e oferece publicação imutável, leitura, consulta de tamanho e exclusão idempotente. `ITemporaryStorage` separa temporários, limite real de bytes e hashing; contratos específicos acrescentam tentativas rastreadas para o Worker. `IDerivativeStorage` publica PNGs por geração/tipo e limpa tentativas preservando a geração confirmada. HTTP Range é tratado na borda de download. Rename fica interno ao adaptador local, sem virar requisito da interface; um futuro storage S3 poderá implementar publicação por PUT/multipart sem reescrever os casos de uso.

PostgreSQL e filesystem não compartilham uma transação. A Fase 3 persiste a intenção `Staging` antes de publicar sem sobrescrita, depois confirma `Ready` e resolve o Asset em outra transação. Advisory locks por hash e locks de linha coordenam conclusões concorrentes. Um reenvio pode verificar/publicar a mesma geração e concluir uma operação interrompida. Blob Ready ausente ou corrompido gera falha de integridade.

Os adaptadores locais mantêm `temp` e `blobs` no mesmo filesystem, fazem flush do arquivo e publicam sem sobrescrita. No Linux, usam `renameat2(RENAME_NOREPLACE)` e sincronizam diretórios com `fsync`, recusando movimento entre mounts. Caminhos não canônicos, symlinks e junctions são rejeitados; diretórios e arquivos recebem permissões restritas. Essa responsabilidade fica no adaptador, sem contaminar a interface de storage.

A Fase 4 recupera jobs com lease expirado, verifica montagens persistidas e retoma publicação da mesma geração, ou registra falha explícita. Manutenção limpa temporários terminais antes de liberar reservas e coleta temporários internos antigos, sem referência e sem escritor ativo. A Fase 6 acrescenta coleta de Blobs sem Assets por intenção Deleting, incluindo os derivados; não implementa uma varredura de reparação de todos os originais. Execução nativa Linux e persistência após reinício/queda de energia no Arch continuam pendentes na operação. Os guias de [armazenamento](storage.md), [uploads](uploads.md) e [biblioteca](library.md) detalham os limites de recuperação.

Reconciliação recupera operações interrompidas; não recria bytes perdidos por falha física. Backup e teste de restauração continuam necessários para proteger o conteúdo diante de falhas de hardware e perda de dados.

## Processamento e exclusão

BackgroundJob é a fonte durável de trabalho. Finalizing e job são gravados na mesma transação. O Worker reclama jobs com `FOR UPDATE SKIP LOCKED`, utiliza lease renovável e só confirma resultados com token válido. Cada tentativa tem identidade persistida antes da escrita; retry remove tentativas anteriores sem atingir escritores ativos. O padrão é cinco tentativas para montagem e três para imagem, com falhas terminais consultáveis. Channel poderá ser usado futuramente para sinalização, sem substituir a persistência.

A Fase 5 usa SkiaSharp 4.153.1 e MetadataExtractor 2.9.3 para JPEG, PNG e WebP estáticos. Orientação é aplicada ao renderizar thumbnail de até 256 px e preview de até 1280 px, sem ampliar imagens menores. Datas EXIF sem offset confiável permanecem locais; a extração não inventa UTC nem inclui câmera/geolocalização. Os [contratos de imagem](images.md) detalham campos, estados e limites.

O codec roda em processo filho do Worker, sem inicializar serviços/banco e sem receber ambiente de segredos, usando um protocolo limitado por pipes. O filho mantém as credenciais do Worker: essa separação não é sandbox de SO. Limites de entrada/pixels/bitmaps, heap gerenciado, watchdog de 30 segundos e monitoramento de working set reduzem recursos consumidos. A amostragem de memória não impõe um teto rígido nativo; isolamento e `MemoryMax` serão preparados e validados na operação Arch.

Cada imagem reserva duas vezes o máximo de derivado, inicialmente 16 MiB, sob o mesmo lock/orçamento de capacidade de uploads. Retries reutilizam reserva e limpam tentativas antigas; confirmar os dois PNGs e metadados exige lease válido e Blob `Ready`. Reservas permanecem até cleanup terminal. Backfill idempotente permite processar originais compatíveis anteriores à migration.

A falha do processamento não remove nem invalida um original publicado. Imagens animadas e acima dos limites ficam disponíveis como originais, com falha consultável. A API informa estado e serve somente PNGs `Ready`, sem carregar o original. Não há retry manual HTTP de imagem nesta entrega. Vídeos e FFmpeg ficam para depois; HTTP Range para originais já está disponível, sem transcodificação.

Na Fase 6, soft delete altera `Asset.DeletedAt` e mantém o Blob. Lixeira conserva bytes e referências. Restauração e purge disputam locks de Blob/Asset; uma exclusão definitiva concluída não pode ser restaurada pela API.

Após a retenção configurada, o Worker remove o Asset e destaca resultados de uploads concluídos com `ResultPurgedAt`, preservando sucesso/histórico. A coleta adquire o mesmo advisory de hash da criação de referências, protege contra processamento ativo de imagem, trava o Blob, verifica ausência de todos os Assets e confirma `Deleting`. Depois exclui e sincroniza originais, derivados e tentativas; somente então remove a linha/reservas. Erros mantêm a intenção para retry. Referências novas recusam Deleting; reuploads após coleta usam outro UUID físico. Intenções Staging são preservadas. Os contratos estão em [library.md](library.md).

## Segurança, operação e backup

- Segredos fora do Git; User Secrets somente no desenvolvimento e arquivo protegido ou credenciais do serviço na produção.
- Caminhos físicos derivados exclusivamente de chaves internas; rejeitar traversal e nunca usar paths fornecidos pelo cliente.
- Streaming limitado, timeouts, cancelamento e controle de capacidade antes e durante a escrita.
- Validação de MIME por conteúdo e limites de dimensões/pixels durante a decodificação de imagens.
- Rate limiting e autorização adicionados junto aos endpoints que precisam deles, antes de disponibilizar conteúdo pessoal.
- Na operação prevista, Tailscale Serve terminará HTTPS e encaminhará para a API em loopback; Funnel permanecerá desativado e grants restringirão o acesso privado na tailnet.
- Logs estruturados com IDs técnicos e resultado, sem tokens, connection strings, conteúdo de arquivos, nomes pessoais ou coordenadas.
- Observabilidade de espaço, reservas, jobs e uploads falhos; diferenciar tamanho lógico de Assets de bytes físicos de Blobs, derivados, lixeira e temporários.

Nexora oferece armazenamento; uma única cópia no servidor não é backup. Os scripts de operação pausam API e Worker, conferem a parada dos processos filhos e registram PostgreSQL, storage, configuração e chaves em um snapshot manual. A restauração exige banco novo e destino isolado, sem reaplicar automaticamente configurações de produção. Preserve uma cópia independente do servidor e o código correspondente às migrations. O [procedimento de recuperação](backup-and-restore.md) precisa ser exercitado no Arch; integração com restic/borg permanece futura.

## Testes implementados e prioridades seguintes

Na autenticação, verificar expiração de access/session, rotação concorrente, reutilização de refresh, revogação imediata de sessão/dispositivo e preservação de tokens ao reiniciar com as mesmas chaves de Data Protection.

A Fase 3 verifica regras de Blob/Asset, chaves canônicas, hashing e cancelamento, streams sem seek, publicação imutável, limites de bytes, traversal/junctions, deduplicação entre proprietários e concorrente, conflito na lixeira, corrupção e retomada de Staging por reenvio. Testes de filesystem e banco usam fixtures isoladas.

A Fase 4 acrescenta testes de autorização, limites, chunks repetidos/conflitantes/fora de ordem, hash, deduplicação, conclusão repetida, leases, expiração, falhas nas fronteiras filesystem/banco, limpeza/capacidade e HTTP Range. Testes usam bancos e storage isolados. Os resultados da validação estão no [roadmap](status-and-roadmap.md).

A Fase 5 acrescenta verificação de orientação/captura, metadados sem UTC inventado, PNGs autenticados, limites/protocolo e preservação do original em falha. A Fase 6 verifica filtros/paginação, favoritos, lixeira/restauração, GC contra deduplicação, purge contra restauração, histórico purgado, falha de exclusão e conexão do processor perdida. Reinícios reais, caminhos nativos Linux/Skia e restauração permanecem na operação.

Na Fase 1, verificar configuração, health checks, acesso PostgreSQL e migrations em bancos de integração isolados. Não simular funcionalidades que ainda não existem apenas para gerar cobertura.

## Roadmap incremental

| Fase | Entrega |
| --- | --- |
| 0 — Arquitetura | Decisões de stack, segurança, modelo e fluxos; registrada neste documento. |
| 1 — Fundação | Solution, configuração, PostgreSQL, persistência Identity, migrations, health checks e testes de infraestrutura. |
| 2 — Autenticação | Bootstrap do administrador, login, tokens, rotação/revogação e dispositivos. |
| 3 — Modelos e storage | Blob/Asset, abstração local, hashing e deduplicação verificados por testes, sem endpoints de arquivos. |
| 4 — API de arquivos | Upload retomável, download/Range, uso de espaço, Worker durável, sessões/chunks, reservas, recuperação e expiração. |
| 5 — Imagens | SkiaSharp/MetadataExtractor, metadados iniciais e thumbnails/previews JPEG/PNG/WebP usando o Worker existente. |
| 6 — Timeline e lixeira | Listagem/timeline, favoritos, soft delete, restore, purge e GC seguro. |
| 7 — Operação | Deploy systemd/Arch, HTTPS/Tailscale, revisão de segurança, observabilidade e procedimentos de backup/restauração. |
| 8 — Mobile | Contrato de sincronização, registro de mudanças e cliente MAUI Android; iOS posteriormente, respeitando as restrições de background de cada plataforma. |

Segurança acompanha cada funcionalidade desde sua criação. O aceite local das fases 1 a 6 está verificado. A Fase 7 tem ferramentas de preparação e procedimentos documentados em [arch-deployment.md](arch-deployment.md); seu aceite exige as evidências de execução real descritas em [arch-validation.md](arch-validation.md).

## Referências técnicas

- [Streaming e segurança de uploads no ASP.NET Core 10](https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-10.0).
- [Tokens nativos do Identity no ASP.NET Core 10](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-api-authorization?view=aspnetcore-10.0).
- [Locks e concorrência no PostgreSQL](https://www.postgresql.org/docs/current/explicit-locking.html) e [SKIP LOCKED para consumidores de filas](https://www.postgresql.org/docs/current/sql-select.html).
- [Channels no .NET](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels).
- [SkiaSharp](https://github.com/mono/SkiaSharp), [MetadataExtractor](https://github.com/drewnoakes/metadata-extractor-dotnet) e [Tailscale Serve](https://tailscale.com/docs/reference/tailscale-cli/serve).
- [Atomicidade de rename no Linux](https://man7.org/linux/man-pages/man2/rename.2.html) e [limites de persistência de fsync](https://man7.org/linux/man-pages/man2/fsync.2.html).

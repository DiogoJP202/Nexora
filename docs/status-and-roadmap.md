# Nexora — estado atual e próximas etapas

Atualizado em **9 de outubro de 2026**. Código de referência da Fase 6: `e27ca91`; preparação da Fase 7: `fb7d9b6`; incremento Android/sincronização da Fase 8: `089c593`; envio fora da tela: `451925b`. As fases 1 a 6 foram verificadas localmente no Windows com PostgreSQL nativo. A operação no Arch e o aceite no dispositivo continuam pendentes.

O Nexora já tem fundação, autenticação, armazenamento com deduplicação, arquivos/imagens por API e biblioteca com timeline, favoritos, renomeação, lixeira, restauração e coleta segura. A Fase 7 tem ferramentas Linux e procedimentos disponíveis, com execução no Arch e restauração real pendentes. Na Fase 8, o cliente Android usa registro durável de mudanças, cache de metadados e fila de uploads com retomada explícita. O incremento `0.1.1` permite continuar envios iniciados pelo usuário fora da tela, com serviço Android e notificação de pausa; seu aceite no aparelho permanece pendente.

## 1. O que o projeto pretende ser

Uma nuvem pessoal hospedada por você, para guardar e acessar fotos, vídeos e arquivos. O servidor manterá os originais, os metadados e, para imagens compatíveis, versões menores para consulta. Uma conta administrativa poderá acessar a biblioteca por diferentes dispositivos, com sessões revogáveis.

O destino de produção será Arch Linux, com PostgreSQL local, armazenamento em disco, API e Worker executados por systemd e acesso privado por Tailscale com HTTPS. Os aproximadamente 500 GB serão uma referência de planejamento; a admissão de arquivos dependerá do espaço livre real.

O desenvolvimento começou pela API e agora inclui um cliente MAUI Android. iOS e interface web continuam futuros, sem data definida neste roadmap.

### Decisões já tomadas

- Monólito modular em .NET 10: API, Application, Domain e Infrastructure; Worker separado para processamento e manutenção.
- Uma conta administrativa, sem cadastro público; biblioteca plana no MVP.
- PostgreSQL para metadados e estados; filesystem para originais, derivados e temporários.
- Blob representa os bytes imutáveis; Asset representa o item da biblioteca e suas permissões, nome e lixeira.
- Conteúdo idêntico reutiliza o Asset existente, preservando seus metadados. Um item na lixeira exige restauração explícita pelas rotas da Fase 6.
- O servidor será confiável e poderá ler os originais para processamento.
- Thumbnails e previews processam JPEG, PNG e WebP estáticos. Outros formatos, animações e imagens acima dos limites continuam disponíveis como originais.
- Segurança, testes e recuperação acompanharão cada fase; operação em produção será validada na Fase 7.

Os detalhes técnicos e as razões dessas decisões estão em [architecture.md](architecture.md).

## 2. Situação das etapas

| Fase | Situação | Entrega principal |
| --- | --- | --- |
| 0 — Arquitetura | Concluída | Modelo, contratos, decisões e sequência de desenvolvimento documentados. |
| 1 — Fundação | Concluída | Solution, configuração, PostgreSQL, migrations Identity e health checks. |
| 2 — Identidade | Concluída | Administração local da conta, login, sessões, refresh e dispositivos. |
| 3 — Armazenamento | Concluída | Blob/Asset, adaptador local, streaming, SHA-256 e deduplicação interna. |
| 4 — Arquivos utilizáveis | Concluída | Upload retomável, Worker durável, biblioteca básica, download e capacidade. |
| 5 — Imagens | Concluída localmente | Metadados, orientação, thumbnails/previews PNG e processamento com limites. |
| 6 — Biblioteca | Concluída localmente | Timeline, favoritos, renomeação, lixeira, restauração e limpeza definitiva segura. |
| 7 — Operação | Em preparação | Publicação Linux, scripts e guias disponíveis; instalação, HTTPS e restauração real no Arch pendentes. |
| 8 — Mobile | Implementação inicial | Journal/sincronização, núcleo testável e app MAUI Android; aceite no dispositivo e iOS pendentes. |

Não há estimativas de datas ou percentual de conclusão. As fases têm tamanhos diferentes; o avanço será registrado pelas entregas verificadas.

## 3. O que já está implementado

### Fase 1 — Fundação

- [x] Solution com `Nexora.Api`, `Nexora.Application`, `Nexora.Domain`, `Nexora.Infrastructure` e `Nexora.IntegrationTests`.
- [x] SDK `10.0.400` com atualização de patch permitida, nullable habilitado e versões de dependências centralizadas com arquivos de lock.
- [x] Configuração nativa, User Secrets em desenvolvimento e provider de variáveis de ambiente com prefixo `NEXORA_`.
- [x] Validação de connection string e do caminho absoluto de storage na inicialização.
- [x] DbContext PostgreSQL, tabelas Identity e migration inicial versionada.
- [x] Scripts Windows de provisionamento e controle de PostgreSQL nativo, com roles/bancos de desenvolvimento e testes separados e credenciais fora do repositório.
- [x] `/health/live` independente do banco e `/health/ready` verificando conectividade e migrations pendentes.
- [x] ProblemDetails com código estável e identificador de rastreamento; logs JSON com timestamps UTC.
- [x] OpenAPI disponível em Development e migrations aplicadas explicitamente.
- [x] Testes de configuração, saúde e migrations em bancos de integração isolados.

### Fase 2 — Identidade

- [x] `bootstrap-admin`: criação local do administrador, com senha sem eco e recusa de segundo bootstrap.
- [x] `reset-admin-password`: recuperação local da senha e revogação das sessões anteriores.
- [x] ASP.NET Core Identity para hashing, validação de credenciais e lockout após cinco falhas por 15 minutos.
- [x] Entidades Device, AuthSession e RefreshToken, seus índices, vínculos de proprietário e migration.
- [x] Login com cadastro de dispositivo ou reutilização de dispositivo ativo da própria conta.
- [x] Access token opaco nativo com validade de até cinco minutos e sessão com duração absoluta de 30 dias.
- [x] Refresh aleatório de 256 bits, persistido somente como SHA-256, com rotação transacional e revogação da sessão diante de replay.
- [x] Logout, listagem de dispositivos e revogação de dispositivo e suas sessões.
- [x] Validação de usuário, sessão, dispositivo, expiração e security stamp no banco a cada requisição autenticada; falha de banco impede acesso.
- [x] Data Protection com diretório persistente, ACL restrita e DPAPI no Windows; suporte a certificado RSA e diretório `0700` no Linux.
- [x] Rate limiting para login e refresh, respostas sem cache e exigência de HTTPS nas rotas `/api/*` fora de Development.
- [x] Confiança em Forwarded Headers limitada a proxies loopback.
- [x] Testes de expiração, rotação/replay concorrente, revogação, isolamento entre proprietários, lockout/reset e persistência de chaves.

O suporte a configurações de produção está no código. A Fase 7 disponibiliza unidades systemd, configuração Tailscale e procedimentos de backup/restauração; sua execução e validação no servidor permanecem pendentes.

### Fase 3 — Armazenamento e identidade dos arquivos

- [x] Blob e Asset com estados, regras de domínio, vínculos, índices e migration `20261006114248_ContentBlobsAssets`.
- [x] SHA-256 único em Blob e `(OwnerId, BlobId)` único em Asset, incluindo itens na lixeira.
- [x] `IBlobStorage` para publicação imutável, leitura por stream, informações e exclusão idempotente; temporários separados em `ITemporaryStorage`.
- [x] Adaptadores locais com chaves internas por geração, diretórios privados, confinamento e rejeição de traversal, symlinks/junctions.
- [x] Streaming com buffers limitados, tamanho real, cancelamento e SHA-256 calculado no servidor; assinatura preliminar de JPEG, PNG e WebP, sem decodificação.
- [x] Publicação sem sobrescrita e sincronização de gravações; intenção Staging persistida antes do filesystem, confirmação Ready e Asset em transação posterior.
- [x] Deduplicação entre proprietários e concorrente, preservando nome, upload e favorito do Asset existente; lixeira retorna conflito identificável.
- [x] Recuperação por reenvio após falha de publicação, preservando a mesma geração; Blob Ready ausente/corrompido é recusado.
- [x] `Nexora.UnitTests` e testes de integração de storage/persistência, incluindo falhas de leitura, limpeza e publicação.

**Aceite verificado localmente:** o adaptador publica, lê e exclui; duplicatas preservam identidade; conflito na lixeira e paths maliciosos são tratados; concorrência e retomada de Staging estão testadas no Windows com PostgreSQL nativo. Não há novos endpoints de arquivos nesta fase.

Os [contratos de armazenamento](storage.md) detalham essa fundação. A Fase 4 acrescenta jobs duráveis, reservas e recuperação de operações de upload. Execução nativa Linux e persistência após reinício no filesystem Arch continuam pendentes; testes com falha injetada não equivalem a simular queda de energia.

### Fase 4 — Arquivos utilizáveis por API

- [x] UploadSession, UploadChunk, BackgroundJob e BackgroundJobAttempt, índices e migration `20261008112515_UploadsDurableJobs`.
- [x] `Nexora.Worker` separado, fila PostgreSQL, lease renovável, tentativas limitadas e falhas consultáveis.
- [x] Criação, consulta, cancelamento, chunks binários numerados a partir de zero e conclusão assíncrona de uploads.
- [x] Tamanho/hash real de cada chunk, reenvio idêntico e conflito para conteúdo diferente no mesmo índice.
- [x] Montagem em streaming, verificação do hash final e persistência da montagem antes de remover chunks.
- [x] Confirmação de Blob/Asset/upload/job em transação com lease válido; conclusão repetida preserva operação/resultado.
- [x] Tentativas físicas rastreadas por UUID persistido antes da escrita e limpeza exclusiva antes de novo retry.
- [x] Reservas conservadoras, limite de arquivo/chunk, timeouts, expiração de sessão aberta e limpeza terminal antes de liberar capacidade.
- [x] Coleta de temporários internos antigos sem referência, excluindo escritores ativos; não coleta Blobs.
- [x] Biblioteca ativa com cursor, detalhes, download autenticado por GET/HEAD com HTTP Range/ETag e `/api/storage`.
- [x] Limite HTTP de chunk por rota; corpos JSON continuam limitados a 16 KiB.

**Aceite verificado localmente:** restore com dependências travadas, build Release sem avisos/erros, migration aplicada ao banco de desenvolvimento e 129 testes aprovados, sem falhas ou ignorados. Os testes incluem retomada após reinício da API, concorrência, publicação interrompida, commit ambíguo, reservas, autorização, Range e renovação real de lease durante processamento demorado. API e Worker também iniciaram pela CLI; live/readiness retornaram `200`, OpenAPI expôs os contratos e rotas privadas recusaram acesso sem autenticação. Os contratos e o fluxo de execução estão em [uploads.md](uploads.md). Testes nativos no Arch, reinício de servidor e queda de energia continuam pendentes na Fase 7. Favoritos, edição e lixeira HTTP pertencem à Fase 6.

### Fase 5 — Fotos, metadados e derivados

- [x] BlobImage compartilhada por Blob, estados, captura local/UTC, dimensões orientadas, geração/hashes/tamanhos e migration `20261008122015_ImageMetadataAndDerivatives`.
- [x] Jobs `ProcessImage` com alvo exclusivo, unicidade por Blob, leases, tentativas persistidas e fences de conclusão.
- [x] Enfileiramento na mesma transação de conclusão do original; backfill idempotente de Blobs antigos compatíveis sem registro de imagem.
- [x] SkiaSharp 4.153.1 e MetadataExtractor 2.9.3 com versões centralizadas e dependências nativas Linux incluídas.
- [x] JPEG, PNG e WebP estáticos; orientação aplicada e PNGs de até 256/1280 px sem ampliar imagens menores.
- [x] EXIF DateTimeOriginal/frações/offset original; preservação de horário local sem UTC inventado.
- [x] Processo filho sem Host/banco/ambiente de segredos, protocolo por pipes limitado, watchdog de tempo e working set, limite próprio de heap gerenciado.
- [x] Limites de entrada, dimensões, pixels, bitmaps e derivados; animação, corrupção e limites excedidos preservam o original.
- [x] Publicação imutável por tentativa, validação do envelope PNG/CRCs, hashes/tamanhos e confirmação dos dois derivados em transação.
- [x] Reservas de imagens no orçamento global e em `/api/storage`, mantidas até cleanup terminal; retries removem tentativas anteriores com acesso exclusivo.
- [x] `image` opcional nos snapshots e endpoints GET/HEAD de thumbnail/preview, autorizados, verificados por hash/tamanho e com Range/ETag.

**Aceite verificado localmente:** restore com dependências travadas, build Release sem avisos/erros, migration aplicada ao banco de desenvolvimento e 189 testes aprovados (50 unitários e 139 de integração), sem falhas ou ignorados. A suíte verifica JPEG/PNG/WebP no processo filho, EXIF sem fuso e com offset persistido no banco e exposto pela API, timeout com preservação do original, renovação real de lease e reversão/reaplicação da migration em banco isolado. API e Worker iniciaram pela CLI; live/readiness responderam `200`, OpenAPI incluiu os derivados e suas rotas recusaram acesso sem autenticação. Os [contratos de imagem](images.md) detalham campos, configuração e recuperação. Execução nativa Skia/Linux, limites de serviço, reinícios reais e restauração no Arch continuam pendentes na Fase 7. O processo filho mantém as permissões do Worker e a medição de memória é amostrada: esta entrega não equivale a sandbox de SO ou teto rígido de memória nativa.

### Fase 6 — Biblioteca, timeline e lixeira

- [x] Timeline com captura UTC confiável ou fallback para upload, filtros de imagem/favorito e cursor vinculado à conta/consulta.
- [x] Watermark para uploads novos e EXIF processado entre páginas, com desempate por UUID.
- [x] `PATCH /api/assets/{id}` para nome/favorito, com validação estrita de JSON e preservação da identidade.
- [x] Exclusão lógica idempotente, listagem da lixeira e restauração explícita; conteúdo/derivados ocultos na lixeira.
- [x] Retenção configurável de 30 dias, purge em lotes e referência de outras contas/lixeira preservada.
- [x] Coleta por intenção `Deleting`, sincronização física antes do cascade e retry após falha de exclusão.
- [x] Locks coordenando restauração/purge e deduplicação/coleta; conexão compartilhada protegendo processamento ativo de imagens, inclusive após lease expirar.
- [x] Histórico de uploads concluídos com `resultPurgedAt`, sem recriar resultado após purge.
- [x] Migration `20261008125535_LibraryTrashAndPurge`, incluindo recusa de reversão quando resultados já foram purgados.

**Aceite verificado localmente:** restore travado, build Release sem avisos/erros e 229 testes aprovados (60 unitários, 169 de integração), sem falhas/ignorados. Testes incluem duas contas, payloads inválidos, captura/fallback e EXIF tardio, paginação, retenção exata, corridas com locks PostgreSQL reais, exclusão interrompida, nova geração de reupload, processamento com lease expirado e cancelamento por perda da conexão do guard. Migration aplicada ao banco local de desenvolvimento. API e Worker iniciaram pela CLI; live/readiness retornaram 200, OpenAPI documentou edição/filtros/lixeira e rotas privadas retornaram 401 sem autenticação. Contratos e limites estão em [library.md](library.md); reinícios reais, restauração e filesystem nativo no Arch permanecem na Fase 7. Paginação limita uploads/processamento novos, mas favoritos/exclusões/restaurações refletem mudanças atuais.

### Fase 8 — Sincronização e primeiro cliente Android

- [x] Journal transacional por proprietário, com sequência em ordem de commit, projeções completas e tombstones de purge.
- [x] Snapshot paginado congelado e feed incremental autenticado; cursores vinculados a proprietário, propósito e época do banco.
- [x] Backfill dos Assets existentes e migration `20261008183547_DurableAssetSync`.
- [x] UUID opcional `clientRequestId` na criação de upload, unicidade por conta, conflito de conteúdo e recuperação da resposta perdida; migration `20261008184113_ClientUploadRequests`.
- [x] `Nexora.Mobile.Core` independente do Android: cliente HTTPS, refresh serializado, cache atômico e fila persistente com cópia/hash privados.
- [x] `Nexora.Mobile` MAUI Android: login, armazenamento seguro, filtros/busca, favoritos/lixeira, detalhe, PNG e original por ação explícita.
- [x] Fila manual de upload, progresso, pausa e retomada a partir dos chunks confirmados no servidor.
- [x] Serviço Android `dataSync` para um envio explícito, notificação genérica e pausa; vida independente da página e sem reinício automático.
- [x] Leituras concorrentes da biblioteca durante o envio, troca de escopo exclusiva e finalização acompanhada por tempo limitado.
- [x] Solução Android separada e script de build que utiliza Android SDK/JDK existentes, sem exigir esses workloads no build do backend.
- [x] Rotação de época após restauração isolada para invalidar cursores de uma história anterior.
- [ ] Executar o [roteiro de aceite Android](mobile.md) em dispositivo/emulador, incluindo layout, reinstalação, revogação, offline e encerramento do processo durante upload.
- [ ] Validar serviço, notificações, permissões, tela bloqueada e timeout no aparelho conforme [android-background-uploads.md](android-background-uploads.md).
- [ ] Projetar galeria automática, sincronização periódica e UIDT para transferências longas posteriormente.
- [ ] Evoluir para iOS e assinatura/distribuição de produção posteriormente.

O app mantém metadados e conteúdo baixado no diretório privado. O serviço acompanha somente um envio iniciado com Activity visível; não agenda galeria nem retoma envios após perda do processo. O journal não tem compactação automática nesta entrega. [mobile.md](mobile.md), [android-background-uploads.md](android-background-uploads.md) e [synchronization.md](synchronization.md) registram os contratos e limites.

**Validação local em 9 de outubro de 2026:** restore travado e **293 testes aprovados** (60 unitários, 181 de integração, 52 mobile), sem falhas/ignorados. Os 19 casos novos cobrem pausa/retomada, revogação, acompanhamento limitado, reentrada, leituras concorrentes e troca de escopo. O incremento Android inicial (`089c593`) também teve migrations, API/health/OpenAPI e APK verificados. [mobile-validation.md](mobile-validation.md) registra o pacote atual e os limites da evidência. Nenhum dispositivo/emulador estava disponível para o aceite Android.

### Superfície HTTP disponível

| Método | Rota | Finalidade |
| --- | --- | --- |
| `GET` | `/health/live` | Confirmar que o processo responde. |
| `GET` | `/health/ready` | Verificar banco e migrations. |
| `GET` | `/openapi/v1.json` | Consultar contratos em Development. |
| `POST` | `/api/auth/login` | Abrir sessão e receber tokens. |
| `POST` | `/api/auth/refresh` | Rotacionar refresh e receber novo par. |
| `POST` | `/api/auth/logout` | Revogar a sessão autenticada atual. |
| `GET` | `/api/devices` | Listar dispositivos da conta autenticada. |
| `DELETE` | `/api/devices/{id}` | Revogar um dispositivo da própria conta. |
| `POST` | `/api/uploads` | Criar sessão e reservar capacidade. |
| `GET` | `/api/uploads/{id}` | Consultar progresso, operação e resultado/falha. |
| `PUT` | `/api/uploads/{id}/chunks/{number}` | Confirmar chunk binário ou reenvio idêntico. |
| `POST` | `/api/uploads/{id}/complete` | Solicitar finalização durável e repetir consulta de resultado. |
| `DELETE` | `/api/uploads/{id}` | Cancelar sessão elegível. |
| `GET` | `/api/assets` | Listar biblioteca ativa com paginação por cursor. |
| `GET` | `/api/assets/{id}` | Consultar metadados do item ativo. |
| `PATCH` | `/api/assets/{id}` | Renomear e/ou editar favorito do item ativo. |
| `DELETE` | `/api/assets/{id}` | Mover para a lixeira sem apagar os bytes. |
| `GET` | `/api/trash` | Listar a lixeira com cursor. |
| `POST` | `/api/trash/{id}/restore` | Restaurar explicitamente item não purgado. |
| `GET`, `HEAD` | `/api/assets/{id}/content` | Baixar original ou consultar headers, com Range/ETag. |
| `GET`, `HEAD` | `/api/assets/{id}/thumbnail` | Servir thumbnail PNG pronto e autorizado, com Range/ETag. |
| `GET`, `HEAD` | `/api/assets/{id}/preview` | Servir preview PNG pronto e autorizado, com Range/ETag. |
| `GET` | `/api/storage` | Consultar tamanhos lógicos, consumo físico, reservas e volume. |
| `GET` | `/api/sync` | Obter snapshot paginado consistente da conta, incluindo lixeira. |
| `GET` | `/api/sync/changes` | Receber alterações e tombstones após um cursor protegido. |

Os contratos e códigos de erro estão em [authentication.md](authentication.md), [uploads.md](uploads.md), [images.md](images.md) e [library.md](library.md).

### Evidência registrada

| Marco | Referência |
| --- | --- |
| Fundação entregue | Commit `f0b48d7`. |
| Identidade entregue | Commit `107d7df`. |
| Armazenamento interno entregue | Commit `435e051`; migration aplicada ao banco local de desenvolvimento. |
| Validação da Fase 3, em 6 de outubro de 2026 | Restore em locked mode; build Release sem avisos/erros; 95 testes aprovados: 29 unitários e 66 de integração, zero falhas e zero ignorados. |
| Arquivos por API / Worker | Commit `7873ec8`; migration `20261008112515_UploadsDurableJobs` aplicada ao banco local de desenvolvimento. |
| Validação da Fase 4, em 8 de outubro de 2026 | Restore em locked mode; build Release com zero avisos/erros; 129 testes aprovados: 39 unitários e 90 de integração, zero falhas e zero ignorados. |
| Imagens e derivados | Commit `b4ecf15`; migration `20261008122015_ImageMetadataAndDerivatives` aplicada ao banco local de desenvolvimento. |
| Validação da Fase 5, em 8 de outubro de 2026 | Restore em locked mode; build Release com zero avisos/erros; 189 testes aprovados: 50 unitários e 139 de integração, zero falhas e zero ignorados; API/Worker e health checks verificados pela CLI. |
| Biblioteca, timeline e lixeira | Commit `e27ca91`; migration `20261008125535_LibraryTrashAndPurge` aplicada ao banco local de desenvolvimento. |
| Validação da Fase 6, em 8 de outubro de 2026 | Restore travado; build Release com zero avisos/erros; 229 testes aprovados: 60 unitários e 169 de integração, zero falhas e zero ignorados. |
| Incremento Android da Fase 8, em 9 de outubro de 2026 | Commit `089c593`; 274 testes aprovados, migrations aplicadas, API iniciada e APK Debug gerado/inspecionado. Pacote Linux atualizado com manifesto conferido. Aceite no dispositivo e operação Arch pendentes; evidências em [mobile-validation.md](mobile-validation.md) e [arch-validation.md](arch-validation.md). |
| Envios Android fora da tela, em 9 de outubro de 2026 | Commit `451925b`; 293 testes aprovados, APK `0.1.1`/código `2` com zero avisos/erros, assinatura v2/v3 e manifest `dataSync` não exportado conferidos. Pausa, retomada, escopo concorrente e deadline testados no núcleo; ciclo de vida real Android pendente. |

Os resultados das fases 3 a 6 são do Windows com PostgreSQL nativo. Para verificar outra revisão, execute os comandos de [development.md](development.md) e registre o novo resultado.

## 4. Etapas que faltam

As listas abaixo são trabalho planejado. Cada fase só será concluída após implementar suas entregas e verificar os critérios de aceite.

### Fase 7 — Operação no servidor pessoal

Os fluxos de arquivos e biblioteca foram verificados localmente. A preparação abaixo pode ser feita no Windows; o aceite exige execução no servidor Arch.

- [x] Preparar publicação Linux x64 com runtime 10.0.12 incluído, versões travadas, migration bundle, SQL para revisão e manifesto SHA-256.
- [x] Preparar instalador sem sobrescrita, usuário dedicado, diretórios privados, unidades systemd e ativação explícita com parada dos processos.
- [x] Documentar PostgreSQL 18 com credenciais externas e roles separadas para migrations e aplicação, certificados, chaves e Kestrel em loopback.
- [x] Revisar configuração de proxy/Host, limites dos serviços e regras privadas Tailscale; disponibilizar exemplos sem segredos.
- [ ] Executar instalação e verificar permissões, isolamento e configuração recusada quando inválida no Arch real.
- [ ] Configurar Tailscale Serve com HTTPS, grants restritas e Funnel desativado; verificar o acesso no ambiente real.
- [ ] Verificar Data Protection com certificado no Linux e recuperação de chaves fora da instalação.
- [ ] Revisar autorização, limites, dependências nativas e comportamento após reinício de serviços/servidor.
- [ ] Verificar carga e processamento Skia no Arch, limites de memória de serviço e recuperação do processo filho após timeout/reinício.
- [x] Documentar diagnóstico de serviços, espaço, reservas, temporários, processamento e tarefas esgotadas.
- [x] Preparar scripts e documentar backup manual consistente de PostgreSQL, storage, configuração e chaves, com cópia independente do servidor.
- [ ] Executar backup no Arch e conferir a cópia independente e seus hashes.
- [ ] Executar restauração em ambiente isolado e verificar banco, login, originais e derivados recuperados.
- [x] Documentar migrations explícitas e recuperação de uma atualização interrompida, sem rollback automático de schema.

O [registro de validação](arch-validation.md) separa publicação/testes no Windows de verificações reais pendentes. A presença dos scripts não comprova funcionamento de systemd, Skia Linux, Tailscale ou recuperação de dados.

**Aceite:** serviços iniciam corretamente após reinício; acesso funciona somente pelo desenho privado aprovado; backup é restaurado e seu conteúdo é conferido; procedimentos de atualização e recuperação são reproduzíveis. Esse é o marco para considerar o backend do MVP pronto para uso com dados pessoais em produção.

### Fase 8 — Cliente mobile e sincronização

O incremento Android foi implementado antes do aceite real da operação no Arch, conforme autorizado. A utilização com dados pessoais em produção continua dependendo da Fase 7.

- [x] Definir contrato de sincronização e registro de mudanças, incluindo exclusões e retomada.
- [x] Criar `apps/Nexora.Mobile` em MAUI, começando por Android.
- [x] Implementar login, armazenamento seguro de tokens, refresh serializado e identificação da instalação.
- [x] Implementar consulta de biblioteca, derivados e upload retomável usando os contratos da API.
- [x] Implementar cache offline e retomada manual com testes do núcleo independente da plataforma.
- [ ] Verificar os fluxos e comportamento offline no dispositivo Android, conforme roteiro de aceite.
- [x] Implementar envio explícito fora da página com serviço Android, pausa e recuperação durável da fila.
- [ ] Validar seu ciclo de vida no Android; evolução para UIDT/galeria e sincronização periódica permanece posterior.
- [ ] Evoluir para iOS posteriormente, considerando as restrições específicas da plataforma.

**Aceite inicial:** um dispositivo Android autentica, consulta a biblioteca e envia arquivos com retomada; trata revogação e necessidade de novo login; sincronização não cria duplicatas nem perde exclusões. O aceite de iOS será definido quando essa etapa começar.

## 5. Parâmetros de arquivos e defaults futuros

As opções estão implementadas nas seções `Uploads`, `Images` e `Library`. Validar `Storage:RootPath` e iniciar a API não cria os diretórios de conteúdo; escritas criam diretórios privados sob a raiz configurada.

| Parâmetro | Default / estado |
| --- | --- |
| Chunk | 8 MiB. |
| Arquivo máximo | 20 GiB. |
| Sessões abertas por usuário | 2, condicionadas à capacidade global. |
| Temporários e reservas | 50 GiB. |
| Margem mínima de espaço livre | 50 GiB. |
| Reserva de montagem | Duas vezes o tamanho declarado. |
| Expiração de upload | 7 dias sem atividade. |
| Retenção da lixeira | 30 dias. |
| Graça para Blob sem referências | 1 dia desde criação; intenções Staging preservadas. |
| Manutenção da biblioteca | Lotes de 100, a cada minuto. |
| Processamento simultâneo | Uma montagem e uma imagem por Worker, em rotinas separadas. |
| Thumbnail / preview | Lado máximo de 256 / 1280 px, PNG, sem ampliação. |
| Entrada / pixels de imagem | 32 MiB / 24.000.000 pixels. |
| Reserva por imagem | Duas vezes o máximo de derivado: 16 MiB com defaults. |
| Watchdog de imagem | 30 segundos e working set observado de 512 MiB; heap gerenciado de 256 MiB. |

O teto de sessões não garante dois arquivos de 20 GiB simultâneos. A admissão considera reservas existentes de uploads/imagens, temporários físicos, nova reserva e margem de espaço livre. `/api/storage` distingue biblioteca ativa, lixeira, Blobs físicos, derivados, temporários e reservas; o tamanho real do volume substitui qualquer quota fixa presumida de 500 GB. Opções completas estão em [uploads.md](uploads.md) e [images.md](images.md).

## 6. Evoluções fora da primeira entrega

Interface web, pastas, compartilhamento, IA, reconhecimento facial, transcodificação, processamento HEIC/RAW/vídeo, geolocalização e dados de câmera ficam para avaliação posterior. Um adaptador S3 poderá ser acrescentado por trás dos contratos de storage. Integração de backup com restic ou borg também é futura; o MVP prevê procedimentos manuais documentados e restauração testada.

Essas possibilidades não recebem uma fase concluída, prazo ou compromisso de implementação neste documento.

## 7. Como manter este documento atualizado

Ao concluir um incremento:

1. Conferir as funcionalidades no código e os critérios de aceite da fase.
2. Marcar somente as entregas implementadas e verificadas; registrar limitações restantes.
3. Atualizar a data, o commit de referência e a evidência de build/testes com seus resultados e eventuais skips.
4. Atualizar a superfície HTTP e os documentos de contratos quando houver novas rotas ou alterações.
5. Manter o README e a arquitetura coerentes com a fase atual e a próxima entrega.

| Documento | Uso |
| --- | --- |
| [Estado e próximas etapas](status-and-roadmap.md) | Acompanhar entregas, pendências e critérios de aceite. |
| [Arquitetura](architecture.md) | Consultar decisões, modelo e fluxos projetados. |
| [Desenvolvimento](development.md) | Preparar o ambiente, aplicar migrations e executar a API/testes. |
| [Autenticação](authentication.md) | Integrar os contratos já disponíveis e administrar a conta localmente. |
| [Armazenamento](storage.md) | Consultar importação interna, deduplicação, publicação e recuperação por reenvio. |
| [Uploads e Worker](uploads.md) | Integrar chunks, retomada, finalização, biblioteca/download e capacidade. |
| [Imagens e derivados](images.md) | Integrar metadados, estados e PNGs autorizados; consultar limites, processamento e recuperação. |
| [Biblioteca e lixeira](library.md) | Integrar filtros, timeline, edição, restauração e conhecer retenção/coleta. |
| [Instalação no Arch](arch-deployment.md) | Publicar, configurar, instalar, atualizar e diagnosticar os serviços privados. |
| [Backup e restauração](backup-and-restore.md) | Criar snapshot manual e exercitar recuperação isolada. |
| [Validação no Arch](arch-validation.md) | Registrar evidências e acompanhar o aceite operacional pendente. |

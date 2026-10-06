# Nexora — estado atual e próximas etapas

Atualizado em **6 de outubro de 2026**. Estado do código conferido no commit `435e051`.

O Nexora já tem fundação, autenticação e armazenamento interno com deduplicação implementados. A próxima entrega é a Fase 4, que disponibiliza upload retomável, Worker e arquivos por API. Biblioteca completa, imagens e clientes dependem das fases seguintes.

## 1. O que o projeto pretende ser

Uma nuvem pessoal hospedada por você, para guardar e acessar fotos, vídeos e arquivos. O servidor manterá os originais, os metadados e, para imagens compatíveis, versões menores para consulta. Uma conta administrativa poderá acessar a biblioteca por diferentes dispositivos, com sessões revogáveis.

O destino de produção será Arch Linux, com PostgreSQL local, armazenamento em disco, API e Worker executados por systemd e acesso privado por Tailscale com HTTPS. Os aproximadamente 500 GB serão uma referência de planejamento; a admissão de arquivos dependerá do espaço livre real.

O desenvolvimento começa pela API. O cliente mobile virá depois, inicialmente em MAUI para Android, com iOS posteriormente. A interface web também é uma evolução futura, sem fase de implementação definida neste roadmap.

### Decisões já tomadas

- Monólito modular em .NET 10: API, Application, Domain e Infrastructure; Worker separado quando houver processamento demorado.
- Uma conta administrativa, sem cadastro público; biblioteca plana no MVP.
- PostgreSQL para metadados e estados; filesystem para originais, derivados e temporários.
- Blob representa os bytes imutáveis; Asset representa o item da biblioteca e suas permissões, nome e lixeira.
- A importação interna de conteúdo idêntico reutiliza o Asset existente, preservando seus metadados. Um item na lixeira exige restauração explícita; os endpoints correspondentes serão introduzidos nas próximas fases.
- O servidor será confiável e poderá ler os originais para processamento.
- Thumbnails e previews começarão por JPEG, PNG e WebP. Outros formatos permanecerão acessíveis como originais quando o upload estiver implementado.
- Segurança, testes e recuperação acompanharão cada fase; operação em produção será validada na Fase 7.

Os detalhes técnicos e as razões dessas decisões estão em [architecture.md](architecture.md).

## 2. Situação das etapas

| Fase | Situação | Entrega principal |
| --- | --- | --- |
| 0 — Arquitetura | Concluída | Modelo, contratos, decisões e sequência de desenvolvimento documentados. |
| 1 — Fundação | Concluída | Solution, configuração, PostgreSQL, migrations Identity e health checks. |
| 2 — Identidade | Concluída | Administração local da conta, login, sessões, refresh e dispositivos. |
| 3 — Armazenamento | Concluída | Blob/Asset, adaptador local, streaming, SHA-256 e deduplicação interna. |
| 4 — Arquivos utilizáveis | Próxima; não iniciada | Upload retomável, Worker durável, biblioteca básica, download e capacidade. |
| 5 — Imagens | Pendente | Metadados, orientação, thumbnails, previews e estado de processamento. |
| 6 — Biblioteca | Pendente | Timeline, favoritos, lixeira, restauração e limpeza definitiva segura. |
| 7 — Operação | Pendente | Arch/systemd, Tailscale/HTTPS, revisão de segurança e restauração de backup. |
| 8 — Mobile futuro | Pendente | Contrato de sincronização e cliente MAUI Android; iOS depois. |

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

O suporte a configurações de produção está no código. O deploy Arch, os serviços systemd, as grants Tailscale e os procedimentos de backup/restauração ainda serão preparados e verificados na Fase 7.

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

Os [contratos de armazenamento](storage.md) detalham a entrega. Reconciliação automática, jobs duráveis, reservas e testes de reinício dos processos entram na Fase 4. Execução nativa Linux e persistência após reinício no filesystem Arch continuam pendentes; testes com falha injetada não equivalem a simular queda de energia.

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

Os contratos de autenticação, códigos de erro e instruções de recuperação estão em [authentication.md](authentication.md).

### Evidência registrada

| Marco | Referência |
| --- | --- |
| Fundação entregue | Commit `f0b48d7`. |
| Identidade entregue | Commit `107d7df`. |
| Armazenamento interno entregue | Commit `435e051`; migration aplicada ao banco local de desenvolvimento. |
| Última validação da implementação, em 6 de outubro de 2026 | Restore em locked mode; build Release sem avisos/erros; 95 testes aprovados: 29 unitários e 66 de integração, zero falhas e zero ignorados. |
| Verificação HTTP local com a migration da Fase 3 | Live, ready e OpenAPI responderam `200`; dispositivos sem autenticação `401`; `/api/uploads`, ainda não implementada, `404`. |

Esses resultados são o registro da entrega da Fase 3 no Windows. Para verificar uma revisão posterior, execute os comandos de [development.md](development.md) e registre o novo resultado.

## 4. Etapas que faltam

As listas abaixo são trabalho planejado. Cada fase só será concluída após implementar suas entregas e verificar os critérios de aceite.

### Fase 4 — Arquivos utilizáveis por API

Depende do armazenamento e das regras de deduplicação da Fase 3.

- [ ] Criar UploadSession, UploadChunk e BackgroundJob, com migrations e transições persistidas.
- [ ] Criar `Nexora.Worker` com fila PostgreSQL, lease renovável, tentativas limitadas e falhas visíveis.
- [ ] Implementar criação, consulta, cancelamento, chunks numerados e conclusão de uploads.
- [ ] Validar tamanho real e hash de cada chunk; aceitar repetição idêntica e recusar conteúdo conflitante no mesmo índice.
- [ ] Montar arquivos em streaming no Worker, verificar tamanho/hash final, publicar, resolver o Asset e concluir a sessão de forma repetível.
- [ ] Implementar reservas, limites de escrita, expiração por inatividade e limpeza repetível de temporários.
- [ ] Recuperar interrupções entre publicação física e commit no PostgreSQL.
- [ ] Implementar listagem com cursor, detalhes, download autenticado com HTTP Range e `/api/storage`.
- [ ] Definir limites próprios nas rotas de upload: a API atual limita corpos a 16 KiB, o que será ajustado para os chunks previstos.

**Aceite:** um arquivo pode ser enviado, retomado após reinício, listado e baixado; conclusão repetida retorna a mesma operação/resultado; chunks fora de ordem, concorrência e cancelamento são testados; disco cheio e falhas entre banco/filesystem não produzem sucesso incorreto; acesso entre proprietários é recusado; memória usada no streaming não cresce proporcionalmente ao arquivo.

### Fase 5 — Fotos e derivados

Depende de originais publicados e do Worker durável.

- [ ] Acrescentar dimensões, data de captura e estado de processamento.
- [ ] Integrar SkiaSharp e MetadataExtractor e verificar as dependências nativas no Arch Linux.
- [ ] Corrigir orientação e gerar thumbnail de até 256 px e preview de até 1280 px, sem ampliar imagens menores.
- [ ] Processar inicialmente JPEG, PNG e WebP com limites de pixels, memória e tempo.
- [ ] Preservar datas EXIF sem fuso; usar upload quando não houver captura UTC confiável.
- [ ] Servir derivados autorizados em `/thumbnail` e `/preview`; preservar o original quando o processamento falhar.

**Aceite:** imagens compatíveis geram derivados corretos; formatos sem suporte continuam disponíveis como originais; arquivos malformados e imagens acima dos limites falham de forma controlada; falha de preview mantém o original acessível.

### Fase 6 — Biblioteca, timeline e lixeira

Depende da biblioteca básica, dos metadados e das rotinas do Worker.

- [ ] Implementar timeline de imagens com ordenação por captura/upload e paginação por cursor.
- [ ] Implementar favoritos e as alterações de metadados previstas em `PATCH /api/assets/{id}`.
- [ ] Implementar exclusão lógica em `DELETE /api/assets/{id}` e listagem em `GET /api/trash`.
- [ ] Implementar restauração explícita em `POST /api/trash/{id}/restore`.
- [ ] Remover Assets após a retenção configurada e coletar somente Blobs sem qualquer referência, incluindo a lixeira.
- [ ] Coordenar restauração contra purge e criação de referência contra coleta com bloqueios no banco.

**Aceite:** favoritos e timeline permanecem estáveis na paginação; exclusão lógica mantém os bytes e pode ser restaurada; duplicata na lixeira exige restauração; corridas de purge/restauração e deduplicação/coleta não removem conteúdo ainda referenciado.

### Fase 7 — Operação no servidor pessoal

Depende dos fluxos de arquivos e biblioteca verificados.

- [ ] Preparar instalação e atualização no Arch Linux, usuário dedicado e unidades systemd para API e Worker.
- [ ] Restringir diretórios, chaves, credenciais e PostgreSQL; manter Kestrel em loopback.
- [ ] Configurar Tailscale Serve com HTTPS, grants restritas e Funnel desativado; verificar o acesso no ambiente real.
- [ ] Verificar Data Protection com certificado no Linux e recuperação de chaves fora da instalação.
- [ ] Revisar autorização, limites, dependências nativas e comportamento após reinício de serviços/servidor.
- [ ] Documentar e observar espaço livre, reservas, temporários, processamento e tarefas esgotadas.
- [ ] Documentar backup manual consistente de PostgreSQL, storage, configuração e chaves, com cópia independente do servidor.
- [ ] Executar restauração em ambiente isolado e verificar banco, login, originais e derivados recuperados.
- [ ] Documentar migrations explícitas e recuperação de uma atualização interrompida.

**Aceite:** serviços iniciam corretamente após reinício; acesso funciona somente pelo desenho privado aprovado; backup é restaurado e seu conteúdo é conferido; procedimentos de atualização e recuperação são reproduzíveis. Esse é o marco para considerar o backend do MVP pronto para uso com dados pessoais em produção.

### Fase 8 — Cliente mobile e sincronização

Depende de uma API estável e da operação do backend.

- [ ] Definir contrato de sincronização e registro de mudanças, incluindo exclusões e retomada.
- [ ] Criar `apps/Nexora.Mobile` em MAUI, começando por Android.
- [ ] Implementar login, armazenamento seguro de tokens, refresh serializado e identificação da instalação.
- [ ] Implementar consulta de biblioteca, derivados e upload retomável usando os contratos da API.
- [ ] Definir e testar comportamento offline e restrições de sincronização em background no Android.
- [ ] Evoluir para iOS posteriormente, considerando as restrições específicas da plataforma.

**Aceite inicial:** um dispositivo Android autentica, consulta a biblioteca e envia arquivos com retomada; trata revogação e necessidade de novo login; sincronização não cria duplicatas nem perde exclusões. O aceite de iOS será definido quando essa etapa começar.

## 5. Parâmetros previstos para arquivos

Estes valores ainda serão implementados nas fases de arquivos, imagens e lixeira. O storage interno já limita a escrita ao tamanho declarado por operação; não aplica quotas globais, reservas, expiração ou esses defaults. Validar `Storage:RootPath` e iniciar a API não cria os diretórios de conteúdo.

| Parâmetro | Default planejado |
| --- | --- |
| Chunk | 8 MiB. |
| Arquivo máximo | 20 GiB. |
| Sessões abertas por usuário | 2, condicionadas à capacidade global. |
| Temporários e reservas | 50 GiB. |
| Margem mínima de espaço livre | 50 GiB. |
| Reserva de montagem | Até duas vezes o tamanho declarado. |
| Expiração de upload | 7 dias sem atividade. |
| Retenção da lixeira | 30 dias. |
| Processamento simultâneo | Uma montagem e uma imagem por vez. |

O teto de sessões não garante dois arquivos de 20 GiB simultâneos. O orçamento global e o espaço livre determinarão a admissão. `/api/storage` distinguirá biblioteca ativa, lixeira, Blobs físicos, derivados, temporários e reservas.

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

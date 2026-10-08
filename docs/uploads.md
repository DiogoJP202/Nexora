# Uploads, biblioteca básica e Worker

A Fase 4 disponibiliza arquivos por API: sessões retomáveis, chunks, finalização durável, listagem, download e capacidade. API e Worker compartilham PostgreSQL e storage, mas executam como processos separados. A autenticação está documentada em [authentication.md](authentication.md); a identidade dos bytes e a publicação local estão em [storage.md](storage.md).

## Contratos HTTP

Todas as rotas abaixo exigem `Authorization: Bearer <accessToken>`. O proprietário e o dispositivo vêm da sessão autenticada, nunca de IDs informados no corpo. Um recurso de outra conta retorna `404`. Erros usam ProblemDetails com `code` estável e `traceId`; DTOs não expõem caminhos físicos.

| Método | Rota | Resposta e finalidade |
| --- | --- | --- |
| `POST` | `/api/uploads` | `201` e `Location`: criar sessão e reservar capacidade. |
| `GET` | `/api/uploads/{id}` | `200`: estado, chunks confirmados, operação e resultado/falha. |
| `PUT` | `/api/uploads/{id}/chunks/{number}` | `200`: confirmar chunk ou reconhecer repetição idêntica. |
| `POST` | `/api/uploads/{id}/complete` | `202` enquanto `Finalizing`; `200` quando já concluído ou falhou. |
| `DELETE` | `/api/uploads/{id}` | `204`: cancelar sessão elegível; limpeza física ocorre no Worker. |
| `GET` | `/api/assets` | `200`: biblioteca ativa, paginada por cursor. |
| `GET` | `/api/assets/{id}` | `200`: metadados do item ativo. |
| `GET`, `HEAD` | `/api/assets/{id}/content` | Original autenticado, com HTTP Range e validadores. |
| `GET` | `/api/storage` | Tamanhos lógicos, consumo físico, reservas e espaço do volume. |

### Criar e consultar a sessão

O corpo de criação é JSON, com tamanho em bytes e SHA-256 opcional:

```json
{
  "originalName": "arquivo.txt",
  "expectedLength": 12,
  "expectedSha256": null
}
```

O servidor valida o nome, determina `chunkSize` e `chunkCount` e retorna `UploadSnapshot`: `id`, `originalName`, `expectedLength`, `chunkSize`, `chunkCount`, `state`, `confirmedChunks`, `createdAt`, `lastActivityAt`, `result`, `failureCode` e `operation`. `operation`, quando existe, contém `id`, `state`, `attempts` e `failureCode`. Timestamps operacionais são UTC. Consultar progresso não renova a atividade de uma sessão aberta; a confirmação de chunk a renova.

Os estados atualmente usam a serialização numérica padrão do ASP.NET Core:

| Upload `state` | Significado | Operação `state` | Significado |
| --- | --- | --- | --- |
| `0` | `Open` | `0` | `Pending` |
| `1` | `Finalizing` | `1` | `Running` |
| `2` | `Completed` | `2` | `Succeeded` |
| `3` | `Cancelled` | `3` | `Failed` |
| `4` | `Expired` | `4` | `Cancelled` |
| `5` | `Failed` | — | — |

### Enviar chunks e retomar

Os índices começam em **zero**. Para um arquivo não vazio, envie os índices `0` a `chunkCount - 1`, em qualquer ordem. Cada índice exige `min(chunkSize, expectedLength - number × chunkSize)` bytes; somente o último pode ser menor que o tamanho de chunk. Um arquivo de zero bytes tem zero chunks e pode ser finalizado diretamente.

O corpo de cada `PUT` contém os bytes crus, com `Content-Type: application/octet-stream`. Multipart, JSON e base64 não são aceitos. O header opcional `X-Chunk-SHA256` recebe um único hash hexadecimal de 64 caracteres; o servidor sempre calcula seu próprio SHA-256 e, se houver expectativa, a compara.

Uma confirmação retorna `number`, `size`, `sha256` e `reused`. Repetir o índice com os mesmos bytes retorna `reused: true`; bytes diferentes retornam `409` com `chunk_conflict`. O temporário completo é publicado antes do registro no banco. Um erro de commit sem confirmação preserva o arquivo, pois a transação pode ter sido confirmada no servidor.

Após interrupção, consulte a sessão e envie somente os índices faltantes. `confirmedChunks` informa os chunks aceitos; quando a montagem já está persistida durante `Finalizing`, informa todos os índices originais mesmo que os arquivos de chunk já tenham sido removidos. Não envie novos chunks após começar a finalização.

### Concluir, observar e cancelar

`POST /complete` não recebe arquivo nem monta conteúdo dentro da requisição. Ele valida a presença de todos os chunks e grava `Finalizing` e o job na mesma transação. Retorna `202`, `Location` da sessão e o snapshot. Repetir a chamada preserva a mesma operação; depois de `Completed` ou `Failed`, retorna `200` com o resultado terminal. Sessões canceladas ou expiradas retornam conflito.

O cliente consulta `GET /api/uploads/{id}` até o estado terminal. Em sucesso, `result` contém o Asset, que pode ser o item previamente existente da mesma conta. Deduplicação preserva ID, nome original, data de upload e favorito desse item. O resultado não distingue criação de reutilização por um campo adicional.

Se uma duplicata estiver na lixeira, a finalização registra `Failed` e `failureCode: asset_in_trash`; não restaura o item. O job pode estar `Succeeded`, pois terminou a decisão de negócio. Restaure explicitamente por `POST /api/trash/{id}/restore`, conforme [library.md](library.md). Não há endpoint para reiniciar uma sessão terminal que falhou: examine o código de falha e, quando apropriado, crie uma nova sessão. Quando um resultado concluído for purgado após retenção, o upload permanece `Completed`, com `result: null` e `resultPurgedAt`; repetir conclusão não recria o Asset.

Cancelar uma sessão `Open` ou `Finalizing` invalida a conclusão pelo Worker. O cancelamento disputa o mesmo bloqueio da confirmação final; se o arquivo já foi concluído, retorna `409`. Cancelar novamente uma sessão já cancelada é idempotente. Reservas não são liberadas no instante da resposta: permanecem até a manutenção remover todos os temporários elegíveis. Revogar um dispositivo impede novas requisições autenticadas; isso não cancela automaticamente os uploads já registrados.

| Situação | Status/código principal |
| --- | --- |
| Nome, tamanho ou hash esperado inválido | `400`, `invalid_request` |
| Arquivo acima do máximo | `413`, `file_too_large` |
| Teto de sessões abertas/finalizando atingido | `409`, `upload_limit_reached` |
| Reserva/orçamento/espaço livre insuficiente | `507`, `insufficient_storage` |
| Índice inválido ou chunk curto | `400`, `invalid_chunk` / `chunk_size_mismatch` |
| Hash de chunk diferente da expectativa | `400`, `chunk_hash_mismatch` |
| Chunk acima do tamanho permitido | `413`, `chunk_too_large` |
| Formato do corpo diferente de binário | `415`, `unsupported_media_type` |
| Chunk confirmado com bytes diferentes | `409`, `chunk_conflict` |
| Chunk confirmado ausente/corrompido | `409`, `chunk_integrity_failed` |
| Conclusão sem todos os chunks | `409`, `chunks_incomplete` |
| Sessão sem aceitar essa operação | `409`, `upload_not_open` |
| Tempo de recepção do chunk excedido | `408`, `chunk_timeout` |

Falhas assíncronas permanecem no snapshot, incluindo `content_integrity`, `content_missing`, `storage_unavailable`, `processing_timeout`, `processing_failed`, `unsafe_storage_path`, `blob_unavailable` e `attempts_exhausted`. Problemas de banco/armazenamento nas requisições recebem erros sanitizados; nomes, credenciais, paths e exceções internas não aparecem na resposta.

## Biblioteca e download

`GET /api/assets?limit=50&cursor=<nextCursor>` retorna `items` e `nextCursor`. O default é 50; o intervalo permitido é de 1 a 100. A ordem é upload decrescente, com UUID como desempate. O cursor é opaco para o cliente, vinculado à conta e validado; reutilize-o sem modificações. Cursor inválido retorna `400` com `invalid_cursor`. A paginação não representa um snapshot transacional de toda a biblioteca.

Listagem, detalhes e conteúdo incluem somente Assets ativos da própria conta cujo Blob está `Ready`. Download ausente ou de outra conta retorna `404`; bytes físicos indisponíveis retornam erro sanitizado, sem declarar sucesso. O download verifica existência e tamanho antes de abrir o stream; não recalcula SHA-256 a cada leitura HTTP.

Originais usam `application/octet-stream`, `Content-Disposition: attachment` e `X-Content-Type-Options: nosniff`. O nome do Asset aparece no download, nunca no caminho físico. O stream suporta `Range`, `If-Range`, `If-None-Match` e `If-Modified-Since` pela implementação nativa do ASP.NET Core, com `206`, `304` e `416` conforme a requisição. `HEAD` consulta os headers sem transferir os bytes. O ETag identifica a geração Blob e `Last-Modified` usa a data do Asset. Requisições `/api/*` recebem `Cache-Control: no-store`.

Snapshots de Asset incluem `image` opcional, com estado, metadados e existência de thumbnail/preview. O upload concluído pode anteceder o processamento da imagem. Os derivados PNG autenticados por GET/HEAD, Range e ETag estão em [images.md](images.md). Filtros, timeline, favoritos, edição e lixeira estão em [library.md](library.md); itens na lixeira não permitem abrir originais ou derivados.

## Worker e fronteiras de recuperação

O Worker usa o Host nativo do .NET, sem broker adicional. `BackgroundJobs` guarda o trabalho, e `BackgroundJobAttempts` guarda cada UUID de tentativa antes de qualquer montagem/publicação. A aquisição combina bloqueio de upload e `FOR UPDATE SKIP LOCKED`; jobs com lease expirado podem ser reclamados. Todas as mutações do processamento verificam job, upload, token e prazo do lease.

Uma montagem e uma imagem executam por vez em cada Worker, em rotinas separadas. Renovação e manutenção usam scopes próprios para não compartilhar DbContext com o processamento. Interromper o processo ou perder a renovação cancela o processamento; o lease persistido permite retry. O desenho assume um Worker de produção; iniciar vários processos aumenta a concorrência total, embora os locks preservem a identidade e as leases.

O processamento segue esta ordem:

1. Limpar tentativas anteriores com acesso exclusivo; manter uma montagem registrada ainda necessária. Um escritor anterior ativo impede a limpeza e o retry aguarda outra tentativa.
2. Ler chunks em ordem com stream, verificando tamanho/hash de cada um, e criar a montagem com SHA-256 final e limite de bytes. O UUID vem da tentativa persistida.
3. Confirmar a montagem na UploadSession antes de excluir chunks. Commit sem confirmação mantém a geração, permitindo recuperação posterior.
4. Remover chunks físicos e seus registros de forma repetível. A montagem persistida permite continuar mesmo que a interrupção ocorra entre exclusão física e commit da remoção.
5. Obter ou criar Blob `Staging`, publicar usando `.publishing` da tentativa ou verificar o Blob `Ready` existente.
6. Confirmar Blob, Asset, UploadSession e job em uma transação, exigindo lease válido, e registrar imagem/job para JPEG/PNG/WebP caso ausentes. Uma publicação física anterior ao commit pode ser verificada e reaproveitada no retry. A decodificação de imagem ocorre depois, sem atrasar a confirmação do original.
7. Remover montagem e tentativas terminais pela manutenção. Somente após todas as exclusões e sincronizações, liberar a reserva no banco.

Não há três cópias completas simultâneas no fluxo planejado: os chunks saem antes da cópia de publicação do Blob. As tentativas antigas são limpas antes de criar nova montagem. Locks de arquivo impedem limpeza de escritor ativo; no Linux o adaptador acrescenta `flock` às restrições de compartilhamento.

Erros de integridade/caminho/conteúdo ausente terminam como falha. Falhas transitórias recebem atraso e tentativas limitadas; jobs esgotados permanecem consultáveis. Manutenção expira sessões `Open` inativas, limpa estados terminais e remove somente nomes temporários internos canônicos com idade suficiente, sem referência de chunk, montagem ou tentativa no banco e sem escritor ativo. Arquivos desconhecidos e Blobs não são removidos por essa coleta.

## Capacidade e opções

Para tamanho declarado `N`, a sessão reserva `2 × N`. A admissão é serializada no PostgreSQL. Ela exige que **reservas existentes de uploads/imagens + temporários físicos + nova reserva** caibam no orçamento de temporários/reservas. Também exige espaço livre real para **margem mínima + reservas existentes + nova reserva**. Imagens reservam duas vezes o limite de cada derivado antes do processamento e só liberam após limpeza terminal. A contagem é conservadora e pode recusar uma sessão mesmo havendo espaço imediato; ela protege a montagem/publicação e não promete dois arquivos máximos simultâneos.

As opções abaixo pertencem à seção `Uploads`, compartilhada entre API e Worker, e são validadas na inicialização:

| Opção | Default |
| --- | --- |
| `ChunkSizeBytes` | 8 MiB |
| `MaximumFileSizeBytes` | 20 GiB |
| `MaximumOpenUploadsPerOwner` | 2, incluindo `Open` e `Finalizing` |
| `MaximumReservedBytes` | 50 GiB |
| `MinimumFreeBytes` | 50 GiB |
| `InactivityExpiration` | 7 dias |
| `ChunkTimeout` | 2 minutos |
| `LeaseDuration` / `LeaseRenewInterval` | 1 minuto / 20 segundos |
| `ProcessingTimeout` | 2 horas por tentativa |
| `MaximumJobAttempts` / `RetryDelay` | 5 / 30 segundos |
| `PollInterval` / `MaintenanceInterval` | 2 segundos / 1 minuto |
| `OrphanGracePeriod` | 24 horas |

Use hierarquia de configuração nativa, por exemplo `NEXORA_Uploads__ChunkSizeBytes` ou `NEXORA_Uploads__InactivityExpiration=7.00:00:00`. Chunks permitem até 64 MiB e no máximo 65.536 índices por arquivo. Durações devem ser positivas e de até 365 dias; timers de recepção, lease, renovação, processamento, polling e manutenção permitem até 30 dias. Renovação deve ocorrer antes de metade do lease; a graça de órfãos deve exceder processamento + recepção + lease. Opções inválidas recusam a inicialização.

`GET /api/storage` retorna `activeLibraryBytes` e `trashBytes` lógicos da conta; `blobBytes`, `derivativeBytes`, `temporaryBytes` físicos; `reservedBytes` global, incluindo uploads/imagens; `totalBytes` e `availableBytes` do volume. Essas métricas têm significados diferentes e podem se sobrepor: não some reserva e bytes como consumo definitivo. O snapshot reúne consultas e inventário de disco, sem atomicidade entre ambos. A referência de aproximadamente 500 GB não é uma quota fixa. O consumo de derivados inclui PNGs e suas publicações intermediárias nos respectivos diretórios.

## Execução, verificação e pendências

As migrations são sempre explícitas. A migration `20261008112515_UploadsDurableJobs` acrescenta uploads, jobs e tentativas. Execute API e Worker em terminais distintos, seguindo [development.md](development.md). Em Development, ambos usam `Nexora.Api.Development` para User Secrets; overrides `NEXORA_` precisam estar disponíveis em cada processo. Produção usará configuração protegida e serviços systemd preparados na Fase 7.

Um fluxo de verificação usa login, criação, envio fora de ordem, consulta/retomada, conclusão `202`, polling, listagem e download/Range; um reenvio igual retorna o mesmo Asset. Testes também exercitam concorrência, cancelamento, leases expirados, falhas entre filesystem/banco, limpeza antes da liberação de reserva e isolamento por proprietário. O resultado da execução da entrega está em [status-and-roadmap.md](status-and-roadmap.md).

A validação local usa Windows e PostgreSQL nativo. Execução nativa no Arch, reinício real de serviços/servidor, queda de energia, systemd, Tailscale e restauração de backup permanecem pendentes na Fase 7. Falhas injetadas e recuperação de lease verificam transições do software, mas não comprovam durabilidade física do volume de produção. Jobs de imagem estão em [images.md](images.md), e a coleta de Blobs sem referências em [library.md](library.md).

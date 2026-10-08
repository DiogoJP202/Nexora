# Biblioteca, timeline e lixeira

A Fase 6 acrescenta edição do nome, favoritos, timeline de imagens, lixeira e restauração à biblioteca. Todas as rotas exigem autenticação; consultas e alterações verificam o proprietário. Conhecer um UUID não concede acesso, e um item de outra conta retorna `404` com `resource_not_found`.

## Contratos HTTP

| Método | Rota | Resultado |
| --- | --- | --- |
| `GET` | `/api/assets` | Biblioteca ativa, filtros e cursor. |
| `GET` | `/api/assets/{id}` | Detalhes de item ativo. |
| `PATCH` | `/api/assets/{id}` | Atualiza nome e/ou favorito; retorna o Asset. |
| `DELETE` | `/api/assets/{id}` | Move para lixeira; `204`, sem apagar os bytes. |
| `GET` | `/api/trash` | Itens na lixeira, paginados por exclusão. |
| `POST` | `/api/trash/{id}/restore` | Restaura o item; `200` com Asset ativo. |

`PATCH` recebe JSON como `{"originalName":"foto.png","isFavorite":true}`. Cada campo é opcional, mas ao menos um deve existir. `originalName` exige string válida conforme [storage.md](storage.md), e `isFavorite` exige booleano. Objeto vazio, campos desconhecidos/duplicados, null, tipos incorretos e nomes inválidos retornam `400 invalid_request`. O limite JSON continua 16 KiB. Renomear preserva conteúdo, upload e identidade; um reenvio idêntico preserva o nome editado.

`DELETE` repetido preserva o primeiro `deletedAt`, evitando prolongar a retenção. Restauração repetida de um item ativo retorna o mesmo item. Alterar um item na lixeira retorna `404`; restaurar um item já purgado ou com Blob indisponível também retorna `404`.

Originais, thumbnails e previews de um item na lixeira retornam `404` em GET/HEAD. Os bytes permanecem armazenados e compartilhados, e um download já aberto pode terminar. Os metadados da lixeira podem informar que a imagem compartilhada está pronta; isso não concede acesso aos derivados. Reenviar o mesmo conteúdo enquanto o Asset está na lixeira mantém o conflito `asset_in_trash` e exige restauração explícita.

## Filtros, timeline e paginação

| Parâmetro em `/api/assets` | Uso |
| --- | --- |
| `limit` | 1–100, default 50. |
| `cursor` | Continuação retornada em `nextCursor`. |
| `imagesOnly` | Default false; true seleciona MIME detectado `image/*`. |
| `isFavorite` | Omitido: todos; true/false: filtra favorito. |
| `sort` | `uploadedAt` (default) ou `timeline`; valores são exatos. |

A timeline usa `/api/assets?imagesOnly=true&sort=timeline`. `sort=timeline` exige o filtro de imagens; combinação inválida retorna `400`. Ordena por captura UTC confiável, quando a imagem está pronta, ou por upload; captura EXIF sem offset continua preservada sem inventar UTC. A lixeira aceita `limit` e `cursor`, ordenando por `deletedAt`. Todas as ordens são decrescentes, com UUID como desempate consistente com PostgreSQL.

O cursor v2 é base64url canônico e associa proprietário, biblioteca/lixeira, filtros, ordenação, instante inicial e fronteira de data/UUID. Mudar filtros, ordem, conta ou escopo exige uma nova consulta sem cursor; incompatibilidade retorna `400 invalid_cursor`. Mudar `limit` é permitido. Clientes devem tratar o cursor como opaco. Cursors antigos da Fase 4 exigem reiniciar a listagem.

O instante inicial limita uploads novos entre páginas. Para ordenar a timeline, captura só é usada quando `processedAt` não ultrapassa esse instante; processamento tardio não desloca um item durante a paginação. Metadados retornados são atuais. Favoritos, exclusões e restaurações continuam refletindo alterações atuais: não há snapshot MVCC de toda a biblioteca entre requisições. Inicie nova listagem após essas alterações quando precisar de uma visão atualizada.

## Retenção e coleta

O Worker executa manutenção em paralelo às rotinas de upload/imagem. Purga Assets cujo `deletedAt <= agora - TrashRetention`, em lotes limitados. Depois procura Blobs `Ready` antigos sem **qualquer** Asset, incluindo referências de outras contas e da lixeira; também retoma Blobs já `Deleting`. Intenções `Staging` não são coletadas, pois podem pertencer a uma publicação em andamento.

Restauração e purge bloqueiam o mesmo Blob e Asset e reavaliam o estado após esperar. Se restauração vencer, o purge mantém o item; se purge vencer, restauração recebe `404`. Criação de referência e coleta usam o mesmo advisory de hash e lock de Blob. Um reenvio que encontre a geração em exclusão pode receber `blob_unavailable`; após a coleta, um novo upload usa outro UUID físico.

A coleta confirma `Deleting` no banco antes de excluir arquivos. Ela remove derivados confirmados e tentativas rastreadas, sincroniza suas exclusões e a do original e só então remove a linha Blob e seus jobs/imagem/reservas por cascade. Erros físicos mantêm a intenção, gerações e reservas para retry. Não existe endpoint HTTP de purge manual nesta entrega. Alterar a retenção pode antecipar a remoção definitiva de itens já existentes na lixeira.

Jobs de imagem pendentes não impedem coleta indefinidamente: tornam-se falha terminal quando o original está sendo coletado. Um lease ainda válido adia coleta. Além disso, toda execução de imagem usa uma conexão PostgreSQL dedicada, sem pool, com advisory compartilhado; a coleta tenta exclusividade sem esperar, protegendo inclusive um renderer que ainda esteja encerrando após expiração do lease. O monitor consulta a própria conexão a cada segundo e cancela o processamento quando ela é perdida; lease e conexão são revalidados antes de renderizar/publicar. PostgreSQL [documenta os locks de sessão e de transação](https://www.postgresql.org/docs/18/explicit-locking.html#ADVISORY-LOCKS).

Leitores de derivados podem adiar a exclusão física, mantendo `Deleting` até retry. Originais já abertos permitem terminar o download após unlink. A identidade física por geração impede que uma limpeza antiga apague um reupload do mesmo hash. Reinícios reais, durabilidade do filesystem e procedimentos coordenados de PostgreSQL/Worker ainda serão verificados no Arch na Fase 7; esta entrega não equivale a simulação de queda de energia.

## Histórico de uploads

Um upload concluído mantém estado `Completed` e o mesmo ID/resultado do job quando seu Asset é purgado. O purge destaca a referência ao Asset na mesma transação e grava `resultPurgedAt` UTC. Consultas passam a retornar `result: null` com essa data, sem transformar sucesso em falha. Repetir a conclusão retorna o histórico e não recria um item purgado.

## Configuração e migration

Opções na seção `Library`, validadas na inicialização da API e do Worker:

| Opção | Default |
| --- | --- |
| `TrashRetention` | 30 dias. |
| `UnreferencedBlobGracePeriod` | 1 dia, contado desde criação do Blob. |
| `MaintenanceInterval` | 1 minuto. |
| `MaintenanceBatchSize` | 100. |

Retenção e graça devem ser positivas e de até 365 dias, intervalo positivo até 30 dias e lote entre 1–1000. Exemplo: `NEXORA_Library__TrashRetention=30.00:00:00`. Compartilhe opções entre API/Worker, junto às configurações de [development.md](development.md).

`20261008125535_LibraryTrashAndPurge` acrescenta histórico de resultados purgados e índices de favoritos, lixeira e coleta. Migrations continuam explícitas. O `Down` recusa reversão se houver uploads com resultados purgados: não é possível reconstruir o Asset removido. Restauração de backup consistente é necessária nesse caso. Reverter schema não recupera originais ou Assets já purgados; mantenha backups independentes.

Evidência de build/testes e o próximo marco estão em [status-and-roadmap.md](status-and-roadmap.md).

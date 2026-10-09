# Sincronização da biblioteca

A Fase 8 acrescenta um snapshot paginado estável e um diário durável de alterações por proprietário. A sincronização inclui biblioteca ativa, favoritos, metadados de imagem e lixeira. O cliente guarda cursores opacos e aplica exclusões físicas; `GET /api/assets` e `GET /api/trash` continuam sendo consultas de navegação, conforme [library.md](library.md).

## Snapshot inicial

`GET /api/sync?limit=100` exige autenticação e retorna:

```json
{
  "items": [],
  "nextCursor": null,
  "cursor": "<cursor de alterações>"
}
```

O limite padrão é 50, de 1 a 100. A primeira página fixa a sequência do diário daquele proprietário. As páginas seguintes, pedidas com `GET /api/sync?limit=100&cursor=<nextCursor>`, usam a mesma fronteira e retornam os últimos metadados de cada Asset até aquele ponto, ordenados por UUID. Mudanças posteriores, inclusive purge, não alteram esse snapshot em andamento. Assets na lixeira aparecem com `deletedAt`; Assets já purgados naquela fronteira não aparecem.

`nextCursor` serve somente à paginação do snapshot. `cursor` serve somente a `/api/sync/changes`. Termine todas as páginas antes de substituir o cache completo e use o cursor de alterações da última página. As strings protegidas podem diferir entre respostas mesmo quando representam a mesma fronteira; não compare nem decodifique seu conteúdo no cliente.

## Alterações incrementais

`GET /api/sync/changes?limit=100&cursor=<cursor>` retorna `items`, `cursor` e `hasMore`. Cada item tem `sequence`, `kind`, `assetId` e `asset`:

| Kind | Payload | Aplicação no cache |
| --- | --- | --- |
| `upsert` | Snapshot completo do Asset. | Substituir os metadados daquele ID; `deletedAt` define presença na lixeira. |
| `purge` | `asset: null`. | Remover o ID do cache, mesmo que estivesse na lixeira. |

As sequências são crescentes por proprietário. Continue usando o novo `cursor` enquanto `hasMore` for verdadeiro. Persista os metadados aplicados e o novo cursor juntos; uma interrupção antes da gravação permite repetir a página sem perder a mudança. Uma página vazia com `hasMore: false` é válida. O diário não transfere originais nem derivados: downloads continuam autorizados pelos endpoints de conteúdo.

Triggers PostgreSQL registram publicação, edição de Asset, entrada/saída da lixeira, purge e mudanças de imagem na transação que altera o catálogo, incluindo os caminhos de SQL usados pelo Worker. A linha do contador do proprietário permanece bloqueada até o commit; outra transação não publica uma sequência posterior antes de a anterior ser confirmada. O payload histórico permanece imutável e permite reconstruir páginas de um snapshot antigo. Essa primeira entrega conserva todo o diário; retenção, compactação e um limite de histórico ainda precisam de um contrato próprio.

## Cursores inválidos e recuperação

Os cursores são protegidos com Data Protection e vinculados ao proprietário, ao tipo de consulta, à época (`Epoch`) e à sequência. Não contêm autorização para ler outra conta. As chaves persistentes da API também são necessárias para ler esses cursores.

- `400 invalid_cursor`: formato, proteção, proprietário ou tipo incompatível. Reinicie o snapshot sem cursor.
- `409 sync_reset_required`: época diferente ou sequência do cliente maior que o contador atual. Reinicie o snapshot sem cursor.
- `401`: sessão inválida/revogada; o cliente exige novo login.

O cache móvel substitui o snapshot apenas depois de receber todas as páginas. Falha de rede ou cancelamento durante essa reconstrução preserva o último cache completo. Durante sincronização incremental, cada página confirmada é persistida atomicamente com seu cursor.

## Restauração exige uma nova época

**Depois de restaurar consistentemente banco, storage e chaves, rotacione obrigatoriamente `Epoch` de todos os proprietários antes de iniciar escritores ou permitir que clientes se reconectem.** Execute no banco restaurado, com a aplicação parada e a role administrativa/dona apropriada:

```sql
UPDATE public."AssetSyncStates" SET "Epoch" = gen_random_uuid();
```

Preserve o contador e as entradas recuperadas. A rotação invalida os cursores da linha do tempo anterior e força um novo snapshot; não revoga tokens de autenticação nem substitui a recuperação das chaves. Ao comparar o banco com a evidência do backup, considere a nova época uma alteração administrativa planejada.

Só detectar um contador menor é insuficiente: o banco recuperado pode alcançar novamente uma sequência antiga e aceitar um cursor cujo cache pertence a outra linha do tempo, omitindo alterações. A época precisa mudar mesmo quando os contadores do backup e do cliente coincidem. O `restore.sh` executa a rotação no banco isolado após conferir o histórico EF, sob `nexora_restore`, e interrompe em caso de erro. Releases anteriores sem a tabela de sincronização continuam aceitas. Uma restauração por outro procedimento também precisa dessa operação antes de abrir o acesso. O procedimento completo está em [backup-and-restore.md](backup-and-restore.md).

Uma fila local também pode referenciar uploads posteriores ao snapshot recuperado. A retomada explícita trata uma sessão remota ausente usando o mesmo UUID de criação, após verificar a cópia privada; essa recuperação é independente do cursor da biblioteca e não inicia envio automático. O contrato de remoção após respostas perdidas e os limites de recuperação estão em [mobile.md](mobile.md).

## Verificação

`tests/Nexora.IntegrationTests/SyncApiTests.cs` verifica snapshot com mudanças entre páginas, alterações e purges, proprietário, autenticação, limites e recuperação após troca de época/rollback do contador. `apps/Nexora.Mobile.Tests/OfflineTests.cs` verifica aplicação incremental, persistência, separação de servidor/conta e preservação do cache anterior quando a reconstrução falha. `ScopeConcurrencyTests.cs` verifica que uma troca de escopo aguarda leituras, hashing e requisições pendentes sem enviar dados privados ao novo servidor. As evidências da revisão corrente devem ser registradas em [status-and-roadmap.md](status-and-roadmap.md).

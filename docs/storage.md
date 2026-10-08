# Armazenamento e identidade do conteúdo

A Fase 3 introduziu modelos e casos de uso internos para importar conteúdo. A Fase 4 acrescentou uploads retomáveis, downloads, Worker e limpeza durável. A Fase 5 aplica publicação imutável e tentativas rastreadas a thumbnails/previews; seus contratos estão em [images.md](images.md), e os de arquivos em [uploads.md](uploads.md).

## Modelo e identidade

`Blob` representa conteúdo físico imutável: UUID, SHA-256 em hexadecimal minúsculo, tamanho, MIME detectado, chave interna, criação UTC e estado `Staging`, `Ready` ou `Deleting`. O hash tem índice único no PostgreSQL.

`Asset` representa um item de uma conta: UUID, proprietário, Blob, nome original, upload UTC, favorito e `DeletedAt`. O par `(OwnerId, BlobId)` é único, inclusive quando o item está na lixeira. A mesma sequência de bytes pode ter Assets de proprietários diferentes, compartilhando um Blob físico.

Um reenvio da mesma conta preserva ID, nome editado, data de upload e favorito do Asset existente. Se estiver na lixeira, o caso de uso retorna `AssetInTrash` com o item da própria conta e exige restauração explícita. Não há `ReferenceCount` persistido. A Fase 6 implementa purge e coleta somente após confirmar ausência de qualquer Asset, incluindo a lixeira e outras contas. Estado `Deleting`, geração física distinta e limpeza sincronizada permitem retry sem apagar um reupload posterior. Os contratos de restauração, retenção e locks estão em [library.md](library.md).

`BlobImage` pertence ao Blob e compartilha dimensões, captura e estado entre Assets dos mesmos bytes. Thumbnail e preview publicados usam uma geração de tentativa distinta da identidade física do original. Confirmar ambos no banco torna a imagem `Ready`; falhar nunca remove ou invalida o Blob original.

Nome original é metadado, nunca caminho físico. O domínio limita nomes a 255 caracteres e rejeita valores em branco, controles, `/`, `\`, `:`, `.` e `..`. As chaves físicas são geradas por UUID e têm representação canônica, por exemplo:

```text
blobs/a8/52/a852127452ae46efa5ccdcf50ffadac8
```

O hash identifica conteúdo; o UUID identifica a geração física. Reutilizar um hash após a remoção definitiva do Blob criará outra geração, evitando que uma limpeza antiga atinja a nova chave.

## Contratos e composição

| Contrato | Responsabilidade |
| --- | --- |
| `IBlobStorage` | Publicar conteúdo imutável, abrir stream de leitura, consultar tamanho e excluir de forma idempotente. |
| `ITemporaryStorage` | Criar conteúdo temporário com limite real de bytes, SHA-256 e MIME preliminar; ler, consultar e excluir por chave interna. |
| `IContentCatalog` | Registrar a intenção de publicação e concluir Blob/Asset em transações PostgreSQL específicas. |
| `IAssetIngestionService` | Coordenar validação, staging, hashing, publicação, deduplicação e limpeza. |
| `ITrackedTemporaryStorage` | Criar montagem por chave de tentativa persistida e limpar seus arquivos com exclusão exclusiva. |
| `ITrackedBlobStorage` | Publicar Blob usando uma chave de tentativa persistida para o arquivo intermediário. |
| `IDerivativeStorage` | Publicar PNG imutável por geração/tipo, ler, verificar hash/tamanho e limpar uma tentativa preservando resultado confirmado. |
| `IImageWorkStore` | Coordenar fila, leases, metadados, reservas e cleanup de imagens no PostgreSQL. |

Os contratos estão em Application. Domain não depende de EF Core, ASP.NET ou filesystem. Infrastructure implementa `LocalFileBlobStorage`, `LocalFileTemporaryStorage` e `PostgresContentCatalog`. Os adaptadores são singletons; catálogo e caso de uso são scoped, com um DbContext por operação. Operações concorrentes devem usar scopes distintos.

`ImportAsync` recebe proprietário, nome, tamanho esperado, SHA-256 esperado opcional e um stream legível. O chamador deve obter o proprietário de um contexto autenticado; os endpoints HTTP obtêm essa identidade da sessão autenticada. O caso de uso não exige streams seekable e não dispõe o stream de entrada do chamador.

Os resultados são `Created`, `Reused`, `AssetInTrash` e `BlobUnavailable`. Os snapshots de Asset não expõem chave física, caminho absoluto nem hash. `CompleteAsync` é uma operação interna confiável, chamada somente depois de publicação ou verificação física bem-sucedida; ela não substitui essa verificação.

## Fluxo de importação

1. Validar proprietário, nome, tamanho e formato do hash esperado.
2. Gravar um temporário, limitando os bytes efetivamente recebidos e calculando SHA-256 no servidor. Tamanho ou hash divergentes impedem a publicação.
3. Em transação curta, verificar a conta e obter ou criar Blob `Staging` pelo hash. A intenção é confirmada antes da publicação física.
4. Se o Blob estiver `Staging`, publicar a partir do temporário, verificando novamente tamanho e hash. Uma chave existente só é aceita quando seu conteúdo é idêntico.
5. Se já estiver `Ready`, verificar tamanho e SHA-256 do conteúdo físico antes de reutilizá-lo. Ausência ou corrupção gera erro de integridade; conteúdo existente não é sobrescrito para esconder a falha.
6. Em nova transação, confirmar o estado do Blob, marcar `Ready` e criar/reutilizar o Asset. Para JPEG/PNG/WebP, registrar imagem e job de processamento na mesma transação, caso ainda não existam. Estado `Deleting` impede criação de referência e retorna indisponibilidade.
7. Excluir o temporário de forma repetível, inclusive quando a operação falha. Se operação e limpeza falharem, ambas as causas permanecem em `AggregateException`.

O catálogo serializa conteúdo pelo mesmo advisory lock transacional derivado do SHA-256, seguido de locks de Blob e Asset. Índices únicos também protegem a identidade no banco. As transações não permanecem abertas durante a cópia do arquivo. O mecanismo de [locks PostgreSQL](https://www.postgresql.org/docs/18/explicit-locking.html) permite coordenar processos distintos.

## Filesystem local

`Storage:RootPath` continua configurado pelo operador; em produção, a raiz prevista é `/srv/nexora`. Os adaptadores criam diretórios somente quando executam operações de escrita. A construção dos serviços e os health checks não inicializam storage de conteúdo.

A disposição usada é:

```text
<RootPath>/
  blobs/<prefixos>/<blob-uuid>
  thumbnails/<prefixos>/<attempt-uuid>.png
  previews/<prefixos>/<attempt-uuid>.png
  thumbnails/<prefixos>/<attempt-uuid>.png.publishing
  previews/<prefixos>/<attempt-uuid>.png.publishing
  temp/<temporary-uuid>.chunk
  temp/<temporary-uuid>.part
  temp/<publication-uuid>.publishing
```

Os buffers de leitura/escrita de originais têm 64 KiB. Upload e download usam streaming sem alocação proporcional ao arquivo completo. A decodificação de imagem usa buffers limitados no processo filho e PNGs limitados em memória; é uma operação diferente, descrita em [images.md](images.md). `.part` e `.publishing` representam tentativas incompletas. O UUID da tentativa está persistido antes da escrita; um retry limpa tentativas anteriores antes de começar nova montagem ou imagem. A montagem `.chunk` é registrada antes de remover chunks de origem e permanece referenciada até a limpeza terminal.

Os diretórios de storage recebem ACL para o usuário Windows atual e SYSTEM, ou modo `0700` no Linux; arquivos criados no Linux usam `0600`. Os adaptadores verificam a raiz, seus ancestrais, diretórios internos e arquivos, recusando symlinks/junctions e chaves não canônicas. O usuário do serviço e o administrador são confiáveis: outro processo com a mesma credencial não deve substituir diretórios durante operações.

A exclusão de tentativas e órfãos exige acesso exclusivo ao arquivo. A restrição de compartilhamento do Windows e `flock` no adaptador Linux impedem que a manutenção apague um arquivo enquanto um escritor do Nexora ainda o utiliza. Um processo que perdeu seu lease não pode confirmar estado no banco com o token anterior; seu arquivo intermediário permanece protegido até o handle fechar.

O conteúdo é sincronizado antes da publicação com [FileStream.Flush(true)](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush?view=net-10.0). No Windows, a publicação usa movimento sem sobrescrita. No Linux, usa `renameat2(RENAME_NOREPLACE)` e sincroniza com `fsync` a cadeia de diretórios e os pais alterados. A escrita também sincroniza diretórios já existentes: eles podem ter sido criados por uma operação concorrente ainda sem flush do pai. Temporários e Blobs precisam ficar no mesmo filesystem montado; uma travessia entre mounts é recusada, sem fallback para cópia. Veja as garantias de [rename](https://man7.org/linux/man-pages/man2/rename.2.html) e [fsync de diretórios](https://man7.org/linux/man-pages/man2/fsync.2.html).

Se a sincronização falhar depois de publicar um Blob, o arquivo publicado é preservado e a falha é propagada. O catálogo não o declara pronto prematuramente. Um temporário ainda não entregue ao chamador pode ser descartado com segurança quando sua sincronização falha.

## MIME e originais

A detecção preliminar reconhece assinaturas de JPEG, PNG e WebP, sem usar extensão ou MIME do cliente. Demais conteúdos recebem `application/octet-stream`. Reconhecer a assinatura apenas seleciona trabalho de imagem; não comprova validade. A Fase 5 verifica container, tamanho, hash, codec, dimensões/pixels e limites antes de publicar PNGs reencodificados. Originais continuam opacos e são baixados como attachment, inclusive se o processamento falhar ou recusar animação.

A API serve somente derivados confirmados, autenticados e verificados por hash/tamanho a cada acesso. A publicação valida envelope PNG e CRCs sem carregar um codec na API. Essa validação e os metadados completos são pré-condições do estado de imagem `Ready`.

## Recuperação e limites desta entrega

PostgreSQL e filesystem não compartilham uma transação. Uma falha após registrar intenção ou publicar conteúdo pode deixar Blob `Staging`. Um reenvio com os mesmos bytes encontra a mesma geração, verifica/publica seu objeto e conclui o Asset. Um Blob `Deleting` não volta a `Ready` por reenvio.

Um encerramento abrupto do processo também pode deixar temporários, pois não há execução de `finally` garantida. O Worker da Fase 4 recupera jobs com lease expirado, verifica a montagem persistida ou a recria, retoma publicação da mesma geração e confirma Blob/Asset/upload/job em uma transação. A manutenção remove temporários terminais antes de liberar a reserva e pode coletar arquivos temporários gerados, sem referência e antigos. Isso não constitui uma varredura de reparação de todos os Blobs nem substitui backup. As fronteiras e os casos de recuperação estão em [uploads.md](uploads.md); testes de falha injetada não representam queda de energia.

A importação interna aplica o tamanho esperado como limite da operação. Uploads HTTP da Fase 4 acrescentam limite de 20 GiB por arquivo, reservas globais, margem livre, expiração e `/api/storage`, com defaults configuráveis em `Uploads`. Os contratos internos são fronteiras confiáveis entre serviços; clientes só podem enviar conteúdo pelas rotas autenticadas que aplicam essas políticas.

A validação local desta entrega utiliza Windows e PostgreSQL nativo. A execução dos caminhos nativos Linux e a persistência após reinício no filesystem de produção deverão ser verificadas no Arch antes de disponibilizar dados pessoais.

## Migrations e testes

`20261006114248_ContentBlobsAssets` acrescenta Blobs e Assets; `20261008112515_UploadsDurableJobs` acrescenta uploads e trabalho durável; `20261008122015_ImageMetadataAndDerivatives` acrescenta imagens; `20261008125535_LibraryTrashAndPurge` acrescenta resultados purgados e índices de biblioteca/coleta. Migrations permanecem explícitas:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet ef database update --project src/Nexora.Infrastructure --startup-project src/Nexora.Api
dotnet test Nexora.sln -c Release
```

UnitTests cobrem regras de domínio, chaves, hashing e cancelamento. IntegrationTests usam diretórios temporários próprios e bancos `nexora_it_<guid>` para verificar storage, deduplicação, conflitos e recuperação por reenvio. Preparação e credenciais de testes estão em [development.md](development.md); pendências estão em [status-and-roadmap.md](status-and-roadmap.md).

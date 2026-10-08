# Imagens, metadados e derivados

A Fase 5 acrescenta processamento de imagens ao fluxo de [uploads](uploads.md). Os originais continuam imutáveis e disponíveis para download. JPEG, PNG e WebP estáticos podem gerar thumbnail e preview; imagens animadas, formatos sem suporte, arquivos malformados ou acima dos limites continuam armazenados como originais.

## Contratos HTTP e estados

As rotas exigem bearer válido e incluem o proprietário na consulta. Assets de outra conta, excluídos ou sem Blob `Ready` retornam `404`. Somente derivados de imagem `Ready` são servidos; imagem ausente, `Pending`, `Processing` ou `Failed` também retorna `404`, com ProblemDetails `resource_not_found`.

| Método | Rota | Resposta |
| --- | --- | --- |
| `GET`, `HEAD` | `/api/assets/{id}/thumbnail` | PNG com lado máximo configurado, inicialmente 256 px. |
| `GET`, `HEAD` | `/api/assets/{id}/preview` | PNG com lado máximo configurado, inicialmente 1280 px. |
| `GET` | `/api/assets`, `/api/assets/{id}` | Asset com campo `image` opcional, também presente no resultado de upload. |

Os derivados usam `Content-Type: image/png`, `Content-Disposition: inline`, `X-Content-Type-Options: nosniff`, `Content-Security-Policy: default-src 'none'; sandbox` e `Cache-Control: no-store`. A API verifica tamanho e SHA-256 persistidos a cada acesso, incluindo `HEAD`, antes de abrir o stream. Um derivado registrado, mas ausente ou corrompido, recebe erro sanitizado `503`; o original não é apagado. Não há acesso estático aos diretórios.

Range e condicionais são tratados pelo resultado de stream nativo do ASP.NET Core, com `206`, `304` e `416` conforme a requisição. `Last-Modified` usa `processedAt`; o ETag identifica geração física e tipo, por exemplo `"<generation-uuid>-Thumbnail"`. IDs de geração, hashes e caminhos físicos não aparecem no DTO de imagem.

`image` é `null` quando não há registro de processamento. Quando presente, contém `state`, `width`, `height`, `capturedAtLocal`, `capturedAtUtc`, `processedAt`, `hasThumbnail`, `hasPreview` e `failureCode`. Estados usam a serialização numérica padrão:

| `image.state` | Significado |
| --- | --- |
| `0` | `Pending`: intenção aguardando processamento, retry ou capacidade. |
| `1` | `Processing`: tentativa com lease válido e espaço reservado. |
| `2` | `Ready`: os dois derivados e seus metadados foram confirmados. |
| `3` | `Failed`: erro permanente ou tentativas esgotadas. |

`hasThumbnail` e `hasPreview` só são verdadeiros em `Ready`. O upload pode estar `Completed` enquanto a imagem continua pendente: concluir o arquivo não aguarda a decodificação. Consulte novamente os detalhes do Asset para acompanhar a imagem. Não existe endpoint HTTP de retry manual de imagens nesta entrega.

## Identidade, orientação e captura

`BlobImage` tem uma linha por Blob, compartilhada por todos os Assets que referenciam os mesmos bytes. A conclusão de um upload ou da importação interna cria `BlobImage` e job `ProcessImage` na mesma transação que confirma o original. Unicidade e locks de Blob impedem dois jobs de imagem para uma duplicata. Um reenvio não muda o nome ou os metadados do Asset existente e não reinicia uma imagem terminal.

Skia aplica a orientação codificada ao renderizar os derivados. `width` e `height` representam a imagem após orientação; thumbnail e preview preservam proporção e não ampliam imagens menores. Ambos são PNG reencodificados, sem copiar os blocos de metadados do original. A assinatura preliminar de upload apenas decide se o Blob entra na fila; o processamento verifica hash, tamanho, container e codec antes de aceitar pixels.

O extrator lê EXIF `DateTimeOriginal`, frações de segundo válidas e offset original quando presente. `capturedAtLocal` preserva o horário de origem como `DateTimeKind.Unspecified`, armazenado em PostgreSQL `timestamp without time zone`. `capturedAtUtc` só é preenchido quando data e offset original válidos permitem conversão confiável para UTC. Offset ausente, inválido ou `-00:00` não autoriza inferir UTC pelo fuso do servidor. Registros de captura conflitantes ou metadados ilegíveis deixam a captura ausente e não impedem um preview válido.

Captura não substitui `uploadedAt`. A [timeline da biblioteca](library.md) usa upload quando não houver captura UTC confiável e fixa a fronteira de processamento entre páginas. Localização, câmera e edição de metadados ficam para uma evolução posterior.

## Worker e processo de imagem

O mesmo executável Worker mantém uma montagem e uma imagem por vez, em rotinas separadas. Cada rotina usa scopes próprios para processamento, renovação e manutenção. Mais processos Worker aumentam a concorrência total; o desenho inicial de produção prevê uma instância. As instruções para iniciá-lo estão em [development.md](development.md).

A decodificação nativa roda em um processo filho `Nexora.Worker render-image`, iniciado sem janela e sem shell. O modo de renderização não inicializa Host, PostgreSQL, Data Protection ou configuração de serviços. O processo recebe por stdin uma linha JSON com expectativas e limites, seguida dos bytes originais; devolve por stdout uma resposta JSON limitada, com PNGs em base64 ou código de falha. Stderr também tem limite e não é incorporado aos logs. Nenhuma senha, token, connection string ou caminho de storage é passada nesse protocolo.

O ambiente do filho é reconstruído com variáveis mínimas necessárias ao runtime/sistema, diagnósticos .NET desativados, fuso UTC e limite de heap gerenciado. Overrides `NEXORA_` e segredos do processo pai não são propagados. O filho executa com as mesmas credenciais do Worker: separar processo e ambiente **não constitui uma sandbox do sistema operacional** nem retira suas permissões de filesystem.

O watchdog encerra a árvore do filho ao exceder tempo, receber cancelamento, perder o lease, receber protocolo acima dos limites ou observar working set acima do orçamento. Memória é amostrada aproximadamente a cada 50 ms; esse controle admite picos entre amostras e não é um limite rígido de memória nativa imposto pelo SO. `DOTNET_GCHeapHardLimit` limita o heap gerenciado, não todas as alocações do codec; com os defaults, vale 256 MiB. A configuração do [GC .NET](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector#heap-hard-limit) descreve essa distinção. Na Fase 7, limites de serviço como `MemoryMax` e as dependências nativas serão configurados e verificados no Arch.

## Publicação, reservas e recuperação

1. Reclamar o job sob lock de capacidade, Blob/imagem e job. Reservar espaço antes da escrita e persistir UUID da tentativa em `BackgroundJobAttempts` antes de publicar derivados.
2. Limpar gerações de tentativas anteriores com acesso exclusivo; um escritor anterior ativo impede sua exclusão.
3. Ler e verificar o original, processar no filho com limites e validar os PNGs recebidos.
4. Publicar thumbnail e preview imutáveis sob o UUID da tentativa. Arquivos `.publishing` são sincronizados e movidos sem sobrescrita para a chave final.
5. Confirmar metadados, hashes, comprimentos, geração física, `BlobImage.Ready` e job concluído em uma transação com lease válido e Blob ainda `Ready`.
6. Na manutenção terminal, excluir tentativas abandonadas e intermediários, preservar a geração confirmada e liberar a reserva somente após todas as exclusões/sincronizações.

A disposição é `thumbnails/<prefixos>/<attempt-uuid>.png` e `previews/<prefixos>/<attempt-uuid>.png`. O UUID físico distingue tentativas; uma limpeza antiga não remove a geração confirmada de um retry posterior. O adaptador valida envelope PNG, dimensões máximas e CRCs sem executar um decoder no processo da API. Diretórios privados, confinamento, publicação e locks de arquivo reutilizam as proteções de [storage](storage.md).

Uma imagem reserva `2 × MaximumDerivativeBytes`: 16 MiB com os defaults. Essas reservas entram no mesmo orçamento global de uploads de 50 GiB, na margem livre de 50 GiB e em `reservedBytes` de `/api/storage`. A admissão também considera temporários físicos e reservas existentes de uploads/imagens. Falta de espaço adia o job como `Pending`, com `insufficient_storage`, sem consumir uma tentativa de processamento. Retries mantêm a reserva existente; se o limite de derivados aumentar, só o adicional é admitido sob o lock de capacidade. Ela permanece em estados terminais até cleanup.

Se houver interrupção entre publicação e commit, a tentativa permanece registrada; o próximo processamento limpa a geração abandonada e usa outra. Se o commit tiver sido confirmado sem resposta, a manutenção consulta o banco e preserva a geração `Ready`. Um token de lease antigo não confirma metadados ou torna pronto um Blob em exclusão. Falhas de limpeza mantêm referências e reserva para nova manutenção.

A manutenção também enfileira Blobs antigos `Ready`, reconhecidos como JPEG/PNG/WebP e sem `BlobImage`, em lotes idempotentes de até 100 por execução, inicialmente a cada minuto. Isso permite processar originais já existentes antes da migration. A rotina de imagens não coleta originais; purge e coleta pertencem à manutenção da [biblioteca](library.md). A Fase 6 protege toda execução de imagem com advisory compartilhado em conexão dedicada e adia coleta enquanto houver um processor ativo, mesmo com lease expirado. Perda dessa conexão cancela processamento; lease/conexão são revalidados antes de renderizar/publicar.

## Opções e falhas

As opções pertencem à seção `Images`, compartilhada entre API e Worker e validada na inicialização. Exemplos: `NEXORA_Images__MaximumInputBytes` e `NEXORA_Images__ProcessingTimeout=00:00:30`. O limite de entrada de imagem é independente dos 20 GiB de upload: um original maior pode ser armazenado e baixado, mas seu processamento de imagem falha pelo limite.

| Opção | Default |
| --- | --- |
| `MaximumInputBytes` | 32 MiB |
| `MaximumPixels` | 24.000.000 |
| `MaximumDecodedBytes` | 128 MiB para bitmap de origem e maior derivado |
| `MaximumDimension` | 16.384 px por lado |
| `ThumbnailSize` / `PreviewSize` | 256 / 1280 px |
| `MaximumDerivativeBytes` | 8 MiB por PNG |
| `MaximumProcessMemoryBytes` | 512 MiB de working set observado |
| `ProcessingTimeout` | 30 segundos |
| `LeaseDuration` / `LeaseRenewInterval` | 1 minuto / 20 segundos |
| `MaximumJobAttempts` / `RetryDelay` | 3 / 30 segundos |
| `PollInterval` / `MaintenanceInterval` | 2 segundos / 1 minuto |

A validação exige limites positivos e coerentes, memória do processo compatível com buffers/runtime, preview ao menos do tamanho do thumbnail, durações de até 30 dias e renovação antes de metade do lease. A decodificação verifica dimensões/pixels antes de alocar o bitmap principal; limites de bytes, hashes e cancelamento são conferidos na leitura. Containers truncados ou imagens animadas são recusados. A API continua servindo o original como attachment independentemente do sucesso da imagem.

| Situação | `image.failureCode` principal |
| --- | --- |
| Original acima do limite de entrada | `image_input_too_large` |
| Hash/tamanho do original divergente | `image_integrity_failed` |
| Container/decodificação inválida | `image_invalid` |
| Formato/animação sem suporte | `image_format_unsupported` / `image_animation_unsupported` |
| Pixels/dimensões ou memória de bitmaps acima do limite | `image_pixel_limit_exceeded` / `image_decoded_limit_exceeded` |
| Memória observada ou alocação acima do limite | `image_memory_limit_exceeded` |
| PNG acima do limite ou inválido | `image_derivative_too_large` / `image_invalid_derivative` |
| Watchdog de tempo | `image_processing_timeout` |
| Filho indisponível, encerrado ou protocolo inválido | `image_renderer_unavailable` / `image_renderer_failed` / `image_protocol_invalid` / `image_protocol_limit` |
| Capacidade insuficiente | `insufficient_storage` |
| Original indisponível ou sem estado pronto | `image_source_missing` / `image_original_unavailable` |
| Armazenamento indisponível/caminho recusado | `storage_unavailable` / `unsafe_storage_path` |
| Limite de tentativas consumido | `attempts_exhausted` |

Falhas transitórias recebem atraso e tentativas limitadas; erros permanentes permanecem `Failed`. Não há descarte do original, reset automático de uma falha terminal ou retry manual por HTTP. Os logs registram IDs, estado, código e tipo da falha, sem nomes pessoais, conteúdo, capturas, credenciais ou stderr nativo.

## Dependências, migrations e validação

As versões centralizadas são `SkiaSharp` e `SkiaSharp.NativeAssets.Linux.NoDependencies` 4.153.1 e `MetadataExtractor` 2.9.3. [SkiaSharp](https://github.com/mono/SkiaSharp) fornece o codec/renderização; [MetadataExtractor](https://github.com/drewnoakes/metadata-extractor-dotnet) fornece leitura EXIF. Incluir binários Linux no restore não comprova funcionamento no Arch; carga nativa e processamento real serão verificados no ambiente de produção antes do deploy.

A migration `20261008122015_ImageMetadataAndDerivatives` acrescenta BlobImages e generaliza os targets de BackgroundJobs. A aplicação não executa migrations automaticamente; aplique explicitamente antes de iniciar API/Worker da nova versão. Para validação, execute restore em locked mode, build Release e testes conforme [development.md](development.md).

O `Down` remove registros BlobImage e jobs/tentativas de imagem antes de restaurar a exigência de alvo de upload. Ele preserva tabelas e registros de originais/uploads da Fase 4, mas não preserva o histórico/metadados de processamento de imagens e não remove PNGs do filesystem. Não trate rollback de schema como operação sem perda: mantenha backup consistente, pare gravações/Worker e planeje restauração de banco/storage antes de uma reversão. Publicações remanescentes de uma reversão não são coletadas automaticamente nesta entrega.

Código de referência: `b4ecf15`. Aceite local verificado: restore com dependências travadas, build Release sem avisos/erros e 189 testes aprovados (50 unitários e 139 de integração), sem falhas ou ignorados. A migration foi aplicada explicitamente ao banco de desenvolvimento; API, Worker, health checks e contratos OpenAPI também foram conferidos pela CLI. A evidência está em [status-and-roadmap.md](status-and-roadmap.md). Testes locais no Windows não comprovam funcionamento nativo Linux, durabilidade após queda de energia nem recuperação de backup; essas verificações continuam na Fase 7.

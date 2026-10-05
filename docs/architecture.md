# Arquitetura do Nexora

Nexora é uma nuvem privada para fotos, vídeos e arquivos pessoais, executada em um único servidor Arch Linux com aproximadamente 500 GB disponíveis. O MVP terá uma conta administrativa, acesso privado por Tailscale e uma API ASP.NET Core. O cliente mobile será uma etapa posterior.

Este documento registra o desenho aprovado. A Fase 1 implementa somente a fundação técnica; os modelos e fluxos descritos para fases posteriores ainda não estão disponíveis na API.

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

A árvore abaixo é a estrutura alvo. Worker e testes de domínio serão criados quando tiverem comportamento a implementar; a Fase 1 mantém apenas os projetos necessários à fundação.

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
```

`Domain` contém entidades e invariantes sem dependência de ASP.NET Core, EF Core ou filesystem. `Application` coordena casos de uso e define os contratos necessários. `Infrastructure` implementa persistência, storage e integrações. `Api` concentra HTTP, autenticação, validação de entrada e composição de dependências. `Worker` será o host de processamento e manutenção em background.

Não introduzir repositório genérico, mediator, event bus ou camadas adicionais sem uma necessidade concreta. Manter dependências direcionadas para Domain/Application e reutilizar recursos nativos do framework.

## Modelo previsto

| Modelo | Responsabilidade |
| --- | --- |
| Usuário Identity | Conta administrativa e credenciais verificadas pelo ASP.NET Core Identity. |
| Device | Identidade do dispositivo, proprietário, plataforma, atividade e revogação. |
| AuthSession | Sessão autenticada vinculada ao usuário e dispositivo, com expiração e revogação. |
| RefreshToken | Hash do token, sessão, expiração, consumo, sucessor e revogação para rotação e detecção de reutilização. |
| Blob | Conteúdo imutável: SHA-256, tamanho, chave interna, MIME detectado e estado de publicação. |
| Asset | Referência do proprietário ao Blob: nome original, upload, data da foto, favorito e lixeira. |
| UploadSession | Sessão retomável, tamanho esperado, chunks, estado, atividade e resultado. |
| UploadChunk | Índice, tamanho, SHA-256 e localização interna de um chunk confirmado. |
| BackgroundJob | Trabalho durável, tentativas, próxima execução e lease do worker. |

Separar Blob e Asset permite deduplicar bytes sem acoplar nome, favorito ou exclusão à cópia física. SHA-256 é calculado pelo servidor e tem unicidade no Blob. Um Asset por `(OwnerId, BlobId)` evita duplicatas lógicas mesmo em conclusões concorrentes.

Enviar novamente conteúdo idêntico retorna o Asset ativo existente, sem alterar seus metadados. Se o Asset estiver na lixeira, retornar um conflito que exige restauração explícita. Concluir novamente a mesma UploadSession retorna seu resultado anterior.

Não manter `ReferenceCount` persistido inicialmente: consultar referências incluindo a lixeira evita divergência de contadores. A Fase 5 acrescenta dimensões, orientação e data extraída da imagem. Câmera e geolocalização ficam para um incremento posterior à Fase 5. Preservar datas EXIF sem fuso como dados locais de origem, sem adivinhar que representam UTC; timestamps operacionais da aplicação são UTC.

## Autenticação e acesso

Criar o administrador por um comando local de bootstrap, com senha solicitada sem eco e sem registrá-la em logs; não oferecer registro público. A implementação da autenticação pertence à Fase 2; a Fase 1 não cria contas nem emite tokens.

O login verificará credenciais pelo Identity e emitirá access token opaco do mecanismo nativo de bearer authentication do ASP.NET Core, válido por cinco minutos. AuthSession terá duração de 30 dias. O refresh token será aleatório, com pelo menos 256 bits de entropia, vinculado à sessão/dispositivo; guardar somente seu hash, rotacionar a cada uso e revogar a família diante de reutilização.

Cada requisição autenticada verifica no banco que a sessão está ativa e que seu dispositivo pertence ao usuário e não foi revogado. Revogar o dispositivo revoga suas sessões, bloqueando inclusive access tokens ainda não expirados. Device representa o cadastro vinculado à autenticação; nome, plataforma e modelo informados pelo cliente não constituem atestação de hardware.

As chaves de ASP.NET Core Data Protection que protegem os tokens devem persistir em diretório permanente fora da instalação, com permissões restritas, backup e restauração documentados. Reinstalar a aplicação não deve gerar outro conjunto de chaves inadvertidamente. Rate limiting, proteção contra tentativas repetidas e logs sanitizados acompanham esses fluxos desde sua implementação.

Endpoints de Assets, uploads, lixeira e conteúdo sempre verificam o proprietário. UUID, StorageKey ou SHA-256 não concedem acesso. Consultas de existência por hash só podem retornar resultados autorizados para a conta solicitante.

Downloads autenticados usam streaming e nomes de resposta sanitizados. MIME e extensão informados pelo cliente não são prova de conteúdo; arquivos arbitrários não devem ser executados nem renderizados como HTML no domínio da API.

## Contratos HTTP previstos

Estes endpoints pertencem às fases indicadas e não estão implementados na Fase 1. Recursos de usuário sempre exigem autenticação e autorização; login/refresh têm validação e limitação próprias.

| Fase | Contrato | Responsabilidade |
| --- | --- | --- |
| 2 | `POST /api/auth/login`, `POST /api/auth/refresh`, `POST /api/auth/logout` | Abrir, renovar e encerrar a sessão. |
| 2 | `GET /api/devices`, `DELETE /api/devices/{id}` | Listar dispositivos e revogar o selecionado. |
| 4 | `POST /api/uploads`, `GET /api/uploads/{id}`, `DELETE /api/uploads/{id}` | Criar, consultar e cancelar uma sessão de upload. |
| 4 | `PUT /api/uploads/{id}/chunks/{number}`, `POST /api/uploads/{id}/complete` | Receber chunk idempotente e solicitar conclusão em background. |
| 4 | `GET /api/assets`, `GET /api/assets/{id}`, `GET /api/assets/{id}/content` | Listagem básica, metadados e download com HTTP Range. |
| 4 | `GET /api/storage` | Espaço físico, tamanho lógico e reservas. |
| 5 | `GET /api/assets/{id}/thumbnail`, `GET /api/assets/{id}/preview` | Servir derivados autorizados, sem carregar o original. |
| 6 | `DELETE /api/assets/{id}`, `GET /api/trash`, `POST /api/assets/{id}/restore` | Soft delete, listagem da lixeira e restauração explícita. |

Usar Problem Details para erros e DTOs sem caminhos físicos ou entidades EF expostas. A conclusão do upload retorna `202` durante processamento; consultar a sessão informa o resultado ou a falha. Downloads completos/parciais trabalham com Streams e respeitam Range válido, inclusive `206` e `416` quando aplicável.

## Upload e publicação

A interface HTTP prevista cria a sessão, recebe chunks numerados, informa o progresso e solicita a conclusão. Upload simples, quando introduzido, deve usar as mesmas invariantes de publicação e deduplicação.

1. Criar a UploadSession autenticada, validar tamanho e reservar capacidade. O servidor determina o tamanho dos chunks e a quantidade esperada.
2. Receber cada chunk em streaming limitado, escrevendo uma tentativa temporária independente. Validar índice, quantidade de bytes e hash antes de confirmar o chunk no banco.
3. Um reenvio com mesmo índice e conteúdo é idempotente. Conteúdo diferente para um índice confirmado é conflito. O progresso enumera somente chunks confirmados.
4. A conclusão verifica todos os chunks e muda a sessão de `Open` para `Finalizing`, gravando um job na mesma transação. Retornar `202 Accepted`, sem montar um arquivo grande dentro da requisição HTTP.
5. O worker monta o arquivo em streaming, valida tamanho e hash final e resolve a deduplicação. Um Blob `Ready` pode ser reutilizado; outro Blob em publicação ou exclusão exige retry.
6. Para conteúdo novo, persistir a intenção `Staging` e sua chave física antes de publicar o arquivo. Só depois da publicação, marcar Blob `Ready`, criar ou resolver o Asset, concluir a sessão e agendar processamento de mídia na mesma transação.
7. Remover chunks e arquivos temporários após a confirmação. A limpeza é idempotente e pode ser repetida após reinício.

Estados de UploadSession: `Open`, `Finalizing`, `Completed`, `Cancelled`, `Expired` e `Failed`. Estados de Blob: `Staging`, `Ready` e `Deleting`. Transições são verificadas no banco; não depender somente de locks em memória, porque API e Worker poderão ser processos separados.

Reservas globais limitam o espaço que uploads em curso podem consumir. A montagem precisa considerar simultaneamente os chunks e o arquivo final, chegando a aproximadamente duas vezes o tamanho declarado. Um limite de sessões concorrentes não garante que todas possam receber arquivos do tamanho máximo ao mesmo tempo: o orçamento global decide a admissão.

Defaults previstos, configuráveis quando esses fluxos forem implementados:

| Limite | Valor inicial |
| --- | --- |
| Chunk | 8 MiB. |
| Arquivo | 20 GiB. |
| Orçamento global de temporários/reservas | 50 GiB. |
| Margem mínima de espaço livre | 50 GiB. |
| Sessões simultâneas por usuário | 2, condicionadas à capacidade global. |
| Expiração de upload inativo | 7 dias sem atividade. |
| Retenção na lixeira | 30 dias. |

Reservar aproximadamente `2 × tamanho declarado` na criação da sessão, serializando a admissão no banco e conferindo espaço real. O orçamento de 50 GiB admite somente um upload de 20 GiB durante montagem, mesmo que o teto de sessões seja dois. Não permitir que o corpo exceda o tamanho declarado; considerar derivados e demais usos do volume na avaliação de espaço.

## Storage e recuperação

`Storage:RootPath` é um caminho absoluto definido pelo operador. Em produção, a raiz prevista é `/srv/nexora`, com diretórios `blobs`, `thumbnails`, `previews` e `temp`. O nome original nunca é usado como caminho físico.

Chaves internas são opacas e imutáveis, com identificador distinto por geração, por exemplo `blobs/ab/cd/<blob-id>`. SHA-256 identifica conteúdo no banco; uma chave física por geração impede uma limpeza antiga de remover um novo upload do mesmo hash.

O contrato de IBlobStorage deve receber/devolver Streams e permitir publicação imutável, leitura completa/parcial e exclusão idempotente. Spool e chunks pertencem a uma abstração separada de staging. Rename é uma otimização interna do storage local, sem virar requisito da interface; um futuro storage S3 poderá implementar publicação por PUT/multipart sem reescrever os casos de uso.

PostgreSQL e filesystem não compartilham uma transação. O MVP utiliza intenção persistida, publicação local sem sobrescrita e reconciliação após reinício. A recuperação examina sessões em finalização e Blobs Staging: remontar antes da publicação, concluir após verificar conteúdo publicado ou registrar uma falha explícita quando os bytes esperados estiverem ausentes. Nunca apagar metadados silenciosamente para esconder inconsistências.

Manter `temp` e `blobs` no mesmo filesystem permite rename local atômico, mas rename isolado não assegura durabilidade. Ao implementar storage, o adaptador local precisará fazer flush do arquivo e sincronizar os diretórios Linux envolvidos antes de confirmar a publicação durável no banco. Essa responsabilidade fica no adaptador, sem contaminar a interface de storage. A Fase 1 apenas valida o caminho configurado e não implementa esse fluxo.

Reconciliação recupera operações interrompidas; não recria bytes perdidos por falha física. Backup e teste de restauração continuam necessários para proteger o conteúdo diante de falhas de hardware e perda de dados.

## Processamento e exclusão

BackgroundJob é a fonte durável de trabalho. A alteração de negócio e o job são gravados na mesma transação. O worker reclama jobs com `FOR UPDATE SKIP LOCKED`, utiliza lease renovável e só confirma resultados com seu token de lease válido. Reexecução deve ser idempotente, com tentativas limitadas e falhas visíveis. Channel pode ser usado para sinalização, sem substituir a persistência.

Na Fase 5, usar SkiaSharp para imagens e MetadataExtractor para metadados de JPEG, PNG e WebP. Aplicar orientação e gerar thumbnail com lado máximo de 256 px e preview com lado máximo de 1280 px, preservando proporção e sem ampliar imagens menores. Datas EXIF sem fuso permanecem preservadas, sem conversão UTC inventada; câmera/geolocalização não entram nesta fase.

A falha do processamento não remove nem invalida um original publicado. A API informa o estado da mídia e não carrega o original para servir cards. Processamento avançado de vídeos e FFmpeg ficam para depois; a Fase 4 já suporta HTTP Range para conteúdo, sem antecipar transcodificação.

Soft delete altera `Asset.DeletedAt` e mantém o Blob. Lixeira conserva bytes e referências. Restauração e purge disputam o mesmo lock do Asset; uma exclusão definitiva concluída não pode ser restaurada pela API.

Após o período configurável de retenção, o worker remove o Asset. Para remover um Blob sem referências, trava sua linha, verifica ausência de todos os Assets e muda o estado para `Deleting` em transação. A criação de referências também trava essa linha e aceita somente `Ready`. Depois do commit, a exclusão física é idempotente e a linha é removida quando a limpeza terminar.

## Segurança, operação e backup

- Segredos fora do Git; User Secrets somente no desenvolvimento e arquivo protegido ou credenciais do serviço na produção.
- Caminhos físicos derivados exclusivamente de chaves internas; rejeitar traversal e nunca usar paths fornecidos pelo cliente.
- Streaming limitado, timeouts, cancelamento e controle de capacidade antes e durante a escrita.
- Validação de MIME por conteúdo e limites de dimensões/pixels durante a decodificação de imagens.
- Rate limiting e autorização adicionados junto aos endpoints que precisam deles, antes de disponibilizar conteúdo pessoal.
- Tailscale Serve termina HTTPS e encaminha para a API em loopback; Funnel permanece desativado e grants restringem o acesso privado na tailnet.
- Logs estruturados com IDs técnicos e resultado, sem tokens, connection strings, conteúdo de arquivos, nomes pessoais ou coordenadas.
- Observabilidade de espaço, reservas, jobs e uploads falhos; diferenciar tamanho lógico de Assets de bytes físicos de Blobs, derivados, lixeira e temporários.

Nexora oferece armazenamento; uma única cópia no servidor não é backup. Documentar backup conjunto de PostgreSQL e storage, preferencialmente pausando gravações/workers ou usando uma estratégia de snapshot consistente. Testar restauração em ambiente isolado. A integração com restic/borg não pertence à Fase 1.

## Testes que orientam as próximas fases

Na autenticação, verificar expiração de access/session, rotação concorrente, reutilização de refresh, revogação imediata de sessão/dispositivo e preservação de tokens ao reiniciar com as mesmas chaves de Data Protection.

Priorizar autorização entre proprietários, traversal, limites de upload, chunks repetidos/conflitantes/fora de ordem, retomada, hash, deduplicação concorrente, conclusão repetida, expiração durante upload e disco cheio. Introduzir fault injection nas fronteiras filesystem/banco, recuperação de Staging, lease expirado, GC contra upload e purge contra restauração. Testar falha de thumbnails com original ainda acessível.

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
| 8 — Mobile | Cliente MAUI e sincronização; execução em background específica de Android/iOS. |

Segurança acompanha cada funcionalidade desde sua criação; a Fase 7 verifica e fecha a operação de produção. A implementação autorizada nesta etapa termina na Fase 1. O próximo incremento será a autenticação, sem antecipar endpoints de arquivos ou cliente mobile.

## Referências técnicas

- [Streaming e segurança de uploads no ASP.NET Core 10](https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-10.0).
- [Tokens nativos do Identity no ASP.NET Core 10](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-api-authorization?view=aspnetcore-10.0).
- [Locks e concorrência no PostgreSQL](https://www.postgresql.org/docs/current/explicit-locking.html) e [SKIP LOCKED para consumidores de filas](https://www.postgresql.org/docs/current/sql-select.html).
- [Channels no .NET](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels).
- [SkiaSharp](https://github.com/mono/SkiaSharp), [MetadataExtractor](https://github.com/drewnoakes/metadata-extractor-dotnet) e [Tailscale Serve](https://tailscale.com/docs/reference/tailscale-cli/serve).
- [Atomicidade de rename no Linux](https://man7.org/linux/man-pages/man2/rename.2.html) e [limites de persistência de fsync](https://man7.org/linux/man-pages/man2/fsync.2.html).

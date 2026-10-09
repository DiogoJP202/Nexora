# Nexora — continuidade em outro PC e outro chat

Atualizado em **9 de outubro de 2026**, após a decisão de trocar de computador porque Tailscale não pode ser instalado no Windows atual. Este documento é o ponto de entrada para retomar o trabalho. As informações de processos, banco, espaço e artefatos são uma fotografia desta máquina, não uma configuração automaticamente transferida.

## Objetivo imediato e onde paramos

O próximo objetivo é **executar o servidor de desenvolvimento no outro PC e testar o APK no celular por HTTPS privado**, sem exigir Arch agora. Não continuar tentando instalar Tailscale neste PC. O usuário informou que baixou o APK no celular; instalação, login, upload e ciclo de vida Android ainda não foram confirmados.

O backend já iniciou e respondeu localmente no Windows. A conexão celular–servidor ficou pendente porque Tailscale não está instalado, não há URL HTTPS privada configurada e o administrador Nexora ainda não foi criado. Nenhum hostname real de tailnet deve ser presumido. A configuração de rede no novo PC segue [new-pc-setup.md](new-pc-setup.md) e [windows-mobile-test.md](windows-mobile-test.md).

Arch continua sendo o destino de produção. Testar o app com servidor Windows é uma etapa de desenvolvimento; o aceite operacional e de recuperação da Fase 7 permanece separado.

## Código e histórico para localizar a revisão

| Item | Estado na preparação deste handoff |
| --- | --- |
| Repositório | `https://github.com/DiogoJP202/Nexora.git`. |
| Branch de trabalho | `main`; futuras branches novas seguem o prefixo `codex/`. |
| HEAD antes desta documentação | `c2918c9`, guia Windows/celular; estava limpo e alinhado com `origin/main`. O commit deste handoff vem depois dele. |
| Última alteração funcional | `451925b`, uploads Android por serviço foreground e pausa. |
| Primeiro incremento mobile/sync | `089c593`, cliente Android e sincronização durável. |
| Biblioteca concluída localmente | `e27ca91`, Fase 6. |
| Preparação operacional | `fb7d9b6`, Fase 7. |
| Entregas anteriores | Fundação `f0b48d7`; identidade `107d7df`; storage `435e051`; arquivos `7873ec8`; imagens `b4ecf15`. |

No novo PC, usar a versão atual de `origin/main`, ler `git log` e conferir alterações locais antes de continuar. Os IDs acima permitem encontrar o código validado; não exigem voltar a um commit antigo. O usuário já pediu commits e push neste trabalho; preservar alterações próprias encontradas no novo checkout.

## O que o Nexora pretende ser

Uma nuvem pessoal hospedada pelo usuário para fotos, vídeos e arquivos, com biblioteca plana no MVP, uma conta administrativa e vários dispositivos revogáveis. API primeiro, cliente Android inicial agora, iOS e interface web posteriormente. Sem cadastro público, compartilhamento, pastas, IA, reconhecimento facial ou transcodificação no escopo atual.

O servidor confiável pode ler os originais para processamento. PostgreSQL guarda metadados e estados; arquivos ficam em storage local privado. Produção prevista: Arch Linux, PostgreSQL 18, API/Worker por systemd, Kestrel em loopback e HTTPS por Tailscale Serve com acesso restrito à tailnet. A referência de cerca de 500 GB não é uma quota garantida: admissão usa o volume e o espaço livre reais.

## Entregas e pendências por fase

| Fase | Implementado e evidência | Ainda necessário |
| --- | --- | --- |
| 0 — Arquitetura | Modelo, contratos, decisões e roadmap documentados. | Evoluir as decisões conforme os próximos incrementos. |
| 1 — Fundação | Solution, versões travadas, configuração validada, PostgreSQL, migrations, ProblemDetails, JSON logs e health checks. | Reproduzir setup no novo PC. |
| 2 — Identidade | Identity, bootstrap/reset locais, login, tokens opacos, sessões, refresh com replay, dispositivos e revogação. | Criar administrador no novo banco e verificar login pelo app. |
| 3 — Storage | Blob/Asset, streaming, SHA-256, publicação imutável e deduplicação, testes locais. | Validar comportamento nativo Linux/reinício real na Fase 7. |
| 4 — Arquivos | Chunks retomáveis, reservas, conclusão durável pelo Worker, listagem, download/Range e capacidade. | Ensaiar o fluxo no celular e no servidor de produção. |
| 5 — Imagens | JPEG/PNG/WebP estáticos, orientação, captura, thumbnails/previews PNG, processo filho e limites; falha preserva original. | Aceite Skia/processos/limites no Arch. |
| 6 — Biblioteca | Timeline, filtros, favorito, renomeação, lixeira, restauração e purge/coleta coordenados. | Aceite operacional real com backup recuperável. |
| 7 — Operação | Publicação Linux, manifesto, migration bundle, scripts de instalação/ativação, systemd e runbooks de backup/restore. | Instalar no Arch, HTTPS/grants, permissões, reinício, processamento nativo, backup independente e restore isolado verificados. |
| 8 — Mobile | MAUI Android `0.1.1`, núcleo testável, cache offline de metadados, snapshot/delta, fila retomável e upload foreground com notificação/pausa. | Instalação/UI/keystore/FilePicker reais, HTTPS e matriz Android; galeria automática, sync periódico, UIDT, assinatura de produção/loja e iOS futuros. |

As fases 1–6 foram verificadas localmente no Windows. A Fase 7 não está concluída. A Fase 8 é um incremento inicial cujo build e núcleo foram verificados; isso não representa aceite do app no aparelho. O checklist completo e os critérios permanecem em [status-and-roadmap.md](status-and-roadmap.md).

## Estrutura e pontos de entrada do código

| Local | Responsabilidade / onde continuar |
| --- | --- |
| `Nexora.sln` | Backend, Worker, unitários/integração e núcleo/testes mobile; não exige workload Android. |
| `src/Nexora.Api/Program.cs`, `Endpoints/` | Minimal APIs, configuração HTTP, autenticação, erros, health e OpenAPI. |
| `src/Nexora.Api/Administration/` | Comandos interativos `bootstrap-admin` e `reset-admin-password`. |
| `src/Nexora.Api/Security/` | Validação persistente de sessão e proteção das chaves. |
| `src/Nexora.Application/` | Casos de uso, contratos específicos de storage, conteúdo, uploads, imagens e sincronização. |
| `src/Nexora.Domain/` | Entidades/regras e estados, sem ASP.NET/EF. |
| `src/Nexora.Infrastructure/` | EF/Identity/PostgreSQL, filesystem, health, imagens, locks e persistência dos jobs. |
| `src/Nexora.Infrastructure/Persistence/Migrations/` | Oito migrations versionadas e snapshot EF. |
| `src/Nexora.Worker/` | Montagem de upload, processamento em filho, leases, manutenção, expiração e limpeza. |
| `apps/Nexora.Mobile.Core/` | Cliente HTTP, sessões, cache, sincronização, outbox e coordenação portátil de transferências. |
| `apps/Nexora.Mobile/`, `apps/Nexora.Mobile.sln` | App MAUI Android, páginas, SecureStorage e serviço foreground nativo. |
| `tests/` e `apps/Nexora.Mobile.Tests/` | Testes backend/integrados/filesystem e núcleo mobile. |
| `scripts/` | PostgreSQL local, build APK e publicação Arch. |
| `ops/arch/`, `tests/operations/` | Configuração/unidades/scripts operacionais e guardas Bash. |

## Contratos e invariantes que devem ser preservados

- **Autenticação:** access token opaco protegido por Data Protection, até 5 minutos; refresh aleatório de 256 bits, somente hash persistido, rotação em transação, replay revoga sessão. Sessão absoluta de 30 dias. Cada request autenticada verifica usuário/sessão/dispositivo no banco. O cliente serializa refresh; uma resposta de rotação perdida exige novo login.
- **Conta e dispositivo:** conta única por bootstrap local sem eco; Identity lockout/rate limiting; reset local revoga sessões. Device identifica instalação registrada e não comprova hardware. Consultas incluem proprietário e recursos de outra conta retornam 404.
- **Conteúdo:** Blob é físico/imutável com hash único; Asset é a referência da biblioteca, única por proprietário/Blob inclusive na lixeira. Reenvio idêntico preserva nome e metadados existentes; item na lixeira exige restauração explícita. Não há ReferenceCount persistido. Limpeza verifica referências reais e usa identidade física UUID para não remover uma geração nova por hash reutilizado.
- **Upload:** temporário completo é publicado antes de registrar chunk confirmado; chunks repetidos iguais são aceitos e diferentes conflitam. Conclusão muda estado e insere job na mesma transação; retorna 202 e é repetível. Worker monta/hash em streaming, resolve deduplicação, publica Blob antes de Ready/Asset e recupera estados intermediários. O hash do cliente é expectativa, não autoridade.
- **Fila:** PostgreSQL com lease, renovação e tentativas limitadas; tarefas esgotadas permanecem como falha. Uma montagem e uma imagem por Worker; operações repetíveis e reconciliação entre filesystem e banco.
- **Download/imagem:** originais autenticados por stream/Range, attachment e nosniff, sem arquivos estáticos públicos. Somente derivados verificados podem ser inline. Dimensões máximas 256/1280 px sem ampliação; EXIF sem fuso preservado sem inventar UTC. Falha de preview não remove original. HEIC/RAW, vídeos/animações e formatos não processados ficam como originais.
- **Lixeira:** soft delete só marca exclusão; retenção default 30 dias. Restauração/purge e criação de referência/coleta disputam locks coordenados. Blob referenciado por qualquer Asset, inclusive lixeira, permanece.
- **Sincronização:** journal durável por proprietário, sequência em ordem de commit, projeções e tombstones; snapshot paginado consistente e cursores protegidos por proprietário/finalidade/época. Após restore, rotacionar épocas antes de reconectar clientes; ver [synchronization.md](synchronization.md).
- **Mobile:** URL HTTPS raiz com certificado confiável, sem redirects. UI/manifest não habilitam HTTP. Sessões em SecureStorage, senha não persistida; cache/outbox separados por URL/conta. Fonte privada e UUID do pedido persistidos antes do POST permitem retomar após resposta perdida. `clientRequestId` igual recupera operação; payload diferente retorna conflito.
- **Uploads Android:** iniciar por ação visível do usuário, serviço `dataSync` não exportado, notificação/pausa, uma transferência ativa. Navegação permite leitura da biblioteca; troca de conta/servidor pausa e aguarda exclusividade. Pausa conserva fonte/UUID/chunks; após perda de processo a retomada é manual. Sem promessa de execução contínua com tela apagada, sem reinício automático e sem wake lock. Limites/timeout Android e fila devem ser testados no aparelho.
- **Finalização mobile:** polling limitado a 2 minutos, depois permite consultar novamente. Upload confirmado continua concluído se o sync posterior falhar. Serviço encerra na conclusão/pausa/erro; restrições Android não são contornadas.
- **Operação:** sem migrations automáticas; configuração e chaves externas à release. Logs não incluem credenciais, tokens, nomes pessoais nem conteúdo. Backups independentes continuam necessários.

Defaults implementados: chunk 8 MiB, arquivo até 20 GiB, até 2 sessões abertas sujeitas à capacidade, orçamento temporários/reservas 50 GiB, margem livre 50 GiB, reserva de montagem 2× tamanho declarado, expiração por inatividade 7 dias. Ver limites adicionais em [uploads.md](uploads.md), [images.md](images.md) e [library.md](library.md).

## Estado confirmado do Windows antigo

Inventário somente de leitura nesta entrega; senhas e tokens não foram incluídos.

| Item | Resultado observado |
| --- | --- |
| Checkout | `C:\Users\dsoares\Nextcloud\Documents\Projetos\Nexora`. |
| SDK | `10.0.400`; `global.json` permite patch da mesma faixa. |
| Runtime do host | .NET/ASP.NET Core `10.0.11` instalados; distinto do runtime `10.0.12` incluído no pacote Linux. |
| Dependências travadas | ASP.NET/EF e ferramenta local `dotnet-ef` `10.0.12`; Npgsql EF `10.0.3`; SkiaSharp `4.153.1`; MetadataExtractor `2.9.3`; MAUI `10.0.110`. Versões e locks no Git. |
| PostgreSQL | 18.6, nativo, `127.0.0.1:55432`; bancos/roles separados `nexora_dev` e `nexora_test`. |
| Cluster local | `%LOCALAPPDATA%\Nexora\postgresql\18`, fora do checkout/Nextcloud. |
| Schema dev | Última migration `20261008184113_ClientUploadRequests` aplicada. |
| Dados dev | `AspNetUsers=0`, `Assets=0`, `Blobs=0`, `UploadSessions=0`. Ainda sem administrador, arquivos ou envios reais nesse banco. |
| Storage configurado | `C:\Users\dsoares\AppData\Local\Nexora\storage`; diretório ainda inexistente no inventário. É criado na primeira escrita, não pelo health check. |
| Chaves | `%LOCALAPPDATA%\Nexora\keys` existe; padrão DPAPI do usuário Windows, sem certificado configurado. |
| API | Em execução, PID observado `21340`, Development, `http://127.0.0.1:5100`. |
| Worker | Em execução, PID observado `14428`, Development, mesmos User Secrets/storage. |
| Health | `/health/live` e `/health/ready`: HTTP 200, `{"status":"Healthy"}`. |
| Host permitido | `localhost;127.0.0.1;[::1]`; nenhum override de hostname Tailscale. |
| Tailscale | CLI/instalação ausentes; usuário informou que não consegue instalar neste PC. Nenhuma URL Serve estabelecida. |
| Disco C: | 53.859.893.248 bytes livres, aproximadamente 50,16 GiB, quase na margem de 50 GiB. Reavaliar antes de escrever; não planejar upload grande aqui. |
| Celular | APK baixado segundo o usuário; login/transferência/aceite não confirmados. Validação anterior por ADB não encontrou aparelho. |

PIDs e sessões do agente pertencem ao PC antigo e podem mudar/encerrar ao fechar o chat ou desligar. Não são credenciais nem comandos de inicialização no PC novo. API/Worker foram iniciados em sessões de terminal, não instalados como serviços Windows; PostgreSQL também é controlado explicitamente. Esta entrega não interrompeu esses processos.

Como o banco de desenvolvimento foi confirmado vazio, **recriar o ambiente com credenciais novas no PC novo é o caminho recomendado**. Preservar o PC antigo e seus arquivos até conferir o novo. Se dados forem adicionados depois deste inventário, rever essa decisão e seguir transferência consistente em [new-pc-setup.md](new-pc-setup.md).

## O que o Git transfere e o que fica nesta máquina

| Material | Vai pelo clone/pull? | Providência |
| --- | --- | --- |
| Código, migrations, locks, scripts e documentação | Sim. | Clonar `main` atual e verificar status/log. |
| Binários/cluster/log/`admin.pw` PostgreSQL | Não. | Reprovisionar localmente; não copiar cluster ativo pelo Nextcloud. |
| User Secrets | Não. | Setup gera credenciais dev/test novas; não colar o JSON no chat. |
| Storage de originais/derivados/temporários | Não. | Vazio/inexistente neste inventário; se mudar, transferir junto do banco de forma consistente. |
| Data Protection | Não. | DPAPI do usuário atual não é um backup portátil. Ambiente novo vazio gera chaves novas e requer novo login. |
| APK em `bin/` | Não. | Reutilizar arquivo baixado, copiar APK original separadamente ou recompilar. |
| Keystore de assinatura Debug | Não. | Preservar privadamente se precisar atualizar o app instalado com a mesma assinatura. |
| Releases Linux em `artifacts/` | Não. | Copiar pacote validado separadamente ou publicar de novo a partir de commit limpo. |
| Processos em execução e ambiente dos terminais | Não. | Iniciar PostgreSQL/API/Worker e configurar ambiente no PC novo. |
| Fila/cache/sessão Android | Não. | Permanecem no app do celular; backups/transferência Android desabilitados. Desinstalar apaga esses dados. |

User Secrets atuais: `%APPDATA%\Microsoft\UserSecrets\Nexora.Api.Development\secrets.json` para API/Worker e `%APPDATA%\Microsoft\UserSecrets\Nexora.IntegrationTests.Development\secrets.json` para testes. O prefixo de ambiente é `NEXORA_`, por exemplo `NEXORA_Storage__RootPath`; não usar `NEXORA_STORAGE_PATH`. Não versionar connection strings reais, senhas, `.pfx`, keystores ou conteúdo pessoal. A proteção de volumes/cópias privadas cabe ao operador.

## Evidências e artefatos

Última suíte funcional registrada no código `451925b`: **293 aprovados = 60 unitários backend + 181 integração + 52 núcleo mobile; zero falhas e zero ignorados**. Restore travado e build APK sem avisos/erros. Esses resultados são históricos de Windows/PostgreSQL, não testes executados no celular/Arch. Esta entrega de documentação fez novo inventário, health checks e inspeção/assinatura do APK; não repetiu a suíte completa.

```powershell
dotnet restore Nexora.sln --locked-mode
dotnet test Nexora.sln --configuration Release --no-restore
```

Sem conexão de testes, casos PostgreSQL podem ser ignorados com motivo explícito. Conferir **zero ignorados** para reproduzir a evidência integral. Fixtures criam seus próprios bancos `nexora_it_<guid>` e diretórios temporários; não usar produção nem apagar bancos por wildcard.

### APK disponível no PC antigo

- Caminho: `apps/Nexora.Mobile/bin/Debug/net10.0-android/com.nexora.mobile-Signed.apk`.
- App `com.nexora.mobile`, versão `0.1.1`, código `2`; mínimo API 24, target/compile 36; `arm64-v8a` e `x86_64`; 84.455.305 bytes.
- SHA-256 **do arquivo reinventariado nesta entrega**: `d5d1bd7999d0a54efb31f769c059e582cfc33b4e4283a74dbbcc2e342a11ce39`.
- `aapt dump badging` e `apksigner verify --verbose` conferidos novamente: assinatura v2/v3 válida, um signatário de desenvolvimento. O hash anterior `8c228d…` permanece evidência histórica em [mobile-validation.md](mobile-validation.md); não identifica o arquivo atual. Compilações/assinaturas podem produzir bytes diferentes; o hash do APK efetivamente baixado no celular não foi lido.
- Keystore Debug encontrado em `C:\Users\dsoares\AppData\Local\Xamarin\Mono for Android\debug.keystore`, fora do Git. Outro PC pode gerar assinatura diferente. Antes de atualizar um app instalado, conferir assinatura ou preservar essa chave por transferência privada. Não desinstalar para resolver isso sem considerar perda da fila/cache/sessão.

O ambiente anterior tem workload Android `36.1.69`, MAUI Windows `10.0.20`, pacotes MAUI `10.0.110`, Android SDK 36/build-tools `36.0.0` em `C:\Program Files (x86)\Android\android-sdk` e JDK `21.0.8` em `C:\Program Files\Android\openjdk\jdk-21.0.8`. Workloads iOS/MacCatalyst instalados não significam que exista um cliente iOS implementado.

### Pacote Linux preparado

`artifacts/arch/phase8-linux-x64-20261009` contém API/Worker self-contained Linux x64, runtime `10.0.12`, bundle/SQL de migrations e operações. Fonte `089c593597cc62bbb4560e1139a7ea50d6d07ded`, `sourceDirty=false`, última migration `20261008184113_ClientUploadRequests`. SHA-256 do `SHA256SUMS`: `17d8e6def9b7c0a082acd16f0fecf28feacee02682a8cc50b13cea4fac5501cd`, conferido novamente nesta entrega. A validação anterior verificou os 723 arquivos. A entrega atual não repetiu todos esses hashes nem executou Linux.

O incremento foreground foi somente mobile, mas releases futuras devem ser regeneradas para o commit que será instalado. Pacote histórico `phase7-linux-x64-20261008` não inclui sync. Diretórios `phase7-check-*` são ensaios, não selecionar como release aceita. Detalhes em [arch-validation.md](arch-validation.md).

## Ordem recomendada de continuação

1. No novo PC, clonar/pull e ler este documento, [new-pc-setup.md](new-pc-setup.md) e o roadmap. Conferir sistema operacional, SDK, alterações locais e espaço; não presumir caminhos/PIDs/segredos antigos.
2. Preparar PostgreSQL dev/test novo, restaurar/build, aplicar migrations explicitamente e confirmar health da API/Worker. Criar administrador em terminal humano sem eco e sem enviar senha pelo chat.
3. Instalar/conectar Tailscale no PC novo e no Android, mesma conta/tailnet; configurar Serve HTTPS para loopback e o hostname real em `AllowedHosts`. Confirmar `/health/ready` pelo navegador do celular.
4. No Nexora, usar URL HTTPS raiz real, conta bootstrap e nome do dispositivo. Primeiro um JPEG pequeno; testar login, biblioteca/preview, deduplicação, favorito, lixeira/restauração e download.
5. Com espaço adequado, testar arquivo controlado de 20–50 MiB: chunks, Home/navegação, notificação, pausa, retomada, rede perdida e reinício. Executar a matriz de [android-background-uploads.md](android-background-uploads.md) e registrar aparelho/API/resultados.
6. Corrigir problemas observados, verificar testes relevantes, atualizar evidências/roadmap e publicar mudanças. Não marcar Fase 8 aceita só porque o APK compila.
7. Quando houver Arch disponível, executar o roteiro Fase 7 e restore isolado antes de uso com dados pessoais. Galeria automática, UIDT/sync periódico, iOS e web vêm depois dos aceites básicos.

Uma URL nova tem escopo próprio no app. Cache/fila e identificação de dispositivo não são migrados automaticamente entre URLs/contas. Tokens do servidor anterior não servem a um backend recém-criado; entrar novamente. Não apagar fontes privadas da fila antiga para tentar mover o escopo. As limitações de recuperação após restore estão em [synchronization.md](synchronization.md) e [mobile.md](mobile.md).

## Prompt para colar no próximo chat

Abra o próximo chat na pasta clonada do Nexora e cole:

```text
Continue o Nexora a partir do repositório atual. Leia primeiro docs/handoff.md,
docs/new-pc-setup.md, docs/status-and-roadmap.md e docs/windows-mobile-test.md.
Eu troquei de PC porque não conseguia instalar Tailscale no Windows antigo.
Quero preparar o servidor neste computador e testar o APK Android por HTTPS
privado, sem precisar de Arch ainda. Já informei que baixei o APK no celular;
confira comigo o que chegou a ser instalado/testado antes de marcar aceite.

Fases 1–6 implementadas/verificadas no Windows, Fase 7 preparada mas não aceita
no Arch; Fase 8 Android 0.1.1/código 2 com uploads foreground, pausa e retomada,
mas execução real no aparelho pendente. Último código funcional de referência:
451925b; sync/primeiro mobile: 089c593. Os resultados anteriores eram 293 testes,
zero falhas/ignorados. Use main atual, não faça checkout de código antigo.

No inventário de 9/10/2026 o banco dev antigo estava sem usuário/Asset/Blob/upload,
storage ainda não existia e Tailscale/HTTPS não estavam configurados. Credenciais,
DPAPI, APK, keystore Debug, releases e processos não vêm pelo Git. Confira o novo
ambiente; recrie dev/test com credenciais novas conforme o guia, sem apagar o PC
antigo nem dados de um checkout existente. Não imprima senhas, tokens ou Secrets.
Execute migrations explicitamente, mantenha API e Worker com a mesma configuração,
use URL real do Serve em AllowedHosts e preserve HTTPS exigido pelo app.

Antes de recompilar/atualizar o APK, considere a assinatura Debug do outro PC e
dados privados existentes no celular. Não desinstale sem tratar essa perda.
Continue as etapas autorizadas, documente evidências e pendências e mantenha Git
atualizado. Login Tailscale, consentimento HTTPS e senha do bootstrap são etapas
humanas; prepare o restante e dê as instruções precisas quando forem necessárias.
```

## Índice da documentação técnica

| Documento | Conteúdo |
| --- | --- |
| [new-pc-setup.md](new-pc-setup.md) | Setup sequencial, transferência de estado e assinatura Android. |
| [status-and-roadmap.md](status-and-roadmap.md) | Implementado, pendente, defaults e critérios de aceite. |
| [architecture.md](architecture.md) | Decisões de arquitetura, modelo e ameaças. |
| [development.md](development.md) | Banco Windows, configuração, EF, comandos e testes. |
| [authentication.md](authentication.md) | Login/refresh, administração local, revogação e chaves. |
| [storage.md](storage.md) | Blob/Asset, publicação e deduplicação. |
| [uploads.md](uploads.md) | API de chunks, estados, jobs, streaming/Range e capacidade. |
| [images.md](images.md) | Metadados, derivados, limites e renderer filho. |
| [library.md](library.md) | Biblioteca, timeline, lixeira, restauração/purge. |
| [synchronization.md](synchronization.md) | Snapshot/delta, época, tombstones e restore. |
| [mobile.md](mobile.md) | Build, sessão/cache/fila, comportamento e roteiro Android. |
| [windows-mobile-test.md](windows-mobile-test.md) | Tailscale Windows/Android, Serve, AllowedHosts e primeiro teste. |
| [android-background-uploads.md](android-background-uploads.md) | Serviço foreground, restrições, pausas e matriz manual. |
| [mobile-validation.md](mobile-validation.md) | Evidências automatizadas/APK e aceite real pendente. |
| [arch-deployment.md](arch-deployment.md) | Publicação/instalação/ativação e operação de produção. |
| [backup-and-restore.md](backup-and-restore.md) | Snapshot consistente, restore isolado e rotação de época. |
| [arch-validation.md](arch-validation.md) | Evidências locais e verificações reais Arch ainda pendentes. |

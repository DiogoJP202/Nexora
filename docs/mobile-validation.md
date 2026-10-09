# Validação local da Fase 8

Verificado em **9 de outubro de 2026**, no Windows, incremento `0.1.1` de envio fora da tela. **Aceite real Android e operação no Arch continuam pendentes.** `adb devices -l` não encontrou dispositivo; não havia emulador disponível. Nenhuma instalação, tela ou notificação foi verificada em execução Android.

## Evidências executadas

- `dotnet restore Nexora.sln --locked-mode` e restore travado do projeto Android aprovados.
- `dotnet test Nexora.sln --configuration Release --no-restore`: **293 testes aprovados**, 60 unitários do backend, 181 de integração e 52 do núcleo mobile; zero falhas/ignorados.
- Integração com PostgreSQL 18 nativo em bancos isolados: journal/snapshot, ordem de commit, exclusões, proprietário/cursores/época, backfill e idempotência de criação de upload.
- Fluxo do `NexoraClient` contra TestServer/PostgreSQL: login, respostas de criação/chunk perdidas, reinício dos objetos locais, conclusão pelo Worker, imagem pronta, downloads, snapshot/delta, favoritos, lixeira/restauração e logout. O transporte local de ensaio é HTTP; ele não valida TLS Android.
- Núcleo mobile: refresh serializado, revogação/resposta perdida, isolamento de conta/servidor durante I/O e requests, cache atômico, fonte privada/hash, retomada, sessão ausente após restore e cancelamento com resposta perdida.
- Os **19 novos casos** verificam leitores concorrentes durante upload, prioridade/cancelamento da troca de escopo, leitura antiga durante substituição atômica de cache, pausa/retomada, início duplicado/reentrante, revogação, erro de rede, polling limitado e HTTP travado cancelado pelo deadline. Uma falha posterior de sincronização não muda um upload concluído para falho.
- O incremento não altera o schema nem os contratos HTTP. No incremento inicial `089c593`, as migrations `20261008183547_DurableAssetSync` e `20261008184113_ClientUploadRequests` foram aplicadas explicitamente ao banco de desenvolvimento; API real retornou live/readiness `200`, sync sem autenticação `401` e expôs as rotas/UUID no OpenAPI. Esses ensaios anteriores não foram repetidos para a alteração exclusivamente mobile.
- Build Android Debug pelo `Build-Android.ps1 -NoRestore`: zero avisos e erros. Workloads Android 36.1.69/MAUI Windows 10.0.20 existentes, pacotes MAUI 10.0.110, Android SDK/build-tools 36.0.0 e JDK 21.0.8.
- APK final inspecionado por `aapt`: serviço `com.nexora.mobile.UploadForegroundService` não exportado, `foregroundServiceType=0x1` (`dataSync`), `stopWithTask=false` e permissões foreground/dataSync/notificações presentes. A inspeção detectou a omissão inicial do tipo na entrada manual; corrigida e pacote regenerado antes deste registro.
- No incremento anterior, scripts Bash e guardas operacionais foram conferidos no Git Bash. Eles não executam restauração nem systemd; evidências Linux estão em [arch-validation.md](arch-validation.md).

## APK de desenvolvimento

Arquivo ignorado pelo Git: `apps/Nexora.Mobile/bin/Debug/net10.0-android/com.nexora.mobile-Signed.apk`.

| Propriedade conferida | Resultado |
| --- | --- |
| Aplicação | `com.nexora.mobile`, versão `0.1.1`, código `2`, label Nexora e ícone incluído. |
| Android | Mínimo API 24; target/compile API 36. |
| Arquiteturas | `arm64-v8a`, `x86_64`. |
| Instalação manual | Assemblies de app/núcleo incluídos nos dois ABIs; build com `EmbedAssembliesIntoApk=true`. |
| Assinatura | `apksigner verify --verbose` aprovado, esquemas v2/v3 e um signatário de desenvolvimento. |
| Tamanho | 84.455.305 bytes. |
| SHA-256 | `8c228dc51e9cfd320050ff6f2722119ff1a48b4c6d0652cf5b2d1eb4cf53d449`. |
| Manifest | Backup/cleartext desabilitados e regras de transferência incluídas; FileProvider e serviço não exportados. |
| Serviço | Tipo `dataSync`, `stopWithTask=false`, permissões de foreground/dataSync/notificações. |
| FileProvider | XML compilado contém somente `cache-path` com `nexora-sharing/`; biblioteca, fila e sessão não são raízes compartilhadas. |

O APK é Debug, para ensaio por instalação manual. O hash identifica estes bytes, não futuras compilações; a assinatura de desenvolvimento e timestamps podem diferir em outro ambiente. Build e inspeção do pacote não comprovam layout, keystore, FilePicker, abertura externa ou comportamento do Android ao encerrar o processo.

O registro anterior (`089c593`, versão `0.1.0`, código `1`) tinha 274 testes e APK de 84.067.239 bytes, SHA-256 `91a638c99432c5b8ae14371539c6ae29a063f62743f9e7d633d8369250b69263`. O caminho de build acima agora contém `0.1.1`; o hash antigo é somente uma evidência histórica.

## Aceite que falta

Execute e registre o [roteiro Android](mobile.md) e a [matriz de envio fora da tela](android-background-uploads.md) em dispositivo controlado, com HTTPS confiável e backend correspondente às migrations atuais. Verifique navegação/teclado, armazenamento seguro/reinstalação, Home, notificações negadas, pausa durante sync, tela bloqueada, perda de rede/processo, revogação, timeout e recuperação da fila. A Fase 7 depende de [arch-validation.md](arch-validation.md). Galeria automática, sincronização periódica, UIDT, iOS, assinatura de produção e loja permanecem futuros.

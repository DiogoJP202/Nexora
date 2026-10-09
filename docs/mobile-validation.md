# Validação local da Fase 8

Verificado em **9 de outubro de 2026**, no Windows. O incremento inclui o cliente Android inicial e a sincronização durável. **Aceite real Android e operação no Arch continuam pendentes.** Não havia dispositivo conectado nem emulador disponível; nenhuma tela ou instalação foi verificada em execução Android.

## Evidências executadas

- `dotnet restore Nexora.sln --locked-mode` e restore travado do projeto Android aprovados.
- `dotnet test Nexora.sln --configuration Release --no-restore`: **274 testes aprovados**, 60 unitários do backend, 181 de integração e 33 do núcleo mobile; zero falhas/ignorados.
- Integração com PostgreSQL 18 nativo em bancos isolados: journal/snapshot, ordem de commit, exclusões, proprietário/cursores/época, backfill e idempotência de criação de upload.
- Fluxo do `NexoraClient` contra TestServer/PostgreSQL: login, respostas de criação/chunk perdidas, reinício dos objetos locais, conclusão pelo Worker, imagem pronta, downloads, snapshot/delta, favoritos, lixeira/restauração e logout. O transporte local de ensaio é HTTP; ele não valida TLS Android.
- Núcleo mobile: refresh serializado, revogação/resposta perdida, isolamento de conta/servidor durante I/O e requests, cache atômico, fonte privada/hash, retomada, sessão ausente após restore e cancelamento com resposta perdida.
- Migrations `20261008183547_DurableAssetSync` e `20261008184113_ClientUploadRequests` aplicadas explicitamente ao banco de desenvolvimento; nenhuma migration pendente.
- API real de desenvolvimento iniciada: live/readiness `200`, sync sem autenticação `401`, OpenAPI contendo as duas rotas sync e `clientRequestId`. Processo de ensaio encerrado depois das verificações.
- Build Android Debug pelo `Build-Android.ps1 -NoRestore`: zero avisos e erros. Workloads Android 36.1.69/MAUI Windows 10.0.20 existentes, pacotes MAUI 10.0.110, Android SDK/build-tools 36.0.0 e JDK 21.0.8.
- Scripts Bash passam verificação de sintaxe; guardas operacionais: 18 aprovadas, 3 de symlink indisponíveis por emulação do Git Bash. Isso não executa restauração nem systemd.

## APK de desenvolvimento

Arquivo ignorado pelo Git: `apps/Nexora.Mobile/bin/Debug/net10.0-android/com.nexora.mobile-Signed.apk`.

| Propriedade conferida | Resultado |
| --- | --- |
| Aplicação | `com.nexora.mobile`, versão `0.1.0`, código `1`, label Nexora e ícone incluído. |
| Android | Mínimo API 24; target/compile API 36. |
| Arquiteturas | `arm64-v8a`, `x86_64`. |
| Instalação manual | Assemblies de app/núcleo incluídos nos dois ABIs; build com `EmbedAssembliesIntoApk=true`. |
| Assinatura | `apksigner verify --verbose` aprovado, esquemas v2/v3 e um signatário de desenvolvimento. |
| Tamanho | 84.067.239 bytes. |
| SHA-256 | `91a638c99432c5b8ae14371539c6ae29a063f62743f9e7d633d8369250b69263`. |
| Manifest | Backup/cleartext desabilitados e regras de transferência incluídas; FileProvider não exportado. |
| FileProvider | XML compilado contém somente `cache-path` com `nexora-sharing/`; biblioteca, fila e sessão não são raízes compartilhadas. |

O APK é Debug, para ensaio por instalação manual. O hash identifica estes bytes, não futuras compilações; a assinatura de desenvolvimento e timestamps podem diferir em outro ambiente. Build e inspeção do pacote não comprovam layout, keystore, FilePicker, abertura externa ou comportamento do Android ao encerrar o processo.

## Aceite que falta

Execute e registre o [roteiro Android](mobile.md) em um dispositivo controlado, com HTTPS confiável e backend correspondente às migrations atuais. Verifique navegação/teclado, armazenamento seguro e reinstalação, perda de conexão/processo durante upload, revogação, cache offline, permissões de leitura externa e recuperação após backup. A Fase 7 depende também de [arch-validation.md](arch-validation.md). Background, galeria automática, iOS, assinatura de produção e loja permanecem futuros.

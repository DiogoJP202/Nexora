# Cliente Android

A Fase 8 implementa o cliente .NET MAUI em `apps/Nexora.Mobile`, começando por Android, e o protocolo/persistência em `apps/Nexora.Mobile.Core`. O incremento de uploads permite continuar um envio escolhido pelo usuário fora da página por um serviço Android `dataSync`, conforme [android-background-uploads.md](android-background-uploads.md). Os testes de núcleo ficam em `apps/Nexora.Mobile.Tests` e rodam sem o SDK Android. Build de APK e testes de protocolo não comprovam comportamento visual nem execução em um dispositivo: o aceite Android continua dependendo do roteiro manual abaixo. A operação real do backend no Arch também permanece pendente, conforme [arch-validation.md](arch-validation.md).

## Compilar e instalar para o ensaio

O repositório fixa .NET SDK 10.0.400 e MAUI 10.0.110. Use uma instalação Windows com as ferramentas MAUI/Android, Android SDK 36/build-tools 36.0.0 e JDK 21. O ambiente Windows verificado já tem os workloads `android` 36.1.69 e `maui-windows` 10.0.20 instalados pelo Visual Studio; não é necessário reinstalá-los para este ensaio. Em uma instalação nova por CLI, `dotnet workload install maui-android` prepara o workload Android. O script localiza SDK/JDK existentes; os parâmetros permitem indicar caminhos explicitamente:

```powershell
.\scripts\Build-Android.ps1
```

Ou:

```powershell
.\scripts\Build-Android.ps1 `
  -AndroidSdkDirectory 'C:\caminho\Android\Sdk' `
  -JavaSdkDirectory 'C:\caminho\jdk-21'
```

O default é Debug, APK com assemblies incluídos, e o script imprime o caminho de `*-Signed.apk` em `apps/Nexora.Mobile/bin/Debug/net10.0-android`. A assinatura de desenvolvimento serve ao ensaio por instalação manual; distribuição, chave de assinatura de produção e publicação em loja são etapas futuras. O projeto tem arquiteturas ARM64 e x64 e exige Android 7/API 24 ou superior.

O núcleo e seus testes pertencem a `Nexora.sln`. O projeto que exige as ferramentas Android fica na solução separada `apps/Nexora.Mobile.sln`, permitindo validar backend e protocolo sem instalar MAUI.

Com um dispositivo de ensaio conectado e depuração USB autorizada, use o `adb` do SDK para conferir o alvo e instalar o APK no dispositivo escolhido:

```powershell
adb devices -l
adb -s '<serial-do-dispositivo>' install -r '<caminho-do-APK-Signed.apk>'
```

Não há evidência de instalação ou inspeção visual Android nesta entrega enquanto o roteiro não tiver sido executado em um dispositivo/emulador disponível.

## Conexão e sessão

Para testar com servidor Windows antes de preparar o Arch, siga [windows-mobile-test.md](windows-mobile-test.md): API/Worker locais, administrador, Tailscale e hostname permitido.

Informe a URL raiz HTTPS do servidor privado e a conta administrativa já criada no backend. A URL não aceita credenciais embutidas, caminho, parâmetros ou fragmento. O app recusa tráfego HTTP e redirecionamentos; o certificado precisa ser confiável para Android. O acesso pela tailnet e Tailscale Serve depende da configuração de [arch-deployment.md](arch-deployment.md).

O núcleo admite HTTP de desenvolvimento somente quando explicitamente habilitado, para loopback ou `10.0.2.2`; essa opção não é habilitada na interface Android e seu manifest bloqueia cleartext. Um endpoint HTTP local da API precisa de uma entrada HTTPS confiável para uso neste APK.

Access/refresh tokens pertencem ao armazenamento seguro da plataforma, separado dos JSON de cache/fila. Senha não é persistida. A identificação da instalação/dispositivo e os dados locais são separados por URL canônica e conta. Android backup e transferência de dados do app são desabilitados no manifest/regras Android para evitar restaurar a cópia de uma credencial rotativa. O núcleo mantém o escopo durante as operações assíncronas: leituras podem compartilhar o escopo atual, enquanto configurar outra conta/servidor exige exclusividade. A interface pausa um envio ativo antes de sair ou trocar o escopo, aguardando seu encerramento e as demais operações pendentes.

Refresh é serializado e remove o token persistido antes de enviar a requisição de uso único. Uma resposta perdida, cancelamento ou interrupção nessa janela exige novo login; o app não repete o token consumido. `401` também limpa a sessão local. Logout remove credenciais locais antes de tentar revogar a sessão no servidor; sem conexão, a revogação remota depende de conectividade ou da administração de dispositivos. Consulte [authentication.md](authentication.md).

Um dispositivo revogado pode retornar `invalid_device` ao tentar entrar com a identificação antiga. A tela de login oferece **Registrar dispositivo novamente**, com confirmação explícita para remover o UUID local daquele escopo e cadastrar outro no próximo login. O app não tenta registrar outro dispositivo automaticamente após uma revogação.

## Biblioteca, conteúdo e offline

A interface oferece biblioteca, busca local, fotos, favoritos, lixeira e detalhe do item. Favoritar, mover para a lixeira e restaurar exigem conexão e confirmação do servidor. A sincronização completa/incremental usa [synchronization.md](synchronization.md), incluindo tombstones de purge.

O cache de metadados da biblioteca e da lixeira fica no diretório privado do app, em `library/<escopo>/metadata.json`; tokens não fazem parte dele. Dados de outro servidor/conta usam outro escopo. A última sincronização informa a antiguidade do cache. O app pode abrir a biblioteca com a sessão local recuperada e os dados anteriores; expiração/revogação pode exigir autenticação ao iniciar o app ou retomar uma operação remota. Sem conexão, o cliente não consegue confirmar uma revogação feita no servidor.

Prévia e thumbnail aceitam apenas derivadas PNG autorizadas; o original só é baixado como `application/octet-stream` e attachment. Downloads usam streaming: a interface limita originais ao tamanho do Asset, até o limite atual do backend de 20 GiB, e derivados a 20 MiB. O default de 2 GiB do método genérico de download do núcleo é substituído pela interface para originais.

Cópias baixadas podem ser reutilizadas offline quando o último cache informa que o Asset está ativo. Previews ficam em `downloads/<escopo>/` no diretório privado de dados; originais, no cache interno descrito abaixo. Originais são conferidos por tamanho; PNGs, por assinatura e limite de bytes. Um item conhecido como purgado ou na lixeira deixa de ser aberto por esse caminho. Não há pré-carregamento, quota global ou coleta automática pelo app; o Android pode remover arquivos de cache. Cache local não constitui backup de originais.

Originais baixados ficam no cache interno `nexora-sharing/<escopo>/`. O FileProvider permite apenas essa pasta; cache de metadados, fila e sessão não são caminhos compartilháveis. Abrir um original concede leitura daquele arquivo ao app escolhido, sem uma segunda cópia síncrona do arquivo inteiro. O [Launcher do MAUI](https://learn.microsoft.com/en-us/dotnet/maui/platform-integration/appmodel/launcher?view=net-maui-10.0) descreve o controle dos caminhos compartilhados. O roteiro Android precisa conferir abertura, permissão e remoção do cache pelo sistema no dispositivo.

## Fila de upload e serviço Android

A seleção usa o stream fornecido pelo FilePicker. Antes de criar uma sessão remota, a fila copia os bytes para um arquivo privado, calcula SHA-256 e grava os metadados. O arquivo original selecionado pode deixar de estar acessível depois disso. A cópia privada ocupa espaço local proporcional ao arquivo, limitada a 20 GiB por item; falta de espaço ou cancelamento da cópia falha antes do envio.

Cada item tem UUID persistido, enviado como `clientRequestId` na criação de upload. Repetir a criação com a mesma conta, UUID e conteúdo recupera a mesma sessão mesmo após perda da resposta, conforme [uploads.md](uploads.md). Na retomada, o servidor informa os chunks já confirmados; a fila envia somente os faltantes e valida recibos. A cópia privada é verificada por tamanho/hash antes do envio e removida depois de registrar conclusão. Metadados e progresso persistem em `outbox/<escopo>/` para retomada após reiniciar o app.

Uma restauração do banco pode remover a sessão remota ainda referenciada pela fila. Se uma retomada explícita receber `404 resource_not_found` ao consultá-la, o cliente persiste a remoção do ID/progresso antigo, verifica a cópia privada e cria/recupera a sessão usando o mesmo `clientRequestId`. Uma nova perda de resposta continua retomável sem duplicar a reserva. Isso exige a cópia privada original ainda disponível; um item já concluído teve essa cópia removida e não reenvia bytes perdidos pelo servidor automaticamente.

Enviar/retomar é uma ação explícita na interface, após persistir a cópia privada. Há um único envio ativo. O Android inicia um serviço em primeiro plano `dataSync`, com notificação genérica de progresso e ação **Pausar**; o envio pode continuar ao navegar ou pressionar Home, independentemente da vida da página. Biblioteca e detalhe permanecem consultáveis durante a transferência. A permissão de notificações em Android 13+ pode ser recusada sem bloquear o serviço, mas sua ação na gaveta fica indisponível; a pausa continua acessível na interface.

Pausar interrompe a tentativa local e permite nova retomada; remover um item tenta cancelar a sessão remota antes de excluir a cópia privada. Após uma criação com resposta perdida (`CreateInFlight` sem ID remoto), a remoção recupera a sessão pelo mesmo UUID, persiste seu ID e solicita o cancelamento. Um item apenas pendente, nunca enviado, é removido localmente sem criar sessão remota. `404` na exclusão da sessão significa que ela já está ausente; `409` ou falha de rede preservam os dados locais para consulta/nova tentativa. Sessões já terminais permitem a limpeza local.

A finalização continua durável no Worker. O coordenador consulta `Finalizing` a cada dois segundos, por no máximo dois minutos; depois passa para `WaitingForServer` e encerra o serviço. **Consultar** na fila acompanha o resultado posterior por ação explícita. Não há retomada automática após perda do processo, reboot ou retorno da rede, nem início automático de outros itens da fila. O app não observa a galeria nem agenda sincronização periódica.

O serviço respeita o limite cumulativo Android 15+ de seis horas por 24 horas e para imediatamente no callback de timeout. Este incremento não usa wake lock de CPU: transmissão contínua com tela bloqueada depende do sistema, rede e restrições do aparelho. Os limites, recuperação e matriz manual estão em [android-background-uploads.md](android-background-uploads.md).

## Roteiro de aceite Android pendente

As evidências automatizadas e do APK de desenvolvimento estão em [mobile-validation.md](mobile-validation.md). Execute também a [matriz de uploads fora da tela](android-background-uploads.md), incluindo Home/navegação, tela bloqueada, notificações negadas, pausa, parada do processo e timeout Android 15+.

Use somente dados controlados e registre dispositivo/versão Android, revisão, APK, horários e resultados, sem tokens ou senhas:

1. Instalar e abrir o APK; conferir layout, legibilidade, teclado, navegação e retorno da aplicação.
2. Entrar por HTTPS privado e consultar biblioteca, fotos, busca, favoritos, detalhe e lixeira. Conferir PNG e download do original por hash.
3. Favoritar, mover um item para a lixeira e restaurá-lo; sincronizar e confirmar os metadados no app e na API.
4. Criar uma fila, interromper a conexão no envio e encerrar/reabrir o app. Retomar e conferir chunks confirmados, um único Asset e o hash original. Repetir com perda da resposta de criação/conclusão em um ambiente de ensaio controlado.
5. Desconectar a rede, abrir o último cache e verificar que os comandos remotos falham sem registrar sucesso. Confirmar que a fila só envia bytes após uma ação explícita com conexão.
6. Revogar o dispositivo/sessão pelo backend e confirmar necessidade de novo login; verificar refresh concorrente, reinício e resposta perdida sem reutilizar um refresh token.
7. Alternar servidor/conta e conferir separação de cache, fila e sessão; retornar ao escopo original e conferir a persistência dos dados locais.
8. Restaurar um backup isolado, rotacionar obrigatoriamente a época antes da reconexão e confirmar reconstrução integral do cache, inclusive exclusões. Retomar um item cuja sessão não existe no banco recuperado e conferir recriação com o mesmo UUID, integridade e ausência de duplicatas. Remover um item após perda da resposta de criação e conferir cancelamento/ausência de reserva órfã. Seguir [backup-and-restore.md](backup-and-restore.md).

iOS, UIDT jobs para transferências longas, sincronização da galeria, distribuição em loja e assinatura de produção ficam para incrementos posteriores. O serviço deste incremento executa apenas um envio iniciado pelo usuário; agendamento periódico e retomada automática permanecem futuros. O aceite exige evidência real do dispositivo e a operação do backend; a presença de testes automatizados não conclui esses passos.

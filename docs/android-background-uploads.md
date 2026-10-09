# Upload Android fora da tela da biblioteca

Este incremento permite continuar um envio escolhido pelo usuário ao navegar para outra tela ou colocar o app em segundo plano. O Android executa um serviço em primeiro plano do tipo `dataSync`, com notificação de progresso e ação **Pausar**. A transferência tem vida própria; sair da página não cancela o envio.

O aceite de execução Android permanece **pendente**. Não havia celular conectado nem emulador disponível no ambiente de desenvolvimento. Testes do núcleo e compilação/inspeção do APK não comprovam notificações, transições de Activity, armazenamento seguro ou comportamento com a tela bloqueada. As evidências executadas ficam em [mobile-validation.md](mobile-validation.md); o APK verificado é `0.1.1`, código `2`, com mínimo API 24 e target API 36.

## Início, progresso e conclusão

Selecionar um arquivo primeiro cria uma cópia privada, calcula seu SHA-256 e persiste o UUID da fila. O serviço só começa depois dessa preparação, por uma ação explícita de enviar/retomar com a Activity visível. Falha ou cancelamento durante a cópia não inicia uma transferência remota. Há somente um envio ativo por vez; outros itens continuam na fila, aguardando uma ação do usuário.

O serviço publica imediatamente uma notificação genérica antes de iniciar I/O assíncrono. Ela informa progresso sem nome de arquivo, conta, URL, caminho ou credenciais. O componente não é exportado; o comando transporta um ticket de uso único, resolvido em memória para o item e seu escopo. A ação Pausar pertence à geração que criou a notificação, evitando que um comando atrasado interrompa um envio posterior. Atualizações de progresso da notificação são limitadas a uma por segundo, com mudanças de fase imediatas. Biblioteca e detalhe podem continuar sendo consultados durante o envio. A notificação e a interface acompanham o mesmo coordenador; navegar entre páginas não cria outra transferência nem outro cliente autenticado.

Após transmitir os chunks, o app solicita a conclusão durável ao Worker. Se o servidor retornar `Finalizing`, o coordenador consulta o resultado a cada dois segundos, por no máximo dois minutos de acompanhamento. Se ainda não houver resultado terminal, o estado local de acompanhamento passa para `WaitingForServer`, o serviço encerra e o item permanece disponível para **Consultar** na fila. Esse estado não cancela o job do Worker nem representa falha do original. Não há polling indefinido; uma nova consulta depende da ação do usuário.

Conclusão, falha, pausa e perda de autorização encerram o serviço e sua notificação ativa. A cópia privada só é removida após o resultado concluído ser persistido, ou pela remoção explícita da fila conforme [uploads.md](uploads.md). O Android pode recusar o início de um serviço; nessa situação, a cópia persistida continua retomável.

## Pausa, cancelamento e recuperação

**Pausar** interrompe a tentativa local e preserva sessão remota, UUID e cópia privada. O coordenador aguarda a tentativa terminar antes de permitir sua substituição. Retomar consulta a sessão, confere os chunks já confirmados e envia somente os faltantes. Uma resposta perdida pode ter confirmado um chunk no servidor; a consulta recupera esse progresso sem presumir que o último request falhou.

**Cancelar/remover** é uma operação distinta: tenta cancelar a sessão remota antes de apagar a cópia local. Uma criação com resposta perdida é recuperada pelo mesmo `clientRequestId` antes desse cancelamento. `404 resource_not_found` indica ausência da sessão e permite limpeza; `409` ou falha de rede preservam os dados para consulta ou nova tentativa. Uma sessão em finalização pode recusar o cancelamento. Os detalhes e a recuperação após restauração estão em [mobile.md](mobile.md) e [uploads.md](uploads.md).

O app não reinicia automaticamente o envio após perda do processo, ação **Parar** do Android, reboot ou reconexão da rede. Reabrir mostra a fila persistida; transmitir novamente exige **Retomar**. O serviço usa comportamento não persistente (`NotSticky`), sem agendamento de fila ou receptor de boot. Quando o usuário para o app pelo Task Manager do Android 13+, o sistema pode encerrar todo o processo sem callbacks de limpeza; a recuperação usa os registros duráveis, conforme a [documentação Android sobre parada pelo usuário](https://developer.android.com/develop/background-work/services/fgs/handle-user-stopping).

Leituras simultâneas compartilham o escopo atual; configurar outra conta/URL exige acesso exclusivo. Antes de sair ou trocar o escopo, a interface pausa o envio e aguarda seu encerramento. O UUID só pode ser retomado no escopo em que foi criado. Revogação ou necessidade de novo login encerra a tentativa, sem cadastro automático de outro dispositivo e sem repetir um refresh já consumido.

## Restrições da plataforma

O manifest declara `FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_DATA_SYNC` e serviço `dataSync`. O início usa `StartForegroundService` no Android 8/API 26 ou superior, com promoção imediata; versões anteriores usam `StartService`. A seleção desse tipo e as permissões seguem os [tipos de serviço Android](https://developer.android.com/develop/background-work/services/fgs/service-types). O app inicia o serviço a partir de uma interação com Activity visível, respeitando as [restrições de início em segundo plano](https://developer.android.com/develop/background-work/services/fgs/restrictions-bg-start).

Android 13+ oferece permissão de notificações. Recusá-la não impede o serviço em primeiro plano: sua indicação permanece no Task Manager, mas a notificação e a ação Pausar podem não aparecer na gaveta. A pausa continua disponível na interface. O comportamento da permissão precisa ser conferido no dispositivo, conforme a [documentação de notificações](https://developer.android.com/develop/ui/compose/notifications/notification-permission).

Android 15+ limita serviços `dataSync` em segundo plano a um total de seis horas por 24 horas, compartilhado entre serviços desse tipo; trazer o app ao primeiro plano reinicia esse orçamento. No callback `OnTimeout`, o serviço cancela a tentativa e chama a parada imediatamente, sem aguardar I/O de rede. O estado durável permite retomada manual. Consulte os [limites e ensaio de timeout do Android](https://developer.android.com/develop/background-work/services/fgs/timeout).

Este incremento não adquire wake lock de CPU. Um foreground service protege a relevância do processo, mas não mantém a CPU acordada quando a tela apaga, conforme a [explicação oficial sobre wake locks](https://developer.android.com/blog/posts/battery-technical-quality-enforcement-is-here-how-to-optimize-common-wake-lock-use-cases). Não há garantia de transmissão contínua com tela bloqueada; suspensão, rede, restrições de bateria/OEM e encerramento pelo usuário podem interromper o trabalho. A retomada conserva a integridade do upload.

UIDT jobs, disponíveis a partir da API 34 e recomendados pelo Android para transferências longas iniciadas pelo usuário, ficam para uma evolução da execução nativa. Este incremento não implementa UIDT, sincronização periódica, varredura da galeria ou cliente iOS. Consulte a [orientação Android para UIDT](https://developer.android.com/develop/background-work/background-tasks/uidt).

## Matriz manual de aceite pendente

Use backend de ensaio com API e Worker atuais, HTTPS confiável e arquivos controlados. Registre revisão/hash do APK, Android/modelo, horários, cenário, resultado e hash do original; não registre tokens, senhas ou conteúdo privado. O resultado deve distinguir comportamento esperado, observado e limitações do aparelho.

| Cenário | Procedimento | Resultado a conferir |
| --- | --- | --- |
| Início explícito | Preparar arquivo, enviar/retomar e tocar novamente enquanto ativo. | Uma transferência/notificação; outros itens não iniciam sozinhos. Cancelar a preparação não cria upload remoto. |
| Home | Durante chunks, pressionar Home e retornar. | Serviço continua elegível e a página mostra o mesmo progresso ao voltar; nenhum envio duplicado. |
| Navegação | Abrir detalhe, consultar biblioteca e retornar durante envio. | Leituras continuam disponíveis; navegação não pausa a transferência e o progresso atual reaparece. |
| Tela bloqueada | Bloquear/desbloquear durante um arquivo maior. | Registrar continuidade ou suspensão real. Uma interrupção permanece retomável; não considerar continuidade garantida. |
| Notificações negadas | Em API 33+, negar a permissão e iniciar envio; repetir com permissão concedida. | Recusa não bloqueia o serviço; conferir Task Manager, ausência da ação na gaveta e pausa pela interface. Com permissão, conferir progresso genérico/Pausar. |
| Pausa e retomada | Pausar pela notificação e pela interface; retomar. | Serviço/notificação encerram, fonte permanece e somente chunks faltantes são enviados; um único Asset e hash idêntico. |
| Processo parado | Parar pelo Task Manager ou comando abaixo; reabrir. | Nenhuma retomada automática; fila e UUID sobrevivem, e Retomar reconcilia os chunks confirmados. |
| Revogação | Revogar sessão/dispositivo durante envio e tentar continuar. | Tentativa para ao receber recusa; app exige login, preserva fila e não registra dispositivo alternativo sozinho. |
| Conta/servidor | Solicitar saída/troca durante chunks e entrar em outro escopo de teste. | Envio anterior para antes da mudança; biblioteca/fila/sessão não se misturam. Retornar ao escopo original recupera a fila. |
| Cancelamento remoto | Remover item aberto; repetir com sessão ausente, resposta de criação perdida e sessão `Finalizing`. | Ausência permite limpeza; `409`/rede preservam fonte e metadados; criação ambígua não abandona uma reserva desconhecida. |
| Espera finita | Manter um job em finalização por mais de dois minutos no backend de ensaio. | Polling termina em `WaitingForServer`, serviço para, Worker permanece durável e Consultar obtém o resultado posterior. |
| Timeout do SO | Executar o ensaio acelerado em API 35+ descrito abaixo. | Parada imediata sem crash por serviço excedido; fonte permanece, e Retomar resolve qualquer confirmação remota já recebida. |

Para simular a ação Parar do Task Manager em um alvo API 33+ escolhido, o [comando oficial Android](https://developer.android.com/develop/background-work/services/fgs/handle-user-stopping) é:

```powershell
adb -s '<serial-do-dispositivo>' shell cmd activity stop-app com.nexora.mobile
```

Para o timeout de API 35+, use um dispositivo exclusivamente de ensaio e anote primeiro o valor existente. A configuração afeta o dispositivo, não apenas este APK. O target 36 já está sujeito ao limite; não é necessário habilitar a compatibilidade para um target antigo. Reduza a duração, inicie um upload suficientemente lento e pressione Home:

```powershell
adb -s '<serial-do-dispositivo>' shell device_config get activity_manager data_sync_fgs_timeout_duration
adb -s '<serial-do-dispositivo>' shell device_config put activity_manager data_sync_fgs_timeout_duration 15000
```

Após o ensaio, restaure o valor original. Se a primeira consulta retornou `null`, remova a alteração:

```powershell
adb -s '<serial-do-dispositivo>' shell device_config delete activity_manager data_sync_fgs_timeout_duration
```

Se havia um valor explícito, reponha esse valor em vez de remover a configuração:

```powershell
adb -s '<serial-do-dispositivo>' shell device_config put activity_manager data_sync_fgs_timeout_duration '<valor-original>'
```

Alguns aparelhos podem recusar mudanças por `device_config`; nesse caso, registre a limitação e mantenha o aceite de timeout pendente. Esses comandos são um roteiro manual; não foram executados em um dispositivo nesta entrega.

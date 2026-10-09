# Preparar outro PC para continuar o Nexora

Este roteiro parte de **um Windows novo**, com banco de desenvolvimento vazio. Leia o [handoff](handoff.md) antes de executar: o PC antigo foi confirmado sem conta ou arquivos no banco dev em 9 de outubro de 2026. Se já houver dados no destino, não inicialize/apague nem sobrescreva configuração para simular uma instalação nova. Arch não é necessário para testar o servidor e o APK agora.

## 1. Preservar o antigo e decidir sobre dados

Código/documentação vêm por Git. Banco/storage, User Secrets, chaves, APK e keystore não vêm. Manter o PC antigo até verificar o novo. Não copiar `%LOCALAPPDATA%\Nexora\postgresql\18\data` com PostgreSQL ativo nem tratar sincronização Nextcloud como backup do banco.

Para o estado vazio inventariado, provisionar bancos/credenciais/chaves novos é suficiente. Não é necessário transferir senhas antigas. Se o antigo receber dados depois deste registro, planejar uma transferência de desenvolvimento antes de usar o destino:

1. Parar API, Worker e escritores administrativos durante a captura; PostgreSQL deve continuar ativo para `pg_dump`.
2. Capturar dump PostgreSQL 18, storage inteiro (originais, derivados e temporários), configuração privada e chaves no mesmo intervalo sem escritores. Proteger cópia privada e conferir hashes. Não colocar dumps, credenciais ou arquivos pessoais no Git.
3. Restaurar em banco/diretório novos com roles e configuração adequadas, preservando o original. Não aplicar migrations posteriores antes de conferir recuperação do schema/conteúdo daquela revisão.
4. Tratar Data Protection separadamente: este Windows usa **DPAPI do usuário**. Copiar XML para outro usuário/PC não garante que ele consiga descriptografá-lo. Para preservar tokens/cursores anteriores, é necessário planejar recuperação/exportação usando o contexto capaz de abrir essas chaves. Certificado RSA é o desenho de produção, mas não transforma automaticamente chaves DPAPI antigas em portáteis. Não prometer continuidade de sessão por cópia simples.
5. Se a migração for planejada com chaves novas, invalidar sessões anteriores, exigir login novo e reiniciar sincronização dos clientes. Após restaurar o banco, rotacionar épocas de sync **antes de reconectar os clientes**, conforme [synchronization.md](synchronization.md) e [backup-and-restore.md](backup-and-restore.md).
6. Verificar saúde, login, hashes dos originais, derivados e recuperação de uploads/jobs antes de retirar o PC antigo.

Não há script de transferência completa Windows→Windows implementado. `ops/arch/backup.sh` e `restore.sh` têm contrato Linux/systemd e não devem ser executados contra o banco Windows. O procedimento acima é planejamento para dados reais; o caso atual recomendado é reprovisionamento vazio.

## 2. Ferramentas para o servidor

Instalar Git, PowerShell 7 (`pwsh`), .NET SDK da faixa **10.0.400** definida em `global.json` e garantir `tar.exe` disponível. Tailscale deve poder ser instalado/conectado neste novo PC. Android SDK/JDK/workloads são necessários apenas para recompilar o app; o servidor e os testes do núcleo mobile rodam sem eles.

O destino do clone é livre. Preferir pasta de desenvolvimento local; nunca instalar o cluster PostgreSQL/storage pessoal dentro de uma pasta sincronizada. Exemplo, escolhendo um diretório pai existente:

```powershell
git clone https://github.com/DiogoJP202/Nexora.git
Set-Location -LiteralPath Nexora
git status --short --branch
git log -5 --oneline
dotnet --version
```

Se já existe clone, revisar alterações locais antes de atualizar; não clonar por cima nem fazer reset. Após preservar alterações e confirmar branch, atualizar `main` por fast-forward quando possível. Todos os comandos seguintes partem da raiz do checkout no PC novo, sem presumir `C:\Users\dsoares`.

## 3. Restaurar, compilar e preparar PostgreSQL

```powershell
dotnet tool restore
dotnet restore Nexora.sln --locked-mode
dotnet build Nexora.sln --configuration Release --no-restore
pwsh -File scripts/Initialize-LocalPostgres.ps1
```

Verificar sucesso de cada comando antes do próximo. O script baixa PostgreSQL nativo 18.6, configura loopback `127.0.0.1:55432`, SCRAM e UTC, cria `nexora_dev`/`nexora_test` com roles próprias e registra segredos locais. Ele não cria administrador Nexora nem aplica migrations. Não instala serviço Windows.

Dados ficam em `%LOCALAPPDATA%\Nexora\postgresql\18`; storage configurado em `%LOCALAPPDATA%\Nexora\storage`. API e Worker usam User Secrets `Nexora.Api.Development`; os testes usam `Nexora.IntegrationTests.Development`. O script preserva estado conhecido existente e recusa provisionamento parcial; não recupera credenciais perdidas redefinindo roles. Consulte [development.md](development.md) se já existe uma instalação PostgreSQL alternativa.

Não executar `dotnet user-secrets list` para enviar saída ao chat. Não copiar senhas reais para comandos/documentos compartilhados. Configuração por ambiente usa `NEXORA_`, com hierarquia `__`; exemplo `NEXORA_Storage__RootPath`.

## 4. Aplicar migrations e criar a conta

No PowerShell da raiz do projeto:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:DOTNET_ENVIRONMENT = 'Development'
dotnet ef migrations list --project src/Nexora.Infrastructure --startup-project src/Nexora.Api
dotnet ef database update --project src/Nexora.Infrastructure --startup-project src/Nexora.Api
```

Existem oito migrations. A última desta revisão é `20261008184113_ClientUploadRequests`. Conferir nenhuma pendente; não adicionar migration apenas para recriar um banco. Inicialização dos serviços nunca aplica migrations automaticamente.

Em **terminal humano interativo**, depois das migrations e do build Release:

```powershell
dotnet run --project src/Nexora.Api --configuration Release --no-build --no-restore -- bootstrap-admin
```

Informar email, senha de 14–256 caracteres e confirmação; senha sem eco. Não enviar a senha no chat, pipe, argumentos ou arquivo versionado. O comando exige console interativo e recusa segundo administrador. Se banco existente já tem conta, usar a conta; recuperação por `reset-admin-password` é uma operação separada que revoga sessões.

## 5. Iniciar API e Worker

Terminal A, raiz do checkout:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:DOTNET_ENVIRONMENT = 'Development'
dotnet run --project src/Nexora.Api --configuration Release --no-build --no-restore
```

Terminal B, raiz do mesmo checkout:

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
dotnet run --project src/Nexora.Worker --configuration Release --no-build --no-restore
```

O profile da API fixa `http://127.0.0.1:5100`. API/Worker devem ler a mesma conexão, raiz e limites. Variáveis de ambiente em um terminal não se propagam ao outro. Se a porta já estiver ocupada, identificar a instância antes de iniciar outra; PIDs antigos do handoff não valem aqui.

Terminal C para conferência:

```powershell
Invoke-RestMethod http://127.0.0.1:5100/health/live
Invoke-RestMethod http://127.0.0.1:5100/health/ready
dotnet test Nexora.sln --configuration Release --no-build --no-restore
```

Esperado: ambos `status=Healthy`, HTTP 200. `/` pode retornar 404, pois não existe interface web. Ready confere banco/migrations, mas não verifica Worker, Tailscale, disco, TLS ou login. Confirmar separadamente o Worker e o fluxo de conclusão.

A evidência anterior era 293 testes sem falhas nem ignorados. Conferir a configuração de testes PostgreSQL e **zero ignorados**; suite verde com casos ignorados não reproduz aquela validação. Não usar o banco dev/produção para fixtures. O espaço livre deve superar a margem default de 50 GiB além da reserva exigida para o arquivo. Começar com arquivos pequenos.

Aqui `--no-build` usa o Release compilado no passo 3 e evita reconstruir executáveis em uso no Windows. Se mudar código, parar API/Worker, compilar novamente e então repetir os testes/reiniciar serviços. O resultado vale para o código compilado.

Depois de reiniciar o PC, iniciar o cluster antes dos serviços:

```powershell
pwsh -File scripts/LocalPostgres.ps1 -Action Start
```

Para parar, Ctrl+C nos terminais da API/Worker; depois `pwsh -File scripts/LocalPostgres.ps1 -Action Stop`. Interromper apenas processos identificados desse ambiente.

## 6. HTTPS privado para o celular

Seguir [windows-mobile-test.md](windows-mobile-test.md): instalar Tailscale no PC e Android, autenticar ambos na mesma tailnet, aceitar VPN e habilitação HTTPS. Conta Tailscale e conta Nexora são distintas; login/consentimento requerem ação humana. Manter o PC ligado/acordado durante o teste.

PowerShell **como Administrador**, no Windows novo com Tailscale instalado:

```powershell
& 'C:\Program Files\Tailscale\tailscale.exe' serve --bg --https=443 http://127.0.0.1:5100
& 'C:\Program Files\Tailscale\tailscale.exe' serve status
```

Concluir eventual consentimento para HTTPS e anotar a URL verdadeira. Serve é acesso privado à tailnet; manter grants restritas e Funnel desativado. Não abrir Kestrel em `0.0.0.0`, expor arquivos estáticos ou substituir HTTPS por HTTP no APK para contornar esta etapa.

No terminal do checkout, substituir o exemplo pelo hostname real, sem protocolo/porta/caminho:

```powershell
dotnet user-secrets set 'AllowedHosts' 'localhost;127.0.0.1;[::1];seu-pc.sua-tailnet.ts.net' --project src/Nexora.Api
```

Reiniciar a API que já está ativa, depois conferir health local e `https://<hostname-real>/health/ready` no navegador do celular conectado. O `127.0.0.1` do celular é o próprio celular, não o servidor. Usar no app **a URL HTTPS raiz real**, sem `/api`/`/health`, email/senha bootstrap e nome de dispositivo.

URL/conta novas têm cache/fila/identificação próprios. Em servidor recém-criado, fazer login novamente; tokens antigos não são transferidos com o Git. Testar JPEG pequeno, preview, favoritos, lixeira/restauração, download e deduplicação; depois arquivo controlado de 20–50 MiB para pause/retomada/Home, conforme a matriz Android. Registrar os resultados, inclusive falhas.

## 7. APK e assinatura ao trocar de PC

O APK não é versionado. O usuário já informou download no celular; confirmar se foi instalado e se existem itens na fila antes de alterar o app. Copiar o APK original por meio privado ou usar o que já está baixado permite testar sem instalar ferramentas Android no novo PC.

Se precisar recompilar, instalar MAUI/Android, SDK plataforma 36/build-tools 36.0.0 e JDK 21. A referência antiga e o build estão em [mobile.md](mobile.md). `Build-Android.ps1` apenas localiza/verifica ferramentas já instaladas; não as instala. Com elas prontas:

```powershell
pwsh -File scripts/Build-Android.ps1 -Configuration Debug
```

Para indicar instalações não detectadas, usar `-AndroidSdkDirectory` e `-JavaSdkDirectory` com caminhos absolutos reais. Para verificar as dependências travadas do app antes do build, executar restore do projeto com `--locked-mode` e esses caminhos como propriedades MSBuild; em seguida usar `-NoRestore`. O projeto Android é separado da solution principal.

**A assinatura Debug do novo PC pode ser diferente.** Android exige certificado compatível para atualizar `com.nexora.mobile` já instalado. O keystore antigo encontrado foi `%LOCALAPPDATA%\Xamarin\Mono for Android\debug.keystore`. Caso seja necessário manter atualização com a mesma assinatura, transferir esse arquivo privadamente, fora do checkout, preservar permissões e verificar que o build usa a chave correta antes de instalar. Nunca publicar a chave em Git/chat; o projeto ainda não tem assinatura de distribuição de produção configurada.

Não desinstalar automaticamente para resolver incompatibilidade de assinatura: isso remove sessão, cache e **fontes privadas de uploads pendentes**. Backup/transferência Android estão desabilitados; não há restauração automática desses dados. Concluir ou tratar explicitamente a fila antes de uma reinstalação necessária. Se a assinatura for compatível, instalação por cima preserva dados do app, mas o comportamento ainda deve ser verificado no aparelho.

Conferir versão/código/hash do APK efetivamente transferido. Rebuilds não precisam ter o mesmo SHA-256. O inventário do APK antigo está em [handoff.md](handoff.md) e o histórico em [mobile-validation.md](mobile-validation.md).

## 8. Registrar a nova evidência

Atualizar documentos com sistema/SDK/PG, commit, comandos, contagens de testes sem ignorados, versão/API do aparelho e casos Android realmente observados. A URL privada e identificadores pessoais podem ficar em anotação local privada; senhas/tokens/chaves nunca entram nos relatórios versionados.

Até concluir esse ensaio, manter Fase 8 como aceite no aparelho pendente. Até executar instalação/reinício/backup/restore no Arch, manter Fase 7 pendente. Continuar a partir do prompt em [handoff.md](handoff.md), sem depender da memória do chat antigo.

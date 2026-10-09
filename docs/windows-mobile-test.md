# Testar o Android com servidor Windows

O APK pode ser testado com PostgreSQL, API e Worker no Windows. Arch é o destino de produção; não é necessário para este ensaio. O servidor usa o banco e o storage locais de desenvolvimento.

Em 9 de outubro de 2026, PostgreSQL estava ativo, migrations atualizadas, API em `http://127.0.0.1:5100` e Worker em execução. `/health/live` e `/health/ready` retornaram `200`, com `{"status":"Healthy"}`. A conta administrativa ainda não havia sido criada. Isso verifica o servidor local; conexão pelo Tailscale e execução do APK no celular continuam dependendo das etapas abaixo.

## 1. Conectar o PC e o celular

Instale [Tailscale para Windows](https://tailscale.com/download/windows) e Tailscale para Android pela Play Store. Entre com a mesma conta nos dois dispositivos e conecte ambos; no Android, aceite a configuração da VPN. O Tailscale fornecerá a conexão privada e HTTPS. A conta Tailscale é separada da conta administrativa Nexora.

O APK atual exige HTTPS e certificado confiável. O endereço HTTP de loopback serve à API local; o celular usará a URL HTTPS do Tailscale. A configuração segue a [instalação Windows](https://tailscale.com/docs/install/windows) e o [Tailscale Serve](https://tailscale.com/docs/features/tailscale-serve).

## 2. Criar a conta Nexora

Na raiz do repositório, abra um PowerShell interativo:

```powershell
dotnet run --project src/Nexora.Api --configuration Release --no-build --no-restore -- bootstrap-admin
```

Informe o email, uma senha de 14–256 caracteres e sua confirmação. A senha não aparece enquanto é digitada; não a envie pelo chat. Use essas credenciais no app Nexora. O comando recusa criar um segundo administrador. `reset-admin-password` é o procedimento separado de recuperação e revoga sessões anteriores, conforme [authentication.md](authentication.md).

## 3. Preparar o HTTPS privado

Depois de entrar no Tailscale no PC, abra **PowerShell como Administrador**:

```powershell
& 'C:\Program Files\Tailscale\tailscale.exe' serve --bg --https=443 http://127.0.0.1:5100
& 'C:\Program Files\Tailscale\tailscale.exe' serve status
```

Se aparecer um link de consentimento para habilitar HTTPS, conclua a configuração no navegador e repita o comando. Anote a URL mostrada, por exemplo `https://seu-pc.sua-tailnet.ts.net`. O exemplo deve ser substituído pelo endereço real do seu PC. Serve oferece acesso à tailnet; mantenha o modo Serve durante este ensaio. [Sintaxe e HTTPS](https://tailscale.com/docs/reference/tailscale-cli/serve), [terminal administrativo no Windows](https://tailscale.com/docs/reference/examples/serve).

## 4. Permitir o hostname na API

A API aceita inicialmente apenas hosts de loopback. Acrescente o domínio completo real, sem `https://`, porta ou caminho:

```powershell
dotnet user-secrets set 'AllowedHosts' 'localhost;127.0.0.1;[::1];seu-pc.sua-tailnet.ts.net' --project src/Nexora.Api
```

Reinicie a API para aplicar a configuração. Se ela foi iniciada pelo agente, envie a URL mostrada pelo Serve para que o processo correto seja reconfigurado e reiniciado. Iniciar outra API enquanto a porta 5100 estiver ocupada resulta em conflito.

Para iniciar manualmente, na raiz do projeto, use um terminal para a API:

```powershell
dotnet run --project src/Nexora.Api --configuration Release --no-build --no-restore
```

Em outro terminal, inicie o Worker, caso ainda esteja parado:

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
dotnet run --project src/Nexora.Worker --configuration Release --no-build --no-restore
```

PostgreSQL local pode ser iniciado com `pwsh -File scripts/LocalPostgres.ps1 -Action Start`. API e Worker compartilham User Secrets em Development. Os comandos `--no-build` pressupõem o build Release já existente; em uma instalação nova, siga [development.md](development.md).

## 5. Entrar e fazer o primeiro teste

No navegador do celular conectado ao Tailscale, abra `https://seu-pc.sua-tailnet.ts.net/health/ready`. O resultado esperado é `{"status":"Healthy"}`. A raiz `/` não tem interface web e pode retornar `404`; isso não indica falha da API.

No Nexora, preencha:

| Campo | Valor |
| --- | --- |
| Servidor HTTPS | URL real `https://seu-pc.sua-tailnet.ts.net`, sem `/api` ou `/health`. |
| Conta | Email criado pelo bootstrap Nexora. |
| Senha | Senha criada no bootstrap. |
| Dispositivo | Um nome para seu celular; o app sugere o modelo. |

Envie primeiro um JPEG pequeno e sincronize a biblioteca. Confira o preview, favorito, lixeira e restauração. Reenviar os mesmos bytes deve reutilizar o item existente. Depois, um arquivo controlado de aproximadamente 20–50 MiB permite observar chunks, Home, notificação, pausa e retomada; o primeiro JPEG pode terminar rápido demais para esse ensaio.

Mantenha o PC ligado, acordado e conectado, com PostgreSQL, API e Worker ativos. Sem Worker, conclusões e imagens ficam pendentes. O limite de margem livre continua sendo 50 GiB; a capacidade disponível no Windows deve ser conferida antes de testes grandes. O roteiro completo está em [mobile.md](mobile.md) e [android-background-uploads.md](android-background-uploads.md).

## Encerrar o ensaio

Encerre API e Worker por `Ctrl+C` nos terminais que os iniciaram, ou peça ao agente para encerrar os processos que abriu. Para desligar este proxy HTTPS no terminal administrativo:

```powershell
& 'C:\Program Files\Tailscale\tailscale.exe' serve --https=443 off
```

Essa configuração de desenvolvimento não conclui o aceite de operação no Arch nem o exercício de restauração da Fase 7.

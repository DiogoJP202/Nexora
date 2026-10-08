# Autenticação da Fase 2

Nexora tem uma conta administrativa, dispositivos e sessões revogáveis. A autenticação usa ASP.NET Core Identity para credenciais e o bearer handler nativo para access tokens opacos, conforme o [mecanismo do framework](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-api-authorization?view=aspnetcore-10.0). Não há registro público, recuperação de senha por HTTP ou dependência de um servidor externo de identidade.

## Conta administrativa e recuperação local

Inicie PostgreSQL e aplique migrations antes de criar a conta:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet ef database update --project src/Nexora.Infrastructure --startup-project src/Nexora.Api
dotnet run --project src/Nexora.Api -- bootstrap-admin
```

O comando local solicita email, senha e confirmação; a senha não aparece na tela nem é argumento do processo. Email é também o username. O bootstrap é serializado por lock no PostgreSQL e recusa uma segunda conta. A API não inicializa um administrador automaticamente.

Passphrases devem ter entre 14 e 256 caracteres, sem exigência de dígitos, maiúsculas ou símbolos. Identity aplica hashing; nenhuma senha é armazenada em claro. A conta é bloqueada por 15 minutos depois de cinco falhas de login.

Para recuperação local:

```powershell
dotnet run --project src/Nexora.Api -- reset-admin-password
```

O comando pede nova senha e confirmação sem eco. Um reset revoga todas as sessões e invalida tokens anteriores pela verificação de security stamp. Execute os comandos em terminal interativo e não redirecione senhas para arquivos, logs ou histórico.

## Contratos HTTP

| Método e rota | Entrada | Resultado |
| --- | --- | --- |
| `POST /api/auth/login` | `login`, `password`, `deviceName`, `platform`, `deviceId` opcional. | Par de tokens e identificação da sessão/dispositivo. |
| `POST /api/auth/refresh` | `refreshToken`. | Novo par; o refresh apresentado é consumido. |
| `POST /api/auth/logout` | Sem corpo, com access token. | Revoga a sessão atual. |
| `GET /api/devices` | Access token. | Dispositivos autorizados do usuário. |
| `DELETE /api/devices/{id}` | Access token e UUID do dispositivo. | Revoga o dispositivo e suas sessões. |

Login utiliza email como `login`. Exemplo de formato, com valores ilustrativos:

```json
{
  "login": "admin@example.test",
  "password": "<sua-passphrase>",
  "deviceName": "Meu celular",
  "platform": "Android"
}
```

O retorno de login e refresh contém `tokenType`, `accessToken`, `expiresIn`, `refreshToken`, `deviceId`, `sessionId` e `sessionExpiresAt`. `expiresIn` expressa segundos; o access token dura até cinco minutos, limitado pelo fim da sessão. Trate tokens como valores opacos: não decodifique claims no cliente nem suponha formato JWT.

Use `Authorization: Bearer <accessToken>` nos endpoints protegidos. Em login e refresh, omita um Authorization antigo: um bearer ainda válido criptograficamente, mas de sessão revogada, é rejeitado antes do endpoint anônimo. Tokens não devem ser enviados na URL, gravados em logs ou publicados em exemplos de diagnóstico. O servidor guarda apenas SHA-256 do refresh token aleatório de 256 bits; o cliente recebe o valor original uma única vez por emissão.

O corpo das requisições de autenticação está limitado a 16 KiB; chunks de arquivo têm limite próprio descrito em [uploads.md](uploads.md). `deviceName` aceita até 100 caracteres e `platform` até 32, sem controles ou valores em branco; `login` aceita até 254 caracteres. Respostas de `/api/*` utilizam `Cache-Control: no-store`.

Login é limitado a cinco chamadas por IP/minuto; refresh a 30 chamadas por IP/minuto. Erros usam Problem Details e códigos estáveis, sem connection strings ou detalhes de exceções. Login inválido não distingue conta ausente de senha incorreta. Um `deviceId` inválido só é informado depois de a senha ser validada.

| Status | Códigos relevantes |
| --- | --- |
| `400` | `invalid_request`, `invalid_device`, `https_required`. |
| `401` | `invalid_credentials`, `invalid_refresh_token`, `authentication_required`. |
| `404` | `resource_not_found`, inclusive dispositivo de outro proprietário. |
| `413` | `request_too_large`. |
| `429` | `rate_limited`; respeitar `Retry-After` quando informado. |
| `503` | `service_unavailable` quando a dependência de autenticação está indisponível. |

Logout e revogação bem-sucedidos retornam `204 No Content`. A listagem de dispositivos retorna uma lista com `id`, `name`, `platform`, `createdAt`, `lastSeenAt` e `revokedAt`.

## Dispositivos e sessões

Um login sem `deviceId` cria um cadastro de dispositivo. Para reutilizar um dispositivo, enviar o ID retornado em login anterior; ele precisa pertencer à conta e estar ativo. Dispositivo de outro proprietário ou revogado produz `invalid_device` com `400`. Um novo login no mesmo dispositivo revoga suas sessões anteriores.

O cadastro de Device identifica uma sessão/dispositivo reconhecido pela conta; UUID, nome e plataforma não comprovam a identidade física do hardware. O cliente não pode usar o UUID sozinho para autenticar-se.

AuthSession tem duração absoluta de 30 dias, sem renovação desse prazo por refresh. Access tokens duram até cinco minutos. Cada requisição autenticada consulta PostgreSQL para verificar sessão, expiração, dispositivo, security stamp e lockout. Revogação e reset bloqueiam imediatamente requisições posteriores, mesmo com access token ainda válido. Se o banco estiver indisponível, o servidor nega acesso com `503`.

LastSeen é atualizado com intervalo mínimo de cinco minutos para reduzir escritas. Logout revoga a sessão atual; revogação de Device revoga todas as sessões vinculadas. O cadastro permanece disponível para representar o estado revogado.

## Refresh de uso único

A rotação é transacional: consumir o token atual e criar seu sucessor fazem parte da mesma operação. Reutilizar um token consumido revoga a família da sessão. Um cliente deve serializar chamadas de refresh e substituir o token local somente após receber o novo par.

Não repetir automaticamente um refresh anterior quando sua resposta foi perdida: o servidor pode já ter consumido o token. Nesse caso, o cliente pode precisar solicitar login novamente. Logout, dispositivo revogado, sessão expirada, conta bloqueada ou security stamp alterado impedem renovação.

## Chaves de proteção e HTTPS

Data Protection utiliza ApplicationName fixo `Nexora.Auth`. A persistência fica por padrão em `Nexora/keys` sob LocalApplicationData do usuário do processo, fora da instalação e do storage de arquivos. `DataProtection:KeyDirectory`, ou `NEXORA_DataProtection__KeyDirectory`, permite caminho absoluto alternativo; raiz de volume não é permitida.

- Windows: diretório com ACL restrita ao usuário atual e SYSTEM; por padrão, key ring protegido com DPAPI desse usuário. Um certificado configurado substitui a proteção DPAPI. Alterar o usuário do processo exige planejar recuperação das chaves.
- Linux em Development: diretório restrito `0700`, com key ring sem criptografia de arquivo permitido para o desenvolvimento local.
- Linux fora de Development: exige `DataProtection:CertificatePath` para PFX RSA com chave privada; `DataProtection:CertificatePassword` é um segredo opcional. Certificado e chave privada devem ter acesso restrito e permanecer fora do Git. Esse certificado protege o key ring e tem função distinta do certificado TLS do Tailscale Serve.

Backup deve preservar o key ring e o material que permite descriptografá-lo. Mover a instalação sem essas chaves invalida os access tokens existentes. Ao trocar certificado, mantenha a capacidade de recuperar chaves antigas; não apague o material anterior sem uma estratégia de rotação/restauração. Veja a [configuração de Data Protection](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0).

Fora de Development, `/api/*` exige HTTPS. O desenho de operação usa Tailscale Serve para terminar TLS e encaminhar a requisição à API em loopback. Forwarded Headers são aceitos somente de proxies loopback explicitamente confiáveis. Funnel permanece desativado e grants restringem o acesso privado. Esta fase não publica o servidor na internet nem implementa o deploy systemd completo.

## Verificação

Testes relevantes cobrem bootstrap concorrente, política de senha/lockout, login, expiração, refresh concorrente, reutilização, logout, revogação de dispositivo, acesso entre proprietários, reset e indisponibilidade do banco. Os testes usam bancos próprios `nexora_it_<id>` e Data Protection efêmero por padrão, sem criar contas ou tocar chaves pessoais no ambiente de desenvolvimento.

Execute `dotnet test Nexora.sln` com a conexão de testes configurada conforme o [guia de desenvolvimento](development.md). Esta documentação descreve os fluxos e comandos; não é um relatório de resultados de uma execução específica.

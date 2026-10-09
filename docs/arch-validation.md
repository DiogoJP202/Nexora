# Nexora — validação operacional da Fase 7

Atualizado em 9 de outubro de 2026. Código de referência da preparação: `fb7d9b6`; pacote com sincronização da Fase 8: `089c593`. **A Fase 7 permanece em preparação.** Nenhum servidor Arch foi acessado, instalado ou restaurado durante estes incrementos. Os comandos deste documento são um roteiro a executar com dados de teste antes do uso pessoal em produção.

## Evidências locais

- Desenvolvimento e verificação realizados no Windows, SDK 10.0.400 e PostgreSQL 18 nativo.
- Restore com arquivos de lock; build Release; 229 testes aprovados, sendo 60 unitários e 169 de integração, sem falhas ou ignorados.
- Publicação cruzada para Linux x64: API, Worker e migration bundle são ELF x86-64. API e Worker incluem SkiaSharp nativo; os três artefatos incluem runtimes .NET/ASP.NET Core 10.0.12, verificados pela configuração publicada ou embutida.
- Scripts Bash verificados sintaticamente com Git Bash. Guardas de caminhos e manifesto testadas em diretórios temporários; o resultado informa explicitamente verificações de links indisponíveis nesse ambiente.
- Guardas: 18 verificações aprovadas e 3 de symlinks ignoradas por emulação do Git Bash. Fixtures GNU tar aceitaram o arquivo POSIX e recusaram traversal e duplicatas; não executaram serviços nem restore de banco.
- Release limpa `phase7-linux-x64-20261008`, gerada por `Publish-ArchRelease.ps1` a partir de `fb7d9b6`, com `sourceDirty=false`. SHA-256 do manifesto: `a655352b7580021dc0c9ce1853dad43b6ca82d746579a6b12dc54d17aacb677f`. Configuração local sintética foi excluída durante a publicação.
- Conferência independente por `common.sh`: cobertura completa do manifesto e hashes dos 723 arquivos aprovados.
- Os pacotes ficam em `artifacts/arch/<release-id>`, ignorados pelo Git. Releases de produção exigem commit limpo, `sourceDirty=false` e hash do manifesto obtido na origem. A opção `-AllowDirty` produz somente pacotes de verificação, recusados pelo instalador.

Em 9 de outubro, o incremento mobile gerou a release **`phase8-linux-x64-20261009`** a partir de `089c593597cc62bbb4560e1139a7ea50d6d07ded`, com `sourceDirty=false`. Ela inclui as duas migrations novas, última `20261008184113_ClientUploadRequests`, API/Worker/bundle Linux x64 com runtime 10.0.12 e a rotação de época no restore isolado. SHA-256 do manifesto: `17d8e6def9b7c0a082acd16f0fecf28feacee02682a8cc50b13cea4fac5501cd`. A conferência independente por `common.sh` validou os 723 arquivos. Use essa release, ou uma publicação posterior equivalente, para o protocolo do cliente Android; o pacote histórico da Fase 7 não inclui sync. A solução teve 274 testes locais aprovados, conforme [mobile-validation.md](mobile-validation.md); não houve execução Linux.

Publicação cruzada confere o formato e o conteúdo do pacote. A execução de código nativo, unidades systemd, permissões Linux e recuperação após reinício dependem das verificações abaixo.

O SQL de provisionamento PostgreSQL foi revisado e seus comandos permanecem pendentes de execução no host. Os testes de integração usam bancos próprios de desenvolvimento; não reproduzem os privilégios Linux/roles do provisionamento de produção.

## Registro do servidor

Antes do exercício, registrar em uma cópia privada deste documento:

| Evidência | Valor a preencher |
| --- | --- |
| Data UTC, operador e host | Pendente. |
| Release, commit e SHA-256 do manifesto confiável | Pendente. |
| Arch/kernel/systemd, arquitetura e cgroup v2 | Pendente. |
| PostgreSQL cliente e servidor, versão major 18 | Pendente. |
| Volume, filesystem, espaço livre e orçamento admitido | Pendente. |
| FQDN HTTPS privado, revisão das grants e Funnel desativado | Pendente. |
| Identificação da cópia de backup independente | Pendente. |
| Banco e diretório novos usados no restore isolado | Pendente. |

Não registrar senhas, connection strings, tokens, conteúdo pessoal, nomes de arquivos ou localização nesse relatório versionado. IDs e hashes de arquivos de teste permitem comprovar resultados.

## Instalação e acesso privado

Seguir [arch-deployment.md](arch-deployment.md), revisar SQL/migrations e executar o preflight antes de ativar. Registrar:

1. Instalador recusa manifesto alterado, release existente, links e pacote `sourceDirty=true`; não aplica migrations nem inicia serviços.
2. PostgreSQL usa role de aplicação sem privilégios de DDL. Migrations explícitas terminam sem pendências e a API pode ler o histórico de migrations.
3. Configurações privadas e chaves ficam fora da release; permissões são as documentadas e usuário `nexora` não possui shell nem privilégios administrativos.
4. Unidades passam `systemd-analyze verify`. API e Worker iniciam com limites de memória que incluem o processo filho. Porta 5100 escuta somente em loopback.
5. `/health/live` e `/health/ready` retornam 200. Banco indisponível produz readiness 503 e impede requisições autenticadas; após recuperação retorna 200. Não executar essa falha com dados pessoais ou operações em curso.
6. HTTPS funciona no FQDN autorizado do Tailnet, com cadeia TLS válida. Origem/usuário sem grant não chega ao serviço; regras amplas existentes foram revisadas. Funnel está desativado. `/api/*` sem HTTPS é recusado; `/openapi/v1.json` não está disponível em Production.
7. Bootstrap cria somente o administrador inicial. Login, refresh serializado, logout, revogação e reset local funcionam; nenhuma credencial aparece no journal.

## Arquivos, imagens e reinícios

Usar conteúdo descartável e IDs anotados. Consultar [uploads.md](uploads.md), [images.md](images.md) e [library.md](library.md) para os contratos e estados esperados.

1. Enviar arquivo em chunks fora de ordem, repetir chunk idêntico e retomar uma sessão após reinício da API. Confirmados são preservados e chunks diferentes no mesmo índice são recusados.
2. Concluir upload, conferir tamanho e SHA-256 do original baixado. HTTP Range devolve os bytes esperados; concluir novamente preserva operação/Asset.
3. Reenviar os mesmos bytes e verificar o mesmo Asset, incluindo nome e favorito. Colocar na lixeira; reenvio exige restauração explícita. Restaurar e conferir o conteúdo.
4. Enviar JPEG com orientação/EXIF, PNG e WebP estáticos. Conferir metadados, orientação, derivados PNG com até 256/1280 px, sem ampliação, e hash do original preservado. Isso comprova a carga nativa Skia no Arch.
5. Arquivo não suportado, imagem corrompida e limite de pixels mantêm o original utilizável. Conferir falha/estado e memória da unidade, sem invalidar outros arquivos.
6. Interromper o Worker durante uma montagem e durante renderização em ambiente de teste. Confirmar parada dos filhos, recuperação por lease/retry e somente uma publicação concluída por geração. Registrar jobs e seus resultados.
7. Reiniciar serviços e, depois, o servidor. Conferir login, originais, derivados, temporários, reservas, fila e readiness. Restaurar um item não deve competir incorretamente com purge; verificar em banco descartável, com retenção apropriada ao teste.
8. Conferir `/api/storage`, journal sanitizado e consulta administrativa de tarefas esgotadas. Registrar alarmes de espaço e orçamento real antes de usar arquivos grandes.

Reinício normal e interrupção de processo não demonstram resistência a queda de energia. Esse teste exige um ambiente descartável e registro separado do filesystem e das condições da falha.

## Backup e restauração

Executar [backup-and-restore.md](backup-and-restore.md). A conclusão requer evidências de que:

1. API, Worker e filhos ficaram parados durante todo o snapshot; banco, storage, temporários, configuração e Data Protection pertencem ao mesmo intervalo sem escritores.
2. Hashes passaram após a cópia independente. O mesmo código, migration bundle, PFX e keyring estão preservados.
3. Restore usou banco inexistente e diretório novo, sem alterar produção. Configuração recuperada ficou inerte até revisão para apontar exclusivamente ao destino de teste.
4. Uma instância isolada iniciou com o banco/storage restaurados e readiness 200, sem aplicar migrations de outra release. Novo login e tokens válidos preservados para o teste são verificados com o mesmo material de Data Protection.
5. Originais e derivados escolhidos mantêm tamanho/hash; timeline, favoritos, lixeira e upload retomável mantêm seus estados. Nenhum job escreve no banco ou storage de produção.
6. Atualização interrompida foi recuperada seguindo a compatibilidade de schema; retornar código antigo não foi tratado como rollback automático de banco.

Preencher os resultados, corrigir falhas e atualizar o roadmap somente depois do exercício. Esse registro é o aceite do backend para uso com dados pessoais; o cliente mobile continua na Fase 8.

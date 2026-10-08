# Nexora — validação operacional da Fase 7

Atualizado em 8 de outubro de 2026. **A Fase 7 permanece em preparação.** Nenhum servidor Arch foi acessado, instalado ou restaurado durante este incremento. Os comandos deste documento são um roteiro a executar com dados de teste antes do uso pessoal em produção.

## Evidências locais

- Desenvolvimento e verificação realizados no Windows, SDK 10.0.400 e PostgreSQL 18 nativo.
- Restore com arquivos de lock; build Release; 229 testes aprovados, sendo 60 unitários e 169 de integração, sem falhas ou ignorados.
- Publicação cruzada para Linux x64: API, Worker e migration bundle são ELF x86-64. API e Worker incluem SkiaSharp nativo; os três artefatos incluem runtimes .NET/ASP.NET Core 10.0.12, verificados pela configuração publicada ou embutida.
- Scripts Bash verificados sintaticamente com Git Bash. Guardas de caminhos e manifesto testadas em diretórios temporários; o resultado informa explicitamente verificações de links indisponíveis nesse ambiente.
- Os pacotes ficam em `artifacts/arch/<release-id>`, ignorados pelo Git. Releases de produção exigem commit limpo, `sourceDirty=false` e hash do manifesto obtido na origem. A opção `-AllowDirty` produz somente pacotes de verificação, recusados pelo instalador.

Publicação cruzada confere o formato e o conteúdo do pacote. A execução de código nativo, unidades systemd, permissões Linux e recuperação após reinício dependem das verificações abaixo.

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

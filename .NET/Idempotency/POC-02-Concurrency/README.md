# POC 2 — Concorrência dentro de uma instância

## Propósito

Evoluir a POC 1 para que requisições simultâneas com a mesma chave não executem o endpoint juntas. Preservamos o contrato HTTP, o hash dos bytes, a retenção de dez minutos e os pedidos em memória. Esta pasta é independente da POC 1.

## O que mudou e por quê

Leia a [explicação técnica detalhada](./docs/explicacao-tecnica.md) para acompanhar o fluxo, os recursos do .NET e as decisões de coordenação.

- `KeyedRequestLock` é singleton e associa cada chave interna a um `SemaphoreSlim(1, 1)`: uma execução por vez para aquela chave, sem bloqueio global entre chaves distintas.
- `WaitAsync(RequestAborted)` aguarda de forma assíncrona e permite cancelar a espera quando o cliente desconecta. Não usamos `lock` porque a região contém operações assíncronas.
- A consulta ao cache fica **dentro** da região protegida. Depois de esperar, a requisição encontra o resultado que a anterior salvou. Não há consulta inicial fora da região nesta versão.
- `finally` libera o semáforo mesmo quando há conflito, retorno antecipado ou exceção. Só entramos nesse `try` depois de adquirir o semáforo: cancelar a espera não pode liberar a permissão de outra requisição.
- Um atraso didático de 500 ms antes de criar o pedido facilita observar concorrência. Ele usa `Task.Delay`, não bloqueia uma thread com `Thread.Sleep` e pode ser configurado.
- `Idempotency:CoordinationEnabled=false` desliga somente a coordenação para demonstrar a corrida. O padrão é `true`. Não altere esse modo durante um experimento.

```text
A, chave X → adquire → consulta cache → cria pedido → salva resposta → libera
B, chave X → espera .............................................. → adquire
B, chave X → consulta cache → devolve resposta salva → libera
C, chave Y → pode progredir independentemente de X
```

O semáforo permanece adquirido até a escrita da resposta terminar. Isso simplifica o fluxo, mas um cliente lento pode aumentar a espera dos demais com a mesma chave. O mecanismo não promete ordem FIFO entre os participantes.

## Executar com proteção

Requisito: SDK .NET 10. O script funciona no Windows PowerShell 5.1 e no PowerShell 7. Execute-o diretamente no terminal já aberto.

```powershell
# A partir da raiz do repositório Studies
cd ./.NET/Idempotency/POC-02-Concurrency
dotnet run --project Idempotency.Poc02.csproj --urls http://localhost:5102
```

Em outro terminal, na mesma pasta:

```powershell
.\Experiment.ps1
```

Resultado esperado: `Requests = 20`, `DistinctOrderIds = 1`, `Replays = 19`. Cada execução do script gera uma chave nova para não reutilizar resultados de um experimento anterior. O script envia todas as chamadas antes de aguardar suas respostas e falha se o resultado divergir.

O script também confirma os IDs retornados em `GET /orders` e exibe `OrdersFoundInStore`. A contagem considera apenas os IDs da rodada atual. Para comparar com chamadas sequenciais, execute `.\Experiment.ps1 -Sequential`. O parâmetro `-Requests` aceita de 2 a 200 chamadas; `-Sequential` e `-ExpectDuplicates` são mutuamente exclusivos.

Para observar todos os pedidos:

```powershell
Invoke-RestMethod http://localhost:5102/orders
```

`requests.http` preserva os cenários manuais de criação, replay, conflito, nova chave e chave ausente.

## Comparar com o modo sem proteção

Pare a aplicação com Ctrl+C e inicie novamente:

```powershell
dotnet run --project Idempotency.Poc02.csproj --urls http://localhost:5102 --Idempotency:CoordinationEnabled=false --Experiment:DelayMilliseconds=1000
```

No outro terminal:

```powershell
.\Experiment.ps1 -ExpectDuplicates
```

Esperamos mais de um ID para a mesma chave. A quantidade exata depende do agendamento das requisições; o atraso amplia a janela, mas não é uma barreira determinística. Se não observar a corrida, aumente o atraso e repita. Reinicie sem a opção `false` para restaurar a proteção.

## Testes automatizados com xUnit

Na pasta da POC 2, sem iniciar a API:

```powershell
dotnet test ./Tests/Idempotency.Poc02.Tests.csproj -c Release
dotnet test ./Tests/Idempotency.Poc02.Tests.csproj -c Release --filter "Category=Concurrency"
dotnet test ./Tests/Idempotency.Poc02.Tests.csproj -c Release --filter "Category=Unit"
```

A suíte inclui testes unitários do coordenador e integração entre componentes para o contrato HTTP, concorrência, conflitos, cancelamento e recuperação após falha antes do efeito. Consulte o [guia dos testes](./Tests/README.md). O teste sem proteção passa ao caracterizar a duplicação; com proteção espera um pedido e dezenove replays.

## Verificação didática inicial sem sockets

O executável abaixo foi preservado como demonstração inicial. A suíte xUnit é a referência para regressões e possui cobertura mais ampla.

```powershell
dotnet run --project Verification/Verification.csproj
```

Esse executável usa o middleware real com `DefaultHttpContext`, cache e streams em memória. Uma barreira controlada reproduz a corrida sem depender de um tempo de rede ou de bibliotecas externas de testes.

Ele verifica:

- Vinte chamadas de mesma chave sem proteção criam vinte pedidos na barreira controlada.
- Com proteção, as mesmas vinte chamadas criam um pedido e dezenove replays.
- Status, corpo e `Location` são preservados.
- Corpo diferente retorna `409` sem criar outro pedido.
- Chaves diferentes entram no endpoint independentemente.
- Cancelamento de uma espera não libera a permissão da requisição ativa.
- Exceção libera a chave e restaura o stream, permitindo uma tentativa posterior.

Isso exercita concorrência e o middleware em processo; o script HTTP complementa a verificação do servidor, binding e transporte.

## Limitações intencionais

- O lock é **local ao processo**. Dois pods têm dois dicionários de semáforos e podem executar a mesma chave simultaneamente. Esse será o tema da POC 3.
- Os semáforos ficam na memória durante toda a vida da aplicação, inclusive depois que a resposta expira. O número cresce com as chaves distintas: use um conjunto limitado de chamadas neste laboratório. Uma implementação de produção precisa de gestão segura de ciclo de vida ou outra estratégia.
- Não removemos um semáforo simplesmente após `Release`: chamadas podem estar esperando nele, enquanto uma nova chamada criaria outro semáforo para a mesma chave, quebrando a exclusão. Candidatos perdedores de `GetOrAdd`, que nunca foram disponibilizados aos chamadores, são descartados com segurança.
- Reinício perde os pedidos e o cache; expiração permite nova execução. Não existe persistência durável nem atomicidade com banco.
- Uma exceção após produzir um efeito pode permitir que a tentativa seguinte repita esse efeito. Liberar um semáforo não faz rollback.
- Não há limite de espera próprio, apenas cancelamento da requisição. Não existe garantia de recuperação de uma operação travada.
- Continuam as simplificações da POC 1: hash dos bytes exatos, buffering integral, apenas respostas 2xx salvas e ausência de isolamento por tenant.

Os números do experimento são verificações locais, não uma infraestrutura de métricas. Observabilidade será uma etapa posterior.

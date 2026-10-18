# POC 2 — Explicação técnica detalhada

[README](../README.md) · [Guia dos testes](../Tests/README.md) · [Fundamentos da POC 1](../../POC-01-Memory/docs/explicacao-tecnica.md)

## 1. O problema que esta versão resolve

A POC 1 faz `consultar → executar → guardar`. Duas chamadas podem consultar uma chave ausente antes que qualquer resultado seja guardado. A POC 2 coordena essa sequência por chave dentro do mesmo processo.

```text
A / chave X → adquire → consulta → executa → guarda → responde → libera
B / chave X → espera ..................................... → adquire
B / chave X → consulta → encontra resposta → replay → libera
C / chave Y → pode executar enquanto A trabalha
```

A garantia depende de o resultado continuar presente no cache. O semáforo não transforma memória em persistência durável.

## 2. Projeto e organização

[Idempotency.Poc02.csproj](../Idempotency.Poc02.csproj) usa `Microsoft.NET.Sdk.Web`, `net10.0`, nulabilidade e imports implícitos. São as mesmas escolhas da POC 1.

`Tests` e `Verification` são projetos separados. O projeto da API exclui seus fontes de `Compile` para não compilar testes e outro ponto de entrada junto com a API. Também exclui seus conteúdos do conjunto de arquivos da aplicação, evitando incorporar artefatos desses projetos à saída web.

## 3. Injeção de dependência

Em [Program.cs](../Program.cs):

```csharp
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<OrderStore>();
builder.Services.AddSingleton<KeyedRequestLock>();
```

O cache guarda os resultados; `OrderStore` guarda pedidos; `KeyedRequestLock` guarda os semáforos. Os três precisam ser compartilhados pelas requisições da mesma instância.

Se cada requisição tivesse seu próprio coordenador, duas chamadas receberiam semáforos diferentes e executariam simultaneamente mesmo com a mesma chave. Singleton resolve esse compartilhamento local, não entre pods.

## 4. O coordenador por chave

Em [KeyedRequestLock.cs](../KeyedRequestLock.cs):

```csharp
private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
```

O dicionário associa a chave interna (`POST:/orders:valor`) a um semáforo. `ConcurrentDictionary` protege as operações sobre a coleção, mas a proteção da execução do endpoint é responsabilidade do semáforo.

```csharp
if (_gates.TryGetValue(key, out var existing))
    return existing;

var candidate = new SemaphoreSlim(1, 1);
var selected = _gates.GetOrAdd(key, candidate);
if (!ReferenceEquals(candidate, selected))
    candidate.Dispose();
return selected;
```

A consulta inicial evita alocar outro semáforo quando ele já existe. Duas threads ainda podem não encontrar a entrada e criar candidatos diferentes. `GetOrAdd` escolhe a instância armazenada e a devolve para ambas. O candidato que perdeu é descartado, porque não foi entregue a nenhum chamador.

`ReferenceEquals` compara a identidade dos objetos. Não estamos comparando o estado ou a quantidade de permissões.

## 5. Por que SemaphoreSlim(1, 1)

O primeiro argumento é a quantidade inicial de permissões; o segundo é o máximo. Uma permissão permite uma execução por vez naquela chave. O máximo também evita aceitar silenciosamente liberações que elevem o contador acima de um.

`WaitAsync` permite aguardar sem manter uma thread bloqueada enquanto a permissão não está disponível. Não usamos `lock`, pois a região contém operações aguardadas com `await`. `async`, por si só, não oferece exclusão mútua.

Chaves diferentes usam semáforos distintos. Não há um bloqueio global de todos os pedidos, nem promessa de ordem FIFO dos participantes.

## 6. Entrada do middleware e validações

[IdempotencyMiddleware.cs](../IdempotencyMiddleware.cs) recebe `RequestDelegate next` no construtor e os serviços em `InvokeAsync`: contexto, cache, coordenador e configuração.

Antes de coordenar, ele mantém as regras da POC 1:

1. Só protege `POST /orders`.
2. Exige uma chave única, não vazia e com até 128 caracteres.
3. Usa `EnableBuffering` para ler o corpo sem consumi-lo definitivamente.
4. Calcula SHA-256 dos bytes e reposiciona o stream para zero.
5. Monta a chave interna com método e caminho.

O hash é calculado antes da espera. Portanto, chamadas repetidas ainda pagam o custo de ler o corpo. Bytes diferentes, mesmo para JSON semanticamente equivalente, continuam causando conflito.

## 7. O modo de comparação

```csharp
var gate = configuration.GetValue("Idempotency:CoordinationEnabled", true)
    ? locks.Get(cacheKey)
    : null;
```

O padrão é proteger. `false` serve exclusivamente para demonstrar o comportamento sem coordenação no laboratório. Não é uma decisão por cliente ou por header. Não altere a configuração durante uma execução do experimento: misturar chamadas protegidas e desprotegidas elimina a garantia.

## 8. Adquirir antes de entrar no try

```csharp
if (gate is not null)
    await gate.WaitAsync(context.RequestAborted);

try
{
    // consulta, execução e gravação
}
finally
{
    gate?.Release();
}
```

O cancelamento pode interromper a espera antes de adquirir uma permissão. Por isso, `WaitAsync` fica fora do `try` cujo `finally` libera o semáforo. Se colocássemos a espera dentro desse bloco sem controlar a aquisição, poderíamos liberar a permissão de outra requisição.

Cancelar quem espera não deve cancelar a execução original. O teste com três participantes valida exatamente isso: uma execução original, uma espera cancelada e outra espera que depois recebe replay.

## 9. Consultar o cache depois de adquirir

Depois de esperar, a requisição precisa olhar o resultado atualizado. Nesta versão, toda a consulta ocorre dentro da região protegida; não existe uma consulta antecipada fora dela.

Se o resultado está presente:

- Hash diferente: `409`, sem executar o endpoint.
- Hash igual: restaura status, tipo de conteúdo, `Location` e corpo; marca replay `true`.

Ambos os caminhos usam `return`, mas o `finally` externo executa e libera a chave. Testamos chamadas posteriores ao conflito e ao replay para verificar que esses retornos não prendem o semáforo.

## 10. Executar e capturar a resposta

Se a chave não tem resultado, o middleware troca temporariamente `Response.Body` por `MemoryStream` e chama `next(context)`. O endpoint cria o pedido, e `Results.Created` serializa a resposta no buffer.

Ao retornar, `ToArray` copia os bytes. Apenas respostas 2xx são guardadas, com hash, status, tipo de conteúdo, `Location` e corpo, por dez minutos desde a gravação. Ler a entrada não renova o prazo.

O buffer permite reproduzir o resultado sem conhecer o tipo `Order`, mas usa memória adicional. A técnica não foi desenhada para streaming ou corpos grandes.

## 11. Dois finally, duas responsabilidades

O `finally` interno restaura o stream original da resposta. O externo libera a permissão da chave. Nenhum deles desfaz um pedido já criado.

O semáforo permanece adquirido enquanto o middleware escreve a resposta no stream original. Isso simplifica o fluxo, mas um cliente lento pode aumentar o tempo de espera das demais chamadas da chave. Não existe timeout próprio da coordenação; temos apenas o cancelamento da requisição.

## 12. Erros e efeitos

Se o endpoint retorna `503`, não guardamos o resultado. Ao liberar a permissão, o próximo participante pode executar. Uma exceção também libera a chave e restaura o stream.

Os testes de retry após falha produzem o erro **antes de criar o pedido**. Se um efeito aconteceu e depois houve uma exceção antes de guardar a resposta, uma repetição pode duplicá-lo. O semáforo resolve a disputa simultânea, não a atomicidade entre efeito e registro.

## 13. O endpoint e o atraso didático

O endpoint recebe `CreateOrder` pelo corpo, `OrderStore` e `IConfiguration` por DI e `CancellationToken` associado à requisição. Antes de criar o pedido, aguarda `Task.Delay` com `Experiment:DelayMilliseconds`, cujo padrão é 500 ms.

Esse atraso torna a concorrência visível no experimento HTTP. Não faz parte da garantia de idempotência. Nos experimentos documentados, use valores não negativos pequenos, como 500 ou 1000 ms; essa versão não possui a validação de intervalo adicionada posteriormente à POC 1.

O armazenamento mantém `ConcurrentDictionary<int, Order>` e IDs gerados por `Interlocked.Increment`. Essas operações individuais continuam seguras, mas não substituem a coordenação da intenção de compra.

## 14. Por que não removemos os semáforos

Imagine A liberando um semáforo enquanto B já espera nele. Se A remover a entrada, C poderá criar outro semáforo para a mesma chave. B e C passarão a executar sob objetos diferentes.

Nesta POC, mantemos os semáforos até o processo encerrar. Isso evita essa remoção insegura, mas a memória cresce com as chaves distintas, mesmo quando suas respostas expiram. Uma solução de produção precisaria de ciclo de vida seguro ou outra estratégia. Não é suficiente chamar `TryRemove` após `Release`.

## 15. Testes e experimento HTTP

A suíte xUnit cobre o contrato e a coordenação. Barreiras controladas mostram um pedido com proteção e vinte sem proteção. Os testes unitários verificam o coordenador isoladamente. Veja o [guia dos testes](../Tests/README.md).

O [Experiment.ps1](../Experiment.ps1) envia chamadas HTTP usando a mesma chave e corpo. Cada rodada gera uma chave nova. Por padrão espera um pedido e N-1 replays; `-Sequential` permite comparação; `-ExpectDuplicates` valida o modo desprotegido. Além de ler as respostas, consulta `GET /orders` e confirma os IDs daquela rodada, sem confundir pedidos de experimentos anteriores.

`Verification` continua como demonstração executável inicial. Os testes xUnit são a referência para regressões. Nenhum desses mecanismos representa observabilidade contínua de produção.

## 16. O limite para a próxima POC

```text
Pod A → seu cache → seu coordenador → seus pedidos
Pod B → seu cache → seu coordenador → seus pedidos
```

Os dois pods podem executar a mesma chave: não compartilham memória nem semáforos. Reiniciar também perde todo esse estado. A POC 3 demonstrará essa fronteira. Banco compartilhado e atomicidade pertencem às etapas seguintes.

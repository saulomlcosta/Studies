# POC 1 — Explicação técnica detalhada

[Voltar ao README](../README.md)

A POC 1 implementa esta ideia: **antes de criar um pedido, verificar se aquela chave já tem um resultado salvo. Se tiver, devolver esse resultado sem executar novamente o endpoint.**

Temos dois armazenamentos separados, ambos na memória do processo:

```text
OrderStore
└─ Guarda os pedidos criados.

IMemoryCache
└─ Guarda a relação entre chave, conteúdo da requisição e resposta.
```

Esta explicação acompanha o caminho da requisição e descreve especificamente a POC 1. A coordenação de concorrência introduzida na POC 2 não faz parte desta versão.

## 1. A estrutura do projeto

O arquivo [Idempotency.Poc01.csproj](../Idempotency.Poc01.csproj) contém:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

| Configuração | Motivo |
| --- | --- |
| `Microsoft.NET.Sdk.Web` | Configura o projeto para uma aplicação ASP.NET Core |
| `net10.0` | Define o framework alvo; o SDK está instalado na máquina utilizada no laboratório |
| `Nullable` | Habilita a análise do compilador sobre referências potencialmente nulas |
| `ImplicitUsings` | Evita repetir imports comuns, como `System` e namespaces do ASP.NET Core |

Não adicionamos dependências externas. Os recursos utilizados estão disponíveis no framework.

Escolhemos **Minimal APIs** para manter o exemplo pequeno: os endpoints são definidos diretamente no `Program.cs`, sem controllers. A técnica de middleware também poderia ser usada com controllers.

## 2. Inicialização e injeção de dependência

No [Program.cs](../Program.cs):

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<OrderStore>();
```

`WebApplication.CreateBuilder(args)` prepara a configuração da aplicação, logging, servidor e contêiner de injeção de dependência.

`AddMemoryCache()` disponibiliza `IMemoryCache` pelo contêiner. Ele oferece operações de leitura, escrita e expiração de entradas na memória do processo e é integrado à injeção de dependência do ASP.NET Core. Consulte a [documentação do cache em memória](https://learn.microsoft.com/aspnet/core/performance/caching/memory?view=aspnetcore-10.0).

`AddSingleton<OrderStore>()` define que o armazenamento de pedidos terá **uma instância compartilhada durante a vida da aplicação**.

```text
Requisição A → cria um pedido no OrderStore
Requisição B → consulta o mesmo OrderStore e encontra o pedido
```

Se criássemos um armazenamento novo por requisição, a lista de pedidos não seria compartilhada.

**Singleton significa uma instância por contêiner da aplicação, não uma instância para todos os pods.** Duas aplicações terão dois `OrderStore` diferentes.

## 3. Construção do pipeline

```csharp
var app = builder.Build();

app.UseMiddleware<IdempotencyMiddleware>();
```

`Build()` constrói a aplicação. `UseMiddleware` adiciona nosso componente ao pipeline HTTP.

Um middleware pode executar código antes e depois dos próximos componentes, ou encerrar o processamento sem chamá-los. Essa capacidade permite devolver uma resposta salva sem chegar ao endpoint. Consulte a [documentação de middleware](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/middleware/).

```text
Requisição
    ↓
IdempotencyMiddleware
    ↓ se permitir continuar
Endpoint POST /orders
    ↓
IdempotencyMiddleware captura o resultado
    ↓
Resposta ao cliente
```

No final do arquivo:

```csharp
app.Run();
```

Essa chamada inicia a aplicação e mantém o servidor atendendo requisições até o encerramento.

## 4. Os modelos de entrada e saída

```csharp
public record CreateOrder(int ProductId, int Quantity);

public record Order(int Id, int ProductId, int Quantity);
```

`CreateOrder` representa o corpo recebido. `Order` representa o pedido criado, agora com um identificador.

Usamos `record` porque são estruturas pequenas voltadas a transportar dados. A sintaxe gera propriedades e construtor, além de outros comportamentos, como igualdade por valor.

Nesta POC, não utilizamos essa igualdade para decidir se uma requisição é repetida. Essa comparação é feita pelo hash do corpo HTTP.

Também não há validações de negócio como:

```text
Quantity deve ser maior que zero.
ProductId precisa corresponder a um produto existente.
```

O ASP.NET Core ainda precisa conseguir interpretar o JSON e convertê-lo para `CreateOrder`. Ausência de validação de negócio não elimina o processamento e a validação de formato do framework.

## 5. O endpoint que cria pedidos

```csharp
app.MapPost("/orders", async (CreateOrder request, OrderStore store, CancellationToken cancellationToken) =>
{
    if (experimentDelay > 0)
        await Task.Delay(experimentDelay, cancellationToken);
    var order = store.Create(request);

    return Results.Created($"/orders/{order.Id}", order);
});
```

O ASP.NET Core resolve os parâmetros:

- `CreateOrder request`: vem do JSON do corpo da requisição.
- `OrderStore store`: vem da injeção de dependência, pois registramos esse tipo.
- `CancellationToken cancellationToken`: corresponde ao cancelamento da requisição e é passado ao atraso didático.

Para reproduzir a corrida de concorrência, acrescentamos um atraso opcional. Na inicialização, `builder.Configuration.GetValue("Experiment:DelayMilliseconds", 0)` lê o valor em milissegundos, que validamos entre 0 e 30000. O padrão zero não introduz espera. Com um valor positivo, `Task.Delay` suspende o fluxo de forma assíncrona antes de criar o pedido, permitindo que outras chamadas também passem pela consulta ao cache. Não bloqueia uma thread com `Thread.Sleep` e não é um mecanismo de idempotência. A lambda tornou-se `async` para aguardar essa operação.

`Results.Created(...)` produz uma resposta com status `201 Created`, header `Location` apontando para o pedido e corpo JSON:

```http
HTTP/1.1 201 Created
Location: /orders/1
Content-Type: application/json
```

```json
{
  "id": 1,
  "productId": 123,
  "quantity": 2
}
```

**O endpoint não sabe que existe idempotência.** Sempre que essa função executa, ela cria um pedido. A decisão de executá-la novamente fica no middleware.

Os dois endpoints `GET` permitem observar o resultado:

```csharp
app.MapGet("/orders", (OrderStore store) => store.All());

app.MapGet("/orders/{id:int}", (int id, OrderStore store) =>
    store.Find(id) is { } order
        ? Results.Ok(order)
        : Results.NotFound());
```

`{id:int}` restringe esse parâmetro de rota a um inteiro. A expressão `is { } order` verifica se o resultado não é nulo e, nesse caso, disponibiliza o valor na variável `order`.

## 6. Como os pedidos ficam armazenados

O `OrderStore` usa:

```csharp
private readonly ConcurrentDictionary<int, Order> _orders = new();
private int _lastId;
```

Escolhemos `ConcurrentDictionary` porque requisições diferentes podem acessar a coleção simultaneamente. Ele oferece operações individuais apropriadas para acesso concorrente.

Para criar um pedido:

```csharp
var order = new Order(
    Interlocked.Increment(ref _lastId),
    request.ProductId,
    request.Quantity);

_orders[order.Id] = order;
```

`Interlocked.Increment` incrementa o contador de forma atômica. Isso evita que duas chamadas concorrentes disputem um incremento comum, como `_lastId++`, e acabem produzindo identificadores incorretos.

**IDs distintos e uma coleção segura para concorrência não impedem duas execuções da mesma compra.** Duas chamadas podem criar os pedidos `1` e `2`, ambos válidos para a coleção, mas duplicados do ponto de vista da intenção do cliente.

## 7. A assinatura do middleware

No [IdempotencyMiddleware.cs](../IdempotencyMiddleware.cs):

```csharp
public sealed class IdempotencyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        IMemoryCache cache)
    {
        // ...
    }
}
```

| Elemento | Papel |
| --- | --- |
| `sealed` | Impede herança; não é uma exigência de idempotência |
| `(RequestDelegate next)` | Construtor primário do C#, recebendo o próximo componente |
| `RequestDelegate` | Delegate que recebe um `HttpContext` e retorna uma `Task` |
| `InvokeAsync` | Método reconhecido pelo mecanismo de middleware convencional |
| `HttpContext` | Representa a requisição atual e sua resposta |
| `IMemoryCache` | Serviço resolvido por injeção de dependência |

Não precisamos implementar uma interface: essa versão usa a convenção de middleware do ASP.NET Core.

O método é assíncrono porque lê e escreve streams e aguarda os próximos componentes. **`async` não cria exclusão mútua nem impede duas requisições de executarem ao mesmo tempo.**

## 8. Selecionar qual operação será protegida

```csharp
if (!HttpMethods.IsPost(context.Request.Method)
    || context.Request.Path != "/orders")
{
    await next(context);
    return;
}
```

Nosso middleware só aplica a regra ao `POST /orders`.

Para as demais requisições, ele chama `next(context)` e deixa o pipeline continuar normalmente.

Essa seleção explícita mantém o escopo da POC pequeno. Em uma solução maior, poderíamos marcar endpoints com metadados, evitando codificar cada caminho dentro do middleware.

## 9. Exigir uma chave válida

```csharp
var keys = context.Request.Headers["Idempotency-Key"];

if (keys.Count != 1
    || string.IsNullOrWhiteSpace(keys[0])
    || keys[0]!.Length > 128)
{
    await Results.BadRequest(new
    {
        error = "Informe uma única Idempotency-Key de até 128 caracteres."
    }).ExecuteAsync(context);

    return;
}
```

Headers podem conter múltiplos valores, por isso não tratamos o acesso diretamente como uma única string.

Verificamos:

- Existe exatamente um valor.
- O valor não está vazio nem contém apenas espaços.
- O tamanho não ultrapassa 128 caracteres.

O limite de 128 foi uma escolha didática, não uma regra universal do header.

O operador `!` em `keys[0]!` apenas orienta a análise de nulabilidade do compilador. Ele não valida nada em tempo de execução. A validação anterior já rejeita nulo ou vazio.

`ExecuteAsync(context)` escreve o resultado HTTP no contexto. Depois, o `return` encerra o middleware.

**Como não chamamos `next`, nenhum pedido é criado.**

## 10. Ler o corpo sem impedir que o endpoint também o leia

```csharp
context.Request.EnableBuffering();

var hash = Convert.ToHexString(
    await SHA256.HashDataAsync(
        context.Request.Body,
        context.RequestAborted));

context.Request.Body.Position = 0;
```

`Request.Body` é um stream. Ler um stream avança sua posição.

O middleware precisa ler o corpo para calcular o hash, mas o ASP.NET Core ainda precisará lê-lo para montar `CreateOrder`.

`EnableBuffering()` permite múltiplas leituras. O buffering pode usar memória e, acima de um limiar, arquivo temporário. Consulte a [documentação de EnableBuffering](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.http.httprequestrewindextensions.enablebuffering).

Após calcular o hash, `context.Request.Body.Position = 0` volta ao início para que o próximo leitor encontre o corpo completo.

`context.RequestAborted` é um token de cancelamento associado à requisição. Ele permite cancelar a operação de leitura quando a requisição é abortada. Não representa rollback de efeitos já realizados.

## 11. Por que calcular SHA-256

Queremos distinguir:

```text
Chave X + corpo A → primeira execução
Chave X + corpo A → repetição
Chave X + corpo B → uso inconsistente da chave
```

O SHA-256 produz uma representação compacta dos bytes recebidos. `Convert.ToHexString` transforma o resultado em uma string hexadecimal para comparação e armazenamento.

**A chave identifica a intenção; o hash verifica se o conteúdo permaneceu igual.** O hash não substitui a chave e não é criptografia do corpo.

Nesta implementação, a comparação considera os bytes exatos. Estes JSONs têm o mesmo significado, mas hashes diferentes:

```json
{"productId":123,"quantity":2}
```

```json
{ "quantity": 2, "productId": 123 }
```

Aceitamos essa limitação para não introduzir normalização de JSON na primeira POC.

## 12. Construir a chave interna do cache

```csharp
var cacheKey = $"POST:/orders:{keys[0]}";
```

Se o cliente enviar `compra-abc`, o registro fica associado a:

```text
POST:/orders:compra-abc
```

O prefixo vincula a chave à operação. Assim, uma futura operação diferente poderia utilizar o mesmo valor enviado pelo cliente sem necessariamente compartilhar o registro.

Ainda não incluímos usuário ou tenant. Portanto, essa versão assume o contexto simplificado descrito no README.

## 13. Consultar o resultado e detectar conflito

```csharp
if (cache.TryGetValue<CachedResponse>(
        cacheKey, out var saved)
    && saved is not null)
{
    if (saved.RequestHash != hash)
    {
        await Results.Conflict(new
        {
            error = "Esta chave já foi utilizada com outro corpo."
        }).ExecuteAsync(context);

        return;
    }

    // Reproduzir resposta...
}
```

`TryGetValue` informa se encontrou a entrada e entrega o valor em `saved`.

Se a chave existe, mas o hash é diferente, retornamos `409 Conflict`. Isso evita devolver silenciosamente o resultado de outra requisição.

Esse conflito é uma decisão da nossa aplicação. Nesta POC, não existe banco ou constraint produzindo o erro.

## 14. Devolver uma resposta já registrada

Se o hash coincide:

```csharp
context.Response.StatusCode = saved.StatusCode;
context.Response.ContentType = saved.ContentType;

if (saved.Location is not null)
    context.Response.Headers.Location = saved.Location;

context.Response.Headers["Idempotency-Replayed"] = "true";

await context.Response.Body.WriteAsync(
    saved.Body,
    context.RequestAborted);

return;
```

Reproduzimos status HTTP, tipo do conteúdo, header `Location` e bytes do corpo.

O header `Idempotency-Replayed` é um recurso didático nosso para enxergar se a resposta foi reaproveitada.

A repetição também retorna `201`, porque optamos por reproduzir o resultado original. Isso não significa que um segundo pedido foi criado.

**O ponto que evita a segunda execução é o `return` sem chamar `next(context)`.**

## 15. Capturar a resposta quando a chave é nova

Se não existe resultado salvo, precisamos executar o endpoint e observar o que ele respondeu:

```csharp
var originalBody = context.Response.Body;

await using var buffer = new MemoryStream();

context.Response.Body = buffer;
```

Normalmente, o endpoint escreve sua resposta no stream fornecido pelo servidor. Temporariamente, substituímos esse destino por um `MemoryStream`:

```text
Sem captura:
Endpoint → stream da resposta → cliente

Com captura:
Endpoint → MemoryStream
               ↓
          middleware lê
               ↓
          stream original → cliente
```

`await using` garante o descarte do buffer ao sair do escopo, inclusive em caminhos de exceção.

Essa técnica permite guardar o JSON já serializado, sem o middleware precisar conhecer o tipo `Order`.

## 16. Executar o restante do pipeline e salvar o resultado

Dentro do `try`:

```csharp
await next(context);

var bytes = buffer.ToArray();
```

Durante o `await next(context)`:

1. O ASP.NET Core interpreta o corpo.
2. O endpoint chama `store.Create`.
3. O pedido é inserido na coleção.
4. O resultado `Created` escreve o JSON no buffer.

Ao retornar, o middleware copia o conteúdo com `ToArray()`.

```csharp
if (context.Response.StatusCode is >= 200 and < 300)
{
    cache.Set(
        cacheKey,
        new CachedResponse(
            hash,
            context.Response.StatusCode,
            context.Response.ContentType,
            context.Response.Headers.Location.FirstOrDefault(),
            bytes),
        TimeSpan.FromMinutes(10));
}
```

O padrão `is >= 200 and < 300` seleciona respostas de sucesso.

Guardamos somente esses resultados para simplificar a política inicial. Uma resposta de erro não gera uma entrada de sucesso no cache.

Os dez minutos são uma **expiração relativa ao momento de gravação**. Consultar a entrada não renova esse prazo nesta configuração.

Quando a chave expirar, a mesma chamada poderá executar de novo. Portanto, o comportamento tem uma janela de retenção.

## 17. O que exatamente fica no cache

```csharp
private sealed record CachedResponse(
    string RequestHash,
    int StatusCode,
    string? ContentType,
    string? Location,
    byte[] Body);
```

| Campo | Por que existe |
| --- | --- |
| `RequestHash` | Detectar a mesma chave com outro conteúdo |
| `StatusCode` | Reproduzir o status original |
| `ContentType` | Informar como interpretar o corpo |
| `Location` | Preservar o endereço do pedido criado |
| `Body` | Reproduzir os bytes da resposta |

Não estamos guardando todos os headers. Selecionamos os necessários para esse endpoint.

Também não guardamos um `HttpContext`: ele pertence à requisição e não deve ser reutilizado como resultado de outra chamada.

## 18. Enviar a primeira resposta e restaurar o stream

Depois de salvar o resultado:

```csharp
context.Response.Headers["Idempotency-Replayed"] = "false";

await originalBody.WriteAsync(
    bytes,
    context.RequestAborted);
```

Escrevemos no stream original para que o cliente receba a resposta capturada.

```csharp
finally
{
    context.Response.Body = originalBody;
}
```

O `finally` restaura o stream mesmo se ocorrer uma exceção. Isso evita deixar o contexto apontando para um buffer que será descartado.

**Esse `finally` não desfaz o pedido criado.** Ele apenas restaura uma propriedade do contexto HTTP.

Há também um custo de memória: `MemoryStream` mantém a resposta em memória, e `ToArray()` cria uma cópia. Isso é aceitável para os pequenos resultados da POC, mas precisa ser revisto para respostas grandes.

## 19. A limitação que prepara a POC 2

A sequência atual é:

```text
Consultar cache → executar endpoint → salvar resultado
```

Cada chamada ao cache pode ser segura individualmente, mas a sequência inteira não é atômica.

Duas requisições podem se intercalar assim:

```text
A: consulta chave X → não encontrou
B: consulta chave X → não encontrou
A: cria pedido 1
B: cria pedido 2
A: salva resposta da chave X
B: salva resposta da chave X
```

O cache termina com uma resposta associada à chave, mas os dois pedidos já foram criados.

**`IMemoryCache`, `ConcurrentDictionary` e `Interlocked` não garantem, juntos, a idempotência da operação.** Cada um resolve uma responsabilidade específica; nenhum deles está coordenando toda essa sequência.

A [POC 2](../../POC-02-Concurrency/README.md) atua justamente nesse intervalo: controla quem pode passar da consulta para a execução quando várias requisições usam a mesma chave dentro de uma instância.

Para observar a falha nesta POC, use [Experiment.ps1](../Experiment.ps1) com o atraso habilitado, conforme o [README](../README.md#reproduzir-duplicidade-com-chamadas-concorrentes). O script também oferece `-Sequential` para contrastar o comportamento. A quantidade exata de duplicações varia: o atraso amplia a janela de concorrência, mas não constitui uma barreira determinística.

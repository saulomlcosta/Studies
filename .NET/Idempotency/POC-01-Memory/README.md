# POC 1 — Idempotência em memória

## Propósito

Entender a participação do cliente e o papel de um middleware: uma requisição com chave inédita cria um pedido; uma repetição sequencial com a mesma chave e os mesmos bytes de corpo devolve a resposta registrada, sem executar novamente o endpoint.

## Executar

Para estudar a implementação, consulte a [explicação técnica detalhada](./docs/explicacao-tecnica.md), com o fluxo da requisição, os recursos do .NET e o motivo de cada escolha.

Requisito: SDK .NET 10. No PowerShell:

```powershell
# A partir da raiz do repositório Studies
cd ./.NET/Idempotency/POC-01-Memory
dotnet run --urls http://localhost:5101
```

Abra `requests.http` em um cliente compatível e execute os blocos em ordem. Alternativamente:

```powershell
$params = @{
    Uri = 'http://localhost:5101/orders'
    Method = 'Post'
    ContentType = 'application/json'
    Headers = @{ 'Idempotency-Key' = 'compra-abc' }
    Body = '{"productId":123,"quantity":2}'
}
Invoke-WebRequest @params
Invoke-WebRequest @params
Invoke-RestMethod http://localhost:5101/orders
```

As duas chamadas retornam `201`, o mesmo corpo e o mesmo `Location`. O header didático `Idempotency-Replayed` muda de `false` para `true`. A lista contém apenas um pedido. Reinicie a aplicação antes de recomeçar um experimento do zero.

## Reproduzir duplicidade com chamadas concorrentes

O endpoint original termina muito rápido. Disparar chamadas juntas não garante que elas atravessem a consulta ao cache antes de a primeira salvar o resultado. Para tornar a corrida observável, há um atraso opcional **depois da consulta ao cache e antes de criar o pedido**. O padrão é zero, preservando a execução normal.

Pare a instância anterior e inicie nesta pasta:

```powershell
dotnet run --urls http://localhost:5101 --Experiment:DelayMilliseconds=2000
```

Em outro terminal, na mesma pasta, use Windows PowerShell 5.1 ou PowerShell 7:

```powershell
.\Experiment.ps1
```

O script envia vinte chamadas com a mesma chave e corpo sem esperar a anterior terminar. Ele mostra os IDs retornados, quantos existem em `GET /orders` e quantas respostas foram replays. Esperamos **mais de um pedido**; a quantidade exata depende do agendamento. Se a corrida não aparecer, o script informa a falha do experimento em vez de afirmar que houve duplicação. O atraso aceita valores entre 0 e 30000 ms.

Compare com chamadas sequenciais, mantendo a mesma API e o mesmo atraso:

```powershell
.\Experiment.ps1 -Sequential
```

Nesse caso esperamos um pedido e dezenove replays. Cada execução do script gera uma chave nova, evitando interferência do cache de execuções anteriores. Os números de pedidos se referem aos IDs daquela execução, não ao total acumulado no servidor.

O atraso usa `Task.Delay` para simular uma operação assíncrona demorada. Ele não adiciona nem remove proteção: evidencia a janela que já existe entre consultar a chave e salvar a resposta. Para voltar ao comportamento normal, reinicie sem a opção de atraso.

## Testes automatizados

Execute na pasta desta POC, sem precisar iniciar a API:

```powershell
dotnet test .\Tests\Idempotency.Poc01.Tests.csproj -c Release
```

Para executar somente a demonstração de concorrência:

```powershell
dotnet test .\Tests\Idempotency.Poc01.Tests.csproj -c Release --filter "Category=Concurrency"
```

A suíte usa xUnit e componentes reais em memória. O teste de concorrência **passa ao demonstrar a duplicação conhecida**, usando barreiras em vez de atrasos. Consulte o [guia dos testes](./Tests/README.md) para cenários, funcionamento e limites.

## Arquivos

- `Program.cs`: endpoints e armazenamento de pedidos em memória.
- `IdempotencyMiddleware.cs`: exige a chave, compara o hash do corpo e armazena respostas de sucesso em `IMemoryCache` por dez minutos.
- `requests.http`: repetição, conflito, nova intenção, chave ausente e consulta dos pedidos.
- `Experiment.ps1`: comparação entre chamadas concorrentes e sequenciais, validando os IDs no armazenamento de pedidos.
- `Tests/`: projeto xUnit com testes de comportamento e caracterização da concorrência.

O armazenamento de pedidos usa uma coleção segura para concorrência, mas isso não torna a sequência de consulta/execução/cache atômica.

## Resultados esperados

| Experimento | Resultado |
| --- | --- |
| Chave nova | `201`, pedido criado, replay `false` |
| Repetir chave e corpo | `201`, mesmo pedido, replay `true` |
| Mesma chave, corpo diferente | `409`, nenhum pedido adicional |
| Outra chave, mesmo corpo | `201`, outro pedido |
| Sem chave | `400`, nenhum pedido criado |

## Limitações intencionais

- Apenas chamadas sequenciais são o alvo desta etapa. Duas chamadas simultâneas podem passar pela consulta antes do resultado ser salvo e ambas criar pedidos.
- Memória é local: reiniciar apaga pedidos e chaves; outra instância tem seus próprios dados.
- Após dez minutos, a chave expira e pode criar outro pedido. O cache também pode perder entradas; ele não é um registro durável.
- Hash considera bytes exatos: mudar espaços ou ordem das propriedades JSON causa conflito, mesmo com significado equivalente.
- Somente respostas 2xx são guardadas. Isso não é uma política suficiente para falhas com efeitos já produzidos.
- Não há validação de negócio, autenticação, isolamento por tenant nem integração externa. O parsing JSON continua sendo feito pelo ASP.NET Core.
- O buffering integral é adequado apenas aos pequenos corpos deste laboratório. Não generalizar para uploads, streaming ou respostas grandes.
- A captura de resposta não é uma transação com a operação. Com um banco persistente, uma queda entre salvar o pedido e guardar a resposta seria uma janela de duplicação.

## Próxima pergunta

O que acontece se duas chamadas com a mesma chave chegarem ao mesmo tempo? Na POC 2 vamos reproduzir essa corrida de forma controlada e coordenar o acesso por chave dentro de uma instância.

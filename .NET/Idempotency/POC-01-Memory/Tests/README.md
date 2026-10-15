# Testes da POC 1

Testes xUnit de **integração entre componentes**: middleware, `IMemoryCache`, `OrderStore` e resultados HTTP reais, usando `DefaultHttpContext` e streams em memória. Não iniciam Kestrel nem dependem de uma API em execução. O delegate que representa o endpoint é controlado pelos testes; roteamento, binding HTTP e configuração de `Program.cs` não são exercitados aqui. Para esses aspectos, mantenha o experimento HTTP manual.

## Executar

Na pasta `POC-01-Memory`, no Windows PowerShell ou PowerShell 7:

```powershell
dotnet test .\Tests\Idempotency.Poc01.Tests.csproj -c Release
```

O SDK .NET 10 é necessário. Na primeira execução, o restore pode precisar de acesso ao NuGet para obter xUnit, o adaptador e o SDK de testes. `Release` permite executar enquanto a API está rodando normalmente em `Debug`, evitando disputa pelos mesmos arquivos de saída.

Somente o cenário de concorrência:

```powershell
dotnet test .\Tests\Idempotency.Poc01.Tests.csproj -c Release --filter "Category=Concurrency" --logger "console;verbosity=detailed"
```

## Cenários

| Cenário | Resultado esperado |
| --- | --- |
| Nova chave | Um pedido, `201`, corpo e `Location` corretos |
| Mesma chave e corpo, sequencial | Uma execução, mesma resposta e replay `true` |
| Mesma chave, outro corpo | `409`, sem novo pedido |
| JSON semanticamente igual, bytes diferentes | `409`, documentando a comparação por bytes |
| Outra chave, mesmo corpo | Dois pedidos legítimos |
| Chave ausente, vazia, espaços, longa ou múltipla | `400`, sem execução |
| Outra rota ou método | Continua sem exigir chave |
| Resposta `503` | Não guarda resultado; próxima tentativa executa |
| Exceção | Propaga erro, restaura stream e permite tentativa posterior |
| Vinte chamadas concorrentes da mesma chave | Vinte pedidos, demonstrando a limitação intencional |

Os testes criam seu próprio cache, armazenamento e contextos. Não compartilham pedidos nem dependem da ordem de execução. As entradas de `Theory` aparecem como casos separados no resultado do runner.

## Por que o teste de duplicação deve passar?

`MesmaChaveConcorrente_DemonstraDuplicacaoConhecidaDaPoc01` é um teste de caracterização: comprova o comportamento atual, incluindo sua falha conhecida. Não afirma que duplicar é desejável em produção.

```text
1. Inicia vinte chamadas com a mesma chave e corpo.
2. Cada chamada consulta o cache vazio e entra no delegate do endpoint.
3. Um TaskCompletionSource sinaliza quando todas entraram.
4. Outro TaskCompletionSource mantém os endpoints esperando.
5. O teste confirma que ainda não há pedidos e libera os endpoints.
6. Todas criam pedidos; o teste verifica vinte IDs distintos e zero replays.
7. Um retry posterior encontra um resultado no cache, sem criar outro pedido.
```

Não utilizamos `Task.Delay` ou `Thread.Sleep` para provocar a corrida. O timeout de dez segundos apenas impede que uma regressão deixe o teste travado. O `finally` libera a barreira mesmo se uma asserção falhar.

Não presumimos qual resposta fica no cache após a corrida, porque a ordem das gravações é variável. Verificamos que o replay corresponde a um dos pedidos criados.

Quando a proteção for adicionada em outra POC, a expectativa muda para **um pedido e dezenove replays**, com um teste adequado à coordenação daquela versão. Este teste permanece na POC 1 para preservar a demonstração do problema.

## Limites da suíte

Não testa pods, banco, durabilidade, expiração temporal ou desempenho sob carga. Também não comprova que toda falha permite retry seguro: o teste de erro falha antes de criar o pedido. Falhas após produzir efeitos continuam sendo uma limitação da POC 1.

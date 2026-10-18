# Testes da POC 2

Projeto xUnit com testes unitários do coordenador e testes de integração entre middleware, cache, coordenador e armazenamento reais em memória. Cada caso cria seus próprios componentes. Não precisa iniciar a API.

## Executar

Na pasta `POC-02-Concurrency`, usando SDK .NET 10:

```powershell
dotnet test ./Tests/Idempotency.Poc02.Tests.csproj -c Release
dotnet test ./Tests/Idempotency.Poc02.Tests.csproj -c Release --filter "Category=Concurrency"
dotnet test ./Tests/Idempotency.Poc02.Tests.csproj -c Release --filter "Category=Unit"
```

Os comandos funcionam no Windows PowerShell 5.1 e no PowerShell 7. O primeiro restore pode acessar o NuGet. `Release` evita disputar arquivos com uma API aberta normalmente em `Debug`.

## Organização e cobertura

| Arquivo | Tipo | Cenários |
| --- | --- | --- |
| `IdempotencyMiddlewareTests.cs` | Integração entre componentes | Criação, leitura do corpo após hash, replay sequencial, headers, conflito de bytes, nova chave, validações, outras rotas, erro e exceção |
| `ConcurrencyTests.cs` | Integração entre componentes | Vinte chamadas com/sem proteção; chaves independentes; conflito após espera; cancelamento de um participante; erro ou exceção liberando outro participante |
| `KeyedRequestLockTests.cs` | Unitário | Mesma instância por chave com acesso paralelo; uma permissão; independência entre chaves; cancelamento sem liberação indevida |

As duas partes de `IdempotencyMiddlewareTests` usam `partial` para compartilhar helpers sem repetir a montagem de cache, contextos e armazenamento. O endpoint é um delegate controlado pelos testes, que lê o JSON e usa `OrderStore` e `Results.Created` reais. Não executamos o roteamento, o binding automático nem a configuração de `Program.cs` nessa suíte.

## Como testar concorrência sem depender de atrasos

Usamos `TaskCompletionSource` com `RunContinuationsAsynchronously` para sinalizar a entrada no endpoint e liberar sua conclusão. Não usamos `Task.Delay` nem `Thread.Sleep` nos testes.

No teste com vinte chamadas, os corpos são `MemoryStream`: cada chamada avança até o endpoint ou até a espera no semáforo. A primeira execução fica retida numa barreira. Antes de liberá-la, verificamos que somente uma chamada entrou quando a proteção está ativa; sem proteção, todas entraram. Depois verificamos pedidos, IDs, status, headers e replays.

Nos demais testes, sinais explícitos indicam quando a execução original entrou. Iniciamos os participantes seguintes enquanto ela ainda está retida, cancelamos uma espera ou preparamos uma falha e só então liberamos a conclusão. `finally` libera as barreiras, e timeouts de dez segundos evitam travamentos nos cenários coordenados. Timeouts não são evidência de sucesso.

## Expectativas principais

- Proteção ativa: vinte chamadas da mesma chave produzem um pedido e dezenove replays.
- Proteção desativada: vinte pedidos na corrida controlada. Esse caso caracteriza o problema, portanto deve passar ao detectar duplicação.
- Chaves diferentes: ambas entram no endpoint antes de qualquer uma terminar.
- Corpo diferente: aguarda a execução original e recebe `409`; um retry válido posterior continua funcionando.
- Cancelamento de uma espera: não libera a permissão da execução original nem impede o replay de outro participante.
- Erro `503` ou exceção antes do efeito: libera a chave e permite que quem estava esperando execute e crie o pedido.

## Limites

São testes de uma instância em memória, não testes de carga, durabilidade, pods ou banco. Falha antes do efeito não comprova segurança para falha depois de criar o pedido. Expiração e indisponibilidade de armazenamento também não estão cobertas.

O executável `Verification` foi preservado como demonstração didática inicial. A suíte xUnit é a referência para regressão: cenários isolados, filtros e relatório individual. O script HTTP complementa ambos ao exercitar a aplicação pelo servidor.

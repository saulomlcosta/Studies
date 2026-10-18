# POC 3 — Múltiplas instâncias

## Propósito

Demonstrar que cache e semáforo locais não coordenam processos diferentes. Preservamos a proteção da POC 2: cada instância cria um pedido por chave enquanto o resultado está no cache. Ao enviar a mesma operação a duas instâncias, cada uma pode executá-la.

Essa duplicação pode ocorrer mesmo em chamadas sequenciais: primeiro A, depois B. Não depende de uma corrida dentro de um processo.

```text
                   ┌─ Instância A → cache A → semáforo A → pedidos A
Mesma chave/corpo ──┤
                   └─ Instância B → cache B → semáforo B → pedidos B
```

Esta etapa evidencia o limite. Não adiciona Redis, banco compartilhado, Kubernetes ou balanceador. O script alterna os destinos explicitamente; dois processos locais reproduzem a separação de memória relevante para o experimento.

## Executar duas instâncias

Requisitos: SDK .NET 10 e Windows PowerShell 5.1 ou PowerShell 7. A partir da raiz de `Studies`, entre na pasta e compile uma vez:

```powershell
cd ./.NET/Idempotency/POC-03-MultipleInstances
dotnet build ./Idempotency.Poc03.csproj -c Release
```

No terminal A, nessa pasta:

```powershell
dotnet ./bin/Release/net10.0/Idempotency.Poc03.dll --urls http://localhost:5103 --Instance:Id=A
```

No terminal B, na mesma pasta:

```powershell
dotnet ./bin/Release/net10.0/Idempotency.Poc03.dll --urls http://localhost:5104 --Instance:Id=B
```

Usar a DLL já compilada evita duas compilações simultâneas sobre os mesmos arquivos. A coordenação local vem habilitada por padrão; não a desative nesta POC. O atraso didático padrão é 500 ms e pode ser alterado com `--Experiment:DelayMilliseconds=1000` (intervalo permitido: 0 a 30000).

## Experimento 1 — Uma instância

Em um terceiro terminal, na pasta da POC:

```powershell
.\Experiment.ps1 -SingleInstance
```

Esperado: vinte chamadas para A, um pedido e dezenove replays. B não recebe chamadas nessa rodada.

## Experimento 2 — Distribuir a mesma chave entre A e B

```powershell
.\Experiment.ps1
```

Esperado para vinte chamadas:

| Instância | Chamadas | Pedidos da rodada | Replays |
| --- | --- | --- | --- |
| A | 10 | 1 | 9 |
| B | 10 | 1 | 9 |
| Total | 20 | 2 | 18 |

O script gera uma chave nova por rodada, mantém o corpo igual e envia as chamadas antes de aguardar os resultados. Consulta os pedidos de cada instância, confirma os IDs daquela rodada e valida os headers de identificação. Rejeita dois destinos que apontem para a mesma inicialização ou usem o mesmo nome de instância.

Não soma apenas IDs numéricos: A e B podem gerar `1`. A identidade usada é `RunId + OrderId`. A contagem não inclui pedidos criados por rodadas anteriores.

Destinos e quantidade podem ser personalizados:

```powershell
.\Experiment.ps1 -UrlA http://localhost:5103 -UrlB http://localhost:5104 -Requests 40
```

## Experimento 3 — Repetição sequencial em destinos diferentes

Execute os blocos de [requests.http](./requests.http) em ordem com um cliente compatível. A primeira chamada em A cria; a segunda em A reaproveita; a primeira em B cria novamente, mesmo usando a chave original de A.

Alternativamente, no PowerShell:

```powershell
$key = [Guid]::NewGuid().ToString()
$request = @{
    Method = 'Post'
    ContentType = 'application/json'
    Headers = @{ 'Idempotency-Key' = $key }
    Body = '{"productId":123,"quantity":2}'
    UseBasicParsing = $true
}
Invoke-WebRequest -Uri http://localhost:5103/orders @request
Invoke-WebRequest -Uri http://localhost:5103/orders @request
Invoke-WebRequest -Uri http://localhost:5104/orders @request
Invoke-WebRequest -Uri http://localhost:5104/orders @request
```

Observe `Idempotency-Replayed`, `X-Instance-Id` e `X-Instance-Run-Id`.

## Experimento 4 — Reiniciar somente A

Após o experimento 3, mantenha o terminal do cliente aberto para preservar `$key` e `$request`.

1. Consulte `GET /instance` e `GET /orders` em A e B.
2. Pare somente A com Ctrl+C e execute novamente o mesmo comando do terminal A.
3. Consulte A: o nome continua `A`, mas `runId` mudou e os pedidos estão vazios.
4. Repita os comandos abaixo antes de expirar o cache de B (dez minutos):

```powershell
Invoke-WebRequest -Uri http://localhost:5103/orders @request
Invoke-WebRequest -Uri http://localhost:5104/orders @request
```

A executa novamente (`replay=false`), e B ainda reaproveita (`replay=true`). O ID local de A pode voltar a `1`; a mudança de `RunId` distingue a nova execução. Como os pedidos são voláteis, o pedido anterior de A não sobrevive ao reinício. O experimento demonstra perda da memória da operação e reexecução, não dois registros persistidos após reiniciar.

## Testes automatizados

```powershell
dotnet test ./Tests/Idempotency.Poc03.Tests.csproj -c Release
dotnet test ./Tests/Idempotency.Poc03.Tests.csproj -c Release --filter "Category=Concurrency"
```

Não precisam de APIs abertas. Se suas instâncias manuais estiverem usando os binários `Release`, pare-as antes de recompilar, ou execute os testes em `Debug`. Consulte o [guia dos testes](./Tests/README.md).

## Arquivos e estudo técnico

- `Program.cs`: inicialização do host.
- `ApplicationSetup.cs`: serviços, identificação, middleware e endpoints compartilhados pela aplicação e pelos testes.
- `OrderStore.cs`: pedidos em memória e IDs locais.
- `IdempotencyMiddleware.cs` e `KeyedRequestLock.cs`: coordenação local herdada da POC 2.
- `Experiment.ps1` e `requests.http`: experimentos HTTP.
- `Tests/`: integração com dois hosts independentes.
- [docs/explicacao-tecnica.md](./docs/explicacao-tecnica.md): decisões e funcionamento.

## Limitações e próximo passo

O cache retém apenas sucessos por dez minutos, compara bytes exatos e não é durável. Os semáforos crescem com chaves distintas e são locais. Não há autenticação, isolamento por tenant, transação, recuperação de efeitos externos ou métricas contínuas.

Sticky sessions poderiam encaminhar retries ao mesmo processo em alguns cenários, mas não preservariam o estado após reinício nem forneceriam uma garantia compartilhada. A POC 4 introduzirá banco compartilhado, unicidade e transação para coordenar o registro da chave com a criação do pedido.

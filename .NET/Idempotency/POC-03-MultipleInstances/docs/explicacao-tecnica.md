# POC 3 — Explicação técnica

[README](../README.md) · [Testes](../Tests/README.md)

## 1. O que foi preservado

O middleware e o coordenador são cópias independentes da POC 2, com namespace `Idempotency.Poc03`. Não mudamos as versões anteriores. Mantemos chave obrigatória, hash SHA-256 do corpo, cache de respostas 2xx, expiração em dez minutos e `SemaphoreSlim` por chave.

`ConcurrentDictionary` e `Interlocked` continuam protegendo operações individuais do armazenamento. A proteção do fluxo de idempotência continua dependendo do semáforo e do cache daquele processo.

## 2. Serviços singleton são locais

Cada execução da aplicação cria seu próprio contêiner:

```text
Processo A: MemoryCache A + KeyedRequestLock A + OrderStore A
Processo B: MemoryCache B + KeyedRequestLock B + OrderStore B
```

`AddSingleton` não cria um objeto distribuído. Mesmo com código e chave iguais, A e B fazem consultas a objetos diferentes. Ambas podem encontrar a chave inédita e criar um pedido. Isso pode acontecer até sem concorrência: A conclui antes de B receber a chamada.

## 3. Separação da inicialização e do pipeline

`Program.cs` cria o builder, chama `ApplicationSetup.AddServices`, constrói a aplicação, configura o pipeline e inicia o servidor.

Essa separação permite que os testes usem exatamente os mesmos registros de serviços e endpoints com `TestServer`, enquanto a execução normal utiliza Kestrel. Não duplicamos o comportamento de negócio em um endpoint artificial nos testes desta POC.

## 4. Identificação da instância e da inicialização

`InstanceInfo` é um record singleton com dois campos:

- `Id`: nome fornecido por `Instance:Id`, como `A` ou `B`.
- `RunId`: GUID gerado a cada construção do host, independente do nome escolhido.

Os headers são `X-Instance-Id` e `X-Instance-Run-Id`. O endpoint `GET /instance` retorna esses mesmos valores em JSON.

Validamos o nome com uma expressão regular: entre 1 e 64 letras ASCII, números, hífen ou underscore. O valor é usado em headers, e restringir o formato também torna os experimentos previsíveis.

O middleware de identificação vem antes do middleware de idempotência. Assim, ele roda mesmo quando há replay ou rejeição da chave. Os headers de identificação não precisam ser guardados no cache: descrevem a instância que está atendendo a chamada atual.

## 5. Por que apenas o ID do pedido é insuficiente

O contador de pedidos começa em zero em cada `OrderStore`. A e B podem criar o pedido `1`. Contar apenas valores distintos de `Order.Id` produziria a falsa conclusão de que houve uma única execução.

O script agrupa por inicialização e ID local. Após um reinício, A pode novamente criar `1`; seu novo `RunId` permite distinguir essa execução das anteriores. Esses identificadores são ferramentas do laboratório, não uma solução para idempotência ou geração distribuída de IDs.

## 6. Atraso controlado

`ExperimentOptions` guarda o atraso validado no startup, de 0 a 30000 ms, com padrão 500 ms. `Task.Delay` aceita o token de cancelamento da requisição e simula uma operação assíncrona mais demorada.

O atraso facilita observar a proteção local. Não é necessário para duplicar entre instâncias: a ausência de estado compartilhado já é suficiente. Nos testes de integração utilizamos zero.

## 7. Script distribuído

O script consulta `/instance` de cada destino antes de enviar pedidos. Verifica se são inicializações e nomes distintos, evitando um experimento acidental contra o mesmo processo.

Gera uma chave por rodada e alterna os destinos com o índice da chamada. Todas as chamadas são enviadas antes de aguardar suas respostas. Depois valida os headers, conta replays e consulta `/orders` separadamente em cada destino.

No modo padrão, cada instância deve criar um pedido. O total de dois é o resultado esperado da falha de coordenação global. No modo `-SingleInstance`, todas as chamadas vão para A e o total esperado é um.

Não há balanceador real no script. A distribuição explícita torna o resultado previsível e mostra diretamente onde cada chamada foi executada.

## 8. Reinício e perda de estado

Reiniciar destrói cache, pedidos e semáforos. Reenviar uma chave anterior causa uma nova execução, porque a aplicação não possui mais o registro da operação.

Como os pedidos também estão em memória, a tabela local anterior desaparece. Portanto, esse experimento comprova reexecução, não persistência de duas linhas após o reinício. Quando pedidos forem duráveis e o cache continuar volátil, essa separação poderá resultar em duplicação persistida — assunto das próximas etapas.

## 9. Integração HTTP em memória

`TestServer` processa requisições de `HttpClient` pelo pipeline real sem abrir portas. Dois hosts criados no mesmo teste têm serviços distintos. O teste pode descartar somente A, reconstruí-lo e confirmar que B ainda reaproveita o resultado.

Isso verifica roteamento, binding, status, headers e isolamento entre contêineres. O experimento com duas execuções da DLL cobre a fronteira entre processos reais. Nenhum dos dois equivale a testes de Kubernetes, rede ou falhas abruptas.

## 10. O próximo problema a resolver

Enviar retries sempre para o mesmo pod pode ocultar a limitação durante um período, mas não protege contra perda daquela instância. Um armazenamento compartilhado permitirá reconhecer a chave em qualquer processo.

Ainda será necessário tornar a reserva atômica e coordenar o registro da chave com o efeito de criar o pedido. Apenas substituir `IMemoryCache` por consultas a um armazenamento remoto não resolve o intervalo entre consultar, executar e guardar. A POC 4 tratará unicidade e transação no banco compartilhado.

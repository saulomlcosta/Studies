# Testes de integração da POC 3

```powershell
# Na pasta POC-03-MultipleInstances
dotnet test ./Tests/Idempotency.Poc03.Tests.csproj -c Release
```

Usamos xUnit e `Microsoft.AspNetCore.TestHost`. Cada instância de teste tem um `WebApplication`, um contêiner de DI e um `TestServer` próprios. Cache, pedidos e coordenador são singletons de cada contêiner, não variáveis estáticas compartilhadas.

`ApplicationSetup` registra o mesmo pipeline utilizado por `Program.cs`: identificação, middleware, roteamento, binding JSON e endpoints. O transporte é em memória, sem porta TCP. São testes de integração da API com hosts isolados no mesmo processo de testes; não são dois pods ou dois processos de sistema operacional. O script manual complementa essa cobertura usando dois processos reais.

## Cenários

| Teste | Comprovação |
| --- | --- |
| Repetição local | Status, corpo e Location preservados; identificação também aparece em replay |
| Mesma chave em A e B sequencialmente | Cada instância cria um pedido, inclusive com mesmo ID numérico local |
| Vinte chamadas distribuídas | Um pedido por instância e dezoito replays no total |
| Outro corpo em outra instância | A rejeita a chave já usada; B não conhece esse registro e aceita |
| Reiniciar A | Novo host perde cache e pedidos; B permanece com resultado e identificação originais |
| Pipeline HTTP | Chave ausente e JSON inválido retornam 400; JSON válido cria pedido consultável por Location |
| Metadados | Nomes identificam as instâncias e RunId diferencia inicializações |

O teste distribuído envia todas as chamadas antes de aguardar e verifica o resultado agregado. Não força uma determinada interleaving com atrasos: a duplicação entre instâncias independe disso e também é demonstrada pelo caso sequencial. A exclusão local com barreiras controladas continua coberta na suíte específica da POC 2.

Os clientes têm timeout de dez segundos para evitar espera indefinida. Os testes usam atraso de endpoint zero e não dependem de dormir por um tempo para decidir sucesso.

No cenário de reinício, descartamos o host A e construímos outro, mantendo B aberto. Isso modela a perda dos serviços em memória, sem simular uma queda abrupta de processo ou falha de transação.

## O que não é coberto

Não há teste de carga, banco, cache distribuído, rede entre pods, balanceador, expiração ou durabilidade. Sete testes passando significam que os comportamentos documentados foram observados, incluindo duplicação intencional; não significam que existe idempotência distribuída.

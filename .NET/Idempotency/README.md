# Idempotência em APIs .NET

Laboratório incremental em ASP.NET Core / .NET 10. Cada POC terá sua própria pasta, aplicação executável e README com propósito, decisões, experimentos e limitações. As versões anteriores serão preservadas para comparação.

## Linha de estudo

| Etapa | Propósito | Estado |
| --- | --- | --- |
| [POC 1](./POC-01-Memory/README.md) | Chave e resposta em memória; repetição sequencial | Implementada |
| [POC 2 — Concorrência local](./POC-02-Concurrency/README.md) | Reproduzir a corrida e coordenar execuções por chave | Implementada |
| [POC 3 — Múltiplas instâncias](./POC-03-MultipleInstances/README.md) | Mostrar por que memória e locks locais não coordenam dois processos | Implementada |
| POC 4 — Banco compartilhado | Constraint única e transação envolvendo chave, pedido e resultado | Planejada |
| POC 5 — Falhas e recuperação | Reinícios, falha após commit, retenção e resultado desconhecido | Planejada |
| POC 6 — Observabilidade | Contar requisições, execuções, replays, conflitos e falhas; medir latência e espera | Planejada |

Na POC 3, duas instâncias em portas distintas serão suficientes para experimentar o isolamento existente entre pods. Kubernetes não é requisito inicial. Nas etapas futuras, um experimento com Redis poderá comparar reserva distribuída com atomicidade no banco da operação.

## Contrato inicial

- `POST /orders` exige `Idempotency-Key`.
- Uma nova intenção recebe uma chave nova, mesmo com corpo igual.
- Retries da mesma intenção reutilizam a chave e o corpo.
- Chave concluída e corpo igual: devolver o resultado salvo.
- Chave existente com corpo diferente: retornar `409 Conflict`.

Começamos com um único cliente, uma única operação e armazenamento em memória. Escopo por tenant, autenticação, efeitos externos e políticas de produção serão discussões posteriores, não garantias da primeira versão.

## Método de aprendizado

Executar o cenário feliz, observar uma limitação, reproduzi-la e só então adicionar a solução na próxima pasta. Os READMEs indicarão o que cada versão garante e o que ainda pode falhar.

Na observabilidade, separaremos **requisições recebidas** de **operações executadas**: dez chamadas com a mesma chave podem representar uma execução e nove respostas reaproveitadas. Chaves individuais não serão labels de métricas, evitando cardinalidade sem limite.

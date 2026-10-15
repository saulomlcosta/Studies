# Studies

Repositório de estudos de programação, com exemplos, provas de conceito (POCs), documentação e testes. Cada estudo pode ser executado de forma independente.

## Índice

| Área | Estudo | Conteúdo |
| --- | --- | --- |
| .NET / APIs | [Idempotência](./.NET/Idempotency/README.md) | Chaves de idempotência, repetição de requisições, concorrência e evolução para múltiplas instâncias |
| C# / Design Patterns | [Factory Method](./CSharp/DesignPatterns/Creational/FactoryMethod/Program.cs) | Criação de objetos por subclasses de um criador |
| C# / Design Patterns | [Strategy](./CSharp/DesignPatterns/Behavioral/Strategy/Program.cs) | Troca de algoritmos por composição |

## Estrutura

```text
Studies/
├── README.md
├── .gitignore
├── .NET/
│   └── Idempotency/
│       ├── README.md
│       ├── POC-01-Memory/
│       │   ├── docs/
│       │   └── Tests/
│       └── POC-02-Concurrency/
│           └── Verification/
└── CSharp/
    └── DesignPatterns/
        ├── Creational/FactoryMethod/
        └── Behavioral/Strategy/
```

## Requisitos e execução

Os projetos atuais utilizam o SDK .NET 10. Os scripts de experimentos funcionam no Windows PowerShell 5.1 e no PowerShell 7. O restore dos testes pode precisar de acesso ao NuGet.

Execute os comandos abaixo a partir da raiz deste repositório:

```powershell
# Exemplos de padrões de projeto
dotnet run --project ./CSharp/DesignPatterns/Creational/FactoryMethod/FactoryMethod.csproj
dotnet run --project ./CSharp/DesignPatterns/Behavioral/Strategy/Strategy.csproj

# Testes da POC 1 de idempotência
dotnet test ./.NET/Idempotency/POC-01-Memory/Tests/Idempotency.Poc01.Tests.csproj -c Release

# Verificação em processo da POC 2
dotnet run --project ./.NET/Idempotency/POC-02-Concurrency/Verification/Verification.csproj
```

Para iniciar as APIs e realizar experimentos HTTP, consulte os READMEs de [POC 1](./.NET/Idempotency/POC-01-Memory/README.md) e [POC 2](./.NET/Idempotency/POC-02-Concurrency/README.md).

## Organização dos estudos

- Mantenha cada assunto em sua própria pasta e registre seu propósito no README.
- Preserve POCs anteriores para comparar comportamento e limitações.
- Documente requisitos, comandos de execução, resultados esperados e limitações conhecidas.
- Adicione testes quando ajudarem a demonstrar comportamento ou evitar regressões.
- Registre referências e a origem de exemplos utilizados. Os exemplos atuais de Design Patterns contêm namespaces e comentários identificados como `RefactoringGuru`.
- Faça commits por mudança de estudo, como adicionar um experimento ou explicar uma decisão.

## Arquivos locais

O `.gitignore` exclui saídas de compilação, resultados de testes, arquivos pessoais de IDE, logs, variáveis de ambiente locais e formatos comuns de chaves privadas. Arquivos `.env.example` e `.env.sample` podem ser versionados quando contiverem apenas valores fictícios.

Configurações compartilhadas e exemplos devem conter somente dados públicos ou fictícios. O `.gitignore` não detecta segredos inseridos em código ou em arquivos permitidos; revise o diff antes de cada commit.

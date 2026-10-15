# Compatível com Windows PowerShell 5.1 e PowerShell 7. Por padrão, espera reproduzir duplicidade concorrente.
param(
    [string]$BaseUrl = 'http://localhost:5101',
    [int]$Requests = 20,
    [switch]$Sequential
)

$ErrorActionPreference = 'Stop'
if ($Requests -lt 2 -or $Requests -gt 200) { throw 'Use entre 2 e 200 requisições.' }
$key = [Guid]::NewGuid().ToString()
Add-Type -AssemblyName System.Net.Http
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.MaxConnectionsPerServer = $Requests
$client = [System.Net.Http.HttpClient]::new($handler)
$messages = @()
$responses = @()
try {
    $tasks = @(for ($i = 0; $i -lt $Requests; $i++) {
        $message = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$BaseUrl/orders")
        $message.Headers.Add('Idempotency-Key', $key)
        $message.Content = [System.Net.Http.StringContent]::new('{"productId":123,"quantity":2}', [System.Text.Encoding]::UTF8, 'application/json')
        $messages += $message
        $task = $client.SendAsync($message)
        if ($Sequential) { $null = $task.GetAwaiter().GetResult() }
        # No modo concorrente, todas são enviadas antes de aguardar as respostas.
        $task
    })
    $ids = @()
    $replays = 0
    foreach ($task in $tasks) {
        $response = $task.GetAwaiter().GetResult()
        $responses += $response
        if ([int]$response.StatusCode -ne 201) { throw "Status inesperado: $($response.StatusCode)" }
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        $ids += $body.id
        if (($response.Headers.GetValues('Idempotency-Replayed') | Select-Object -First 1) -eq 'true') { $replays++ }
    }
    $uniqueIds = @($ids | Sort-Object -Unique)
    $ordersJson = $client.GetStringAsync("$BaseUrl/orders").GetAwaiter().GetResult()
    $orders = ConvertFrom-Json -InputObject $ordersJson
    $persisted = @($orders | Where-Object { $_.id -in $uniqueIds }).Count
    [pscustomobject]@{
        Mode = $(if ($Sequential) { 'Sequential' } else { 'Concurrent' })
        Requests = $Requests
        DistinctOrderIds = $uniqueIds.Count
        OrdersFoundInStore = $persisted
        Replays = $replays
        Key = $key
        OrderIds = $uniqueIds -join ', '
    }
    if ($persisted -ne $uniqueIds.Count) { throw 'Nem todos os IDs retornados foram encontrados em GET /orders.' }
    if ($Sequential) {
        if ($uniqueIds.Count -ne 1 -or $replays -ne ($Requests - 1)) {
            throw 'Esperávamos um pedido e N-1 replays em chamadas sequenciais.'
        }
    } elseif ($uniqueIds.Count -lt 2) {
        throw 'Corrida não observada. Reinicie a API com --Experiment:DelayMilliseconds=2000 e repita. O atraso amplia a janela, mas não garante o agendamento.'
    }
} finally {
    $client.Dispose()
    foreach ($response in $responses) { $response.Dispose() }
    foreach ($message in $messages) { $message.Dispose() }
}

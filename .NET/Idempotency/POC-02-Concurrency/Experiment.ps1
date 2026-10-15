param(
    [string]$BaseUrl = 'http://localhost:5102',
    [int]$Requests = 20,
    [switch]$ExpectDuplicates
)

$ErrorActionPreference = 'Stop'
if ($Requests -lt 2) { throw 'Use pelo menos duas requisições.' }
$key = [Guid]::NewGuid().ToString()
Add-Type -AssemblyName System.Net.Http
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.MaxConnectionsPerServer = $Requests
$client = [System.Net.Http.HttpClient]::new($handler)
$messages = @()
$responses = @()
try {
    # Envia todas antes de aguardar os resultados: não é um loop sequencial de HTTP.
    $tasks = @(for ($i = 0; $i -lt $Requests; $i++) {
        $message = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$BaseUrl/orders")
        $message.Headers.Add('Idempotency-Key', $key)
        $message.Content = [System.Net.Http.StringContent]::new('{"productId":123,"quantity":2}', [System.Text.Encoding]::UTF8, 'application/json')
        $messages += $message
        $client.SendAsync($message)
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
    $unique = @($ids | Sort-Object -Unique).Count
    [pscustomobject]@{ Requests = $Requests; DistinctOrderIds = $unique; Replays = $replays; Key = $key }
    if ($ExpectDuplicates) {
        if ($unique -lt 2) { throw 'Corrida não observada. Aumente Experiment:DelayMilliseconds e tente novamente.' }
    } elseif ($unique -ne 1 -or $replays -ne ($Requests - 1)) {
        throw 'Falha: esperávamos um pedido e N-1 replays.'
    }
} finally {
    foreach ($response in $responses) { $response.Dispose() }
    foreach ($message in $messages) { $message.Dispose() }
    $client.Dispose()
}

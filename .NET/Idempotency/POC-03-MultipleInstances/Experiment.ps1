# Windows PowerShell 5.1 e PowerShell 7.
param(
    [string]$UrlA = 'http://localhost:5103',
    [string]$UrlB = 'http://localhost:5104',
    [ValidateRange(2, 200)][int]$Requests = 20,
    [switch]$SingleInstance
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.MaxConnectionsPerServer = $Requests
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(30)
$key = [Guid]::NewGuid().ToString()
$messages = @()
$responses = @()
try {
    $urls = @($UrlA.TrimEnd('/'))
    if (!$SingleInstance) { $urls += $UrlB.TrimEnd('/') }
    $instances = @(foreach ($url in $urls) {
        $metadata = $client.GetStringAsync("$url/instance").GetAwaiter().GetResult() | ConvertFrom-Json
        [pscustomobject]@{ Url = $url; Id = $metadata.id; RunId = $metadata.runId }
    })
    if (@($instances.RunId | Sort-Object -Unique).Count -ne $instances.Count) {
        throw 'Os destinos apontam para a mesma inicializacao. Execute dois processos distintos.'
    }
    if (@($instances.Id | Sort-Object -Unique).Count -ne $instances.Count) {
        throw 'Use Instance:Id diferentes, por exemplo A e B.'
    }
    $pending = @(for ($i = 0; $i -lt $Requests; $i++) {
        $target = $instances[$i % $instances.Count]
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$($target.Url)/orders")
        $request.Headers.Add('Idempotency-Key', $key)
        $request.Content = [System.Net.Http.StringContent]::new('{"productId":123,"quantity":2}', [Text.Encoding]::UTF8, 'application/json')
        $messages += $request
        [pscustomobject]@{ Target = $target; Task = $client.SendAsync($request) }
    })
    $results = @(foreach ($item in $pending) {
        $response = $item.Task.GetAwaiter().GetResult()
        $responses += $response
        if ([int]$response.StatusCode -ne 201) { throw "Status inesperado: $($response.StatusCode)" }
        $id = @($response.Headers.GetValues('X-Instance-Id'))[0]
        $run = @($response.Headers.GetValues('X-Instance-Run-Id'))[0]
        if ($id -ne $item.Target.Id -or $run -ne $item.Target.RunId) { throw 'A instancia mudou durante a rodada.' }
        $order = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        [pscustomobject]@{
            Instance = $id
            RunId = $run
            OrderId = $order.id
            Replay = (@($response.Headers.GetValues('Idempotency-Replayed'))[0] -eq 'true')
        }
    })
    $rows = @(foreach ($instance in $instances) {
        $local = @($results | Where-Object { $_.RunId -eq $instance.RunId })
        $ids = @($local.OrderId | Sort-Object -Unique)
        $json = $client.GetStringAsync("$($instance.Url)/orders").GetAwaiter().GetResult()
        $orders = ConvertFrom-Json -InputObject $json
        $stored = @($orders | Where-Object { $_.id -in $ids }).Count
        $replays = @($local | Where-Object { $_.Replay }).Count
        if ($ids.Count -ne 1 -or $stored -ne 1 -or $replays -ne ($local.Count - 1)) {
            throw "Protecao local inesperada na instancia $($instance.Id). Verifique se a coordenacao esta habilitada."
        }
        [pscustomobject]@{ Instance = $instance.Id; Requests = $local.Count; Orders = $stored; Replays = $replays; OrderIds = $ids -join ', ' }
    })
    $rows | Format-Table -AutoSize
    $identities = @($results | ForEach-Object { "$($_.RunId):$($_.OrderId)" } | Sort-Object -Unique)
    if ($identities.Count -ne $instances.Count) { throw 'Quantidade de execucoes entre instancias inesperada.' }
    "Key: $key"
    "Total de pedidos nesta rodada (inicializacao + ID local): $($identities.Count)"
    if ($SingleInstance) { 'PASS: uma instancia criou um pedido.' }
    else { 'PASS: a mesma chave criou um pedido em cada instancia, demonstrando duplicacao entre processos.' }
} finally {
    $client.Dispose()
    foreach ($response in $responses) { $response.Dispose() }
    foreach ($message in $messages) { $message.Dispose() }
}

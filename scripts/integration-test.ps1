param(
    [string]$BaseUrl = "http://127.0.0.1:18080"
)

$ErrorActionPreference = "Stop"

function New-TestDevice([string]$Name) {
    $key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    return [pscustomobject]@{
        Id = [Guid]::NewGuid()
        Name = $Name
        Key = $key
        PublicKey = [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo())
        Token = $null
    }
}

function Register-Device($Device) {
    $canonical = "{0}`n{1}`n{2}" -f $Device.Id.ToString("D"), $Device.Name, $Device.PublicKey
    $payload = [Text.Encoding]::UTF8.GetBytes($canonical)
    $signature = [Convert]::ToBase64String($Device.Key.SignData($payload, [Security.Cryptography.HashAlgorithmName]::SHA256))
    $body = @{
        deviceId = $Device.Id.ToString("D")
        name = $Device.Name
        publicKey = $Device.PublicKey
        signature = $signature
    } | ConvertTo-Json
    Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/devices/register" -ContentType "application/json" -Body $body | Out-Null
}

function Connect-Device($Device) {
    $challenge = Invoke-RestMethod -Uri "$BaseUrl/api/auth/challenge/$($Device.Id.ToString('D'))"
    $payload = [Text.Encoding]::UTF8.GetBytes($challenge.challenge)
    $signature = [Convert]::ToBase64String($Device.Key.SignData($payload, [Security.Cryptography.HashAlgorithmName]::SHA256))
    $body = @{
        deviceId = $Device.Id.ToString("D")
        challenge = $challenge.challenge
        signature = $signature
    } | ConvertTo-Json
    $session = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/auth/session" -ContentType "application/json" -Body $body
    $Device.Token = $session.token
}

$first = New-TestDevice "Integration PC A"
$second = New-TestDevice "Integration PC B"

try {
    Register-Device $first
    Register-Device $second
    Connect-Device $first
    Connect-Device $second

    $firstHeaders = @{ Authorization = "Bearer $($first.Token)" }
    $secondHeaders = @{ Authorization = "Bearer $($second.Token)" }
    $pairCode = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/pairing/code" -Headers $firstHeaders -ContentType "application/json" -Body "{}"
    $joinBody = @{ code = $pairCode.code } | ConvertTo-Json
    Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/pairing/join" -Headers $secondHeaders -ContentType "application/json" -Body $joinBody | Out-Null

    $firstDevices = @(Invoke-RestMethod -Uri "$BaseUrl/api/devices" -Headers $firstHeaders)
    $secondDevices = @(Invoke-RestMethod -Uri "$BaseUrl/api/devices" -Headers $secondHeaders)
    if ($firstDevices.Count -ne 1 -or $secondDevices.Count -ne 1) {
        throw "Pairing did not produce one peer for each device."
    }

    $webSocketUrl = ($BaseUrl -replace '^http', 'ws').TrimEnd('/') + '/connect'
    $firstSocket = [Net.WebSockets.ClientWebSocket]::new()
    $secondSocket = [Net.WebSockets.ClientWebSocket]::new()
    try {
        $firstSocket.Options.SetRequestHeader("Authorization", "Bearer $($first.Token)")
        $secondSocket.Options.SetRequestHeader("Authorization", "Bearer $($second.Token)")
        $firstSocket.ConnectAsync([Uri]$webSocketUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
        $secondSocket.ConnectAsync([Uri]$webSocketUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null

        [byte[]]$message = [byte[]]::new(30)
        $message[0] = 1
        $second.Id.ToByteArray().CopyTo($message, 1)
        $message[17] = 5
        $segment = [ArraySegment[byte]]::new($message)
        $firstSocket.SendAsync($segment, [Net.WebSockets.WebSocketMessageType]::Binary, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null

        [byte[]]$received = [byte[]]::new(64)
        $receiveSegment = [ArraySegment[byte]]::new($received)
        $timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(5))
        $result = $secondSocket.ReceiveAsync($receiveSegment, $timeout.Token).GetAwaiter().GetResult()
        if ($result.Count -ne 30 -or $received[0] -ne 1 -or $received[17] -ne 5) {
            throw "WebSocket relay returned an invalid input envelope."
        }

        Write-Host "PASS: registration, signed authentication, pairing, device listing, and WebSocket relay"
    }
    finally {
        $firstSocket.Dispose()
        $secondSocket.Dispose()
    }
}
finally {
    $first.Key.Dispose()
    $second.Key.Dispose()
}

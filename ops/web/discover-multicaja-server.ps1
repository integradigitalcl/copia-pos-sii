param(
    [int]$ApiPort = 7279,
    [int]$TimeoutMs = 5000
)

$ErrorActionPreference = "Stop"

function Ensure-TrailingSlash([string]$url) {
    if ([string]::IsNullOrWhiteSpace($url)) { return $url }
    if ($url.EndsWith("/")) { return $url }
    return "$url/"
}

function Get-LocalIPv4Prefixes {
    [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() |
        Where-Object {
            $_.OperationalStatus -eq 'Up' -and
            $_.NetworkInterfaceType -ne [System.Net.NetworkInformation.NetworkInterfaceType]::Loopback
        } |
        ForEach-Object { $_.GetIPProperties().UnicastAddresses } |
        Where-Object { $_.Address.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork } |
        ForEach-Object {
            $b = $_.Address.GetAddressBytes()
            if ($b.Length -eq 4) { "$($b[0]).$($b[1]).$($b[2])" }
        } |
        Select-Object -Unique
}

function Find-ViaUdp {
    $magic = "GFPOS-DISCOVER"
    $requestId = [guid]::NewGuid().ToString("N").Substring(0, 8)
    $payload = [Text.Encoding]::UTF8.GetBytes("$magic`n$requestId")
    $discoveryPort = 33279
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    $clients = New-Object System.Collections.Generic.List[System.Net.Sockets.UdpClient]

    try {
        foreach ($ni in [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
            if ($ni.OperationalStatus -ne 'Up') { continue }
            if ($ni.NetworkInterfaceType -eq [System.Net.NetworkInformation.NetworkInterfaceType]::Loopback) { continue }
            foreach ($ua in $ni.GetIPProperties().UnicastAddresses) {
                if ($ua.Address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) { continue }
                try {
                    $udp = New-Object System.Net.Sockets.UdpClient ([System.Net.Sockets.AddressFamily]::InterNetwork)
                    $udp.EnableBroadcast = $true
                    $udp.Client.SetSocketOption([System.Net.Sockets.SocketOptionLevel]::Socket, [System.Net.Sockets.SocketOptionName]::ReuseAddress, $true)
                    $udp.Client.Bind([System.Net.IPEndPoint]::new($ua.Address, 0))
                    [void]$clients.Add($udp)
                } catch { }
            }
        }

        if ($clients.Count -eq 0) {
            $udp = New-Object System.Net.Sockets.UdpClient ([System.Net.Sockets.AddressFamily]::InterNetwork)
            $udp.EnableBroadcast = $true
            $udp.Client.Bind([System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0))
            [void]$clients.Add($udp)
        }

        $broadcast = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Broadcast, $discoveryPort)
        for ($i = 0; $i -lt 3; $i++) {
            foreach ($udp in $clients) {
                try { [void]$udp.Send($payload, $payload.Length, $broadcast) } catch { }
            }
            Start-Sleep -Milliseconds 300
        }

        while ((Get-Date) -lt $deadline) {
            foreach ($udp in $clients) {
                try {
                    while ($udp.Available -gt 0) {
                        $remote = New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Any, 0)
                        $bytes = $udp.Receive([ref]$remote)
                        $text = [Text.Encoding]::UTF8.GetString($bytes)
                        $obj = $text | ConvertFrom-Json
                        if ($obj.requestId -eq $requestId -and $obj.apiBaseUrl) {
                            if (-not $obj.apiPort -or [int]$obj.apiPort -eq $ApiPort) {
                                return (Ensure-TrailingSlash ([string]$obj.apiBaseUrl))
                            }
                        }
                    }
                } catch { }
            }
            Start-Sleep -Milliseconds 120
        }
    } finally {
        foreach ($udp in $clients) { try { $udp.Dispose() } catch { } }
    }
    return $null
}

function Test-ApiLive([string]$url) {
    try {
        $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 2
        return $resp.StatusCode -ge 200 -and $resp.StatusCode -lt 300
    } catch { return $false }
}

function Find-ViaSubnetScan {
    $prefixes = @(Get-LocalIPv4Prefixes)
    if ($prefixes.Count -eq 0) { return $null }
    $firstTry = @(1, 10, 100, 101, 102, 200)
    foreach ($prefix in $prefixes) {
        foreach ($lastOctet in $firstTry) {
            $url = "http://${prefix}.${lastOctet}:${ApiPort}/health/live"
            if (Test-ApiLive $url) { return "http://${prefix}.${lastOctet}:${ApiPort}/" }
        }
        for ($lastOctet = 2; $lastOctet -le 254; $lastOctet++) {
            if ($lastOctet -in $firstTry) { continue }
            $url = "http://${prefix}.${lastOctet}:${ApiPort}/health/live"
            if (Test-ApiLive $url) { return "http://${prefix}.${lastOctet}:${ApiPort}/" }
        }
    }
    return $null
}

$apiBase = Find-ViaUdp
if (-not $apiBase) { $apiBase = Find-ViaSubnetScan }
if ($apiBase) {
    Write-Output (Ensure-TrailingSlash $apiBase)
    exit 0
}
exit 2

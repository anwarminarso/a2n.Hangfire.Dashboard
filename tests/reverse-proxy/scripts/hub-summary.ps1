<#
.SYNOPSIS
    Summarizes dashboard SignalR hub requests from the nginx access log (same table as issue #61).
.PARAMETER Path
    Hub path to match. Default: /jobs/hubs/dashboard/negotiate
.PARAMETER Tail
    Only look at the last N log lines (0 = whole file).
.PARAMETER Clear
    Truncate the log first (useful before measuring a single page load).
#>
param(
    [string]$Path = '/jobs/hubs/dashboard/negotiate',
    [int]$Tail = 0,
    [switch]$Clear
)

$log = Join-Path $PSScriptRoot '..\.generated\nginx-logs\access.log'
if (-not $Clear -and -not (Test-Path $log)) { Write-Warning "Log not found: $log (is the stack running?)"; return }

if ($Clear) {
    # nginx keeps the bind-mounted file open (locked on Windows), so truncate it from inside the container.
    docker compose -f (Join-Path $PSScriptRoot '..\compose.yaml') exec -T nginx sh -c ': > /var/log/nginx/proxy/access.log'
    Write-Host 'Log cleared.'
    return
}

$lines = if ($Tail -gt 0) { Get-Content $log -Tail $Tail } else { Get-Content $log }
if (-not $lines) { Write-Host 'Log is empty.'; return }
$pattern = '^\[(?<time>[^\]]+)\] (?<status>\d{3}) (?<method>\S+) (?<scheme>\S+) "(?<uri>[^"]*)" \[Client (?<client>[^\]]+)\] "(?<ua>[^"]*)" rt=(?<rt>\S+)'

$rows = foreach ($line in $lines) {
    if ($line -match $pattern -and $Matches.uri.StartsWith($Path, [StringComparison]::OrdinalIgnoreCase)) {
        [pscustomobject]@{
            Status = $Matches.status; Method = $Matches.method; Scheme = $Matches.scheme
            Client = $Matches.client; Time = $Matches.time; Rt = [double]$Matches.rt
            UA     = if ($Matches.ua -like 'Microsoft SignalR*') { 'server (.NET SignalR client)' } else { 'browser' }
        }
    }
}

if (-not $rows) { Write-Host "No requests to $Path."; return }

$ok = @($rows | Where-Object { $_.Status -like '2*' }).Count
Write-Host ("{0} requests to {1}, {2} successful`n" -f @($rows).Count, $Path, $ok)
$rows | Group-Object Status, Method, Scheme, Client, UA | Sort-Object Count -Descending |
    ForEach-Object {
        $f = $_.Group[0]
        [pscustomobject]@{ Count = $_.Count; Status = $f.Status; Method = $f.Method; Scheme = $f.Scheme; Client = $f.Client; Caller = $f.UA }
    } | Format-Table -AutoSize | Out-String -Width 200

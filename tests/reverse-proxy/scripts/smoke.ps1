<#
.SYNOPSIS
    Logs in through nginx with curl, loads one dashboard page (server prerender only, no circuit),
    and prints time-to-first-byte plus the hub negotiate summary for that single request.
.EXAMPLE
    ./scripts/smoke.ps1                       # admin, /jobs
    ./scripts/smoke.ps1 -Page /jobs/analytics
#>
param(
    [string]$BaseUrl = 'https://hangfire.127.0.0.1.nip.io',
    [string]$Page = '/jobs',
    [string]$User = 'admin',
    [string]$Password = 'admin'
)

$ErrorActionPreference = 'Stop'
$jar = Join-Path ([IO.Path]::GetTempPath()) "proxy-env-cookies-$PID.txt"
try {
    $login = curl.exe -sk -c $jar "$BaseUrl/Account/Login"
    $token = [regex]::Match(($login -join ''), 'name="__RequestVerificationToken" value="([^"]+)"').Groups[1].Value
    if (-not $token) { throw 'Could not read antiforgery token from the login page.' }

    $null = curl.exe -sk -b $jar -c $jar -o NUL -X POST "$BaseUrl/Account/Login" `
        --data-urlencode "username=$User" --data-urlencode "password=$Password" `
        --data-urlencode "returnUrl=$Page" --data-urlencode "__RequestVerificationToken=$token"

    & "$PSScriptRoot\hub-summary.ps1" -Clear | Out-Null
    $timing = curl.exe -sk -b $jar -o NUL -w '%{http_code} ttfb=%{time_starttransfer}s total=%{time_total}s' "$BaseUrl$Page"
    Write-Host "GET $Page -> $timing`n"
    Start-Sleep -Milliseconds 500   # let nginx flush the log
    & "$PSScriptRoot\hub-summary.ps1"
}
finally {
    Remove-Item $jar -ErrorAction SilentlyContinue
}

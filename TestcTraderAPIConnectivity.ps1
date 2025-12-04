# Test cTrader API Connectivity
# Run this script to verify network access

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  CTRADER CONNECTIVITY TEST" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Test DNS resolution
Write-Host "Test 1: DNS Resolution" -ForegroundColor Yellow
Write-Host "  Resolving demo.ctraderapi.com..." -ForegroundColor White
try {
    $dnsResult = Resolve-DnsName -Name "demo.ctraderapi.com" -ErrorAction Stop
    Write-Host "  ✅ DNS Resolution successful:" -ForegroundColor Green
    foreach ($ip in $dnsResult | Where-Object {$_.Type -eq 'A'}) {
        Write-Host "     - $($ip.IPAddress)" -ForegroundColor Cyan
    }
}
catch {
    Write-Host "  ❌ DNS Resolution failed: $($_.Exception.Message)" -ForegroundColor Red
}
Write-Host ""

# Test TCP connection to port 443
Write-Host "Test 2: TCP Connection (Port 443)" -ForegroundColor Yellow
Write-Host "  Testing connection to demo.ctraderapi.com:443..." -ForegroundColor White
$tcpTest = Test-NetConnection -ComputerName "demo.ctraderapi.com" -Port 443 -WarningAction SilentlyContinue

if ($tcpTest.TcpTestSucceeded) {
    Write-Host "  ✅ TCP Connection SUCCESSFUL!" -ForegroundColor Green
    Write-Host "     - Remote Address: $($tcpTest.RemoteAddress)" -ForegroundColor Cyan
    Write-Host "     - Source Address: $($tcpTest.SourceAddress.IPAddress)" -ForegroundColor Cyan
}
else {
    Write-Host "  ❌ TCP Connection FAILED!" -ForegroundColor Red
    Write-Host "     - Remote Address: $($tcpTest.RemoteAddress)" -ForegroundColor Yellow
    Write-Host "     - Ping Success: $($tcpTest.PingSucceeded)" -ForegroundColor Yellow
}
Write-Host ""

# Test HTTPS with curl
Write-Host "Test 3: HTTPS Connection (curl)" -ForegroundColor Yellow
Write-Host "  Testing HTTPS handshake..." -ForegroundColor White
try {
    $curlResult = curl.exe -s -o /dev/null -w "%{http_code}" --connect-timeout 10 https://demo.ctraderapi.com 2>&1
    if ($curlResult -match "^\d{3}$") {
        Write-Host "  ✅ HTTPS handshake successful (HTTP $curlResult)" -ForegroundColor Green
    }
    else {
        Write-Host "  ⚠️  HTTPS response: $curlResult" -ForegroundColor Yellow
    }
}
catch {
    Write-Host "  ❌ HTTPS test failed: $($_.Exception.Message)" -ForegroundColor Red
}
Write-Host ""

# Check firewall rules
Write-Host "Test 4: Firewall Rules" -ForegroundColor Yellow
$rules = Get-NetFirewallRule -DisplayName "cTrader*" -ErrorAction SilentlyContinue
if ($rules) {
    Write-Host "  ✅ Found $($rules.Count) cTrader firewall rules:" -ForegroundColor Green
    foreach ($rule in $rules) {
        $enabled = if ($rule.Enabled -eq "True") { "✅" } else { "❌" }
        Write-Host "     $enabled $($rule.DisplayName)" -ForegroundColor Cyan
    }
}
else {
    Write-Host "  ⚠️  No cTrader firewall rules found" -ForegroundColor Yellow
    Write-Host "     Run Configure-CTraderFirewall.ps1 first" -ForegroundColor White
}
Write-Host ""

# Summary
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  TEST SUMMARY" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if ($tcpTest.TcpTestSucceeded) {
    Write-Host "  STATUS: ✅ READY FOR CTRADER CONNECTION" -ForegroundColor Green
    Write-Host ""
    Write-Host "  You can now run your application:" -ForegroundColor White
    Write-Host "  dotnet run --project src\DerivCTrader.SignalScraper\DerivCTrader.SignalScraper.csproj" -ForegroundColor Cyan
}
else {
    Write-Host "  STATUS: ❌ CONNECTION BLOCKED" -ForegroundColor Red
    Write-Host ""
    Write-Host "  Troubleshooting steps:" -ForegroundColor Yellow
    Write-Host "  1. Check antivirus web protection settings" -ForegroundColor White
    Write-Host "  2. Try disabling antivirus temporarily" -ForegroundColor White
    Write-Host "  3. Check router firewall settings" -ForegroundColor White
    Write-Host "  4. Try connecting via mobile hotspot" -ForegroundColor White
    Write-Host "  5. Contact your ISP if issue persists" -ForegroundColor White
}
Write-Host ""
# Configure Windows Firewall for cTrader API Access
# Run this script as Administrator

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  CTRADER FIREWALL CONFIGURATION" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# cTrader IP addresses (both observed IPs)
$ctraderIPs = @("15.197.239.248", "3.33.208.221")
$ctraderPort = 443

# Programs that need access
$dotnetPath = "C:\Program Files\dotnet\dotnet.exe"
$vsPath = "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe"

Write-Host "Step 1: Removing any existing cTrader firewall rules..." -ForegroundColor Yellow
Remove-NetFirewallRule -DisplayName "cTrader*" -ErrorAction SilentlyContinue
Write-Host "  ✅ Old rules removed" -ForegroundColor Green
Write-Host ""

Write-Host "Step 2: Creating outbound rule for dotnet.exe..." -ForegroundColor Yellow
try {
    New-NetFirewallRule `
        -DisplayName "cTrader Outbound (dotnet.exe)" `
        -Direction Outbound `
        -Program $dotnetPath `
        -Action Allow `
        -Protocol TCP `
        -RemotePort $ctraderPort `
        -RemoteAddress $ctraderIPs `
        -Profile Any `
        -Enabled True
    
    Write-Host "  ✅ Rule created for dotnet.exe" -ForegroundColor Green
}
catch {
    Write-Host "  ❌ Failed: $($_.Exception.Message)" -ForegroundColor Red
}
Write-Host ""

Write-Host "Step 3: Creating outbound rule for Visual Studio (devenv.exe)..." -ForegroundColor Yellow
if (Test-Path $vsPath) {
    try {
        New-NetFirewallRule `
            -DisplayName "cTrader Outbound (devenv.exe)" `
            -Direction Outbound `
            -Program $vsPath `
            -Action Allow `
            -Protocol TCP `
            -RemotePort $ctraderPort `
            -RemoteAddress $ctraderIPs `
            -Profile Any `
            -Enabled True
        
        Write-Host "  ✅ Rule created for Visual Studio" -ForegroundColor Green
    }
    catch {
        Write-Host "  ❌ Failed: $($_.Exception.Message)" -ForegroundColor Red
    }
}
else {
    Write-Host "  ⚠️  Visual Studio not found at default location - skipping" -ForegroundColor Yellow
}
Write-Host ""

Write-Host "Step 4: Creating general outbound rule (system-wide)..." -ForegroundColor Yellow
try {
    New-NetFirewallRule `
        -DisplayName "cTrader Outbound (System)" `
        -Direction Outbound `
        -Action Allow `
        -Protocol TCP `
        -RemotePort $ctraderPort `
        -RemoteAddress $ctraderIPs `
        -Profile Any `
        -Enabled True
    
    Write-Host "  ✅ System-wide rule created" -ForegroundColor Green
}
catch {
    Write-Host "  ❌ Failed: $($_.Exception.Message)" -ForegroundColor Red
}
Write-Host ""

Write-Host "Step 5: Verifying rules..." -ForegroundColor Yellow
$rules = Get-NetFirewallRule -DisplayName "cTrader*"
Write-Host "  Found $($rules.Count) cTrader rules:" -ForegroundColor Green
foreach ($rule in $rules) {
    Write-Host "    - $($rule.DisplayName) [$($rule.Enabled)]" -ForegroundColor Cyan
}
Write-Host ""

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  CONFIGURATION COMPLETE!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Test connectivity: Test-NetConnection -ComputerName demo.ctraderapi.com -Port 443" -ForegroundColor White
Write-Host "  2. If still blocked, check antivirus settings" -ForegroundColor White
Write-Host "  3. Run your application and test cTrader connection" -ForegroundColor White
Write-Host ""
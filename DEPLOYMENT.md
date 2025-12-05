# VPS Deployment Guide

This guide explains how to deploy DerivCTrader to your VPS using Azure DevOps.

## Prerequisites

1. **VPS Setup** (108.181.161.170)
   - Windows Server with PowerShell remoting enabled
   - .NET 8.0 Runtime installed
   - Deployment user with admin privileges

2. **Azure DevOps Variables**
   
   Configure the following in your Azure DevOps pipeline library as **secret variables**:
   
   | Variable Name | Type | Description | Example |
   |--------------|------|-------------|---------|
   | `DEPLOY_PASSWORD` | Secret | VPS deployment user password | `YourSecurePassword123!` |
   | `TELEGRAM_BOT_TOKEN` | Secret | Bot token for deployment notifications | `123456:ABC-DEF...` |
   | `TELEGRAM_CHAT_ID` | Secret | Chat ID for notifications | `-1001234567890` |

3. **Telegram Session Files**
   
   **IMPORTANT**: Before first deployment, you must create Telegram session files on the VPS:
   
   - Run `DerivCTrader.SignalScraper.exe` locally first
   - Authenticate both Telegram accounts when prompted
   - This creates `DerivCTrader.session` and `DerivCTrader2.session`
   - Copy these files to VPS: `C:\Services\DerivCTraderSignalScraper\`
   
   **Session files will be preserved across deployments** - the pipeline automatically backs them up.

## Deployment Process

### First-Time Setup

1. **Create Session Files Locally**
   ```powershell
   cd src\DerivCTrader.SignalScraper
   dotnet run
   # Authenticate both Telegram accounts when prompted
   ```

2. **Copy Sessions to VPS**
   ```powershell
   # Create directory on VPS
   New-Item -ItemType Directory -Path "C:\Services\DerivCTraderSignalScraper" -Force
   
   # Copy session files (replace <VPS_IP> and adjust path)
   Copy-Item "DerivCTrader.session" -Destination "\\<VPS_IP>\C$\Services\DerivCTraderSignalScraper\"
   Copy-Item "DerivCTrader2.session" -Destination "\\<VPS_IP>\C$\Services\DerivCTraderSignalScraper\"
   ```

3. **Configure Azure DevOps**
   - Add pipeline variables (see Prerequisites)
   - Ensure agent pool "Default" exists and has VPS access
   - Test connection: `Test-WSMan -ComputerName 108.181.161.170`

4. **Run Pipeline**
   - Push to `master` branch or manually trigger `azure-pipelines-deploy.yml`
   - Monitor deployment stages: Build → Authentication Test → Deploy → Notify
   - Check Telegram for deployment notification

### Subsequent Deployments

Simply push to `master` or manually trigger the pipeline. Session files are preserved automatically.

## Pipeline Stages

1. **Build**
   - Builds both `SignalScraper` and `TradeExecutor` projects
   - Creates separate artifacts for each service
   - Verifies EXE and configuration files exist

2. **Authentication Test**
   - Tests VPS connectivity and credentials
   - Determines best authentication method (Negotiate, Kerberos, NTLM, Basic)
   - Fails fast if VPS unreachable

3. **Deploy**
   - **SignalScraper Deployment**:
     - Stops `DerivCTraderSignalScraper` service
     - Backs up existing `*.session` files to `session_backup\`
     - Clears old binaries (preserves logs, session files, backups)
     - Copies new binaries from artifact
     - Restores session files from backup
     - Starts service and verifies status
   
   - **TradeExecutor Deployment**:
     - Stops `DerivCTraderTradeExecutor` service
     - Clears old binaries (preserves logs)
     - Copies new binaries from artifact
     - Starts service and verifies status

4. **Notify**
   - Sends Telegram notification with deployment status
   - Includes build number and links to logs

## Deployed Services

| Service Name | Display Name | EXE Path | Purpose |
|-------------|-------------|----------|---------|
| `DerivCTraderSignalScraper` | Deriv cTrader Signal Scraper | `C:\Services\DerivCTraderSignalScraper\DerivCTrader.SignalScraper.exe` | Monitors Telegram channels, parses signals |
| `DerivCTraderTradeExecutor` | Deriv cTrader Trade Executor | `C:\Services\DerivCTraderTradeExecutor\DerivCTrader.TradeExecutor.exe` | Executes trades on cTrader and Deriv |

## Troubleshooting

### Service Won't Start

Check Windows Event Viewer on VPS:
```powershell
Get-EventLog -LogName Application -Source "DerivCTrader*" -Newest 20
```

Or check service-specific logs:
```powershell
# SignalScraper logs
Get-Content "C:\Services\DerivCTraderSignalScraper\logs\*.txt" -Tail 50

# TradeExecutor logs
Get-Content "C:\Services\DerivCTraderTradeExecutor\logs\*.txt" -Tail 50
```

### Session Files Lost

If Telegram sessions are lost (requiring re-authentication):
```powershell
# Check backup
Get-ChildItem "C:\Services\DerivCTraderSignalScraper\session_backup\*.session"

# Restore from backup
Copy-Item "C:\Services\DerivCTraderSignalScraper\session_backup\*.session" -Destination "C:\Services\DerivCTraderSignalScraper\"
```

### Authentication Failures

Verify credentials:
```powershell
# Test from build agent
$secPass = ConvertTo-SecureString "YOUR_PASSWORD" -AsPlainText -Force
$cred = New-Object PSCredential ("YourUser", $secPass)
New-PSSession -ComputerName 108.181.161.170 -Credential $cred
```

### cTrader Connection Issues

Check configuration in `appsettings.Production.json`:
- Host: `demo.ctraderapi.com` (for both demo and live Deriv accounts)
- Port: `5035`
- DemoAccountId: `45291837`
- LiveAccountId: `45291831`
- AccessToken: Must be fresh (expires every ~30 days)

Renew access token:
1. Go to https://spotware.com/
2. Login → My Apps → DerivCTrader App
3. Generate new OAuth token
4. Update `appsettings.Production.json` on VPS

## Session File Preservation

**How it works:**
1. Before deployment, pipeline backs up all `*.session` files to `session_backup\`
2. Old binaries are deleted, but `session_backup\`, `logs\`, and `charts\` preserved
3. New binaries copied
4. Session files restored from backup

**Expected session files:**
- `DerivCTrader.session` (Telegram account 1)
- `DerivCTrader2.session` (Telegram account 2)

These files contain WTelegram authentication state. **Never commit to git**.

## Manual Deployment (If Needed)

If Azure DevOps unavailable:

```powershell
# Build locally
dotnet publish src\DerivCTrader.SignalScraper -c Release -r win-x64 --self-contained
dotnet publish src\DerivCTrader.TradeExecutor -c Release -r win-x64 --self-contained

# Copy to VPS (preserve sessions!)
$vps = "108.181.161.170"
$scraperPath = "src\DerivCTrader.SignalScraper\bin\Release\net8.0\win-x64\publish"
$executorPath = "src\DerivCTrader.TradeExecutor\bin\Release\net8.0\win-x64\publish"

# Stop services on VPS first
Invoke-Command -ComputerName $vps -ScriptBlock {
    Stop-Service DerivCTraderSignalScraper -Force -ErrorAction SilentlyContinue
    Stop-Service DerivCTraderTradeExecutor -Force -ErrorAction SilentlyContinue
}

# Copy files (use robocopy to exclude sessions)
robocopy $scraperPath "\\$vps\C$\Services\DerivCTraderSignalScraper" /MIR /XF *.session
robocopy $executorPath "\\$vps\C$\Services\DerivCTraderTradeExecutor" /MIR

# Start services
Invoke-Command -ComputerName $vps -ScriptBlock {
    Start-Service DerivCTraderSignalScraper
    Start-Service DerivCTraderTradeExecutor
}
```

## Monitoring

Check service status:
```powershell
Invoke-Command -ComputerName 108.181.161.170 -ScriptBlock {
    Get-Service DerivCTrader* | Format-Table Name, Status, StartType
}
```

Tail logs in real-time:
```powershell
Invoke-Command -ComputerName 108.181.161.170 -ScriptBlock {
    Get-Content "C:\Services\DerivCTraderSignalScraper\logs\*.txt" -Wait -Tail 20
}
```

## Security Notes

- **Never commit** `appsettings.Production.json` with real credentials
- **Never commit** `*.session` files (already in `.gitignore`)
- Use Azure DevOps secret variables for:
  - VPS passwords
  - Telegram bot tokens
  - Database connection strings (if applicable)
- Rotate cTrader access token every 30 days
- Use strong passwords for VPS deployment user

## Next Steps After Deployment

1. **Verify Services Running**
   ```powershell
   Get-Service DerivCTrader* | Where-Object {$_.Status -ne 'Running'}
   ```

2. **Check Telegram Connection**
   - Look for "Connected to Telegram" in logs
   - Verify channels being monitored

3. **Test Signal Parsing**
   - Send test signal to monitored channel
   - Check logs for "Successfully parsed signal"

4. **Monitor cTrader Connection**
   - Look for "Application authenticated" in logs
   - Verify "Account authenticated" for demo account

5. **Set Up Alerts**
   - Configure Telegram bot for error notifications
   - Set up Windows scheduled task to restart services on failure

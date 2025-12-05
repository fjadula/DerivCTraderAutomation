# Copilot / AI Agent Instructions for DerivCTraderAutomation

This repo automates trading signals → cTrader → Deriv binary execution. The goal of these instructions is to get an AI coding agent productive quickly by highlighting the project's architecture, conventions, runtime workflows, and the source files that matter for common change types.

1) Big picture
- Signal ingestion: `src/DerivCTrader.SignalScraper` (WTelegramClient) listens to provider channels and emits `ParsedSignal` objects.
- Parsing layer: `src/DerivCTrader.Application/Parsers/*` implement `ISignalParser` to turn raw messages/images into `ParsedSignal`.
- cTrader flow: parsed signals become pending orders on cTrader (monitor ticks, detect price cross → market execution). See `DerivCTrader.Infrastructure/Trading/CTrader*` (TODOs in codebase).
- Deriv execution: `DerivWebSocketClient` buys binary contracts after cTrader order execution. Expiry calculation lives in `DerivCTrader.Infrastructure/ExpiryCalculation/BinaryExpiryCalculator.cs`.
- Persistence & matching: SQL Server via Dapper. Queue matching uses `TradeExecutionQueue` → `BinaryOptionTrades` matching (FIFO by Asset+Direction).

2) Key files to inspect when making changes
- Architecture overview: `ARCHITECTURE.md` (big picture flows and sequences).
- Quick start + run commands: `QUICKSTART.md` and `README.md`.
- Parsers & interfaces: `src/DerivCTrader.Application/Parsers/` and `src/DerivCTrader.Application/Interfaces/ISignalParser.cs`.
- Execution & integrations: `src/DerivCTrader.Infrastructure/Trading/DerivWebSocketClient.cs`, `CTrader*` files under `Infrastructure/Trading`.
- Expiry calc: `src/DerivCTrader.Infrastructure/ExpiryCalculation/BinaryExpiryCalculator.cs` (used to compute Deriv contract expiries).
- Hosted apps: `src/DerivCTrader.SignalScraper/Program.cs` and `src/DerivCTrader.TradeExecutor/Program.cs` (entry points, DI registrations, hosted services).

3) Common developer workflows (exact commands)
- Build solution: `dotnet build c:\path\to\DerivCTraderAutomation.sln`
- Run SignalScraper: `cd src/DerivCTrader.SignalScraper; dotnet run`
- Run TradeExecutor: `cd src/DerivCTrader.TradeExecutor; dotnet run`
- Publish self-contained (Windows): `dotnet publish -c Release -r win-x64 --self-contained`

4) Configuration & secrets (project-specific)
- Configuration file: copy and edit `src/DerivCTrader.SignalScraper/appsettings.Production.json` and the same for `DerivCTrader.TradeExecutor`.
- Important keys: `ConnectionStrings__DefaultConnection`, `Deriv:Token`, `Deriv:AppId`, `Telegram:WTelegram` accounts, and `CTrader` environment/account IDs.
- Provider-channel mapping is stored in DB table `ProviderChannelConfig` — parsers rely on that mapping. See `QUICKSTART.md` for SQL insert examples.

5) Project-specific conventions and patterns
- Parser pattern: implement `ISignalParser` and register via DI (`services.AddSingleton<ISignalParser, MyParser>()`). See `VipFxParser.cs` and `PerfectFxParser.cs` for examples.
- Multi-account Telegram: repo uses WTelegram client instances per account in `SignalScraper` and expects configured channel IDs for providers.
- Two-phase execution pipeline: cTrader pending-order → monitor price cross → when executed write a row into `TradeExecutionQueue` → `TradeExecutor` consumes queue to call Deriv API.
- DB access: prefer Dapper raw SQL in `DerivCTrader.Infrastructure/Persistence` (not EF Core). Keep SQL names and column names consistent with existing tables (`ForexTrades`, `BinaryOptionTrades`, `TradeExecutionQueue`, `ProviderChannelConfig`).
- Logging: Serilog to console + files; logs live in `src/*/logs/` per app. Use existing log messages to trace flows (search for `OrderExecuted`, `Successfully parsed signal`, `Connected to Deriv`).

6) Integration points and external behavior to respect
- Deriv WebSocket: persistent connection to `wss://ws.binaryws.com/websockets/v3?app_id=109082`. Use `authorize`, `buy` calls as shown in `DerivWebSocketClient`.
- cTrader: pending orders and tick stream are central. cTrader client behavior matters for ordering semantics — code sometimes assumes a pending will later convert to market on cross.
- SQL matching: queue matching is FIFO by asset+direction; any change must preserve matching key logic to avoid mis-association of trades.

7) Safe change guidelines for AI edits
- When editing parsers: add unit-like integration by running `SignalScraper` and sending a sample message to a test channel (or create a local test harness). Update `ProviderChannelConfig` DB entries if adding a channel.
- When touching execution paths (cTrader/Deriv): do not change DB schema or matching logic without updating `ARCHITECTURE.md`, `QUICKSTART.md`, and tests. Prefer minimal API surface changes.
- When adding packages: ensure compatibility with .NET 8.0 target used across projects.

8) Examples (copy-paste snippets)
- Add parser skeleton:
```csharp
public class MyProviderParser : ISignalParser
{
    public bool CanParse(string providerChannelId) => providerChannelId == "-1001234567";
    public Task<ParsedSignal?> ParseAsync(string message, string providerChannelId, byte[]? imageData = null) { /* ... */ }
}
```
- Run both apps locally (PowerShell):
```powershell
cd src\DerivCTrader.SignalScraper; dotnet run
cd ..\..\DerivCTrader.TradeExecutor; dotnet run
```

9) Where to look for TODOs / starting points
- `DerivCTrader.Infrastructure/Trading` contains several `TODO` markers for `CTraderWebSocketClient` and related files — implementing these completes the cTrader integration.
- `DerivCTrader.Application/Parsers/ChartSenseParser.cs` (marked TODO) is where OCR integration belongs.

If anything above is unclear or you'd like me to expand any section (examples, more file excerpts, or add CI/CD automation notes), tell me which part to iterate on. After confirmation I'll commit any changes you approve.

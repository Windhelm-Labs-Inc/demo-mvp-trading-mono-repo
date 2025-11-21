# Market Maker Worker Service

Maintains a liquidity ladder on Hedera perpetual futures. Watches Redis for index price updates and adjusts orders to keep them centered around the index.

## How It Works

```
Redis publishes BTC index → Service updates order prices → Orders stay centered on market
```

The service places N levels of bids and asks around the index price. When the index moves, it cancels old orders and places new ones at the correct prices. Orders are sized in a liquidity shape (larger at top of book, smaller deeper in).

Configure the number of levels in `appsettings.json` or via environment variable `NUMBER_OF_LEVELS`.

## Configuration

### Required Environment Variables

```bash
HEDERA_ACCOUNT_ID=0.0.XXXXXXX
HEDERA_PRIVATE_KEY_DER_HEX=302e...
HEDERA_LEDGER_ID=testnet
REDIS_CONNECTION_STRING=host:port,ssl=True,password=...
```

### Trading Parameters

Set in `appsettings.json` or via environment variables:

```bash
NUMBER_OF_LEVELS=4              # Levels per side (default: 4)
BASE_SPREAD_USD=100.00         # Distance from mid to best bid/ask
LEVEL_SPACING_USD=1.00         # Spacing between levels
INITIAL_MARGIN_FACTOR=0.2      # 20% margin = 5x leverage

# Liquidity shape (order sizes per level)
LEVEL_0_QUANTITY=0.00001       # Best level size
LEVELS_1_TO_2_QUANTITY=0.000005  # Mid levels
LEVELS_3_TO_9_QUANTITY=0.000001  # Deep levels
```

**To change number of levels:** Update `NumberOfLevels` in `appsettings.json` or set `NUMBER_OF_LEVELS` environment variable. The service will place this many levels on each side of the book.

**Liquidity shape sizing:**
- Level 0: Uses `Level0Quantity`
- Levels 1-2: Use `Levels1To2Quantity`
- Levels 3+: Use `Levels3To9Quantity`

If you set `NumberOfLevels=10`, you'll have 10 bids and 10 asks, with sizes following the shape above.

### Order Update Behavior

Two modes for replacing orders when price moves:

**Sequential Mode** (`UPDATE_BEHAVIOR_FLAG=0`)
- Cancel old orders first, then submit new ones
- Creates brief liquidity gaps but guarantees no self-trading
- Use in volatile markets or when you're the only liquidity provider

**Atomic Mode** (`UPDATE_BEHAVIOR_FLAG=1`)
- Submit new orders first, wait, then cancel old ones
- Maintains continuous liquidity
- Includes self-trade prevention (see below)
- Default and recommended

```bash
UPDATE_BEHAVIOR_FLAG=1                    # 0=Sequential, 1=Atomic
ATOMIC_REPLACEMENT_DELAY_MS=300          # Wait time before canceling (ms)
ENABLE_SELF_TRADE_PREVENTION=1           # 0=Disabled, 1=Enabled
SEQUENTIAL_PEEL_DELAY_MS=5               # Delay between levels when peeling
```

## Self-Trade Prevention

When atomic mode detects that new orders would cross existing orders (self-trade risk), it automatically switches behavior for that update.

**Detection:**
- Checks if new bid >= any existing ask
- Checks if new ask <= any existing bid

**Response when crossing detected:**

| Scenario | Action |
|----------|--------|
| Bids would cross asks | Peel asks level-by-level (inside→outside), then submit bids atomically |
| Asks would cross bids | Peel bids level-by-level (inside→outside), then submit asks atomically |
| Both sides cross | Peel both sides level-by-level |

**Sequential peel (per level):**
1. Cancel old orders at level
2. Wait 5ms
3. Submit new orders at level
4. Move to next level

This removes the "victim" side before placing the "aggressor" side. Processes levels from inside-out (L0→LN), so outer levels stay live while inner levels update, minimizing liquidity gaps.

## Running Locally

```bash
cd MarketMakerWorkerService
cp env.example .env
# Edit .env with your credentials
dotnet run
```

## Azure Deployment

Set these application settings:
```
HEDERA_ACCOUNT_ID=0.0.XXXXXXX
HEDERA_PRIVATE_KEY_DER_HEX=...
HEDERA_LEDGER_ID=testnet
REDIS_CONNECTION_STRING=...
UPDATE_BEHAVIOR_FLAG=1
ATOMIC_REPLACEMENT_DELAY_MS=300
ENABLE_SELF_TRADE_PREVENTION=1
```

Run as a single replica. Multiple instances will self-trade.

## Common Issues

**"Order was already closed, filled or did not exist"**
- Normal. Means orders filled before cancellation could complete.
- High rate in volatile markets is expected (you're making money).
- Not an error despite the log level.

**Self-trade warnings appearing frequently**
- Increase `ATOMIC_REPLACEMENT_DELAY_MS` to 500ms
- Check if spread is too tight for current volatility
- Temporarily switch to sequential mode (`UPDATE_BEHAVIOR_FLAG=0`)

**Orders not being placed**
- Check authentication logs
- Verify account balance
- Confirm API endpoint is reachable

**Service consuming too much memory/CPU**
- Service is normally <100MB RAM, <5% CPU
- High usage indicates network issues or API problems
- Check for retry storms in logs

## Log Levels

- `INF`: Normal operation (startup, shutdown, price updates)
- `WRN`: Self-trade prevention triggered, retry attempts
- `ERR`: API failures, order placement failures
- `DBG`: Detailed order flow (enable for troubleshooting only)

## Performance

- Order update latency: 120-400ms in atomic mode
- Orders per update: 2N (N bids + N asks, where N = `NumberOfLevels`)
- Resource usage: ~50MB RAM, ~2% CPU

## Architecture

**BasicMarketMakerStrategy** - Core logic for order placement and updates
**OrderStateManager** - Tracks active orders by side and level
**RedisIndexWatcher** - Subscribes to index price updates
**OrderService** - HTTP client for order API
**AuthenticationService** - JWT token management

Orders are updated atomically (no concurrent updates) via semaphore lock.

# Market Maker Worker Service

Maintains a liquidity ladder on Hedera perpetual futures. Watches Redis for index price updates and adjusts orders to keep them centered around the index.

## How It Works

```
Redis publishes BTC index → Service updates order prices → Orders stay centered on market
```

The service places N levels of bids and asks around the index price. When the index moves, it cancels old orders and places new ones at the correct prices. Orders are sized in a liquidity shape (larger at top of book, smaller deeper in).

**Why Atomic Replacement:** The API lacks an order update endpoint. To maintain continuous liquidity during price updates, new orders are submitted before old ones are canceled, simulating update behavior until the engineering team adds native support.

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

## Continuous Settlement

The orderbook accumulates long/short positions as orders fill. Since there's no auto-settlement, the service periodically settles matched positions to prevent balance depletion.

```bash
CONTINUOUS_SETTLEMENT=0  # 0=Disabled, 1=Enabled
```

**When enabled, settlement runs:**
- On startup
- After each token refresh (~15 minutes)
- On shutdown

Settlement is automatic and requires no manual intervention. Positions are matched and netted (e.g., 100 long + 80 short → settle 80, leaving 20 long).

## Metrics and Observability

The service can export operational metrics to Azure Application Insights:

```bash
ENABLE_METRICS=0                             # 0=Disabled, 1=Enabled
METRICS_EXPORT_INTERVAL_MS=60000             # Export interval (ms)
APPLICATIONINSIGHTS_CONNECTION_STRING=...    # Azure connection string
```

**Tracked metrics:**
- Orders placed/canceled (success/failure counts)
- Self-trade prevention triggers (by side: bids/asks/both)
- Index update success/failure counts

Metrics help monitor market maker health, order fill rates, and API reliability.

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
# Required
HEDERA_ACCOUNT_ID=0.0.XXXXXXX
HEDERA_PRIVATE_KEY_DER_HEX=...
HEDERA_LEDGER_ID=testnet
REDIS_CONNECTION_STRING=...

# Order behaviour
UPDATE_BEHAVIOR_FLAG=1
ATOMIC_REPLACEMENT_DELAY_MS=300 #<-- Shorter delays result in a higher likelihood of cancel requests landing before OB is updated. 
ENABLE_SELF_TRADE_PREVENTION=1 <- STP protection
SEQUENTIAL_PEEL_DELAY_MS=5 #<- Onion peel from inside book to out, STP, side specific, delay prevents the resting liquidity from vanishing. Increase according to system performance. 

# Liquidity ladder (optional - has defaults)
NUMBER_OF_LEVELS=4
BASE_SPREAD_USD=100.00
LEVEL_SPACING_USD=1.00
LEVEL_0_QUANTITY=0.00001 #<- quantity 1000
LEVELS_1_TO_2_QUANTITY:0.000005 #<- quantity 500
LEVELS_3_TO_9_QUANTITY:0.000001 #<- quantity 100

# Settlement (recommended)
CONTINUOUS_SETTLEMENT=1

# Metrics (optional)
ENABLE_METRICS=1
APPLICATIONINSIGHTS_CONNECTION_STRING=...
```

Run as a single replica. Multiple instances will self-trade.

## Common Issues

**"Order was already closed, filled or did not exist"**
- Normal. Means orders filled before cancellation could complete (you're making money).
- These are now logged at Debug level to reduce noise.
- Monitor via `orders.cancel.failed` metric instead of logs.
- High rate in volatile markets is expected.

**Orders not being placed**
- Turn on Debug logging & check authentication logs
- Check API & Dashboard
- Check Postgres instance for maxed out CPU or other indications
- Verify account balance
- Confirm API endpoint is reachable

**Position balance growing unexpectedly**
- Enable continuous settlement (`CONTINUOUS_SETTLEMENT=1`)
- Check settlement logs at token refresh intervals
- Verify settlement API endpoint is accessible

## Log Levels

- `INF`: Normal operation (startup, shutdown, price updates, settlement)
- `WRN`: We got rid of most of these. Should only occur on settlement issues.
- `ERR`: API failures, order placement failures
- `DBG`: Order cancellations, detailed order flow, filled order failures (use metrics instead in staging/prod)

## Performance

- Order update latency: 100-200 in atomic mode
- Orders per update: 2N (N bids + N asks, where N = `NumberOfLevels`)

## Architecture

**BasicMarketMakerStrategy** - Core logic for order placement and updates
**OrderStateManager** - Tracks active orders by side and level
**RedisIndexWatcher** - Subscribes to index price updates
**OrderService** - HTTP client for order API
**AuthenticationService** - JWT token management

Orders are updated atomically (no concurrent updates) via semaphore lock.

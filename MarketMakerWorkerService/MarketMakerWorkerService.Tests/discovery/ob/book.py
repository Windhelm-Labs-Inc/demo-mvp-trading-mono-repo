#!/usr/bin/env python3
"""
View Orderbook via REST API
Simpler alternative that uses the REST API endpoint instead of gRPC
"""

import requests
from datetime import datetime

# Configuration
API_BASE = "https://perps-api-d7cff5fhd9g0b7c4.eastus-01.azurewebsites.net"
TRADING_DECIMALS = 8  # BTC
SETTLEMENT_DECIMALS = 6  # USDC

def format_price(price_base_units):
    """Convert base units to human-readable price"""
    return price_base_units / (10 ** SETTLEMENT_DECIMALS)

def format_quantity(quantity_base_units):
    """Convert base units to human-readable quantity (contracts)"""
    return quantity_base_units / (10 ** TRADING_DECIMALS)

def display_orderbook(data):
    """Display orderbook in a readable format"""
    print("\n" + "=" * 80)
    print("ORDERBOOK DEPTH CHART")
    print("=" * 80)
    print(f"Timestamp: {datetime.utcnow().isoformat()}Z")
    print(f"Version: {data.get('version', 'N/A')}")
    print()

    # Display ASKS (sorted high to low for visual clarity)
    asks = data.get('asks', [])
    if asks:
        print("ASKS (Sell Orders):")
        print("-" * 80)
        print(f"{'Price (USD)':>15} | {'Quantity (BTC)':>15} | {'Orders':>10} | {'Total USD':>15}")
        print("-" * 80)

        total_ask_qty = 0
        total_ask_value = 0

        # Display asks from highest to lowest price
        for level in reversed(asks):
            price = format_price(level['price'])
            quantity = format_quantity(level['total_quantity'])
            order_count = level['order_count']
            total_value = price * quantity

            total_ask_qty += quantity
            total_ask_value += total_value

            print(f"${price:>14,.2f} | {quantity:>15,.8f} | {order_count:>10} | ${total_value:>14,.2f}")

        print("-" * 80)
        print(f"{'TOTAL ASKS:':>15} | {total_ask_qty:>15,.8f} BTC | ${total_ask_value:>14,.2f}")
        print()
    else:
        print("ASKS: No sell orders\n")

    # Calculate spread
    best_ask = asks[0] if asks else None
    bids = data.get('bids', [])
    best_bid = bids[0] if bids else None

    if best_ask and best_bid:
        best_ask_price = format_price(best_ask['price'])
        best_bid_price = format_price(best_bid['price'])
        spread = best_ask_price - best_bid_price
        spread_bps = (spread / best_bid_price) * 10000 if best_bid_price > 0 else 0

        print("=" * 80)
        print(f"SPREAD: ${spread:,.2f} ({spread_bps:.2f} bps)")
        print(f"  Best Ask: ${best_ask_price:,.2f}")
        print(f"  Best Bid: ${best_bid_price:,.2f}")
        print("=" * 80)
        print()

    # Display BIDS
    if bids:
        print("BIDS (Buy Orders):")
        print("-" * 80)
        print(f"{'Price (USD)':>15} | {'Quantity (BTC)':>15} | {'Orders':>10} | {'Total USD':>15}")
        print("-" * 80)

        total_bid_qty = 0
        total_bid_value = 0

        for level in bids:
            price = format_price(level['price'])
            quantity = format_quantity(level['total_quantity'])
            order_count = level['order_count']
            total_value = price * quantity

            total_bid_qty += quantity
            total_bid_value += total_value

            print(f"${price:>14,.2f} | {quantity:>15,.8f} | {order_count:>10} | ${total_value:>14,.2f}")

        print("-" * 80)
        print(f"{'TOTAL BIDS:':>15} | {total_bid_qty:>15,.8f} BTC | ${total_bid_value:>14,.2f}")
        print()
    else:
        print("BIDS: No buy orders\n")

    print("=" * 80)

def get_orderbook(levels=100):
    """Fetch orderbook via REST API"""
    print("\n" + "=" * 80)
    print("FETCHING ORDERBOOK")
    print("=" * 80)
    print(f"API: {API_BASE}/api/v1/market/depth")
    print(f"Levels: {levels}")

    try:
        url = f"{API_BASE}/api/v1/market/depth?levels={levels}"
        response = requests.get(url, timeout=10)

        if response.status_code == 200:
            print("✓ Success")
            return response.json()
        else:
            print(f"✗ Failed: HTTP {response.status_code}")
            print(response.text)
            return None

    except Exception as e:
        print(f"✗ Error: {e}")
        import traceback
        traceback.print_exc()
        return None

def main():
    """Main execution"""
    import argparse

    parser = argparse.ArgumentParser(description='View orderbook via REST API')
    parser.add_argument('--levels', type=int, default=100, help='Number of price levels to fetch (default: 20)')

    args = parser.parse_args()

    print("\n" + "=" * 80)
    print("VIEW ORDERBOOK SCRIPT")
    print("=" * 80)
    print(f"Started: {datetime.now().isoformat()}")

    data = get_orderbook(levels=args.levels)

    if data:
        display_orderbook(data)
        print("\n✓ COMPLETED SUCCESSFULLY")
    else:
        print("\n✗ Failed to fetch orderbook")
        return 1

    print("=" * 80)
    print(f"Finished: {datetime.now().isoformat()}")
    return 0

if __name__ == "__main__":
    exit(main())
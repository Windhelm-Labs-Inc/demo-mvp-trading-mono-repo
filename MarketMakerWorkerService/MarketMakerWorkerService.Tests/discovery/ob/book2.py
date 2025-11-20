#!/usr/bin/env python3
"""
View Orderbook with Full HTTP Request/Response Details
Shows complete HTTP exchange including headers, timing, and raw response
"""

import requests
import json
from datetime import datetime
import time

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

def log_http_request(method, url, headers, body=None):
    """Log complete HTTP request"""
    print("\n" + "=" * 80)
    print("HTTP REQUEST")
    print("=" * 80)
    print(f"{method} {url}")
    print(f"Time: {datetime.utcnow().isoformat()}Z")
    print()
    print("Headers:")
    print("-" * 80)
    for key, value in headers.items():
        print(f"  {key}: {value}")

    if body:
        print()
        print("Body:")
        print("-" * 80)
        print(json.dumps(body, indent=2))

    print("=" * 80)

def log_http_response(response, elapsed_time):
    """Log complete HTTP response"""
    print("\n" + "=" * 80)
    print("HTTP RESPONSE")
    print("=" * 80)
    print(f"Status: {response.status_code} {response.reason}")
    print(f"Time: {datetime.utcnow().isoformat()}Z")
    print(f"Elapsed: {elapsed_time:.3f}s")
    print()
    print("Headers:")
    print("-" * 80)
    for key, value in response.headers.items():
        print(f"  {key}: {value}")

    print()
    print("Body:")
    print("-" * 80)

    try:
        # Try to parse as JSON and pretty-print
        data = response.json()
        print(json.dumps(data, indent=2))
        return data
    except:
        # If not JSON, print raw text
        print(response.text if response.text else "(empty)")
        return None

    print("=" * 80)

def display_orderbook_summary(data):
    """Display a concise orderbook summary"""
    if not data:
        return

    print("\n" + "=" * 80)
    print("ORDERBOOK SUMMARY")
    print("=" * 80)

    asks = data.get('asks', [])
    bids = data.get('bids', [])
    version = data.get('version', 'N/A')

    print(f"Version: {version}")
    print(f"Total Ask Levels: {len(asks)}")
    print(f"Total Bid Levels: {len(bids)}")

    if asks:
        best_ask = asks[0]
        best_ask_price = format_price(best_ask['price'])
        best_ask_qty = format_quantity(best_ask['total_quantity'])
        print(f"Best Ask: ${best_ask_price:,.2f} | {best_ask_qty:.8f} BTC | {best_ask['order_count']} orders")

    if bids:
        best_bid = bids[0]
        best_bid_price = format_price(best_bid['price'])
        best_bid_qty = format_quantity(best_bid['total_quantity'])
        print(f"Best Bid: ${best_bid_price:,.2f} | {best_bid_qty:.8f} BTC | {best_bid['order_count']} orders")

    if asks and bids:
        best_ask_price = format_price(asks[0]['price'])
        best_bid_price = format_price(bids[0]['price'])
        spread = best_ask_price - best_bid_price
        spread_bps = (spread / best_bid_price) * 10000 if best_bid_price > 0 else 0
        print(f"Spread: ${spread:,.2f} ({spread_bps:.2f} bps)")

    # Display top 5 levels of each side
    if asks:
        print("\nTop 5 Asks:")
        print(f"{'Price':>12} | {'Quantity':>12} | {'Orders':>8}")
        print("-" * 40)
        for level in list(reversed(asks))[:5]:
            price = format_price(level['price'])
            qty = format_quantity(level['total_quantity'])
            print(f"${price:>11,.2f} | {qty:>12,.8f} | {level['order_count']:>8}")

    if bids:
        print("\nTop 5 Bids:")
        print(f"{'Price':>12} | {'Quantity':>12} | {'Orders':>8}")
        print("-" * 40)
        for level in bids[:5]:
            price = format_price(level['price'])
            qty = format_quantity(level['total_quantity'])
            print(f"${price:>11,.2f} | {qty:>12,.8f} | {level['order_count']:>8}")

    print("=" * 80)

def get_orderbook_raw(levels=50):
    """Fetch orderbook and show complete HTTP exchange"""
    url = f"{API_BASE}/api/v1/market/depth?levels={levels}"

    headers = {
        "Accept": "application/json",
        "User-Agent": "Python-Orderbook-Viewer/1.0"
    }

    # Log outgoing request
    log_http_request("GET", url, headers)

    try:
        # Make request and time it
        start_time = time.time()
        response = requests.get(url, headers=headers, timeout=10)
        elapsed_time = time.time() - start_time

        # Log incoming response
        data = log_http_response(response, elapsed_time)

        return data, response.status_code == 200

    except requests.exceptions.Timeout:
        print("\n✗ Request timed out after 10 seconds")
        return None, False
    except requests.exceptions.ConnectionError as e:
        print(f"\n✗ Connection error: {e}")
        return None, False
    except Exception as e:
        print(f"\n✗ Error: {e}")
        import traceback
        traceback.print_exc()
        return None, False

def main():
    """Main execution"""
    import argparse

    parser = argparse.ArgumentParser(
        description='View orderbook with full HTTP request/response details'
    )
    parser.add_argument(
        '--levels',
        type=int,
        default=20,
        help='Number of price levels to fetch (default: 20, max: 50)'
    )
    parser.add_argument(
        '--summary',
        action='store_true',
        help='Show summary view of orderbook (in addition to raw HTTP)'
    )

    args = parser.parse_args()

    print("\n" + "=" * 80)
    print("VIEW ORDERBOOK - FULL HTTP DETAILS")
    print("=" * 80)
    print(f"Started: {datetime.now().isoformat()}")
    print(f"Endpoint: {API_BASE}/api/v1/market/depth")
    print(f"Levels: {args.levels}")

    data, success = get_orderbook_raw(levels=args.levels)

    if success and args.summary and data:
        display_orderbook_summary(data)

    print("\n" + "=" * 80)
    if success:
        print("✓ COMPLETED SUCCESSFULLY")
    else:
        print("✗ REQUEST FAILED")
    print("=" * 80)
    print(f"Finished: {datetime.now().isoformat()}")

    return 0 if success else 1

if __name__ == "__main__":
    exit(main())
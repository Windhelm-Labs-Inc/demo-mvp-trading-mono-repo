#!/usr/bin/env python3
"""
View Orderbook via gRPC
Connects to OrderManager.GetDepthChart to fetch the current orderbook
"""

import grpc
import sys
from datetime import datetime

# Configuration
GRPC_ADDRESS = "order-book-frfte4fccmc4c5b8.eastus-01.azurewebsites.net:443"
TRADING_DECIMALS = 8  # BTC
SETTLEMENT_DECIMALS = 6  # USDC

# Proto definitions (inline for simplicity)
# You'll need to compile the .proto files to Python if you want full type safety
# For now, we'll use dynamic gRPC with manual message construction

def format_price(price_base_units):
    """Convert base units to human-readable price"""
    return price_base_units / (10 ** SETTLEMENT_DECIMALS)

def format_quantity(quantity_base_units):
    """Convert base units to human-readable quantity (contracts)"""
    return quantity_base_units / (10 ** TRADING_DECIMALS)

def parse_depth_chart(response):
    """Parse and display the depth chart response"""
    print("\n" + "=" * 80)
    print("ORDERBOOK DEPTH CHART")
    print("=" * 80)
    print(f"Timestamp: {datetime.utcnow().isoformat()}Z")
    print(f"Version: {response.depth_chart.version}")
    print()

    # Display ASKS (sorted high to low for visual clarity)
    asks = list(response.depth_chart.asks)
    if asks:
        print("ASKS (Sell Orders):")
        print("-" * 80)
        print(f"{'Price (USD)':>15} | {'Quantity (BTC)':>15} | {'Orders':>10} | {'Total USD':>15}")
        print("-" * 80)

        total_ask_qty = 0
        total_ask_value = 0

        # Display asks from highest to lowest price
        for level in reversed(asks):
            price = format_price(level.price)
            quantity = format_quantity(level.total_quantity)
            order_count = level.order_count
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
    best_bid = response.depth_chart.bids[0] if response.depth_chart.bids else None

    if best_ask and best_bid:
        best_ask_price = format_price(best_ask.price)
        best_bid_price = format_price(best_bid.price)
        spread = best_ask_price - best_bid_price
        spread_bps = (spread / best_bid_price) * 10000 if best_bid_price > 0 else 0

        print("=" * 80)
        print(f"SPREAD: ${spread:,.2f} ({spread_bps:.2f} bps)")
        print(f"  Best Ask: ${best_ask_price:,.2f}")
        print(f"  Best Bid: ${best_bid_price:,.2f}")
        print("=" * 80)
        print()

    # Display BIDS
    bids = list(response.depth_chart.bids)
    if bids:
        print("BIDS (Buy Orders):")
        print("-" * 80)
        print(f"{'Price (USD)':>15} | {'Quantity (BTC)':>15} | {'Orders':>10} | {'Total USD':>15}")
        print("-" * 80)

        total_bid_qty = 0
        total_bid_value = 0

        for level in bids:
            price = format_price(level.price)
            quantity = format_quantity(level.total_quantity)
            order_count = level.order_count
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

def get_orderbook(bid_levels=20, ask_levels=20):
    """Fetch orderbook via gRPC GetDepthChart"""
    print("\n" + "=" * 80)
    print("CONNECTING TO ORDERBOOK GRPC SERVICE")
    print("=" * 80)
    print(f"Address: {GRPC_ADDRESS}")
    print(f"Method: OrderManager.GetDepthChart")
    print(f"Requesting {bid_levels} bid levels and {ask_levels} ask levels")

    try:
        # Create SSL credentials for HTTPS
        credentials = grpc.ssl_channel_credentials()

        # Create channel
        with grpc.secure_channel(GRPC_ADDRESS, credentials) as channel:
            print("✓ Connected")

            # Create stub (using generic invoke since we don't have compiled protos)
            # Note: For production use, you should compile the .proto files
            from grpc._channel import _UnaryUnaryMultiCallable

            # We'll need to manually construct the request
            # For a proper implementation, compile the protos using:
            # python -m grpc_tools.protoc -I. --python_out=. --grpc_python_out=. order-manager.proto

            print("\n⚠ This script requires compiled protobuf files.")
            print("\nTo generate them:")
            print("  1. cd Orderbook/Perpetuals-Market/Perpetuals.Market/Protobuf/")
            print("  2. pip install grpcio-tools")
            print("  3. python -m grpc_tools.protoc -I. --python_out=. --grpc_python_out=. *.proto")
            print("  4. Copy generated *_pb2.py files to this script's directory")
            print("\nAlternatively, here's a REST API equivalent to view the orderbook:")
            print("  GET https://perps-api-d7cff5fhd9g0b7c4.eastus-01.azurewebsites.net/api/v1/market/depth")

            return None

    except grpc.RpcError as e:
        print(f"\n✗ gRPC Error: {e.code()}")
        print(f"  Details: {e.details()}")
        return None
    except Exception as e:
        print(f"\n✗ Error: {e}")
        import traceback
        traceback.print_exc()
        return None

def main():
    """Main execution"""
    import argparse

    parser = argparse.ArgumentParser(description='View orderbook via gRPC')
    parser.add_argument('--bids', type=int, default=20, help='Number of bid levels to fetch (default: 20)')
    parser.add_argument('--asks', type=int, default=20, help='Number of ask levels to fetch (default: 20)')

    args = parser.parse_args()

    print("\n" + "=" * 80)
    print("VIEW ORDERBOOK SCRIPT")
    print("=" * 80)
    print(f"Started: {datetime.now().isoformat()}")

    response = get_orderbook(bid_levels=args.bids, ask_levels=args.asks)

    if response:
        parse_depth_chart(response)
        print("\n✓ COMPLETED SUCCESSFULLY")
    else:
        print("\n✗ Failed to fetch orderbook")
        return 1

    print("=" * 80)
    print(f"Finished: {datetime.now().isoformat()}")
    return 0

if __name__ == "__main__":
    exit(main())
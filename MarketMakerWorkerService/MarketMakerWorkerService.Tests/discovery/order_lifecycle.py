#!/usr/bin/env python3

"""
Order Lifecycle Test Script

This script demonstrates the complete order lifecycle:
1. Authenticate with the API (HIP-820 message signing)
2. Check account balance (POST /api/v1/account/balance)
3. Dynamically calculate order parameters based on available balance
4. Submit a limit order (POST /api/v1/market/order/submit)
5. View account details including orders (POST /api/v1/account)
6. Cancel the order (POST /api/v1/market/order/cancel)

All requests and responses are logged in detail with timing measurements.
10 second delays between API calls to respect rate limits (5 req/10s).
"""

import requests
import base64
import uuid
import time
import json
from datetime import datetime

# ============================================================================
# CONFIGURATION - All configurable parameters
# ============================================================================

# API Configuration
API_BASE = "https://perps-api-d7cff5fhd9g0b7c4.eastus-01.azurewebsites.net"
LEDGER_ID = "testnet"
ACCOUNT_ID = "0.0.6978377"
PRIVATE_KEY_DER_HEX = "302e020100300506032b6570042204205db3a68cb7831bcefb625238e7800cc9dc85aab09b2acf97537af0d9ef667d7b"

# Rate Limiting
RATE_LIMIT_DELAY = 10  # Wait 10 seconds between API calls

# Order Parameters (in human-readable units)
ORDER_PRICE_USD = 60000.00        # Price per BTC contract in USD
ORDER_SIDE = 0                    # 0 = Long/Buy, 1 = Short/Sell
ORDER_KIND = 1                    # 0 = Market, 1 = Limit
ORDER_TIME_IN_FORCE = 0           # 0 = GoodUntilFilled

# Margin Configuration
# NOTE: The margin field is a MARGIN FACTOR/FRACTION (in base units), not absolute amount!
# Formula: requiredBalance = (quantity * price * marginFactor) / (10^settlementDecimals * 10^tradingDecimals)
# Example: marginFactor of 1,000,000 (with 6 decimals) = 1.0 = 100% initial margin (1x leverage)
INITIAL_MARGIN_FACTOR = 1200000   # 1.2 in base units = 120% = ~0.83x leverage (conservative)

# Dynamic Order Sizing (based on available balance)
ORDER_SIZE_FACTOR = 0.10          # Use 10% of available balance for an order

# Fallback Balance (used if portfolio endpoint not available)
ASSUMED_BALANCE_S_TOKENS = 40.0   # Assumed balance when endpoint returns 404

# Decimal Scaling (will be fetched from API, these are defaults)
DEFAULT_TRADING_DECIMALS = 8      # BTC contract decimals
DEFAULT_SETTLEMENT_DECIMALS = 6   # S token (USDC) decimals
DEFAULT_SETTLEMENT_TOKEN = "0.0.6891795"  # Fallback settlement token ID

# Constraints and Limits
MAX_MARGIN_S_TOKENS = 10          # Maximum margin allowed (10 S tokens)
TOKEN_DISPLAY_SUFFIX_LENGTH = 20  # Number of chars to show from token end

# Exit Codes
EXIT_SUCCESS = 0
EXIT_FAILURE = 1
EXIT_INTERRUPTED = 130            # Standard exit code for Ctrl+C (128 + SIGINT)

# Styling for console output
SEPARATOR = "=" * 80
SUBSEP = "-" * 80


def print_header(text):
    """Print a styled header"""
    print(f"\n{SEPARATOR}")
    print(f"  {text}")
    print(f"{SEPARATOR}\n")


def print_request(method, url, headers=None, body=None):
    """Print full request details and return start time"""
    start_time = time.time()
    print(f"{SUBSEP}")
    print(f"REQUEST: {method} {url}")
    print(f"Time: {datetime.utcnow().isoformat()}Z")
    if headers:
        print("Headers:")
        for key, value in headers.items():
            # Mask authorization token
            if key.lower() == "authorization" and value:
                suffix = value[-TOKEN_DISPLAY_SUFFIX_LENGTH:] if len(value) > TOKEN_DISPLAY_SUFFIX_LENGTH else '***'
                print(f"  {key}: Bearer ***{suffix}***")
            else:
                print(f"  {key}: {value}")
    if body:
        print("Body:")
        print(json.dumps(body, indent=2))
    print(f"{SUBSEP}\n")
    return start_time


def print_response(response, start_time=None):
    """Print full response details including duration"""
    end_time = time.time()
    duration_ms = (end_time - start_time) * 1000 if start_time else None

    print(f"{SUBSEP}")
    print(f"RESPONSE: {response.status_code} {response.reason}")
    print(f"Time: {datetime.utcnow().isoformat()}Z")
    if duration_ms is not None:
        print(f"Duration: {duration_ms:.2f} ms")
    print("Headers:")
    for key, value in response.headers.items():
        print(f"  {key}: {value}")
    print("Body:")
    try:
        json_body = response.json()
        print(json.dumps(json_body, indent=2))
    except:
        print(response.text if response.text else "(empty)")
    print(f"{SUBSEP}\n")


def wait_for_rate_limit():
    """Wait to respect rate limits"""
    print(f"⏳ Waiting {RATE_LIMIT_DELAY} seconds (rate limit)...\n")
    time.sleep(RATE_LIMIT_DELAY)


def get_market_config():
    """Get market configuration including decimals"""
    try:
        print_header("Fetching Market Configuration")

        url = f"{API_BASE}/api/v1/market/info"
        start_time = print_request("GET", url)

        response = requests.get(url)
        print_response(response, start_time)

        if response.status_code == 200:
            data = response.json()
            config = {
                'settlement_decimals': data.get('settlement_decimals', DEFAULT_SETTLEMENT_DECIMALS),
                'trading_decimals': data.get('trading_decimals', DEFAULT_TRADING_DECIMALS),
                'settlement_token': f"{data['settlement_token']['shard']}.{data['settlement_token']['realm']}.{data['settlement_token']['num']}",
                'trading_pair': data.get('trading_pair', 'BTC-S')
            }
            print(f"✓ Market Config: {config['trading_pair']}")
            print(f"  Trading Decimals: {config['trading_decimals']}")
            print(f"  Settlement Decimals: {config['settlement_decimals']}")
            return config
        else:
            print("⚠ Failed to fetch config, using defaults")
            return {
                'settlement_decimals': DEFAULT_SETTLEMENT_DECIMALS,
                'trading_decimals': DEFAULT_TRADING_DECIMALS,
                'settlement_token': DEFAULT_SETTLEMENT_TOKEN,
                'trading_pair': 'BTC-S'
            }
    except Exception as e:
        print(f"✗ Exception fetching market config: {e}")
        print(f"Exception type: {type(e).__name__}")
        import traceback
        traceback.print_exc()
        # Return defaults
        return {
            'settlement_decimals': DEFAULT_SETTLEMENT_DECIMALS,
            'trading_decimals': DEFAULT_TRADING_DECIMALS,
            'settlement_token': DEFAULT_SETTLEMENT_TOKEN,
            'trading_pair': 'BTC-S'
        }


# HIP-820 Authentication helpers
def build_hip820(msg_bytes):
    """Build HIP-820 message wrapper"""
    prefix = b'\x19Hedera Signed Message:\n'
    length = str(len(msg_bytes)).encode('ascii')
    return prefix + length + b'\n' + msg_bytes


def encode_varint(value):
    """Encode integer as protobuf varint"""
    result = []
    while value > 0x7f:
        result.append((value & 0x7f) | 0x80)
        value >>= 7
    result.append(value & 0x7f)
    return bytes(result)


def build_signature_map(pub_key, signature):
    """Build protobuf SignatureMap"""
    sig_pair = b'\x0a' + encode_varint(len(pub_key)) + pub_key
    sig_pair += b'\x1a' + encode_varint(len(signature)) + signature
    return b'\x0a' + encode_varint(len(sig_pair)) + sig_pair


def authenticate(account_id, private_key_hex):
    """Authenticate with HIP-820 message signing"""
    try:
        from cryptography.hazmat.primitives import serialization
        from cryptography.hazmat.backends import default_backend

        print_header("STEP 1: Authentication")

        # Load private key
        private_key_der = bytes.fromhex(private_key_hex)
        private_key = serialization.load_der_private_key(
            private_key_der, password=None, backend=default_backend())
        public_key_bytes = private_key.public_key().public_bytes(
            encoding=serialization.Encoding.Raw,
            format=serialization.PublicFormat.Raw
        )

        # Step 1a: Request challenge
        url = f"{API_BASE}/api/v1/auth/challenge"
        body = {
            "account_id": account_id,
            "ledger_id": LEDGER_ID,
            "method": "message"
        }

        start_time = print_request("POST", url, {"Content-Type": "application/json"}, body)

        challenge_res = requests.post(url, json=body)
        print_response(challenge_res, start_time)

        if challenge_res.status_code != 200:
            print(f"✗ Challenge failed")
            return None

        challenge_data = challenge_res.json()
        print(f"✓ Challenge received: {challenge_data.get('challenge_id')}")

        wait_for_rate_limit()

        # Step 1b: Sign challenge and verify
        hip820_bytes = build_hip820(challenge_data["message"].encode('utf-8'))
        signature = private_key.sign(hip820_bytes)
        signature_map = build_signature_map(public_key_bytes, signature)

        url = f"{API_BASE}/api/v1/auth/verify"
        body = {
            "challenge_id": challenge_data["challenge_id"],
            "account_id": account_id,
            "message_signed_plain_text": challenge_data["message"],
            "signature_map_base64": base64.b64encode(signature_map).decode('utf-8'),
            "sig_type": "ed25519"
        }

        start_time = print_request("POST", url, {"Content-Type": "application/json"}, body)

        verify_res = requests.post(url, json=body)
        print_response(verify_res, start_time)

        if verify_res.status_code != 200:
            print(f"✗ Verify failed")
            return None

        token = verify_res.json()["access_token"]
        print(f"✓ Authenticated successfully")
        print(f"  Token (last {TOKEN_DISPLAY_SUFFIX_LENGTH} chars): ...{token[-TOKEN_DISPLAY_SUFFIX_LENGTH:]}")

        return token

    except Exception as e:
        print(f"\n✗ EXCEPTION in authenticate():")
        print(f"  Type: {type(e).__name__}")
        print(f"  Message: {str(e)}")
        import traceback
        print(f"  Traceback:")
        traceback.print_exc()
        return None


def get_portfolio_balance(token, account_id):
    """Get portfolio balance and available margin"""
    try:
        print_header("STEP 2: Checking Account Balance")

        url = f"{API_BASE}/api/v1/account/balance"
        headers = {
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/json"
        }
        body = {
            "account_id": account_id,
            "owner_type": "hapi"  # lowercase per API's snake_case_lower serialization
        }

        start_time = print_request("POST", url, headers, body)

        response = requests.post(url, json=body, headers=headers)
        print_response(response, start_time)

        if response.status_code == 200:
            data = response.json()
            balance_base = data.get('balance', 0)
            owner_id = data.get('owner_id', '')

            portfolio_info = {
                'current_balance_base': balance_base,
                'owner_id': owner_id
            }

            print(f"✓ Balance retrieved")
            print(f"  Owner ID: {owner_id}")
            print(f"  Available Balance: {balance_base} base units")

            return portfolio_info
        elif response.status_code == 404:
            print("⚠ Portfolio endpoint not implemented yet (404)")
            print("  Using configured balance assumption for testing")
            # Assume a balance if the endpoint isn't available
            # This is a temporary workaround for testing
            settlement_decimals = DEFAULT_SETTLEMENT_DECIMALS
            balance_base = int(ASSUMED_BALANCE_S_TOKENS * (10 ** settlement_decimals))

            portfolio_info = {
                'current_balance_base': balance_base,
                'margin_equity_base': balance_base,
                'unrealized_profit_base': 0,
                'unrealized_loss_base': 0,
                'orders_count': 0,
                'positions_count': 0
            }

            print(f"  Assumed Balance: {balance_base} base units ({ASSUMED_BALANCE_S_TOKENS} S tokens)")
            return portfolio_info
        else:
            print(f"⚠ Failed to fetch portfolio (status {response.status_code})")
            return None

    except Exception as e:
        print(f"✗ Exception fetching portfolio: {e}")
        print(f"Exception type: {type(e).__name__}")
        import traceback
        traceback.print_exc()
        return None


def calculate_order_parameters(balance_base, settlement_decimals, trading_decimals):
    """Calculate safe order parameters based on available balance
    
    NOTE: The 'margin' parameter is a margin FACTOR (fraction), not an absolute amount!
    Formula: requiredBalance = (quantity * price * marginFactor) / (scalingMultipliers)
    """
    try:
        print_header("Calculating Order Parameters")

        # Convert balance from base units to human-readable
        balance_s_tokens = balance_base / (10 ** settlement_decimals)

        print(f"Available Balance: {balance_s_tokens:.6f} S tokens ({balance_base} base units)")

        # Determine how much of our balance we want to use for this order
        target_balance_use = balance_base * ORDER_SIZE_FACTOR

        print(f"Target Balance Usage: {ORDER_SIZE_FACTOR * 100:.1f}% = {target_balance_use / (10 ** settlement_decimals):.6f} S tokens")

        # Working backwards from desired balance usage:
        # requiredBalance = (quantity * price * marginFactor) / (10^settlement_decimals * 10^trading_decimals)
        # So: quantity = requiredBalance * (10^settlement_decimals * 10^trading_decimals) / (price * marginFactor)

        price_base = int(ORDER_PRICE_USD * (10 ** trading_decimals))
        margin_factor_base = INITIAL_MARGIN_FACTOR

        scaling_multipliers = (10 ** settlement_decimals) * (10 ** trading_decimals)

        # Calculate quantity
        quantity_base = int((target_balance_use * scaling_multipliers) / (price_base * margin_factor_base))

        # Calculate actual required balance
        required_balance = (quantity_base * price_base * margin_factor_base + scaling_multipliers - 1) // scaling_multipliers

        # Convert to human-readable units
        quantity_contracts = quantity_base / (10 ** trading_decimals)
        required_balance_s_tokens = required_balance / (10 ** settlement_decimals)
        margin_factor_fraction = margin_factor_base / (10 ** settlement_decimals)
        notional_value_s_tokens = (quantity_base * price_base) / scaling_multipliers / (10 ** settlement_decimals)

        # Validate we have enough balance
        if required_balance > balance_base:
            print(f"⚠ WARNING: Insufficient balance!")
            print(f"  Required: {required_balance_s_tokens:.6f} S tokens")
            print(f"  Available: {balance_s_tokens:.6f} S tokens")
            return None

        order_params = {
            'quantity_base': quantity_base,
            'price_base': price_base,
            'margin_factor_base': margin_factor_base,
            'quantity_contracts': quantity_contracts,
            'required_balance': required_balance,
            'required_balance_s_tokens': required_balance_s_tokens,
            'margin_factor_fraction': margin_factor_fraction,
            'notional_value_s_tokens': notional_value_s_tokens
        }

        print(f"✓ Order Parameters Calculated:")
        print(f"  Quantity: {quantity_contracts:.8f} BTC contracts (base: {quantity_base})")
        print(f"  Price: ${ORDER_PRICE_USD:,.2f} (base: {price_base})")
        print(f"  Margin Factor: {margin_factor_fraction:.2f}x (base: {margin_factor_base})")
        print(f"  Notional Value: ~${notional_value_s_tokens:.6f} S tokens")
        print(f"  Required Balance: {required_balance_s_tokens:.6f} S tokens (base: {required_balance})")
        print(f"  Effective Leverage: ~{1/margin_factor_fraction:.2f}x")

        return order_params

    except Exception as e:
        print(f"✗ Exception calculating order parameters: {e}")
        import traceback
        traceback.print_exc()
        return None


def submit_limit_order(token, account_id, order_params):
    """Submit a limit order with calculated parameters"""
    try:
        print_header("STEP 3: Submit Limit Order")

        # Extract calculated parameters
        price_base = order_params['price_base']
        quantity_base = order_params['quantity_base']
        margin_factor_base = order_params['margin_factor_base']

        print(f"Submitting Order:")
        print(f"  Side: {'Long/Buy' if ORDER_SIDE == 0 else 'Short/Sell'}")
        print(f"  Type: {'Market' if ORDER_KIND == 0 else 'Limit'}")
        print(f"  Price: ${ORDER_PRICE_USD:,.2f} (base: {price_base})")
        print(f"  Quantity: {order_params['quantity_contracts']:.8f} contracts (base: {quantity_base})")
        print(f"  Margin Factor: {order_params['margin_factor_fraction']:.2f}x (base: {margin_factor_base})")
        print(f"  Estimated Cost: ~${order_params['required_balance_s_tokens']:.6f} S tokens")
        print()

        url = f"{API_BASE}/api/v1/market/order/submit"
        headers = {
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/json",
            "Idempotency-Key": str(uuid.uuid4())
        }
        body = {
            "client_order_id": f"test-{int(time.time())}",
            "kind": ORDER_KIND,
            "margin": margin_factor_base,  # This is the margin FACTOR, not absolute amount
            "account": {
                "account_id": account_id,
                "owner_type": "hapi"  # lowercase per API's snake_case_lower serialization
            },
            "price": price_base,
            "quantity": quantity_base,
            "side": ORDER_SIDE,
            "time_in_force": ORDER_TIME_IN_FORCE
        }

        start_time = print_request("POST", url, headers, body)

        response = requests.post(url, json=body, headers=headers)
        print_response(response, start_time)

        if response.status_code in [200, 201]:
            data = response.json()
            order_id = data.get('order_id')
            order_status = data.get('order_status', 'Unknown')
            quantity_filled = data.get('quantity_filled', 0)
            trade_id = data.get('trade_id')
            position_ids = data.get('position_ids', [])

            print(f"✓ Order submitted successfully")
            print(f"  Order ID: {order_id}")
            print(f"  Status: {order_status}")
            print(f"  Quantity Filled: {quantity_filled}")
            if trade_id:
                print(f"  Trade ID: {trade_id}")
            if position_ids:
                print(f"  Position IDs: {', '.join(str(pid) for pid in position_ids)}")

            return order_id
        else:
            print(f"✗ Order submission failed")
            return None

    except Exception as e:
        print(f"\n✗ EXCEPTION in submit_limit_order():")
        print(f"  Type: {type(e).__name__}")
        print(f"  Message: {str(e)}")
        import traceback
        print(f"  Traceback:")
        traceback.print_exc()
        return None


def get_portfolio(token, account_id):
    """Get portfolio (orders and positions)"""
    try:
        print_header("STEP 4: Get Account Details (View Orders)")

        url = f"{API_BASE}/api/v1/account"
        headers = {
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/json"
        }
        body = {
            "account_id": account_id,
            "owner_type": "hapi"  # lowercase per API's snake_case_lower serialization
        }

        start_time = print_request("POST", url, headers, body)

        response = requests.post(url, json=body, headers=headers)
        print_response(response, start_time)

        if response.status_code == 200:
            data = response.json()
            orders = data.get('orders', [])
            positions = data.get('positions', [])
            balance = data.get('balance', 0)

            print(f"✓ Account details retrieved")
            print(f"  Account ID: {data.get('account_id')}")
            print(f"  Balance: {balance} base units")
            print(f"  Open Orders: {len(orders)}")
            print(f"  Open Positions: {len(positions)}")

            if orders:
                print(f"\n  Order Details:")
                for order in orders:
                    print(f"    Order ID: {order.get('order_id')}")
                    print(f"    Side: {order.get('contract_side')}")
                    print(f"    Price: {order.get('price')}")
                    print(f"    Quantity: {order.get('quatity')}")  # Note: API has typo "quatity"
                    print(f"    Margin: {order.get('margin')}")

            return data
        else:
            print(f"✗ Failed to get account details")
            return None

    except Exception as e:
        print(f"\n✗ EXCEPTION in get_portfolio():")
        print(f"  Type: {type(e).__name__}")
        print(f"  Message: {str(e)}")
        import traceback
        print(f"  Traceback:")
        traceback.print_exc()
        return None


def cancel_order(token, order_id):
    """Cancel an order"""
    try:
        print_header("STEP 5: Cancel Order")

        url = f"{API_BASE}/api/v1/market/order/cancel"
        headers = {
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/json",
            "Idempotency-Key": str(uuid.uuid4())
        }
        body = {
            "order_id": str(order_id)  # Ensure it's a string GUID
        }

        start_time = print_request("POST", url, headers, body)

        response = requests.post(url, json=body, headers=headers)
        print_response(response, start_time)

        if response.status_code == 200:
            data = response.json()
            print(f"✓ Order canceled successfully")
            print(f"  Order ID: {data.get('order_id')}")
            print(f"  Unfilled Quantity: {data.get('unfilled_quantity', 0)}")
            return True
        else:
            print(f"✗ Order cancellation failed")
            return False

    except Exception as e:
        print(f"\n✗ EXCEPTION in cancel_order():")
        print(f"  Type: {type(e).__name__}")
        print(f"  Message: {str(e)}")
        import traceback
        print(f"  Traceback:")
        traceback.print_exc()
        return False


def main():
    """Main execution flow"""
    print(f"\n{SEPARATOR}")
    print(f"  Order Lifecycle Test Script")
    print(f"  Account: {ACCOUNT_ID}")
    print(f"  Ledger: {LEDGER_ID}")
    print(f"  API: {API_BASE}")
    print(f"{SEPARATOR}\n")

    try:
        # Get market configuration
        config = get_market_config()
        wait_for_rate_limit()

        # Step 1: Authenticate
        token = authenticate(ACCOUNT_ID, PRIVATE_KEY_DER_HEX)
        if not token:
            print(f"\n✗ FAILED: Could not authenticate")
            return EXIT_FAILURE

        wait_for_rate_limit()

        # Step 2: Check portfolio balance
        portfolio_info = get_portfolio_balance(token, ACCOUNT_ID)
        if not portfolio_info:
            print(f"\n✗ FAILED: Could not retrieve balance")
            return EXIT_FAILURE

        # Calculate order parameters based on available balance
        order_params = calculate_order_parameters(
            portfolio_info['current_balance_base'],
            config['settlement_decimals'],
            config['trading_decimals']
        )

        if not order_params:
            print(f"\n✗ FAILED: Insufficient balance to place order")
            return EXIT_FAILURE

        wait_for_rate_limit()

        # Step 3: Submit limit order
        order_id = submit_limit_order(token, ACCOUNT_ID, order_params)
        if not order_id:
            print(f"\n✗ FAILED: Could not submit order")
            return EXIT_FAILURE

        wait_for_rate_limit()

        # Step 4: Get account details to view order
        portfolio = get_portfolio(token, ACCOUNT_ID)
        if not portfolio:
            print(f"\n⚠ WARNING: Could not retrieve account details")

        wait_for_rate_limit()

        # Step 5: Cancel the order
        canceled = cancel_order(token, order_id)
        if not canceled:
            print(f"\n⚠ WARNING: Could not cancel order")

        # Final summary
        print_header("TEST SUMMARY")
        print(f"✓ Step 1 - Authentication: SUCCESS")
        print(f"✓ Step 2 - Balance Check: SUCCESS ({portfolio_info['current_balance_base']} base units)")
        print(f"✓ Step 2 - Order Calculation: SUCCESS")
        print(f"    Quantity: {order_params['quantity_contracts']:.8f} BTC")
        print(f"    Margin Factor: {order_params['margin_factor_fraction']:.2f}x")
        print(f"    Required Balance: {order_params['required_balance_s_tokens']:.6f} S")
        print(f"✓ Step 3 - Order Submission: {'SUCCESS' if order_id else 'FAILED'}")
        print(f"    Order ID: {order_id if order_id else 'N/A'}")
        print(f"✓ Step 4 - Account View: {'SUCCESS' if portfolio else 'FAILED'}")
        print(f"✓ Step 5 - Order Cancellation: {'SUCCESS' if canceled else 'FAILED'}")
        print(f"\n{SEPARATOR}\n")

        return EXIT_SUCCESS

    except Exception as e:
        print(f"\n✗ FATAL EXCEPTION in main():")
        print(f"  Type: {type(e).__name__}")
        print(f"  Message: {str(e)}")
        import traceback
        print(f"  Full Traceback:")
        traceback.print_exc()
        return EXIT_FAILURE


if __name__ == "__main__":
    try:
        exit_code = main()
        exit(exit_code)
    except KeyboardInterrupt:
        print(f"\n\n⚠ Interrupted by user (Ctrl+C)")
        exit(EXIT_INTERRUPTED)
    except Exception as e:
        print(f"\n✗ UNHANDLED EXCEPTION:")
        print(f"  Type: {type(e).__name__}")
        print(f"  Message: {str(e)}")
        import traceback
        print(f"  Full Traceback:")
        traceback.print_exc()
        exit(EXIT_FAILURE)


#!/usr/bin/env python3

"""
Get My Orders - Complete Script with Full Authentication
Authenticates with the API and retrieves all open orders and positions.
"""

import requests
import base64
import json
import time
from datetime import datetime

# ============================================================================
# CONFIGURATION
# ============================================================================

API_BASE = "https://perps-api-d7cff5fhd9g0b7c4.eastus-01.azurewebsites.net"
LEDGER_ID = "testnet"
ACCOUNT_ID = "0.0.6978377"
PRIVATE_KEY_DER_HEX = "302e020100300506032b6570042204205db3a68cb7831bcefb625238e7800cc9dc85aab09b2acf97537af0d9ef667d7b"

# Decimal scaling for display
TRADING_DECIMALS = 8
SETTLEMENT_DECIMALS = 6

REQUEST_TIMEOUT = 30

# ============================================================================
# HIP-820 AUTHENTICATION HELPERS
# ============================================================================

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

# ============================================================================
# AUTHENTICATION
# ============================================================================

def authenticate(account_id, private_key_hex):
    """
    Complete authentication flow: challenge + verify
    Returns JWT token or None if authentication failed
    """
    try:
        from cryptography.hazmat.primitives import serialization
        from cryptography.hazmat.backends import default_backend

        print("=" * 80)
        print("AUTHENTICATION")
        print("=" * 80)
        print(f"Account: {account_id}")
        print(f"Ledger: {LEDGER_ID}\n")

        # Load private key
        print("Loading private key...")
        private_key_der = bytes.fromhex(private_key_hex)
        private_key = serialization.load_der_private_key(
            private_key_der, password=None, backend=default_backend())
        public_key_bytes = private_key.public_key().public_bytes(
            encoding=serialization.Encoding.Raw,
            format=serialization.PublicFormat.Raw
        )
        print("✓ Private key loaded\n")

        # STEP 1: Request challenge
        print("Step 1: Requesting authentication challenge...")
        url = f"{API_BASE}/api/v1/auth/challenge"
        body = {
            "account_id": account_id,
            "ledger_id": LEDGER_ID,
            "method": "message"
        }

        response = requests.post(url, json=body, timeout=REQUEST_TIMEOUT)

        if response.status_code != 200:
            print(f"✗ Challenge failed: {response.status_code}")
            print(f"Response: {response.text}")
            return None

        challenge_data = response.json()
        challenge_id = challenge_data['challenge_id']
        message = challenge_data['message']

        print(f"✓ Challenge received: {challenge_id}")
        print(f"  Expires at: {challenge_data.get('expires_at_utc', 'N/A')}\n")

        # STEP 2: Sign challenge and verify
        print("Step 2: Signing challenge...")
        hip820_bytes = build_hip820(message.encode('utf-8'))
        signature = private_key.sign(hip820_bytes)
        signature_map = build_signature_map(public_key_bytes, signature)
        print("✓ Challenge signed\n")

        print("Step 3: Verifying signature...")
        url = f"{API_BASE}/api/v1/auth/verify"
        body = {
            "challenge_id": challenge_id,
            "account_id": account_id,
            "message_signed_plain_text": message,
            "signature_map_base64": base64.b64encode(signature_map).decode('utf-8'),
            "sig_type": "ed25519"
        }

        response = requests.post(url, json=body, timeout=REQUEST_TIMEOUT)

        if response.status_code != 200:
            print(f"✗ Verify failed: {response.status_code}")
            print(f"Response: {response.text}")
            return None

        verify_data = response.json()
        token = verify_data['access_token']
        expires_in = verify_data.get('expires_in', 'unknown')

        print(f"✓ Authentication successful!")
        print(f"  Token expires in: {expires_in} seconds")
        print(f"  Token (last 20 chars): ...{token[-20:]}\n")

        return token

    except ImportError:
        print("✗ Missing required library: cryptography")
        print("  Install with: pip install cryptography")
        return None

    except Exception as e:
        print(f"✗ Authentication error: {type(e).__name__}: {e}")
        import traceback
        traceback.print_exc()
        return None

# ============================================================================
# GET ORDERS
# ============================================================================

def get_my_orders(token, account_id):
    """
    Get all open orders and positions for the account
    Returns account data dict or None if request failed
    """
    try:
        print("=" * 80)
        print("FETCHING ORDERS AND POSITIONS")
        print("=" * 80)
        print(f"Account: {account_id}\n")

        url = f"{API_BASE}/api/v1/account?accountId={account_id}&ownerType=Hapi"
        headers = {
            "Authorization": f"Bearer {token}"
        }

        print(f"Request: GET {url}")
        print(f"Time: {datetime.utcnow().isoformat()}Z\n")

        response = requests.get(url, headers=headers, timeout=REQUEST_TIMEOUT)

        print(f"Response: {response.status_code} {response.reason}")
        print(f"Time: {datetime.utcnow().isoformat()}Z\n")

        if response.status_code != 200:
            print(f"✗ Request failed: {response.status_code}")
            print(f"Response body:")
            try:
                print(json.dumps(response.json(), indent=2))
            except:
                print(response.text)
            return None

        data = response.json()
        print("✓ Account data retrieved\n")

        return data

    except requests.exceptions.Timeout:
        print(f"✗ Request timeout after {REQUEST_TIMEOUT} seconds")
        return None

    except requests.exceptions.RequestException as e:
        print(f"✗ Request error: {e}")
        return None

    except json.JSONDecodeError as e:
        print(f"✗ Invalid JSON response: {e}")
        print(f"Raw response: {response.text}")
        return None

    except Exception as e:
        print(f"✗ Error: {type(e).__name__}: {e}")
        import traceback
        traceback.print_exc()
        return None

# ============================================================================
# DISPLAY FUNCTIONS
# ============================================================================

def format_price(price_base):
    """Convert base units to USD"""
    return price_base / (10 ** TRADING_DECIMALS)

def format_quantity(qty_base):
    """Convert base units to BTC"""
    return qty_base / (10 ** TRADING_DECIMALS)

def format_balance(balance_base):
    """Convert base units to S tokens"""
    return balance_base / (10 ** SETTLEMENT_DECIMALS)

def display_account_data(data):
    """Display account data in human-readable format"""

    # Account summary
    print("=" * 80)
    print("ACCOUNT SUMMARY")
    print("=" * 80)

    owner_id = data.get('owner_id', 'N/A')
    account_id = data.get('account_id', 'N/A')
    balance = data.get('balance', 0)
    evm_address = data.get('evm_address', None)

    print(f"Owner ID: {owner_id}")
    print(f"Account ID: {account_id}")
    if evm_address:
        print(f"EVM Address: {evm_address}")
    print(f"Balance: {format_balance(balance):.6f} S tokens ({balance} base units)")
    print()

    # Orders
    orders = data.get('orders', [])
    print(f"{'='*80}")
    print(f"OPEN ORDERS: {len(orders)}")
    print(f"{'='*80}")

    if orders:
        print(f"{'Order ID':<38} {'Side':<6} {'Quantity (BTC)':<16} {'Price (USD)':<15} {'Margin':<10}")
        print("-" * 95)

        for order in orders:
            order_id = order.get('order_id', 'N/A')
            side = order.get('contract_side', 'unknown').upper()
            quantity = order.get('quantity', 0)
            price = order.get('price', 0)
            margin = order.get('margin', 0)

            qty_btc = format_quantity(quantity)
            price_usd = format_price(price)
            margin_factor = margin / (10 ** SETTLEMENT_DECIMALS)

            print(f"{order_id:<38} {side:<6} {qty_btc:<16.8f} ${price_usd:<14,.2f} {margin_factor:.2f}x")

        # Calculate totals
        total_buy_qty = sum(format_quantity(o['quantity']) for o in orders if o.get('contract_side') == 'long')
        total_sell_qty = sum(format_quantity(o['quantity']) for o in orders if o.get('contract_side') == 'short')

        print("-" * 95)
        print(f"Total BUY quantity:  {total_buy_qty:.8f} BTC")
        print(f"Total SELL quantity: {total_sell_qty:.8f} BTC")
    else:
        print("No open orders")

    print()

    # Positions
    positions = data.get('positions', [])
    print(f"{'='*80}")
    print(f"OPEN POSITIONS: {len(positions)}")
    print(f"{'='*80}")

    if positions:
        print(f"{'Position ID':<38} {'Side':<6} {'Quantity (BTC)':<16} {'Entry Price':<15} {'Margin':<10}")
        print("-" * 95)

        for position in positions:
            pos_id = position.get('postion_id', 'N/A')  # Note: API has typo "postion"
            side = position.get('contract_side', 'unknown').upper()
            quantity = position.get('quantity', 0)
            price = position.get('price', 0)
            margin = position.get('margin', 0)
            index = position.get('index', 0)

            qty_btc = format_quantity(quantity)
            price_usd = format_price(price)
            margin_factor = margin / (10 ** SETTLEMENT_DECIMALS)

            print(f"{pos_id:<38} {side:<6} {qty_btc:<16.8f} ${price_usd:<14,.2f} {margin_factor:.2f}x")

        # Calculate net position
        long_qty = sum(format_quantity(p['quantity']) for p in positions if p.get('contract_side') == 'long')
        short_qty = sum(format_quantity(p['quantity']) for p in positions if p.get('contract_side') == 'short')
        net_position = long_qty - short_qty

        print("-" * 95)
        print(f"Long positions:  {long_qty:.8f} BTC")
        print(f"Short positions: {short_qty:.8f} BTC")
        print(f"Net position:    {net_position:+.8f} BTC")
    else:
        print("No open positions")

    print(f"{'='*80}\n")

# ============================================================================
# MAIN
# ============================================================================

def main():
    """Main execution"""
    print("\n" + "=" * 80)
    print("GET MY ORDERS - Complete Authentication Example")
    print("=" * 80)
    print(f"Started: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    print("=" * 80 + "\n")

    try:
        # Step 1: Authenticate
        token = authenticate(ACCOUNT_ID, PRIVATE_KEY_DER_HEX)
        if not token:
            print("\n✗ FAILED: Could not authenticate")
            return 1

        # Small delay between requests
        time.sleep(0.5)

        # Step 2: Get orders
        account_data = get_my_orders(token, ACCOUNT_ID)
        if not account_data:
            print("\n✗ FAILED: Could not retrieve account data")
            return 1

        # Step 3: Display results
        display_account_data(account_data)

        # Save to file
        filename = f"my_orders_{datetime.now().strftime('%Y%m%d_%H%M%S')}.json"
        with open(filename, 'w') as f:
            json.dump(account_data, f, indent=2)
        print(f"✓ Raw data saved to: {filename}\n")

        print("=" * 80)
        print("SUCCESS - All operations completed")
        print("=" * 80 + "\n")

        return 0

    except KeyboardInterrupt:
        print("\n\n⚠ Interrupted by user (Ctrl+C)")
        return 130

    except Exception as e:
        print(f"\n✗ FATAL ERROR: {type(e).__name__}: {e}")
        import traceback
        traceback.print_exc()
        return 1

if __name__ == "__main__":
    exit(main())
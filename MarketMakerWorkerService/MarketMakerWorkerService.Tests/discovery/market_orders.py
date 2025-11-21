#!/usr/bin/env python3

"""
Multi-Order Market Test Script - Raw HTTP Style
Submits 2 long and 2 short market orders with full HTTP visibility
"""

import requests
import base64
import uuid
import time
import json
import sys
from datetime import datetime
from typing import Optional, Dict, Any, List, Tuple

# ============================================================================
# CONFIGURATION
# ============================================================================

API_BASE = "https://perps-api-d7cff5fhd9g0b7c4.eastus-01.azurewebsites.net"
LEDGER_ID = "testnet"
ACCOUNT_ID = "0.0.6978377"
PRIVATE_KEY_DER_HEX = "302e020100300506032b6570042204205db3a68cb7831bcefb625238e7800cc9dc85aab09b2acf97537af0d9ef667d7b"

# Order Parameters
ORDER_KIND = "market"
ORDER_TIME_IN_FORCE = "good_until_filled"

# Margin Configuration
INITIAL_MARGIN_FACTOR = 1200000

# Order Sizing
TOTAL_ORDER_SIZE_FACTOR = 0.40
ORDERS_PER_SIDE = 2

# Fallback
ASSUMED_BALANCE_S_TOKENS = 40.0

# Decimals
DEFAULT_TRADING_DECIMALS = 8
DEFAULT_SETTLEMENT_DECIMALS = 6
DEFAULT_SETTLEMENT_TOKEN = "0.0.6891795"

# HTTP Configuration
REQUEST_TIMEOUT = 30

# Styling
SEPARATOR = "=" * 100
SUBSEP = "-" * 100

# ============================================================================
# RAW HTTP LOGGING
# ============================================================================

def print_raw_request(method: str, url: str, headers: Dict, body: Optional[Dict] = None):
    """Print raw HTTP request with full URL"""
    print(f"\n{SEPARATOR}")
    print(f">>> RAW HTTP REQUEST")
    print(f"{SEPARATOR}")
    
    # Show full URL prominently
    print(f"Full URL: {url}")
    print()
    
    # Extract path from URL
    path = url.replace(API_BASE, "")
    if not path:
        path = "/"
    
    # HTTP request line
    print(f"{method} {path} HTTP/1.1")
    
    # Extract host from URL
    host = url.replace("https://", "").replace("http://", "").split("/")[0]
    print(f"Host: {host}")
    
    # Headers
    for key, value in headers.items():
        print(f"{key}: {value}")
    
    # Body
    if body:
        body_json = json.dumps(body, indent=2)
        print(f"Content-Length: {len(body_json)}")
        print()
        print(body_json)
    
    print(f"{SEPARATOR}\n")

def print_raw_response(response: requests.Response, duration_ms: float):
    """Print raw HTTP response"""
    print(f"\n{SEPARATOR}")
    print(f"<<< RAW HTTP RESPONSE")
    print(f"{SEPARATOR}")
    print(f"HTTP/1.1 {response.status_code} {response.reason}")
    print(f"Duration: {duration_ms:.2f}ms")
    print()
    
    for key, value in response.headers.items():
        print(f"{key}: {value}")
    
    print()
    
    try:
        response_json = response.json()
        print(json.dumps(response_json, indent=2))
    except:
        print(response.text if response.text else "(empty body)")
    
    print(f"{SEPARATOR}\n")

def http_request(method: str, url: str, headers: Optional[Dict] = None, 
                 body: Optional[Dict] = None) -> Optional[requests.Response]:
    """Make HTTP request with raw logging - NO RETRIES"""
    
    if headers is None:
        headers = {}
    
    # Print request
    print_raw_request(method, url, headers, body)
    
    try:
        start_time = time.time()
        
        if method.upper() == 'GET':
            response = requests.get(url, headers=headers, timeout=REQUEST_TIMEOUT)
        elif method.upper() == 'POST':
            response = requests.post(url, json=body, headers=headers, timeout=REQUEST_TIMEOUT)
        elif method.upper() == 'DELETE':
            response = requests.delete(url, headers=headers, timeout=REQUEST_TIMEOUT)
        else:
            print(f"✗ Unsupported method: {method}")
            return None
        
        duration_ms = (time.time() - start_time) * 1000
        
        # Print response
        print_raw_response(response, duration_ms)
        
        return response
    
    except requests.exceptions.Timeout as e:
        print(f"\n✗ REQUEST TIMEOUT: {e}\n")
        return None
    except requests.exceptions.ConnectionError as e:
        print(f"\n✗ CONNECTION ERROR: {e}\n")
        return None
    except Exception as e:
        print(f"\n✗ EXCEPTION: {type(e).__name__}: {e}\n")
        return None

# ============================================================================
# MARKET CONFIG
# ============================================================================

def get_market_config() -> Dict[str, Any]:
    """Get market configuration"""
    print(f"\n{'='*50}")
    print("STEP: Get Market Configuration")
    print(f"{'='*50}")
    
    url = f"{API_BASE}/api/v1/market/info"
    response = http_request('GET', url)
    
    if response and response.status_code == 200:
        data = response.json()
        config = {
            'settlement_decimals': data.get('settlement_decimals', DEFAULT_SETTLEMENT_DECIMALS),
            'trading_decimals': data.get('trading_decimals', DEFAULT_TRADING_DECIMALS),
            'settlement_token': data.get('settlement_token', DEFAULT_SETTLEMENT_TOKEN),
        }
        print(f"✓ Config retrieved: Settlement={config['settlement_decimals']}, Trading={config['trading_decimals']}")
        return config
    
    print("⚠ Using default config")
    return {
        'settlement_decimals': DEFAULT_SETTLEMENT_DECIMALS,
        'trading_decimals': DEFAULT_TRADING_DECIMALS,
        'settlement_token': DEFAULT_SETTLEMENT_TOKEN,
    }

# ============================================================================
# AUTHENTICATION
# ============================================================================

def build_hip820(msg_bytes: bytes) -> bytes:
    prefix = b'\x19Hedera Signed Message:\n'
    length = str(len(msg_bytes)).encode('ascii')
    return prefix + length + b'\n' + msg_bytes

def encode_varint(value: int) -> bytes:
    result = []
    while value > 0x7f:
        result.append((value & 0x7f) | 0x80)
        value >>= 7
    result.append(value & 0x7f)
    return bytes(result)

def build_signature_map(pub_key: bytes, signature: bytes) -> bytes:
    sig_pair = b'\x0a' + encode_varint(len(pub_key)) + pub_key
    sig_pair += b'\x1a' + encode_varint(len(signature)) + signature
    return b'\x0a' + encode_varint(len(sig_pair)) + sig_pair

def authenticate(account_id: str, private_key_hex: str) -> Optional[str]:
    """Authenticate with HIP-820"""
    print(f"\n{'='*50}")
    print("STEP: Authentication")
    print(f"{'='*50}")
    
    try:
        from cryptography.hazmat.primitives import serialization
        from cryptography.hazmat.backends import default_backend
        
        private_key_der = bytes.fromhex(private_key_hex)
        private_key = serialization.load_der_private_key(
            private_key_der, password=None, backend=default_backend())
        public_key_bytes = private_key.public_key().public_bytes(
            encoding=serialization.Encoding.Raw,
            format=serialization.PublicFormat.Raw
        )
        
        # Challenge
        url = f"{API_BASE}/api/v1/auth/challenge"
        headers = {"Content-Type": "application/json"}
        body = {"account_id": account_id, "ledger_id": LEDGER_ID, "method": "message"}
        
        response = http_request('POST', url, headers, body)
        if not response or response.status_code != 200:
            print("✗ Challenge failed")
            return None
        
        challenge_data = response.json()
        challenge_id = challenge_data['challenge_id']
        message = challenge_data['message']
        
        # Sign
        hip820_bytes = build_hip820(message.encode('utf-8'))
        signature = private_key.sign(hip820_bytes)
        signature_map = build_signature_map(public_key_bytes, signature)
        
        # Verify
        url = f"{API_BASE}/api/v1/auth/verify"
        body = {
            "challenge_id": challenge_id,
            "account_id": account_id,
            "message_signed_plain_text": message,
            "signature_map_base64": base64.b64encode(signature_map).decode('utf-8'),
            "sig_type": "ed25519"
        }
        
        response = http_request('POST', url, headers, body)
        if not response or response.status_code != 200:
            print("✗ Verify failed")
            return None
        
        token = response.json()['access_token']
        print(f"✓ Authenticated - Token: ...{token[-20:]}")
        return token
    
    except Exception as e:
        print(f"✗ Auth exception: {e}")
        return None

# ============================================================================
# BALANCE
# ============================================================================

def get_balance(token: str, account_id: str, settlement_decimals: int) -> Optional[int]:
    """Get account balance"""
    print(f"\n{'='*50}")
    print("STEP: Get Balance")
    print(f"{'='*50}")
    
    url = f"{API_BASE}/api/v1/account/balance?accountId={account_id}&ownerType=Hapi"
    headers = {"Authorization": f"Bearer {token}"}
    
    response = http_request('GET', url, headers)
    
    if response and response.status_code == 200:
        balance_base = response.json().get('balance', 0)
        balance_tokens = balance_base / (10**settlement_decimals)
        print(f"✓ Balance: {balance_base} base units ({balance_tokens:.6f} tokens)")
        return balance_base
    elif response and response.status_code == 404:
        balance_base = int(ASSUMED_BALANCE_S_TOKENS * (10 ** settlement_decimals))
        print(f"⚠ Using assumed balance: {balance_base} base units")
        return balance_base
    
    print("✗ Failed to get balance")
    return None

# ============================================================================
# ORDER SUBMISSION
# ============================================================================

def submit_market_order(token: str, account_id: str, side: str, quantity_base: int,
                        margin_factor_base: int, order_num: int) -> Optional[Dict]:
    """Submit a single market order"""
    
    client_order_id = f"market-{side}-{order_num}-{int(time.time())}"
    idempotency_key = str(uuid.uuid4())
    
    print(f"\n{'='*50}")
    print(f"SUBMITTING ORDER #{order_num}: {side.upper()} MARKET")
    print(f"{'='*50}")
    print(f"Client Order ID: {client_order_id}")
    print(f"Quantity: {quantity_base} base units")
    print(f"Side: {side}")
    print(f"Kind: {ORDER_KIND}")
    
    url = f"{API_BASE}/api/v1/order/submit"
    headers = {
        "Authorization": f"Bearer {token}",
        "Content-Type": "application/json",
        "Idempotency-Key": idempotency_key
    }
    
    # CRITICAL: Market orders MUST have price = 0
    body = {
        "client_order_id": client_order_id,
        "kind": ORDER_KIND,
        "margin": margin_factor_base,
        "account": {"account_id": account_id, "owner_type": "hapi"},
        "price": 0,  # MUST BE 0 FOR MARKET ORDERS
        "quantity": quantity_base,
        "side": side,
        "time_in_force": ORDER_TIME_IN_FORCE
    }
    
    response = http_request('POST', url, headers, body)
    
    if response and response.status_code in [200, 201]:
        data = response.json()
        order_id = data.get('order_id')
        order_status = data.get('order_status', 'unknown')
        quantity_filled = data.get('quantity_filled', 0)
        
        print(f"✓ Order submitted: {order_id}")
        print(f"  Status: {order_status}")
        print(f"  Filled: {quantity_filled}")
        return data
    
    print(f"✗ Order #{order_num} failed")
    return None

def submit_multiple_orders(token: str, account_id: str, balance_base: int,
                           settlement_decimals: int, trading_decimals: int) -> List[Dict]:
    """Submit 2 long and 2 short market orders"""
    
    print(f"\n{'='*50}")
    print("SUBMITTING MULTIPLE MARKET ORDERS")
    print(f"{'='*50}")
    
    total_allocation = balance_base * TOTAL_ORDER_SIZE_FACTOR
    allocation_per_order = total_allocation / 4
    
    # For market orders, we don't need price - quantity is calculated based on available balance
    margin_factor_base = INITIAL_MARGIN_FACTOR
    
    # Simple quantity calculation for market orders
    # We'll let the orderbook determine actual fill price
    quantity_per_order_base = int(allocation_per_order / 2)  # Conservative estimate
    
    print(f"Total Balance: {balance_base} base units")
    print(f"Total Allocation: {TOTAL_ORDER_SIZE_FACTOR * 100}% = {total_allocation / (10**settlement_decimals):.6f} tokens")
    print(f"Orders: 2 longs + 2 shorts = 4 total")
    print(f"Quantity per order: {quantity_per_order_base} base ({quantity_per_order_base / (10**trading_decimals):.8f} BTC)")
    print(f"Margin Factor: {margin_factor_base / (10**settlement_decimals):.2f}x")
    print(f"Order Kind: {ORDER_KIND}")
    print(f"⚠ Market orders execute at best available price")
    
    submitted_orders = []
    
    # Submit 2 LONG orders
    for i in range(1, ORDERS_PER_SIDE + 1):
        result = submit_market_order(token, account_id, "long", quantity_per_order_base,
                                     margin_factor_base, i)
        if result:
            result['side'] = 'long'
            submitted_orders.append(result)
        time.sleep(0.5)
    
    # Submit 2 SHORT orders
    for i in range(1, ORDERS_PER_SIDE + 1):
        result = submit_market_order(token, account_id, "short", quantity_per_order_base,
                                     margin_factor_base, i)
        if result:
            result['side'] = 'short'
            submitted_orders.append(result)
        time.sleep(0.5)
    
    print(f"\n✓ Submitted {len(submitted_orders)}/4 orders")
    return submitted_orders

# ============================================================================
# VIEW ACCOUNT
# ============================================================================

def get_account_orders(token: str, account_id: str) -> Optional[Dict]:
    """Get account orders and positions"""
    print(f"\n{'='*50}")
    print("STEP: View Account Orders & Positions")
    print(f"{'='*50}")
    
    url = f"{API_BASE}/api/v1/account?accountId={account_id}&ownerType=Hapi"
    headers = {"Authorization": f"Bearer {token}"}
    
    response = http_request('GET', url, headers)
    
    if response and response.status_code == 200:
        data = response.json()
        orders = data.get('orders', [])
        positions = data.get('positions', [])
        
        print(f"✓ Account Status:")
        print(f"  Open Orders: {len(orders)}")
        print(f"  Open Positions: {len(positions)}")
        
        if orders:
            print(f"\n  Orders:")
            for order in orders:
                print(f"    - {order.get('order_id')}: {order.get('contract_side')} {order.get('quantity')} @ {order.get('price')}")
        
        if positions:
            print(f"\n  Positions:")
            for pos in positions:
                print(f"    - {pos.get('postion_id')}: {pos.get('contract_side')} {pos.get('quantity')}")
        
        return data
    
    print("✗ Failed to get account")
    return None

# ============================================================================
# CANCEL ORDERS
# ============================================================================

def cancel_order(token: str, order_id: str) -> bool:
    """Cancel a single order"""
    print(f"\n{'='*50}")
    print(f"CANCELLING ORDER: {order_id}")
    print(f"{'='*50}")
    
    idempotency_key = str(uuid.uuid4())
    url = f"{API_BASE}/api/v1/order/cancel?orderId={order_id}"
    headers = {
        "Authorization": f"Bearer {token}",
        "Idempotency-Key": idempotency_key
    }
    
    response = http_request('DELETE', url, headers)
    
    if response and response.status_code == 200:
        print(f"✓ Cancelled: {order_id}")
        return True
    
    print(f"✗ Failed to cancel: {order_id}")
    return False

def cancel_all_orders(token: str, account_id: str):
    """Cancel all open orders"""
    print(f"\n{'='*50}")
    print("CANCELLING ALL OPEN ORDERS")
    print(f"{'='*50}")
    
    account_data = get_account_orders(token, account_id)
    if not account_data:
        print("Could not get account orders")
        return
    
    orders = account_data.get('orders', [])
    if not orders:
        print("No orders to cancel")
        return
    
    print(f"\nCancelling {len(orders)} order(s)...")
    for order in orders:
        order_id = order.get('order_id')
        if order_id:
            cancel_order(token, order_id)
            time.sleep(0.3)

# ============================================================================
# MAIN
# ============================================================================

def main() -> int:
    print(f"\n{SEPARATOR}")
    print(f"  MULTI-ORDER MARKET TEST - RAW HTTP MODE WITH FULL URLS")
    print(f"  Account: {ACCOUNT_ID}")
    print(f"  Orders: 2 longs + 2 shorts (MARKET orders)")
    print(f"  Price: 0 (required for market orders)")
    print(f"  NO RETRIES - Single attempt per request")
    print(f"{SEPARATOR}")
    
    try:
        # Config
        config = get_market_config()
        
        # Auth
        token = authenticate(ACCOUNT_ID, PRIVATE_KEY_DER_HEX)
        if not token:
            print("\n✗ FAILED: Authentication")
            return 1
        
        # Balance
        balance_base = get_balance(token, ACCOUNT_ID, config['settlement_decimals'])
        if not balance_base:
            print("\n✗ FAILED: Get balance")
            return 1
        
        # Submit orders
        submitted_orders = submit_multiple_orders(
            token, ACCOUNT_ID, balance_base,
            config['settlement_decimals'], config['trading_decimals']
        )
        
        if not submitted_orders:
            print("\n✗ FAILED: No orders submitted")
            return 1
        
        # View orders
        print("\n--- Waiting 2 seconds ---")
        time.sleep(2)
        get_account_orders(token, ACCOUNT_ID)
        
        # Cancel all (if any limit orders remain)
        print("\n--- Waiting 2 seconds ---")
        time.sleep(2)
        cancel_all_orders(token, ACCOUNT_ID)
        
        # Final check
        print("\n--- Waiting 1 second ---")
        time.sleep(1)
        get_account_orders(token, ACCOUNT_ID)
        
        # Summary
        print(f"\n{SEPARATOR}")
        print(f"  TEST COMPLETE")
        print(f"{SEPARATOR}")
        print(f"✓ Submitted {len(submitted_orders)}/4 orders")
        for i, order in enumerate(submitted_orders, 1):
            print(f"  {i}. {order.get('side', 'unknown').upper()}: {order.get('order_id')} - Status: {order.get('order_status')} - Filled: {order.get('quantity_filled', 0)}")
        print(f"{SEPARATOR}\n")
        
        return 0
    
    except KeyboardInterrupt:
        print("\n\n⚠ INTERRUPTED BY USER")
        return 130
    
    except Exception as e:
        print(f"\n\n✗ FATAL EXCEPTION: {type(e).__name__}: {e}")
        import traceback
        traceback.print_exc()
        return 1

if __name__ == "__main__":
    sys.exit(main())
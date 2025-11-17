#!/usr/bin/env python3
"""
Cancel All Orders Script
Authenticates with the API and cancels all open orders for the account.
"""

import sys
import time
import json
import base64
import requests
from datetime import datetime
from typing import Dict, Optional, List, Any

# ============================================================================
# CONFIGURATION
# ============================================================================

# API Configuration
API_BASE = "https://perps-api-d7cff5fhd9g0b7c4.eastus-01.azurewebsites.net"
LEDGER_ID = "testnet"
ACCOUNT_ID = "0.0.6978377"
PRIVATE_KEY_DER_HEX = "302e020100300506032b6570042204205db3a68cb7831bcefb625238e7800cc9dc85aab09b2acf97537af0d9ef667d7b"

# Request Configuration
REQUEST_TIMEOUT = 30
MAX_RETRIES = 3

# ============================================================================
# UTILITIES
# ============================================================================

def print_header(title: str, width: int = 80):
    """Print a formatted section header"""
    print("\n" + "=" * width)
    print(title.center(width))
    print("=" * width)

def print_subheader(title: str, width: int = 80):
    """Print a formatted subsection header"""
    print("\n" + "=" * width)
    print(title)
    print("=" * width)

def print_request_details(method: str, url: str, headers: Dict, body: Any = None):
    """Print detailed request information"""
    print(f"\n📤 REQUEST:")
    print(f"  Method: {method}")
    print(f"  URL: {url}")
    print(f"  Headers:")
    for key, value in headers.items():
        if key.lower() == "authorization":
            print(f"    {key}: Bearer ...{value[-20:] if len(value) > 20 else value}")
        else:
            print(f"    {key}: {value}")
    if body:
        print(f"  Body: {json.dumps(body, indent=4)}")

def print_response_details(response: requests.Response):
    """Print detailed response information"""
    print(f"\n📥 RESPONSE:")
    print(f"  Status: {response.status_code} {response.reason}")
    print(f"  Headers:")
    for key, value in response.headers.items():
        print(f"    {key}: {value}")
    try:
        response_json = response.json()
        print(f"  Body: {json.dumps(response_json, indent=4)}")
    except:
        if response.text:
            print(f"  Body (raw): {response.text}")
        else:
            print(f"  Body: (empty)")

def safe_request(method: str, url: str, **kwargs) -> requests.Response:
    """Make HTTP request with retry logic and error handling"""
    kwargs.setdefault('timeout', REQUEST_TIMEOUT)

    for attempt in range(MAX_RETRIES):
        try:
            response = requests.request(method, url, **kwargs)
            return response
        except requests.exceptions.Timeout:
            if attempt < MAX_RETRIES - 1:
                print(f"  ⚠️  Request timeout, retrying ({attempt + 1}/{MAX_RETRIES})...")
                continue
            raise
        except requests.exceptions.RequestException as e:
            if attempt < MAX_RETRIES - 1:
                print(f"  ⚠️  Request failed: {e}, retrying ({attempt + 1}/{MAX_RETRIES})...")
                continue
            raise

    raise Exception("Max retries exceeded")

def print_server_error(response: requests.Response):
    """Print detailed server error information"""
    try:
        error_data = response.json()

        # RFC 7807 Problem Details format
        if isinstance(error_data, dict):
            print(f"    Error Type: {error_data.get('type', 'N/A')}")
            print(f"    Title: {error_data.get('title', 'N/A')}")
            print(f"    Status: {error_data.get('status', response.status_code)}")
            print(f"    Detail: {error_data.get('detail', 'No details provided')}")
            print(f"    Code: {error_data.get('code', 'N/A')}")

            if 'errors' in error_data:
                print("    Validation Errors:")
                for field, messages in error_data['errors'].items():
                    for msg in messages:
                        print(f"      - {field}: {msg}")
        else:
            print(f"    Response: {error_data}")
    except json.JSONDecodeError:
        print(f"    Response (non-JSON): {response.text}")
    except Exception as e:
        print(f"    Could not parse error: {e}")

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

def authenticate() -> Optional[str]:
    """Complete HIP-820 authentication flow"""
    try:
        from cryptography.hazmat.primitives import serialization
        from cryptography.hazmat.backends import default_backend

        print_subheader("AUTHENTICATION")
        print(f"Account: {ACCOUNT_ID}")
        print(f"Ledger: {LEDGER_ID}")

        # Load private key
        print("\nLoading private key...")
        private_key_der = bytes.fromhex(PRIVATE_KEY_DER_HEX)
        private_key = serialization.load_der_private_key(
            private_key_der, password=None, backend=default_backend())
        public_key_bytes = private_key.public_key().public_bytes(
            encoding=serialization.Encoding.Raw,
            format=serialization.PublicFormat.Raw
        )
        print("✓ Private key loaded")

        # STEP 1: Request challenge
        print("\nStep 1: Requesting authentication challenge...")
        url = f"{API_BASE}/api/v1/auth/challenge"
        body = {
            "account_id": ACCOUNT_ID,
            "ledger_id": LEDGER_ID,
            "method": "message"
        }

        response = safe_request("POST", url, json=body)
        if response.status_code != 200:
            print(f"✗ Challenge failed: {response.status_code}")
            print(f"Response: {response.text}")
            return None

        challenge_data = response.json()
        challenge_id = challenge_data['challenge_id']
        message = challenge_data['message']
        print(f"✓ Challenge received: {challenge_id}")

        # STEP 2: Sign challenge and verify
        print("\nStep 2: Signing challenge...")
        hip820_bytes = build_hip820(message.encode('utf-8'))
        signature = private_key.sign(hip820_bytes)
        signature_map = build_signature_map(public_key_bytes, signature)
        print("✓ Challenge signed")

        print("\nStep 3: Verifying signature...")
        url = f"{API_BASE}/api/v1/auth/verify"
        body = {
            "challenge_id": challenge_id,
            "account_id": ACCOUNT_ID,
            "message_signed_plain_text": message,
            "signature_map_base64": base64.b64encode(signature_map).decode('utf-8'),
            "sig_type": "ed25519"
        }

        response = safe_request("POST", url, json=body)
        if response.status_code != 200:
            print(f"✗ Verify failed: {response.status_code}")
            print(f"Response: {response.text}")
            return None

        verify_data = response.json()
        token = verify_data['access_token']
        expires_in = verify_data.get('expires_in', 'unknown')
        print(f"✓ Authentication successful!")
        print(f"  Token expires in: {expires_in} seconds")
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
# ORDER MANAGEMENT
# ============================================================================

def get_open_orders(jwt_token: str) -> Optional[List[Dict[str, Any]]]:
    """Fetch all open orders for the account"""
    print_subheader("FETCHING OPEN ORDERS")
    print(f"Account: {ACCOUNT_ID}")

    url = f"{API_BASE}/api/v1/account?accountId={ACCOUNT_ID}&ownerType=Hapi"
    headers = {
        "Authorization": f"Bearer {jwt_token}",
        "Content-Type": "application/json"
    }

    print(f"\nRequest: GET {url}")

    try:
        response = safe_request("GET", url, headers=headers)
        print(f"Response: {response.status_code} {response.reason}")

        if response.status_code == 200:
            data = response.json()
            orders = data.get('orders', [])
            print(f"\n✓ Found {len(orders)} open order(s)")
            return orders
        else:
            print(f"✗ Failed to fetch orders")
            print_server_error(response)
            return None
    except Exception as e:
        print(f"✗ Error fetching orders: {e}")
        return None

def cancel_order(jwt_token: str, order_id: str) -> Dict[str, Any]:
    """Cancel a single order"""
    # Generate unique idempotency key for this cancellation
    idempotency_key = f"cancel_{order_id}_{int(time.time() * 1000)}"

    # Try with PascalCase OrderId (ASP.NET default for records with [AsParameters])
    url = f"{API_BASE}/api/v1/order/cancel?OrderId={order_id}"
    headers = {
        "Authorization": f"Bearer {jwt_token}",
        "Content-Type": "application/json",
        "Idempotency-Key": idempotency_key
    }

    try:
        print_request_details("DELETE", url, headers)
        response = safe_request("DELETE", url, headers=headers)
        print_response_details(response)

        if response.status_code == 200:
            data = response.json()
            return {
                'success': True,
                'order_id': data.get('order_id'),
                'unfilled_quantity': data.get('unfilled_quantity', 0)
            }
        else:
            print_server_error(response)
            return {
                'success': False,
                'error': f"{response.status_code} - {response.reason}",
                'order_id': order_id
            }
    except Exception as e:
        return {
            'success': False,
            'error': str(e),
            'order_id': order_id
        }

def cancel_all_orders(jwt_token: str, orders: List[Dict[str, Any]]):
    """Cancel all orders in the list"""
    print_subheader("CANCELLING ALL ORDERS")
    print(f"Total orders to cancel: {len(orders)}\n")

    results = {
        'successful': [],
        'failed': []
    }

    for idx, order in enumerate(orders, 1):
        order_id = order.get('order_id', order.get('id', 'unknown'))
        side = order.get('contract_side', order.get('side', 'UNKNOWN')).upper()
        quantity = order.get('quantity', 0)
        price = order.get('price', 0)

        print(f"\n[{idx}/{len(orders)}] {side}")
        print(f"  Cancelling order: {order_id}")

        result = cancel_order(jwt_token, order_id)

        if result['success']:
            print(f"  ✓ Cancelled successfully")
            print(f"    Unfilled quantity: {result['unfilled_quantity']}")
            results['successful'].append(result)
        else:
            print(f"  ✗ Failed: {result.get('error', 'Unknown error')}")
            results['failed'].append(result)

    return results

# ============================================================================
# MAIN
# ============================================================================

def main():
    """Main execution flow"""
    print_header("CANCEL ALL MY ORDERS")
    print(f"Started: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    print("=" * 80)

    # Step 1: Authenticate
    jwt_token = authenticate()
    if not jwt_token:
        print("\n✗ Authentication failed. Exiting.")
        sys.exit(1)

    # Step 2: Get open orders
    orders = get_open_orders(jwt_token)
    if orders is None:
        print("\n✗ Failed to fetch orders. Exiting.")
        sys.exit(1)

    if len(orders) == 0:
        print("\n✓ No open orders to cancel.")
        sys.exit(0)

    # Display orders
    print("\nOpen Orders:")
    print(f"{'Order ID':<38} {'Side':<7} {'Quantity':<15} {'Price':<15}")
    print("-" * 80)
    for order in orders:
        order_id = order.get('order_id', order.get('id', 'unknown'))
        side = order.get('contract_side', order.get('side', 'UNKNOWN')).upper()
        quantity = order.get('quantity', 0) / 100000000  # Convert to BTC
        price = order.get('price', 0) / 100000000  # Convert to dollars (8 decimals for trading)
        print(f"{order_id:<38} {side:<7} {quantity:<15.8f} ${price:<14.2f}")

    # Step 3: Cancel all orders (no confirmation)
    results = cancel_all_orders(jwt_token, orders)

    # Step 4: Print summary
    print_subheader("SUMMARY")
    print(f"Total orders processed: {len(orders)}")
    print(f"✓ Successfully cancelled: {len(results['successful'])}")
    print(f"✗ Failed to cancel: {len(results['failed'])}")

    if results['failed']:
        print("\nFailed Orders:")
        for failed in results['failed']:
            print(f"  - {failed['order_id']}: {failed.get('error', 'Unknown error')}")

    print("\n" + "=" * 80)
    print("Done!")
    print("=" * 80)

if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("\n\n✗ Cancelled by user")
        sys.exit(1)
    except Exception as e:
        print(f"\n\n✗ Unexpected error: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)
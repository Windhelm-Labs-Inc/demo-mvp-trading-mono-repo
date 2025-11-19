#!/usr/bin/env python3
"""
Settle All Open Positions
Fetches all open positions and settles them completely
"""

import base64
import requests
import json
import uuid
from datetime import datetime

# Configuration
API_BASE = "https://perps-api-d7cff5fhd9g0b7c4.eastus-01.azurewebsites.net"
ACCOUNT_ID = "0.0.6993636"
PRIVATE_KEY_DER_HEX = "302e020100300506032b65700422042068dc0ee90deccf7437110283103e64d96f2f32d4e280a278682fdefc41b8d2e6"
LEDGER_ID = "testnet"

# HIP-820 helpers
def build_hip820(msg_bytes):
    """Build HIP-820 formatted message"""
    prefix = b'\x19Hedera Signed Message:\n'
    length = str(len(msg_bytes)).encode('ascii')
    return prefix + length + b'\n' + msg_bytes

def encode_varint(value):
    """Encode value as protobuf varint"""
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

def log_request(method, url, headers, body=None):
    """Log full HTTP request"""
    print(f"\n{method} {url}")
    print(f"Time: {datetime.utcnow().isoformat()}Z")
    for key, value in headers.items():
        print(f"{key}: {value if key.lower() != 'authorization' else 'Bearer ***'}")
    if body:
        print()
        print(json.dumps(body, indent=2))

def log_response(response):
    """Log full HTTP response"""
    print(f"\nHTTP {response.status_code} {response.reason}")
    print(f"Time: {datetime.utcnow().isoformat()}Z")
    print()
    try:
        print(json.dumps(response.json(), indent=2))
    except:
        print(response.text if response.text else "(empty)")
    print()

# Authenticate
def authenticate():
    """Authenticate using HIP-820 flow"""
    from cryptography.hazmat.primitives import serialization
    from cryptography.hazmat.backends import default_backend

    print("=" * 80)
    print("AUTHENTICATION")
    print("=" * 80)
    print(f"Account: {ACCOUNT_ID}")
    print(f"Ledger: {LEDGER_ID}")

    private_key_der = bytes.fromhex(PRIVATE_KEY_DER_HEX)
    private_key = serialization.load_der_private_key(
        private_key_der, password=None, backend=default_backend())
    public_key_bytes = private_key.public_key().public_bytes(
        encoding=serialization.Encoding.Raw,
        format=serialization.PublicFormat.Raw
    )

    # Step 1: Get challenge
    url = f"{API_BASE}/api/v1/auth/challenge"
    body = {
        "account_id": ACCOUNT_ID,
        "ledger_id": LEDGER_ID,
        "method": "message"
    }
    headers = {"Content-Type": "application/json"}

    log_request("POST", url, headers, body)
    response = requests.post(url, json=body, headers=headers)
    log_response(response)

    if response.status_code != 200:
        print(f"✗ Challenge request failed: {response.status_code}")
        return None

    challenge_data = response.json()
    challenge_id = challenge_data['challenge_id']
    message = challenge_data['message']

    # Step 2: Sign and verify
    hip820_bytes = build_hip820(message.encode('utf-8'))
    signature = private_key.sign(hip820_bytes)
    signature_map = build_signature_map(public_key_bytes, signature)

    url = f"{API_BASE}/api/v1/auth/verify"
    body = {
        "challenge_id": challenge_id,
        "account_id": ACCOUNT_ID,
        "message_signed_plain_text": message,
        "signature_map_base64": base64.b64encode(signature_map).decode('utf-8'),
        "sig_type": "ed25519"
    }
    headers = {"Content-Type": "application/json"}

    log_request("POST", url, headers, body)
    response = requests.post(url, json=body, headers=headers)
    log_response(response)

    if response.status_code != 200:
        print(f"✗ Verify request failed: {response.status_code}")
        return None

    print("✓ Authentication successful")
    return response.json()['access_token']

def get_account_positions(token):
    """Get all positions for the account"""
    print("\n" + "=" * 80)
    print("FETCHING ACCOUNT POSITIONS")
    print("=" * 80)

    url = f"{API_BASE}/api/v1/account?accountId={ACCOUNT_ID}&ownerType=Hapi"
    headers = {
        "Authorization": f"Bearer {token}",
        "Content-Type": "application/json"
    }

    log_request("GET", url, headers)
    response = requests.get(url, headers=headers)
    log_response(response)

    if response.status_code != 200:
        print(f"✗ Failed to get account data: {response.status_code}")
        return None

    account_data = response.json()
    positions = account_data.get('positions', [])
    
    print(f"\n✓ Found {len(positions)} position(s)")
    
    for i, pos in enumerate(positions, 1):
        position_id = pos.get('postion_id')  # Note: API has typo "postion_id"
        side = pos.get('contract_side', 'unknown')
        quantity = pos.get('quantity', 0)
        entry_price = pos.get('entry_price', 0)
        
        print(f"\nPosition {i}:")
        print(f"  ID: {position_id}")
        print(f"  Side: {side}")
        print(f"  Quantity: {quantity}")
        print(f"  Entry Price: {entry_price}")
    
    return positions

def settle_positions(token, positions):
    """Settle all positions with balanced long/short quantities"""
    if not positions:
        print("\n✓ No positions to settle")
        return True

    print("\n" + "=" * 80)
    print("SETTLING POSITIONS")
    print("=" * 80)

    # Separate positions by side
    long_positions = []
    short_positions = []
    
    for pos in positions:
        side = pos.get('contract_side', '').lower()
        quantity = pos.get('quantity', 0)
        
        if quantity > 0:
            if side == 'long':
                long_positions.append(pos)
            elif side == 'short':
                short_positions.append(pos)
    
    # Calculate total quantities
    total_long_qty = sum(p.get('quantity', 0) for p in long_positions)
    total_short_qty = sum(p.get('quantity', 0) for p in short_positions)
    
    print(f"\nPosition Summary:")
    print(f"  Long positions: {len(long_positions)} ({total_long_qty:,} total quantity)")
    print(f"  Short positions: {len(short_positions)} ({total_short_qty:,} total quantity)")
    
    if total_long_qty == 0 or total_short_qty == 0:
        print("\n⚠ Cannot settle: Need both long and short positions")
        return True
    
    # Calculate how much we can settle (must be balanced)
    max_settleable = min(total_long_qty, total_short_qty)
    
    print(f"\n⚠ Settlement requires balanced long/short quantities")
    print(f"  Maximum settleable: {max_settleable:,} units from each side")
    
    if max_settleable == 0:
        print("\n✓ No settleable quantity")
        return True
    
    # Build settlement request with balanced quantities
    settlement_quantities = []
    remaining_to_settle = max_settleable
    
    # Settle shorts first (complete positions if possible)
    for pos in short_positions:
        if remaining_to_settle == 0:
            break
        position_id = pos.get('postion_id')
        quantity = min(pos.get('quantity', 0), remaining_to_settle)
        settlement_quantities.append({
            "position_id": position_id,
            "quantity": quantity
        })
        remaining_to_settle -= quantity
    
    # Reset for longs
    remaining_to_settle = max_settleable
    
    # Settle longs to match
    for pos in long_positions:
        if remaining_to_settle == 0:
            break
        position_id = pos.get('postion_id')
        quantity = min(pos.get('quantity', 0), remaining_to_settle)
        settlement_quantities.append({
            "position_id": position_id,
            "quantity": quantity
        })
        remaining_to_settle -= quantity

    if not settlement_quantities:
        print("\n✓ No positions to settle")
        return True
    
    # Verify balance
    total_long_settled = sum(s['quantity'] for s in settlement_quantities 
                             if any(p.get('postion_id') == s['position_id'] and p.get('contract_side') == 'long' 
                                   for p in long_positions))
    total_short_settled = sum(s['quantity'] for s in settlement_quantities 
                              if any(p.get('postion_id') == s['position_id'] and p.get('contract_side') == 'short' 
                                    for p in short_positions))
    
    print(f"\nSettlement Plan:")
    print(f"  Long quantity to settle: {total_long_settled:,}")
    print(f"  Short quantity to settle: {total_short_settled:,}")
    print(f"  Balanced: {'✓' if total_long_settled == total_short_settled else '✗'}")
    
    if total_long_settled != total_short_settled:
        print("\n✗ ERROR: Settlement quantities not balanced!")
        return False

    url = f"{API_BASE}/api/v1/position/settle"
    body = {
        "settlement_quantities": settlement_quantities
    }
    
    # Generate idempotency key (required by API to prevent duplicate settlements)
    idempotency_key = str(uuid.uuid4())
    
    headers = {
        "Authorization": f"Bearer {token}",
        "Content-Type": "application/json",
        "Idempotency-Key": idempotency_key
    }

    print(f"\nSettling {len(settlement_quantities)} position(s)...")
    print(f"Idempotency-Key: {idempotency_key}")
    log_request("POST", url, headers, body)
    response = requests.post(url, json=body, headers=headers)
    log_response(response)

    if response.status_code == 200:
        result = response.json()
        settlement_id = result.get('settlement_id')
        print(f"\n✓ Settlement successful!")
        print(f"  Settlement ID: {settlement_id}")
        return True
    else:
        print(f"\n✗ Settlement failed: {response.status_code}")
        return False

def main():
    """Main execution"""
    print("\n" + "=" * 80)
    print("SETTLE ALL POSITIONS SCRIPT")
    print("=" * 80)
    print(f"Started: {datetime.now().isoformat()}")
    print()

    # Step 1: Authenticate
    token = authenticate()
    if not token:
        print("\n✗ Authentication failed. Exiting.")
        return 1

    # Step 2: Get positions
    positions = get_account_positions(token)
    if positions is None:
        print("\n✗ Failed to fetch positions. Exiting.")
        return 1

    # Step 3: Settle positions
    if not settle_positions(token, positions):
        print("\n✗ Settlement failed. Exiting.")
        return 1

    print("\n" + "=" * 80)
    print(f"✓ COMPLETED SUCCESSFULLY")
    print("=" * 80)
    print(f"Finished: {datetime.now().isoformat()}")
    return 0

if __name__ == "__main__":
    exit(main())


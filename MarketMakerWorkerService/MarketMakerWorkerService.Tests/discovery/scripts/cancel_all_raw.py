#!/usr/bin/env python3
"""
Cancel all orders with full request/response logging
"""

import base64
import uuid

import requests
import json
import time
from datetime import datetime

# Configuration
API_BASE = "https://perps-api-d7cff5fhd9g0b7c4.eastus-01.azurewebsites.net"
# ACCOUNT_ID = "0.0.6978377"
# PRIVATE_KEY_DER_HEX = "302e020100300506032b6570042204205db3a68cb7831bcefb625238e7800cc9dc85aab09b2acf97537af0d9ef667d7b"
LEDGER_ID = "testnet"

ACCOUNT_ID = "0.0.6993636"
PRIVATE_KEY_DER_HEX = "302e020100300506032b65700422042068dc0ee90deccf7437110283103e64d96f2f32d4e280a278682fdefc41b8d2e6"

# HIP-820 helpers
def build_hip820(msg_bytes):
    prefix = b'\x19Hedera Signed Message:\n'
    length = str(len(msg_bytes)).encode('ascii')
    return prefix + length + b'\n' + msg_bytes

def encode_varint(value):
    result = []
    while value > 0x7f:
        result.append((value & 0x7f) | 0x80)
        value >>= 7
    result.append(value & 0x7f)
    return bytes(result)

def build_signature_map(pub_key, signature):
    sig_pair = b'\x0a' + encode_varint(len(pub_key)) + pub_key
    sig_pair += b'\x1a' + encode_varint(len(signature)) + signature
    return b'\x0a' + encode_varint(len(sig_pair)) + sig_pair

def log_request(method, url, headers, body=None):
    """Log full HTTP request"""
    print(f"\n{method} {url}")
    print(f"Time: {datetime.utcnow().isoformat()}Z")
    for key, value in headers.items():
        print(f"{key}: {value}")
    if body:
        print()
        print(json.dumps(body, indent=2))

def log_response(response):
    """Log full HTTP response"""
    print(f"\nHTTP {response.status_code} {response.reason}")
    print(f"Time: {datetime.utcnow().isoformat()}Z")
    for key, value in response.headers.items():
        print(f"{key}: {value}")
    print()
    try:
        print(json.dumps(response.json(), indent=2))
    except:
        print(response.text if response.text else "(empty)")
    print()

# Authenticate
def authenticate():
    from cryptography.hazmat.primitives import serialization
    from cryptography.hazmat.backends import default_backend

    print(f"\nAuthentication Flow Started: {datetime.now().isoformat()}")
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

    return response.json()['access_token']

# Get orders
def get_orders(token):
    print(f"\nFetching Orders")

    url = f"{API_BASE}/api/v1/account?accountId={ACCOUNT_ID}&ownerType=Hapi"
    headers = {
        "Authorization": f"Bearer {token}",
        "Content-Type": "application/json"
    }

    log_request("GET", url, headers)
    response = requests.get(url, headers=headers)
    log_response(response)

    if response.status_code == 200:
        data = response.json()
        return data.get('orders', [])
    return []

# Cancel order
def cancel_order(token, order_id, order_num, total_orders):
    print(f"\nCancelling Order {order_num}/{total_orders}: {order_id}")

    idempotency_key = str(uuid.uuid4())
    url = f"{API_BASE}/api/v1/order/cancel?orderId={order_id}"
    headers = {
        "Authorization": f"Bearer {token}",
        "Idempotency-Key": idempotency_key
    }

    log_request("DELETE", url, headers)
    response = requests.delete(url, headers=headers)
    log_response(response)

    return response.status_code == 200

# Main
print(f"Started: {datetime.now().isoformat()}")
print(f"Cancel All Orders Script")
print(f"Account: {ACCOUNT_ID}")

# Authenticate
token = authenticate()

# Get orders
orders = get_orders(token)
print(f"\nFound {len(orders)} orders to cancel")

if len(orders) == 0:
    print("No orders to cancel")
else:
    # Cancel each order
    successful = 0
    failed = 0

    for idx, order in enumerate(orders, 1):
        order_id = order.get('order_id', order.get('id'))
        if cancel_order(token, order_id, idx, len(orders)):
            successful += 1
        else:
            failed += 1

    print(f"\nSummary:")
    print(f"Total orders: {len(orders)}")
    print(f"Successful: {successful}")
    print(f"Failed: {failed}")

print(f"\nCompleted: {datetime.now().isoformat()}")
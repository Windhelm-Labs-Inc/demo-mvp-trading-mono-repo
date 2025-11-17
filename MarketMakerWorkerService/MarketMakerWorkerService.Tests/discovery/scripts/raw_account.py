#!/usr/bin/env python3
"""
Get raw account data with authentication
"""

import base64
import requests
import json

# Configuration
API_BASE = "https://perps-api-d7cff5fhd9g0b7c4.eastus-01.azurewebsites.net"
ACCOUNT_ID = "0.0.6978377"
LEDGER_ID = "testnet"
PRIVATE_KEY_DER_HEX = "302e020100300506032b6570042204205db3a68cb7831bcefb625238e7800cc9dc85aab09b2acf97537af0d9ef667d7b"

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

# Authenticate
def authenticate():
    from cryptography.hazmat.primitives import serialization
    from cryptography.hazmat.backends import default_backend

    private_key_der = bytes.fromhex(PRIVATE_KEY_DER_HEX)
    private_key = serialization.load_der_private_key(
        private_key_der, password=None, backend=default_backend())
    public_key_bytes = private_key.public_key().public_bytes(
        encoding=serialization.Encoding.Raw,
        format=serialization.PublicFormat.Raw
    )

    # Get challenge
    response = requests.post(f"{API_BASE}/api/v1/auth/challenge", json={
        "account_id": ACCOUNT_ID,
        "ledger_id": LEDGER_ID,
        "method": "message"
    })
    challenge_data = response.json()
    challenge_id = challenge_data['challenge_id']
    message = challenge_data['message']

    # Sign and verify
    hip820_bytes = build_hip820(message.encode('utf-8'))
    signature = private_key.sign(hip820_bytes)
    signature_map = build_signature_map(public_key_bytes, signature)

    response = requests.post(f"{API_BASE}/api/v1/auth/verify", json={
        "challenge_id": challenge_id,
        "account_id": ACCOUNT_ID,
        "message_signed_plain_text": message,
        "signature_map_base64": base64.b64encode(signature_map).decode('utf-8'),
        "sig_type": "ed25519"
    })
    return response.json()['access_token']

# Get account data
token = authenticate()
response = requests.get(
    f"{API_BASE}/api/v1/account?accountId={ACCOUNT_ID}&ownerType=Hapi",
    headers={"Authorization": f"Bearer {token}"}
)

# Print raw JSON
print(json.dumps(response.json(), indent=2))
#!/usr/bin/env python3

import requests
import base64
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.backends import default_backend

# Configuration
API_BASE = "https://perps-api-d7cff5fhd9g0b7c4.eastus-01.azurewebsites.net"
LEDGER_ID = "testnet"

ACCOUNT_ID = "0.0.6993636"
PRIVATE_KEY_DER_HEX = "302e020100300506032b65700422042068dc0ee90deccf7437110283103e64d96f2f32d4e280a278682fdefc41b8d2e6"

TOKEN_DECIMALS = 8

# Key helpers
def load_private_key(der_hex):
    return serialization.load_der_private_key(
        bytes.fromhex(der_hex), password=None, backend=default_backend()
    )

def get_public_key_bytes(private_key):
    return private_key.public_key().public_bytes(
        encoding=serialization.Encoding.Raw,
        format=serialization.PublicFormat.Raw
    )

# HIP-820 signing
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
def authenticate(account_id, private_key_hex):
    print(f"Authenticating {account_id}...")

    private_key = load_private_key(private_key_hex)
    public_key_bytes = get_public_key_bytes(private_key)

    # Get challenge
    challenge_res = requests.post(
        f"{API_BASE}/api/v1/auth/challenge",
        json={"account_id": account_id, "ledger_id": LEDGER_ID, "method": "message"}
    )

    if challenge_res.status_code != 200:
        print(f"Challenge failed: {challenge_res.status_code}")
        return None

    challenge_data = challenge_res.json()
    challenge_id = challenge_data["challenge_id"]
    canonical_message = challenge_data["message"]

    # Sign with HIP-820
    hip820_bytes = build_hip820(canonical_message.encode('utf-8'))
    signature = private_key.sign(hip820_bytes)
    signature_map = build_signature_map(public_key_bytes, signature)
    signature_map_base64 = base64.b64encode(signature_map).decode('utf-8')

    # Verify
    verify_res = requests.post(
        f"{API_BASE}/api/v1/auth/verify",
        json={
            "challenge_id": challenge_id,
            "account_id": account_id,
            "message_signed_plain_text": canonical_message,
            "signature_map_base64": signature_map_base64,
            "sig_type": "ed25519"
        }
    )

    if verify_res.status_code != 200:
        print(f"Verify failed: {verify_res.status_code}")
        return None

    token = verify_res.json()["access_token"]
    print(f"✓ Authenticated (expires in {verify_res.json()['expires_in']}s)")
    return token

# Check balance
def check_balance(access_token, account_id):
    res = requests.post(
        f"{API_BASE}/api/v1/account/balance",
        json={"account_id": account_id, "owner_type": "Hapi"},
        headers={"Authorization": f"Bearer {access_token}"}
    )

    if res.status_code == 200:
        balance = res.json().get('balance', 0)
        balance_tokens = balance / (10 ** TOKEN_DECIMALS)
        print(f"✓ Balance: {balance_tokens:.8f} tokens ({balance} base units)")
        return balance
    else:
        print(f"✗ Balance check failed: {res.status_code}")
        print(res.text)
        return None

# Main
if __name__ == "__main__":
    token = authenticate(ACCOUNT_ID, PRIVATE_KEY_DER_HEX)
    if token:
        check_balance(token, ACCOUNT_ID)
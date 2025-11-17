#!/usr/bin/env python3
"""
Hedera Deposit Script - CORRECTED VERSION with Rate Limiting
Fixed: Settlement token has 6 decimals, not 8
Added: 10 second delays between API calls to respect rate limits
"""

import requests
import base64
import uuid
import time
from hedera import PrivateKey

# Configuration
API_BASE = "https://perps-api-d7cff5fhd9g0b7c4.eastus-01.azurewebsites.net"
LEDGER_ID = "testnet"
#ACCOUNT_ID = "0.0.6978377"
#PRIVATE_KEY_DER_HEX = "302e020100300506032b6570042204205db3a68cb7831bcefb625238e7800cc9dc85aab09b2acf97537af0d9ef667d7b"
ACCOUNT_ID = "0.0.6993643"
PRIVATE_KEY_DER_HEX = "302e020100300506032b657004220420a0ea0985865c475c8c7dd48baa1bfa7780369ec7662b398235350e693dc9f426"
RATE_LIMIT_DELAY = 10  # Wait 10 seconds between API calls

# Get market config
def get_market_config():
    """Get market configuration including settlement decimals"""
    res = requests.get(f"{API_BASE}/api/v1/market/info")
    if res.status_code == 200:
        data = res.json()
        return {
            'settlement_decimals': data.get('settlement_decimals', 8),
            'settlement_token': f"{data['settlement_token']}"
        }
    return {'settlement_decimals': 6, 'settlement_token': '0.0.6891795'}

config = get_market_config()
TOKEN_DECIMALS = config['settlement_decimals']
SETTLEMENT_TOKEN = config['settlement_token']

print(f"Market Configuration:")
print(f"  Settlement Token: {SETTLEMENT_TOKEN}")
print(f"  Token Decimals: {TOKEN_DECIMALS}")

DEPOSIT_TOKENS = 20
DEPOSIT_AMOUNT = DEPOSIT_TOKENS * (10 ** TOKEN_DECIMALS)

print(f"  Depositing: {DEPOSIT_TOKENS} tokens = {DEPOSIT_AMOUNT} raw units")
print()

# Helper functions
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

def decode_varint(data, pos):
    value = 0
    shift = 0
    while pos < len(data):
        byte = data[pos]
        pos += 1
        value |= (byte & 0x7f) << shift
        if (byte & 0x80) == 0:
            break
        shift += 7
    return value, pos

def build_signature_map(pub_key, signature):
    sig_pair = b'\x0a' + encode_varint(len(pub_key)) + pub_key
    sig_pair += b'\x1a' + encode_varint(len(signature)) + signature
    return b'\x0a' + encode_varint(len(sig_pair)) + sig_pair

def wait_for_rate_limit():
    """Wait to respect rate limits"""
    print(f"  ⏳ Waiting {RATE_LIMIT_DELAY} seconds (rate limit)...")
    time.sleep(RATE_LIMIT_DELAY)

def authenticate(account_id, private_key_hex):
    from cryptography.hazmat.primitives import serialization
    from cryptography.hazmat.backends import default_backend

    print(f"Authenticating {account_id}...")

    private_key_der = bytes.fromhex(private_key_hex)
    private_key = serialization.load_der_private_key(private_key_der, password=None, backend=default_backend())
    public_key_bytes = private_key.public_key().public_bytes(
        encoding=serialization.Encoding.Raw,
        format=serialization.PublicFormat.Raw
    )

    challenge_res = requests.post(
        f"{API_BASE}/api/v1/auth/challenge",
        json={"account_id": account_id, "ledger_id": LEDGER_ID, "method": "message"}
    )

    if challenge_res.status_code != 200:
        print(f"✗ Challenge failed: {challenge_res.text}")
        return None

    challenge_data = challenge_res.json()
    wait_for_rate_limit()  # Wait before verify call

    hip820_bytes = build_hip820(challenge_data["message"].encode('utf-8'))
    signature = private_key.sign(hip820_bytes)
    signature_map = build_signature_map(public_key_bytes, signature)

    verify_res = requests.post(
        f"{API_BASE}/api/v1/auth/verify",
        json={
            "challenge_id": challenge_data["challenge_id"],
            "account_id": account_id,
            "message_signed_plain_text": challenge_data["message"],
            "signature_map_base64": base64.b64encode(signature_map).decode('utf-8'),
            "sig_type": "ed25519"
        }
    )

    if verify_res.status_code != 200:
        print(f"✗ Verify failed: {verify_res.text}")
        return None

    token = verify_res.json()["access_token"]
    print(f"✓ Authenticated")
    return token

def create_deposit_tx(token, account_id, amount):
    print(f"\nStep 1: Creating deposit transaction for {amount / (10**TOKEN_DECIMALS):.{TOKEN_DECIMALS}f} tokens...")

    res = requests.post(
        f"{API_BASE}/api/v1/account/deposit/transaction",
        json={"account": {"account_id": account_id, "owner_type": "Hapi"}, "amount": amount},
        headers={"Authorization": f"Bearer {token}", "Idempotency-Key": str(uuid.uuid4())}
    )

    if res.status_code != 200:
        print(f"✗ Failed: {res.text}")
        return None

    data = res.json()
    print("✓ Transaction created by backend")
    return data

def sign_with_hedera_sdk(tx_base64, private_key_hex):
    print("\nStep 2: Signing transaction...")

    hedera_private_key = PrivateKey.fromBytes(bytes.fromhex(private_key_hex))
    returned_bytes = base64.b64decode(tx_base64)

    try:
        pos = 0
        body_bytes = None

        while pos < len(returned_bytes):
            field_header = returned_bytes[pos]
            pos += 1

            field_number = field_header >> 3
            wire_type = field_header & 0x07

            if wire_type == 2:
                length, pos = decode_varint(returned_bytes, pos)
                data = returned_bytes[pos:pos+length]
                pos += length

                if field_number == 1:
                    body_bytes = data

        if body_bytes is None:
            raise Exception("Failed to extract bodyBytes")

        signature_bytes_raw = hedera_private_key.sign(body_bytes)
        signature_bytes = bytes(signature_bytes_raw)
        public_key_bytes = bytes(hedera_private_key.getPublicKey().toBytesRaw())

        print(f"  ✓ Signed with account key")

        sig_pair = b''
        sig_pair += b'\x0a' + encode_varint(len(public_key_bytes)) + public_key_bytes
        sig_pair += b'\x1a' + encode_varint(len(signature_bytes)) + signature_bytes

        signature_map = b'\x0a' + encode_varint(len(sig_pair)) + sig_pair

        signed_transaction = b''
        signed_transaction += b'\x0a' + encode_varint(len(body_bytes)) + body_bytes
        signed_transaction += b'\x12' + encode_varint(len(signature_map)) + signature_map

        transaction_base64 = base64.b64encode(signed_transaction).decode('utf-8')

        print(f"  ✓ SignedTransaction built: {len(signed_transaction)} bytes")

        return transaction_base64

    except Exception as e:
        print(f"✗ Signing error: {e}")
        import traceback
        traceback.print_exc()
        return None

def submit_deposit(token, account_id, signed_tx_base64):
    print("\nStep 3: Submitting signed transaction...")

    res = requests.post(
        f"{API_BASE}/api/v1/account/deposit",
        json={
            "account": {"account_id": account_id, "owner_type": "Hapi"},
            "signed_transaction_to_base64_string": signed_tx_base64,
            "rlp_encoded_to_base64_string": None
        },
        headers={"Authorization": f"Bearer {token}", "Idempotency-Key": str(uuid.uuid4())}
    )

    if res.status_code == 200:
        data = res.json()
        balance = data.get('balance', 0) / (10 ** TOKEN_DECIMALS)
        print(f"✓ Deposit successful!")
        print(f"✓ New balance: {balance:.{TOKEN_DECIMALS}f} tokens")
        print(f"✓ Transaction ID: {data.get('transaction_id', 'N/A')}")
        return data
    else:
        print(f"✗ Failed: {res.status_code}")
        print(f"✗ Response: {res.text}")
        return None

def check_balance(token, account_id):
    res = requests.post(
        f"{API_BASE}/api/v1/account/balance",
        json={"account_id": account_id, "owner_type": "Hapi"},
        headers={"Authorization": f"Bearer {token}"}
    )

    if res.status_code == 200:
        balance = res.json().get('balance', 0) / (10 ** TOKEN_DECIMALS)
        print(f"Balance: {balance:.{TOKEN_DECIMALS}f} tokens")
        return balance
    return 0

# Main
if __name__ == "__main__":
    try:
        print("="*60)
        print("Hedera Deposit Script - CORRECTED VERSION")
        print("="*60)
        print()

        token = authenticate(ACCOUNT_ID, PRIVATE_KEY_DER_HEX)
        if not token:
            print("\n✗ Authentication failed")
            exit(1)

        wait_for_rate_limit()  # Wait after auth

        print("\nInitial balance:")
        initial = check_balance(token, ACCOUNT_ID)

        wait_for_rate_limit()  # Wait after balance check

        tx_data = create_deposit_tx(token, ACCOUNT_ID, DEPOSIT_AMOUNT)
        if not tx_data:
            exit(1)

        tx_base64 = tx_data['payment']['signed_transaction_to_base64_string']
        signed_tx_base64 = sign_with_hedera_sdk(tx_base64, PRIVATE_KEY_DER_HEX)

        if not signed_tx_base64:
            print("\n✗ Failed to sign transaction")
            exit(1)

        wait_for_rate_limit()  # Wait before submit

        result = submit_deposit(token, ACCOUNT_ID, signed_tx_base64)

        if result:
            wait_for_rate_limit()  # Wait before final balance check

            print("\nFinal balance:")
            final = check_balance(token, ACCOUNT_ID)
            print(f"\n{'='*60}")
            print(f"✓✓✓ SUCCESS! Deposited: {final - initial:.{TOKEN_DECIMALS}f} tokens ✓✓✓")
            print(f"{'='*60}")
        else:
            print(f"\n{'='*60}")
            print("✗ Deposit failed")
            print(f"{'='*60}")

    except ImportError as e:
        print(f"\n✗ Missing dependency: {e}")
        print("Install: pip install hedera-sdk-py requests cryptography")
        exit(1)
    except Exception as e:
        print(f"\n✗ Unexpected error: {e}")
        import traceback
        traceback.print_exc()
        exit(1)
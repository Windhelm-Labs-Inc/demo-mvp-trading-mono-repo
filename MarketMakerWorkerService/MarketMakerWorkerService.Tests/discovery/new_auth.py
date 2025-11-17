#!/usr/bin/env python3

"""
Perpetuals API Authentication Client
Based on integration test patterns from Perpetuals.API.IntegratedTests
Generates curl commands for reproducing issues
"""

import requests
import base64
import json
from datetime import datetime
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.backends import default_backend

# ============================================================================
# CONFIGURATION
# ============================================================================

API_BASE = "https://perps-api-d7cff5fhd9g0b7c4.eastus-01.azurewebsites.net"

# ED25519 Account
ACCOUNT_ID = "0.0.6978377"
PRIVATE_KEY_DER_HEX = "302e020100300506032b6570042204205db3a68cb7831bcefb625238e7800cc9dc85aab09b2acf97537af0d9ef667d7b"
KEY_TYPE = "ed25519"

# ECDSA Account (alternative)
# ACCOUNT_ID = "0.0.6993716"
# PRIVATE_KEY_DER_HEX = "3030020100300706052b8104000a04220420d4d5f714cb82aa20b45154ad60572d4dc1596314a2b048f63e04d5f259125d8c"
# KEY_TYPE = "ecdsa_secp256k1"

LEDGER_ID = "testnet"

# ============================================================================
# KEY LOADING HELPERS
# ============================================================================

def load_ed25519_private_key(der_hex):
    """Load ED25519 private key from DER format"""
    private_key_der = bytes.fromhex(der_hex)
    return serialization.load_der_private_key(
        private_key_der,
        password=None,
        backend=default_backend()
    )

def load_ecdsa_secp256k1_private_key(der_hex):
    """Load ECDSA secp256k1 private key from DER format"""
    try:
        from ecdsa import SigningKey, SECP256k1
        from ecdsa.util import sigencode_string
        der_bytes = bytes.fromhex(der_hex)
        raw_key = der_bytes[-32:]
        signing_key = SigningKey.from_string(raw_key, curve=SECP256k1)
        return signing_key
    except ImportError:
        raise ImportError("For ECDSA secp256k1 keys, install: pip install ecdsa")

def get_public_key_bytes(private_key, key_type):
    """Get public key bytes from private key"""
    if key_type == "ed25519":
        public_key = private_key.public_key()
        return public_key.public_bytes(
            encoding=serialization.Encoding.Raw,
            format=serialization.PublicFormat.Raw
        )
    else:  # ecdsa_secp256k1
        verifying_key = private_key.get_verifying_key()
        return verifying_key.to_string("compressed")

def sign_message(private_key, message, key_type):
    """Sign message with private key"""
    if key_type == "ed25519":
        return private_key.sign(message)
    else:  # ecdsa_secp256k1
        from ecdsa.util import sigencode_string
        # Sign the raw message (Hedera handles hashing)
        return private_key.sign(message, sigencode=sigencode_string)

# ============================================================================
# HIP-820 HELPER (from AccountManagerSignHelper.cs)
# ============================================================================

def build_hip820(canonical_message_bytes):
    """
    Build HIP-820 wrapped message format:
    \x19Hedera Signed Message:\n<message_length>\n<canonical_message>
    
    This matches AccountManagerSignHelper.BuildHip820 in the C# code.
    """
    prefix = b'\x19Hedera Signed Message:\n'
    length_str = str(len(canonical_message_bytes)).encode('ascii')
    newline = b'\n'
    return prefix + length_str + newline + canonical_message_bytes

# ============================================================================
# PROTOBUF SIGNATURE MAP BUILDER
# ============================================================================

def build_signature_map_protobuf(public_key_bytes, signature_bytes, key_type="ed25519"):
    """
    Build Hedera SignatureMap protobuf manually.
    
    message SignatureMap {
        repeated SignaturePair sigPair = 1;
    }
    message SignaturePair {
        bytes pubKeyPrefix = 1;
        oneof signature {
            bytes ed25519 = 3;
            bytes ECDSA_secp256k1 = 6;
        }
    }
    """
    sig_pair = b''

    # Field 1 (pubKeyPrefix): tag = (1 << 3) | 2 = 0x0a (wire type 2 = length-delimited)
    sig_pair += b'\x0a'
    sig_pair += encode_varint(len(public_key_bytes))
    sig_pair += public_key_bytes

    # Field 3 (ed25519) or Field 6 (ECDSA_secp256k1)
    if key_type == "ed25519":
        sig_pair += b'\x1a'  # Field 3, wire type 2
    else:  # ecdsa_secp256k1
        sig_pair += b'\x32'  # Field 6, wire type 2

    sig_pair += encode_varint(len(signature_bytes))
    sig_pair += signature_bytes

    # Build SignatureMap: Field 1 (sigPair array)
    signature_map = b'\x0a'
    signature_map += encode_varint(len(sig_pair))
    signature_map += sig_pair

    return signature_map

def encode_varint(value):
    """Encode a value as protobuf varint"""
    result = []
    while value > 0x7f:
        result.append((value & 0x7f) | 0x80)
        value >>= 7
    result.append(value & 0x7f)
    return bytes(result)

# ============================================================================
# CURL COMMAND GENERATION
# ============================================================================

def generate_curl_command(challenge_id, account_id, canonical_message, signature_map_base64, key_type, api_base):
    """Generate curl command for reproducing the request"""
    payload = {
        "challenge_id": challenge_id,
        "account_id": account_id,
        "message_signed_plain_text": canonical_message,
        "signature_map_base64": signature_map_base64,
        "sig_type": key_type
    }

    json_payload = json.dumps(payload, indent=2)
    escaped_json = json_payload.replace("'", "'\\''")

    curl_cmd = f'''curl -w "\\nHTTP Status: %{{http_code}}\\n" -X POST "{api_base}/api/v1/auth/verify" \\
  -H "Content-Type: application/json" \\
  -d '{escaped_json}'
'''
    return curl_cmd

def save_curl_to_file(challenge_id, account_id, canonical_message, signature_map_base64, key_type, api_base, filename='verify_request.sh'):
    """Save curl command to executable bash script"""
    challenge_payload = {
        "account_id": account_id,
        "ledger_id": "testnet",
        "method": "message"
    }

    verify_payload = {
        "challenge_id": challenge_id,
        "account_id": account_id,
        "message_signed_plain_text": canonical_message,
        "signature_map_base64": signature_map_base64,
        "sig_type": key_type
    }

    challenge_json = json.dumps(challenge_payload)
    verify_json = json.dumps(verify_payload, indent=2)

    escaped_challenge = challenge_json.replace("'", "'\\''")
    escaped_verify = verify_json.replace("'", "'\\''")

    with open(filename, 'w') as f:
        f.write('#!/bin/bash\n\n')
        f.write('# Test verify endpoint with generated signature\n')
        f.write(f'# Generated: {datetime.now().isoformat()}\n')
        f.write(f'# Challenge ID: {challenge_id}\n')
        f.write(f'# Account: {account_id}\n\n')
        f.write('echo "Testing /api/v1/auth/verify endpoint..."\n')
        f.write('echo ""\n\n')
        f.write(f'curl -w "\\nHTTP Status: %{{http_code}}\\n" -X POST "{api_base}/api/v1/auth/verify" \\\n')
        f.write('  -H "Content-Type: application/json" \\\n')
        f.write(f'  -d \'{escaped_verify}\'\n\n')
        f.write('echo ""\n')

# ============================================================================
# AUTHENTICATION FLOW
# ============================================================================

def authenticate():
    """Complete authentication flow matching integration tests"""
    print("=" * 70)
    print("PERPETUALS API AUTHENTICATION")
    print("=" * 70)
    print(f"API: {API_BASE}")
    print(f"Account: {ACCOUNT_ID}")
    print(f"Network: {LEDGER_ID}")
    print(f"Key Type: {KEY_TYPE}")

    # Load private key
    print("\n→ Loading private key...")
    if KEY_TYPE == "ed25519":
        private_key = load_ed25519_private_key(PRIVATE_KEY_DER_HEX)
    else:
        private_key = load_ecdsa_secp256k1_private_key(PRIVATE_KEY_DER_HEX)

    # Get public key bytes
    public_key_bytes = get_public_key_bytes(private_key, KEY_TYPE)
    print(f"  ✓ Public key: {public_key_bytes.hex()}")

    # Step 1: Request Challenge
    print("\n→ Step 1: Requesting challenge...")

    # FIXED: Use snake_case for field names
    challenge_payload = {
        "account_id": ACCOUNT_ID,
        "ledger_id": LEDGER_ID,
        "method": "message"
    }

    print(f"\n  REQUEST:")
    print(f"    URL: {API_BASE}/api/v1/auth/challenge")
    print(f"    Method: POST")
    print(f"    Headers: {{'Content-Type': 'application/json'}}")
    print(f"    Body: {json.dumps(challenge_payload, indent=6)}")

    challenge_res = requests.post(
        f"{API_BASE}/api/v1/auth/challenge",
        json=challenge_payload,
        headers={"Content-Type": "application/json"}
    )

    print(f"\n  RESPONSE:")
    print(f"    Status: {challenge_res.status_code}")
    print(f"    Headers: {dict(challenge_res.headers)}")
    print(f"    Body: {challenge_res.text}")

    if challenge_res.status_code != 200:
        print(f"\n  ✗ Challenge failed: {challenge_res.status_code}")
        return None

    challenge_data = challenge_res.json()
    challenge_id = challenge_data["challenge_id"]  # FIXED: snake_case
    canonical_message = challenge_data["message"]
    expires_at = challenge_data["expires_at_utc"]  # FIXED: snake_case

    print(f"\n  ✓ Challenge ID: {challenge_id}")
    print(f"  ✓ Message length: {len(canonical_message)} chars")
    print(f"  ✓ Expires: {expires_at}")

    # Step 2: Build HIP-820 wrapped message
    print("\n→ Step 2: Building HIP-820 wrapped message...")
    canonical_bytes = canonical_message.encode('utf-8')
    hip820_bytes = build_hip820(canonical_bytes)

    print(f"  ✓ Original message: {len(canonical_bytes)} bytes")
    print(f"  ✓ HIP-820 wrapped: {len(hip820_bytes)} bytes")
    print(f"  ✓ HIP-820 header: {hip820_bytes[:30]}")

    # Step 3: Sign the HIP-820 wrapped message
    print("\n→ Step 3: Signing HIP-820 message...")
    # FIXED: Sign the raw HIP-820 bytes directly for both key types
    signature = sign_message(private_key, hip820_bytes, KEY_TYPE)

    print(f"  ✓ Signature length: {len(signature)} bytes")
    print(f"  ✓ Signature (hex): {signature.hex()[:64]}...")

    # Step 4: Build protobuf SignatureMap
    print("\n→ Step 4: Building protobuf SignatureMap...")
    signature_map = build_signature_map_protobuf(
        public_key_bytes,
        signature,
        KEY_TYPE
    )

    # Use STANDARD base64 (not base64url)
    signature_map_base64 = base64.b64encode(signature_map).decode('utf-8')

    print(f"  ✓ SignatureMap length: {len(signature_map)} bytes")
    print(f"  ✓ SignatureMap (hex): {signature_map.hex()[:80]}...")
    print(f"  ✓ Base64 length: {len(signature_map_base64)} chars")

    # Step 5: Generate curl command
    print("\n→ Step 5: Generating curl command for verify...")
    curl_cmd = generate_curl_command(
        challenge_id,
        ACCOUNT_ID,
        canonical_message,
        signature_map_base64,
        KEY_TYPE,
        API_BASE
    )

    # Save to file
    save_curl_to_file(
        challenge_id,
        ACCOUNT_ID,
        canonical_message,
        signature_map_base64,
        KEY_TYPE,
        API_BASE,
        'verify_request.sh'
    )

    print(f"  ✓ Curl script saved to: verify_request.sh")

    # Print the curl command
    print("\n" + "=" * 70)
    print("CURL COMMAND TO TEST VERIFY ENDPOINT:")
    print("=" * 70)
    print(curl_cmd)
    print("=" * 70)

    print("\n" + "=" * 70)
    print("✓ SIGNATURE GENERATION COMPLETE")
    print("=" * 70)
    print(f"Challenge ID: {challenge_id}")
    print(f"Account ID: {ACCOUNT_ID}")
    print(f"Signature Map (Base64): {signature_map_base64[:80]}...")
    print(f"\nTo test the verify endpoint, run:")
    print(f"  bash verify_request.sh")
    print(f"\nOr use the curl command printed above.")

    return curl_cmd

# ============================================================================
# MAIN EXECUTION
# ============================================================================

if __name__ == "__main__":
    try:
        curl_command = authenticate()
        if curl_command:
            print("\n" + "=" * 70)
            print("NEXT STEPS")
            print("=" * 70)
            print("\n1. Run the generated bash script:")
            print("   bash verify_request.sh")
            print("\n2. Or copy/paste the curl command above")
            print("\n3. If successful, you'll receive an access token")
            print(f"\n4. Use the token with: {API_BASE}/api/v1/market/portfolio")
        else:
            print("\n⚠ Failed to generate signature - check output above")
            exit(1)
    except requests.exceptions.RequestException as e:
        print(f"\n✗ Network error: {e}")
        exit(1)
    except Exception as e:
        print(f"\n✗ Unexpected error: {e}")
        import traceback
        traceback.print_exc()
        exit(1)
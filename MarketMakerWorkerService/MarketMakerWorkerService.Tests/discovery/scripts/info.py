#!/usr/bin/env python3
"""
Get Settlement Token and Market Configuration from API
"""

import requests

API_BASE = "https://perps-api-d7cff5fhd9g0b7c4.eastus-01.azurewebsites.net"
ACCOUNT_ID = "0.0.6978377"

def get_market_info():
    """Fetch market configuration including settlement token"""
    print("Fetching market configuration...")
    print("="*60)

    # Get market info (public endpoint, no auth needed)
    res = requests.get(f"{API_BASE}/api/v1/market/info")

    if res.status_code != 200:
        print(f"✗ Failed: {res.text}")
        return None

    data = res.json()

    print(f"\n✓ Market Info Retrieved")
    print(f"\nTreasury Account: {data.get('market_treasury', 'N/A')}")
    print(f"Settlement Token: {data.get('settlement_token', 'N/A')}")
    print(f"Settlement Decimals: {data.get('settlement_decimals', 'N/A')}")
    print(f"Trading Pair: {data.get('trading_pair', 'N/A')}")
    print(f"Trading Decimals: {data.get('trading_decimals', 'N/A')}")
    print(f"Chain ID: {data.get('chain_id', 'N/A')}")
    print(f"Ledger ID: {data.get('ledger_id', 'N/A')}")

    settlement_token = data.get('settlement_token')
    settlement_decimals = data.get('settlement_decimals')

    print("\n" + "="*60)

    if settlement_token:
        # Check if account has this token
        check_account_token(settlement_token, settlement_decimals)

    return data

def check_account_token(token_id, decimals):
    """Check if account has the settlement token"""
    print(f"\nChecking if account {ACCOUNT_ID} has token {token_id}...")

    mirror_url = f"https://testnet.mirrornode.hedera.com/api/v1/accounts/{ACCOUNT_ID}/tokens"
    res = requests.get(mirror_url)

    if res.status_code != 200:
        print(f"✗ Failed to check: {res.text}")
        return

    tokens = res.json().get('tokens', [])

    for token in tokens:
        if token['token_id'] == token_id:
            balance_raw = int(token['balance'])
            balance = balance_raw / (10 ** decimals)

            print(f"✓ Account IS associated with settlement token!")
            print(f"  Balance: {balance:.{decimals}f} tokens ({balance_raw} raw)")

            if balance >= 20:
                print(f"  ✓ Sufficient balance for 20 token deposit")
            else:
                print(f"  ✗ INSUFFICIENT balance! Need at least 20 tokens")
            return

    print(f"✗ Account is NOT associated with settlement token {token_id}")
    print(f"\nYou need to:")
    print(f"  1. Associate your account with token {token_id}")
    print(f"  2. Get at least 20 tokens")
    print(f"\nYour account currently has:")
    for token in tokens:
        print(f"  - {token['token_id']}")

if __name__ == "__main__":
    try:
        get_market_info()
    except Exception as e:
        print(f"✗ Error: {e}")
        import traceback
        traceback.print_exc()
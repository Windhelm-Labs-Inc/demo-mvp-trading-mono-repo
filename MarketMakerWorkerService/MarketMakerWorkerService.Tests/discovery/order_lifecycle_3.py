#!/usr/bin/env python3

"""
Order Lifecycle Test Script - Production Grade with Server Error Details
This script demonstrates the complete order lifecycle with comprehensive error handling
and detailed server error reporting.
"""

import requests
import base64
import uuid
import time
import json
import logging
import sys
from datetime import datetime
from typing import Optional, Dict, Any, Tuple
from enum import Enum

# ============================================================================
# CONFIGURATION - All configurable parameters
# ============================================================================

# API Configuration
API_BASE = "https://perps-api-d7cff5fhd9g0b7c4.eastus-01.azurewebsites.net"
LEDGER_ID = "testnet"
ACCOUNT_ID = "0.0.6978377"
PRIVATE_KEY_DER_HEX = "302e020100300506032b6570042204205db3a68cb7831bcefb625238e7800cc9dc85aab09b2acf97537af0d9ef667d7b"
# ACCOUNT_ID = "0.0.6993636"
# PRIVATE_KEY_DER_HEX = "302e020100300506032b65700422042068dc0ee90deccf7437110283103e64d96f2f32d4e280a278682fdefc41b8d2e6"

# Order Parameters (in human-readable units)
ORDER_PRICE_USD = 97927.00        # Price per BTC contract in USD
ORDER_SIDE = "short"               # "long" (buy) or "short" (sell) - lowercase string
ORDER_KIND = "limit"              # "market" or "limit" - lowercase string
ORDER_TIME_IN_FORCE = "good_until_filled"  # lowercase snake_case string

# Margin Configuration
INITIAL_MARGIN_FACTOR = 1200000   # 1.2 in base units = 120% = ~0.83x leverage (conservative)

# Dynamic Order Sizing (based on available balance)
ORDER_SIZE_FACTOR = 0.10          # Use 10% of available balance for an order

# Fallback Balance (used if portfolio endpoint not available)
ASSUMED_BALANCE_S_TOKENS = 40.0   # Assumed balance when endpoint returns 404

# Decimal Scaling (will be fetched from API, these are defaults)
DEFAULT_TRADING_DECIMALS = 8      # BTC contract decimals
DEFAULT_SETTLEMENT_DECIMALS = 6   # S token (USDC) decimals
DEFAULT_SETTLEMENT_TOKEN = "0.0.6891795"  # Fallback settlement token ID

# HTTP Configuration
REQUEST_TIMEOUT = 30              # Timeout for HTTP requests in seconds
MAX_RETRIES = 3                   # Maximum retry attempts for failed requests
RETRY_DELAY = 2                   # Delay between retries in seconds

# Constraints and Limits
TOKEN_DISPLAY_SUFFIX_LENGTH = 20  # Number of chars to show from token end

# Exit Codes
EXIT_SUCCESS = 0
EXIT_FAILURE = 1
EXIT_INTERRUPTED = 130            # Standard exit code for Ctrl+C (128 + SIGINT)

# Styling for console output
SEPARATOR = "=" * 80
SUBSEP = "-" * 80
ERROR_SEP = "!" * 80

# ============================================================================
# LOGGING CONFIGURATION
# ============================================================================

# Configure logging
logging.basicConfig(
    level=logging.DEBUG,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s',
    handlers=[
        logging.StreamHandler(sys.stdout),
        logging.FileHandler(f'order_lifecycle_{datetime.now().strftime("%Y%m%d_%H%M%S")}.log')
    ]
)

logger = logging.getLogger(__name__)

class ErrorCode(Enum):
    """Error codes for different failure scenarios"""
    NETWORK_ERROR = "NETWORK_ERROR"
    AUTH_FAILED = "AUTH_FAILED"
    INSUFFICIENT_BALANCE = "INSUFFICIENT_BALANCE"
    ORDER_SUBMIT_FAILED = "ORDER_SUBMIT_FAILED"
    ORDER_CANCEL_FAILED = "ORDER_CANCEL_FAILED"
    INVALID_RESPONSE = "INVALID_RESPONSE"
    CONFIG_ERROR = "CONFIG_ERROR"
    TIMEOUT = "TIMEOUT"
    UNKNOWN_ERROR = "UNKNOWN_ERROR"

# ============================================================================
# SERVER ERROR HANDLING
# ============================================================================

def print_server_error(response: requests.Response, context: str = ""):
    """
    Extract and print detailed error information from server response
    
    Args:
        response: The HTTP response object
        context: Additional context about where the error occurred
    """
    print(f"\n{ERROR_SEP}")
    print(f"  SERVER ERROR DETAILS")
    if context:
        print(f"  Context: {context}")
    print(f"{ERROR_SEP}")

    logger.error(f"Server error in {context}: Status {response.status_code}")

    print(f"\nHTTP Status: {response.status_code} {response.reason}")
    logger.error(f"HTTP Status: {response.status_code} {response.reason}")

    # Try to parse as JSON (Problem Details format or other JSON error)
    try:
        error_data = response.json()
        print(f"\nServer Response (JSON):")
        print(json.dumps(error_data, indent=2))
        logger.error(f"Server Response: {json.dumps(error_data)}")

        # Extract Problem Details fields (RFC 7807 format)
        if isinstance(error_data, dict):
            print(f"\nParsed Error Details:")

            if 'type' in error_data:
                print(f"  Type: {error_data['type']}")
                logger.error(f"  Error Type: {error_data['type']}")

            if 'title' in error_data:
                print(f"  Title: {error_data['title']}")
                logger.error(f"  Error Title: {error_data['title']}")

            if 'status' in error_data:
                print(f"  Status: {error_data['status']}")

            if 'detail' in error_data:
                print(f"  Detail: {error_data['detail']}")
                logger.error(f"  Error Detail: {error_data['detail']}")

            if 'instance' in error_data:
                print(f"  Instance: {error_data['instance']}")
                logger.error(f"  Error Instance: {error_data['instance']}")

            # Extensions (custom fields)
            if 'traceId' in error_data or 'trace_id' in error_data:
                trace_id = error_data.get('traceId') or error_data.get('trace_id')
                print(f"  Trace ID: {trace_id}")
                logger.error(f"  Trace ID: {trace_id}")

            if 'code' in error_data:
                print(f"  Error Code: {error_data['code']}")
                logger.error(f"  Error Code: {error_data['code']}")

            if 'request_id' in error_data:
                print(f"  Request ID: {error_data['request_id']}")
                logger.error(f"  Request ID: {error_data['request_id']}")

            # Additional error details
            if 'errors' in error_data:
                print(f"  Validation Errors:")
                logger.error(f"  Validation Errors: {error_data['errors']}")
                for error in error_data['errors']:
                    if isinstance(error, dict):
                        path = error.get('path', 'unknown')
                        message = error.get('message', 'unknown')
                        print(f"    - {path}: {message}")
                    else:
                        print(f"    - {error}")

            # Show any other fields not covered above
            known_fields = {'type', 'title', 'status', 'detail', 'instance',
                            'traceId', 'trace_id', 'code', 'request_id', 'errors'}
            other_fields = {k: v for k, v in error_data.items() if k not in known_fields}
            if other_fields:
                print(f"  Additional Fields:")
                for key, value in other_fields.items():
                    print(f"    {key}: {value}")
                    logger.error(f"  Additional Field - {key}: {value}")

    except json.JSONDecodeError:
        # Not JSON, print raw text
        text = response.text
        print(f"\nServer Response (Raw Text):")
        print(text if text else "(empty response)")
        logger.error(f"Server Response (Raw): {text}")

    except Exception as e:
        print(f"\nError parsing server response: {type(e).__name__}: {e}")
        logger.error(f"Error parsing server response: {type(e).__name__}: {e}")
        print(f"Raw response text: {response.text}")
        logger.error(f"Raw response text: {response.text}")

    # Print response headers for debugging
    print(f"\nResponse Headers:")
    for key, value in response.headers.items():
        if key.lower() in ['x-request-id', 'trace-id', 'traceid', 'request-id',
                           'x-correlation-id', 'correlation-id']:
            print(f"  {key}: {value}")
            logger.error(f"  Header - {key}: {value}")

    print(f"{ERROR_SEP}\n")

# ============================================================================
# HELPER FUNCTIONS
# ============================================================================

def print_header(text: str):
    """Print a styled header"""
    print(f"\n{SEPARATOR}")
    print(f"  {text}")
    print(f"{SEPARATOR}\n")
    logger.info(f"=== {text} ===")

def print_request(method: str, url: str, headers: Optional[Dict[str, str]] = None,
                  body: Optional[Dict[str, Any]] = None) -> float:
    """Print full request details and return start time"""
    start_time = time.time()
    print(f"{SUBSEP}")
    print(f"REQUEST: {method} {url}")
    print(f"Time: {datetime.utcnow().isoformat()}Z")

    logger.debug(f"REQUEST: {method} {url}")

    if headers:
        print("Headers:")
        safe_headers = {}
        for key, value in headers.items():
            # Mask authorization token
            if key.lower() == "authorization" and value:
                suffix = value[-TOKEN_DISPLAY_SUFFIX_LENGTH:] if len(value) > TOKEN_DISPLAY_SUFFIX_LENGTH else '***'
                print(f"  {key}: Bearer ***{suffix}***")
                safe_headers[key] = f"Bearer ***{suffix}***"
            else:
                print(f"  {key}: {value}")
                safe_headers[key] = value
        logger.debug(f"Headers: {safe_headers}")

    if body:
        print("Body:")
        print(json.dumps(body, indent=2))
        logger.debug(f"Body: {json.dumps(body)}")

    print(f"{SUBSEP}\n")
    return start_time

def print_response(response: requests.Response, start_time: Optional[float] = None):
    """Print full response details including duration"""
    end_time = time.time()
    duration_ms = (end_time - start_time) * 1000 if start_time else None

    print(f"{SUBSEP}")
    print(f"RESPONSE: {response.status_code} {response.reason}")
    print(f"Time: {datetime.utcnow().isoformat()}Z")

    logger.debug(f"RESPONSE: {response.status_code} {response.reason}")

    if duration_ms is not None:
        print(f"Duration: {duration_ms:.2f} ms")
        logger.debug(f"Duration: {duration_ms:.2f} ms")

    print("Headers:")
    for key, value in response.headers.items():
        print(f"  {key}: {value}")

    print("Body:")
    try:
        json_body = response.json()
        print(json.dumps(json_body, indent=2))
        logger.debug(f"Response Body: {json.dumps(json_body)}")
    except Exception as e:
        text = response.text if response.text else "(empty)"
        print(text)
        logger.debug(f"Response Body (text): {text}")

    print(f"{SUBSEP}\n")

def safe_request(method: str, url: str, **kwargs) -> Optional[requests.Response]:
    """
    Make an HTTP request with error handling and retries
    
    Args:
        method: HTTP method (GET, POST, DELETE, etc.)
        url: Request URL
        **kwargs: Additional arguments to pass to requests
    
    Returns:
        Response object or None if all retries failed
    """
    kwargs.setdefault('timeout', REQUEST_TIMEOUT)

    for attempt in range(MAX_RETRIES):
        try:
            logger.info(f"Attempt {attempt + 1}/{MAX_RETRIES}: {method} {url}")

            if method.upper() == 'GET':
                response = requests.get(url, **kwargs)
            elif method.upper() == 'POST':
                response = requests.post(url, **kwargs)
            elif method.upper() == 'DELETE':
                response = requests.delete(url, **kwargs)
            else:
                logger.error(f"Unsupported HTTP method: {method}")
                return None

            # Log response status
            logger.info(f"Response status: {response.status_code}")

            return response

        except requests.exceptions.Timeout as e:
            logger.error(f"Request timeout on attempt {attempt + 1}: {e}")
            print(f"⚠ Request timeout on attempt {attempt + 1}: {e}")
            if attempt < MAX_RETRIES - 1:
                logger.info(f"Retrying in {RETRY_DELAY} seconds...")
                print(f"  Retrying in {RETRY_DELAY} seconds...")
                time.sleep(RETRY_DELAY)
            else:
                logger.error("Max retries reached for timeout")
                print(f"✗ Max retries reached for timeout")

        except requests.exceptions.ConnectionError as e:
            logger.error(f"Connection error on attempt {attempt + 1}: {e}")
            print(f"⚠ Connection error on attempt {attempt + 1}: {e}")
            if attempt < MAX_RETRIES - 1:
                logger.info(f"Retrying in {RETRY_DELAY} seconds...")
                print(f"  Retrying in {RETRY_DELAY} seconds...")
                time.sleep(RETRY_DELAY)
            else:
                logger.error("Max retries reached for connection error")
                print(f"✗ Max retries reached for connection error")

        except requests.exceptions.RequestException as e:
            logger.error(f"Request exception on attempt {attempt + 1}: {e}")
            print(f"⚠ Request exception on attempt {attempt + 1}: {e}")
            if attempt < MAX_RETRIES - 1:
                logger.info(f"Retrying in {RETRY_DELAY} seconds...")
                print(f"  Retrying in {RETRY_DELAY} seconds...")
                time.sleep(RETRY_DELAY)
            else:
                logger.error("Max retries reached for request exception")
                print(f"✗ Max retries reached for request exception")

        except Exception as e:
            logger.error(f"Unexpected error on attempt {attempt + 1}: {type(e).__name__}: {e}")
            print(f"⚠ Unexpected error on attempt {attempt + 1}: {type(e).__name__}: {e}")
            if attempt < MAX_RETRIES - 1:
                time.sleep(RETRY_DELAY)
            else:
                logger.error("Max retries reached for unexpected error")
                print(f"✗ Max retries reached")

    return None

# ============================================================================
# MARKET CONFIGURATION
# ============================================================================

def get_market_config() -> Dict[str, Any]:
    """Get market configuration including decimals"""
    logger.info("Fetching market configuration...")

    try:
        print_header("Fetching Market Configuration")
        url = f"{API_BASE}/api/v1/market/info"
        start_time = print_request("GET", url)

        response = safe_request('GET', url)

        if response is None:
            logger.error("Failed to get market config after retries")
            print("⚠ Failed to fetch config after retries, using defaults")
            return _get_default_config()

        print_response(response, start_time)

        if response.status_code == 200:
            data = response.json()
            config = {
                'settlement_decimals': data.get('settlement_decimals', DEFAULT_SETTLEMENT_DECIMALS),
                'trading_decimals': data.get('trading_decimals', DEFAULT_TRADING_DECIMALS),
                'settlement_token': data.get('settlement_token', DEFAULT_SETTLEMENT_TOKEN),
                'trading_pair': data.get('trading_pair', 'BTC-S'),
                'chain_id': data.get('chain_id'),
                'ledger_id': data.get('ledger_id')
            }

            logger.info(f"Market config retrieved: {config}")
            print(f"✓ Market Config: {config['trading_pair']}")
            print(f"  Chain ID: {config['chain_id']}")
            print(f"  Ledger ID: {config['ledger_id']}")
            print(f"  Trading Decimals: {config['trading_decimals']}")
            print(f"  Settlement Decimals: {config['settlement_decimals']}")
            print(f"  Settlement Token: {config['settlement_token']}")
            return config
        else:
            logger.warning(f"Market config request failed with status {response.status_code}")
            print_server_error(response, "Market Config Request")
            print("⚠ Failed to fetch config, using defaults")
            return _get_default_config()

    except json.JSONDecodeError as e:
        logger.error(f"JSON decode error in market config: {e}")
        print(f"✗ JSON decode error: {e}")
        if 'response' in locals():
            print(f"Raw response: {response.text}")
        return _get_default_config()

    except Exception as e:
        logger.error(f"Exception fetching market config: {type(e).__name__}: {e}", exc_info=True)
        print(f"✗ Exception fetching market config: {e}")
        print(f"Exception type: {type(e).__name__}")
        import traceback
        traceback.print_exc()
        return _get_default_config()

def _get_default_config() -> Dict[str, Any]:
    """Return default market configuration"""
    return {
        'settlement_decimals': DEFAULT_SETTLEMENT_DECIMALS,
        'trading_decimals': DEFAULT_TRADING_DECIMALS,
        'settlement_token': DEFAULT_SETTLEMENT_TOKEN,
        'trading_pair': 'BTC-S',
        'chain_id': None,
        'ledger_id': LEDGER_ID
    }

# ============================================================================
# HIP-820 AUTHENTICATION
# ============================================================================

def build_hip820(msg_bytes: bytes) -> bytes:
    """Build HIP-820 message wrapper"""
    try:
        prefix = b'\x19Hedera Signed Message:\n'
        length = str(len(msg_bytes)).encode('ascii')
        result = prefix + length + b'\n' + msg_bytes
        logger.debug(f"Built HIP-820 message, length: {len(result)}")
        return result
    except Exception as e:
        logger.error(f"Error building HIP-820 message: {e}")
        raise

def encode_varint(value: int) -> bytes:
    """Encode integer as protobuf varint"""
    try:
        result = []
        while value > 0x7f:
            result.append((value & 0x7f) | 0x80)
            value >>= 7
        result.append(value & 0x7f)
        return bytes(result)
    except Exception as e:
        logger.error(f"Error encoding varint: {e}")
        raise

def build_signature_map(pub_key: bytes, signature: bytes) -> bytes:
    """Build protobuf SignatureMap"""
    try:
        sig_pair = b'\x0a' + encode_varint(len(pub_key)) + pub_key
        sig_pair += b'\x1a' + encode_varint(len(signature)) + signature
        result = b'\x0a' + encode_varint(len(sig_pair)) + sig_pair
        logger.debug(f"Built signature map, length: {len(result)}")
        return result
    except Exception as e:
        logger.error(f"Error building signature map: {e}")
        raise

def authenticate(account_id: str, private_key_hex: str) -> Optional[str]:
    """
    Authenticate with HIP-820 message signing
    
    Returns:
        JWT token or None if authentication failed
    """
    logger.info(f"Starting authentication for account {account_id}")

    try:
        from cryptography.hazmat.primitives import serialization
        from cryptography.hazmat.backends import default_backend

        print_header("STEP 1: Authentication")

        # Load private key
        logger.debug("Loading private key...")
        private_key_der = bytes.fromhex(private_key_hex)
        private_key = serialization.load_der_private_key(
            private_key_der, password=None, backend=default_backend())
        public_key_bytes = private_key.public_key().public_bytes(
            encoding=serialization.Encoding.Raw,
            format=serialization.PublicFormat.Raw
        )
        logger.debug(f"Private key loaded, public key length: {len(public_key_bytes)}")

        # Step 1a: Request challenge
        logger.info("Requesting authentication challenge...")
        url = f"{API_BASE}/api/v1/auth/challenge"
        body = {
            "account_id": account_id,
            "ledger_id": LEDGER_ID,
            "method": "message"
        }
        start_time = print_request("POST", url, {"Content-Type": "application/json"}, body)

        challenge_res = safe_request('POST', url, json=body,
                                     headers={"Content-Type": "application/json"})

        if challenge_res is None:
            logger.error("Failed to get challenge after retries")
            print(f"✗ Challenge request failed after retries")
            return None

        print_response(challenge_res, start_time)

        if challenge_res.status_code != 200:
            logger.error(f"Challenge failed with status {challenge_res.status_code}")
            print_server_error(challenge_res, "Authentication Challenge")
            return None

        try:
            challenge_data = challenge_res.json()
        except json.JSONDecodeError as e:
            logger.error(f"Failed to decode challenge response: {e}")
            print(f"✗ Invalid JSON in challenge response: {e}")
            print(f"Raw response: {challenge_res.text}")
            return None

        challenge_id = challenge_data.get('challenge_id')
        message = challenge_data.get('message')

        if not challenge_id or not message:
            logger.error("Challenge response missing required fields")
            print(f"✗ Invalid challenge response format")
            print(f"Response data: {challenge_data}")
            return None

        logger.info(f"Challenge received: {challenge_id}")
        print(f"✓ Challenge received: {challenge_id}")

        # Step 1b: Sign challenge and verify
        logger.info("Signing challenge...")
        hip820_bytes = build_hip820(message.encode('utf-8'))
        signature = private_key.sign(hip820_bytes)
        signature_map = build_signature_map(public_key_bytes, signature)
        logger.debug(f"Challenge signed, signature length: {len(signature)}")

        url = f"{API_BASE}/api/v1/auth/verify"
        body = {
            "challenge_id": challenge_id,
            "account_id": account_id,
            "message_signed_plain_text": message,
            "signature_map_base64": base64.b64encode(signature_map).decode('utf-8'),
            "sig_type": "ed25519"
        }
        start_time = print_request("POST", url, {"Content-Type": "application/json"}, body)

        verify_res = safe_request('POST', url, json=body,
                                  headers={"Content-Type": "application/json"})

        if verify_res is None:
            logger.error("Failed to verify signature after retries")
            print(f"✗ Verify request failed after retries")
            return None

        print_response(verify_res, start_time)

        if verify_res.status_code != 200:
            logger.error(f"Verify failed with status {verify_res.status_code}")
            print_server_error(verify_res, "Authentication Verify")
            return None

        try:
            verify_data = verify_res.json()
        except json.JSONDecodeError as e:
            logger.error(f"Failed to decode verify response: {e}")
            print(f"✗ Invalid JSON in verify response: {e}")
            print(f"Raw response: {verify_res.text}")
            return None

        token = verify_data.get("access_token")

        if not token:
            logger.error("Verify response missing access_token")
            print(f"✗ No access token in verify response")
            print(f"Response data: {verify_data}")
            return None

        logger.info("Authentication successful")
        print(f"✓ Authenticated successfully")
        print(f"  Token (last {TOKEN_DISPLAY_SUFFIX_LENGTH} chars): ...{token[-TOKEN_DISPLAY_SUFFIX_LENGTH:]}")
        print(f"  Expires in: {verify_data.get('expires_in', 'unknown')} seconds")

        return token

    except ImportError as e:
        logger.error(f"Missing required cryptography library: {e}")
        print(f"\n✗ Missing required library: {e}")
        print(f"  Install with: pip install cryptography")
        return None

    except Exception as e:
        logger.error(f"Exception in authenticate(): {type(e).__name__}: {e}", exc_info=True)
        print(f"\n✗ EXCEPTION in authenticate():")
        print(f"  Type: {type(e).__name__}")
        print(f"  Message: {str(e)}")
        import traceback
        traceback.print_exc()
        return None

# ============================================================================
# ACCOUNT BALANCE
# ============================================================================

def get_portfolio_balance(token: str, account_id: str) -> Optional[Dict[str, Any]]:
    """
    Get portfolio balance and available margin
    
    Returns:
        Portfolio info dict or None if request failed
    """
    logger.info(f"Fetching balance for account {account_id}")

    try:
        print_header("STEP 2: Checking Account Balance")

        url = f"{API_BASE}/api/v1/account/balance?accountId={account_id}&ownerType=Hapi"
        headers = {
            "Authorization": f"Bearer {token}"
        }
        start_time = print_request("GET", url, headers)

        response = safe_request('GET', url, headers=headers)

        if response is None:
            logger.error("Failed to get balance after retries")
            print(f"⚠ Failed to fetch balance after retries")
            return None

        print_response(response, start_time)

        if response.status_code == 200:
            try:
                data = response.json()
            except json.JSONDecodeError as e:
                logger.error(f"Failed to decode balance response: {e}")
                print(f"✗ Invalid JSON in balance response: {e}")
                print(f"Raw response: {response.text}")
                return None

            balance_base = data.get('balance', 0)
            owner_id = data.get('owner_id', '')

            portfolio_info = {
                'current_balance_base': balance_base,
                'owner_id': owner_id
            }

            logger.info(f"Balance retrieved: {balance_base} base units, owner: {owner_id}")
            print(f"✓ Balance retrieved")
            print(f"  Owner ID: {owner_id}")
            print(f"  Available Balance: {balance_base} base units")

            return portfolio_info

        elif response.status_code == 404:
            logger.warning("Balance endpoint returned 404")
            print_server_error(response, "Get Balance")
            print("⚠ Balance endpoint not available (404)")
            print("  Using configured balance assumption for testing")

            settlement_decimals = DEFAULT_SETTLEMENT_DECIMALS
            balance_base = int(ASSUMED_BALANCE_S_TOKENS * (10 ** settlement_decimals))

            portfolio_info = {
                'current_balance_base': balance_base,
                'margin_equity_base': balance_base,
                'unrealized_profit_base': 0,
                'unrealized_loss_base': 0,
                'orders_count': 0,
                'positions_count': 0
            }

            logger.info(f"Using assumed balance: {balance_base} base units")
            print(f"  Assumed Balance: {balance_base} base units ({ASSUMED_BALANCE_S_TOKENS} S tokens)")

            return portfolio_info
        else:
            logger.error(f"Balance request failed with status {response.status_code}")
            print_server_error(response, "Get Balance")
            return None

    except Exception as e:
        logger.error(f"Exception fetching balance: {type(e).__name__}: {e}", exc_info=True)
        print(f"✗ Exception fetching portfolio: {e}")
        print(f"Exception type: {type(e).__name__}")
        import traceback
        traceback.print_exc()
        return None

# ============================================================================
# ORDER CALCULATION
# ============================================================================

def calculate_order_parameters(balance_base: int, settlement_decimals: int,
                               trading_decimals: int) -> Optional[Dict[str, Any]]:
    """Calculate safe order parameters based on available balance"""
    logger.info(f"Calculating order parameters for balance: {balance_base} base units")

    try:
        print_header("Calculating Order Parameters")

        balance_s_tokens = balance_base / (10 ** settlement_decimals)
        logger.debug(f"Balance: {balance_s_tokens} S tokens ({balance_base} base units)")
        print(f"Available Balance: {balance_s_tokens:.6f} S tokens ({balance_base} base units)")

        target_balance_use = balance_base * ORDER_SIZE_FACTOR
        logger.debug(f"Target balance usage: {ORDER_SIZE_FACTOR * 100}% = {target_balance_use} base units")
        print(f"Target Balance Usage: {ORDER_SIZE_FACTOR * 100:.1f}% = {target_balance_use / (10 ** settlement_decimals):.6f} S tokens")

        price_base = int(ORDER_PRICE_USD * (10 ** trading_decimals))
        margin_factor_base = INITIAL_MARGIN_FACTOR
        scaling_multipliers = (10 ** settlement_decimals) * (10 ** trading_decimals)

        logger.debug(f"Price base: {price_base}, Margin factor: {margin_factor_base}")

        quantity_base = int((target_balance_use * scaling_multipliers) / (price_base * margin_factor_base))
        required_balance = (quantity_base * price_base * margin_factor_base + scaling_multipliers - 1) // scaling_multipliers

        quantity_contracts = quantity_base / (10 ** trading_decimals)
        required_balance_s_tokens = required_balance / (10 ** settlement_decimals)
        margin_factor_fraction = margin_factor_base / (10 ** settlement_decimals)
        notional_value_s_tokens = (quantity_base * price_base) / scaling_multipliers / (10 ** settlement_decimals)

        logger.debug(f"Calculated quantity: {quantity_base} base units ({quantity_contracts} contracts)")
        logger.debug(f"Required balance: {required_balance} base units ({required_balance_s_tokens} S tokens)")

        if required_balance > balance_base:
            logger.warning(f"Insufficient balance: required={required_balance}, available={balance_base}")
            print(f"⚠ WARNING: Insufficient balance!")
            print(f"  Required: {required_balance_s_tokens:.6f} S tokens")
            print(f"  Available: {balance_s_tokens:.6f} S tokens")
            return None

        order_params = {
            'quantity_base': quantity_base,
            'price_base': price_base,
            'margin_factor_base': margin_factor_base,
            'quantity_contracts': quantity_contracts,
            'required_balance': required_balance,
            'required_balance_s_tokens': required_balance_s_tokens,
            'margin_factor_fraction': margin_factor_fraction,
            'notional_value_s_tokens': notional_value_s_tokens
        }

        logger.info(f"Order parameters calculated: quantity={quantity_contracts}, price={ORDER_PRICE_USD}")
        print(f"✓ Order Parameters Calculated:")
        print(f"  Quantity: {quantity_contracts:.8f} BTC contracts (base: {quantity_base})")
        print(f"  Price: ${ORDER_PRICE_USD:,.2f} (base: {price_base})")
        print(f"  Margin Factor: {margin_factor_fraction:.2f}x (base: {margin_factor_base})")
        print(f"  Notional Value: ~${notional_value_s_tokens:.6f} S tokens")
        print(f"  Required Balance: {required_balance_s_tokens:.6f} S tokens (base: {required_balance})")
        print(f"  Effective Leverage: ~{1/margin_factor_fraction:.2f}x")

        return order_params

    except ZeroDivisionError as e:
        logger.error(f"Division by zero in order calculation: {e}")
        print(f"✗ Math error in order calculation: {e}")
        return None

    except Exception as e:
        logger.error(f"Exception calculating order parameters: {type(e).__name__}: {e}", exc_info=True)
        print(f"✗ Exception calculating order parameters: {e}")
        import traceback
        traceback.print_exc()
        return None

# ============================================================================
# ORDER SUBMISSION
# ============================================================================

def submit_limit_order(token: str, account_id: str, order_params: Dict[str, Any]) -> Optional[str]:
    """Submit a limit order with calculated parameters"""
    logger.info("Submitting limit order...")

    try:
        print_header("STEP 3: Submit Limit Order")

        price_base = order_params['price_base']
        quantity_base = order_params['quantity_base']
        margin_factor_base = order_params['margin_factor_base']

        side_display = 'Long/Buy' if ORDER_SIDE == 'long' else 'Short/Sell'
        kind_display = 'Market' if ORDER_KIND == 'market' else 'Limit'

        logger.debug(f"Order details: side={ORDER_SIDE}, kind={ORDER_KIND}, price={price_base}, qty={quantity_base}")

        print(f"Submitting Order:")
        print(f"  Side: {side_display}")
        print(f"  Type: {kind_display}")
        print(f"  Price: ${ORDER_PRICE_USD:,.2f} (base: {price_base})")
        print(f"  Quantity: {order_params['quantity_contracts']:.8f} contracts (base: {quantity_base})")
        print(f"  Margin Factor: {order_params['margin_factor_fraction']:.2f}x (base: {margin_factor_base})")
        print(f"  Estimated Cost: ~${order_params['required_balance_s_tokens']:.6f} S tokens")
        print()

        client_order_id = f"debug-{int(time.time())}"
        idempotency_key = str(uuid.uuid4())

        logger.debug(f"Client order ID: {client_order_id}, Idempotency key: {idempotency_key}")

        url = f"{API_BASE}/api/v1/order/submit"
        headers = {
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/json",
            "Idempotency-Key": idempotency_key
        }
        body = {
            "client_order_id": client_order_id,
            "kind": ORDER_KIND,
            "margin": margin_factor_base,
            "account": {
                "account_id": account_id,
                "owner_type": "hapi"
            },
            "price": price_base,
            "quantity": quantity_base,
            "side": ORDER_SIDE,
            "time_in_force": ORDER_TIME_IN_FORCE
        }
        start_time = print_request("POST", url, headers, body)

        response = safe_request('POST', url, json=body, headers=headers)

        if response is None:
            logger.error("Failed to submit order after retries")
            print(f"✗ Order submission failed after retries")
            return None

        print_response(response, start_time)

        if response.status_code in [200, 201]:
            try:
                data = response.json()
            except json.JSONDecodeError as e:
                logger.error(f"Failed to decode submit response: {e}")
                print(f"✗ Invalid JSON in submit response: {e}")
                print(f"Raw response: {response.text}")
                return None

            order_id = data.get('order_id')
            order_status = data.get('order_status', 'Unknown')
            quantity_filled = data.get('quantity_filled', 0)
            trade_id = data.get('trade_id')
            position_ids = data.get('position_ids', [])

            logger.info(f"Order submitted successfully: ID={order_id}, status={order_status}")
            print(f"✓ Order submitted successfully")
            print(f"  Order ID: {order_id}")
            print(f"  Status: {order_status}")
            print(f"  Quantity Filled: {quantity_filled}")

            if trade_id:
                logger.debug(f"Trade ID: {trade_id}")
                print(f"  Trade ID: {trade_id}")
            if position_ids:
                logger.debug(f"Position IDs: {position_ids}")
                print(f"  Position IDs: {', '.join(str(pid) for pid in position_ids)}")

            return order_id
        else:
            logger.error(f"Order submission failed with status {response.status_code}")
            print_server_error(response, "Submit Order")
            return None

    except Exception as e:
        logger.error(f"Exception in submit_limit_order(): {type(e).__name__}: {e}", exc_info=True)
        print(f"\n✗ EXCEPTION in submit_limit_order():")
        print(f"  Type: {type(e).__name__}")
        print(f"  Message: {str(e)}")
        import traceback
        traceback.print_exc()
        return None

# ============================================================================
# ACCOUNT PORTFOLIO
# ============================================================================

def get_portfolio(token: str, account_id: str) -> Optional[Dict[str, Any]]:
    """Get portfolio (orders and positions)"""
    logger.info(f"Fetching portfolio for account {account_id}")

    try:
        print_header("STEP 4: Get Account Details (View Orders)")

        url = f"{API_BASE}/api/v1/account?accountId={account_id}&ownerType=Hapi"
        headers = {
            "Authorization": f"Bearer {token}"
        }
        start_time = print_request("GET", url, headers)

        response = safe_request('GET', url, headers=headers)

        if response is None:
            logger.error("Failed to get account details after retries")
            print(f"✗ Failed to get account details after retries")
            return None

        print_response(response, start_time)

        if response.status_code == 200:
            try:
                data = response.json()
            except json.JSONDecodeError as e:
                logger.error(f"Failed to decode account response: {e}")
                print(f"✗ Invalid JSON in account response: {e}")
                print(f"Raw response: {response.text}")
                return None

            orders = data.get('orders', [])
            positions = data.get('positions', [])
            balance = data.get('balance', 0)
            owner_id = data.get('owner_id', '')

            logger.info(f"Account details retrieved: {len(orders)} orders, {len(positions)} positions")
            print(f"✓ Account details retrieved")
            print(f"  Owner ID: {owner_id}")
            print(f"  Account ID: {data.get('account_id', account_id)}")
            print(f"  Balance: {balance} base units")
            print(f"  Open Orders: {len(orders)}")
            print(f"  Open Positions: {len(positions)}")

            if orders:
                print(f"\n  Order Details:")
                for order in orders:
                    order_id = order.get('order_id')
                    side = order.get('contract_side')
                    price = order.get('price')
                    quantity = order.get('quantity')
                    margin = order.get('margin')

                    logger.debug(f"Order: ID={order_id}, side={side}, price={price}, qty={quantity}")
                    print(f"    Order ID: {order_id}")
                    print(f"    Side: {side}")
                    print(f"    Price: {price}")
                    print(f"    Quantity: {quantity}")
                    print(f"    Margin: {margin}")

            if positions:
                print(f"\n  Position Details:")
                for position in positions:
                    pos_id = position.get('postion_id')  # Note: API has typo "postion_id"
                    side = position.get('contract_side')
                    quantity = position.get('quantity')

                    logger.debug(f"Position: ID={pos_id}, side={side}, qty={quantity}")
                    print(f"    Position ID: {pos_id}")
                    print(f"    Side: {side}")
                    print(f"    Quantity: {quantity}")

            return data
        else:
            logger.error(f"Get account failed with status {response.status_code}")
            print_server_error(response, "Get Account")
            return None

    except Exception as e:
        logger.error(f"Exception in get_portfolio(): {type(e).__name__}: {e}", exc_info=True)
        print(f"\n✗ EXCEPTION in get_portfolio():")
        print(f"  Type: {type(e).__name__}")
        print(f"  Message: {str(e)}")
        import traceback
        traceback.print_exc()
        return None

# ============================================================================
# ORDER CANCELLATION
# ============================================================================

def cancel_order(token: str, order_id: str) -> bool:
    """Cancel an order"""
    logger.info(f"Cancelling order: {order_id}")

    try:
        print_header("STEP 5: Cancel Order")

        idempotency_key = str(uuid.uuid4())
        url = f"{API_BASE}/api/v1/order/cancel?orderId={order_id}"
        headers = {
            "Authorization": f"Bearer {token}",
            "Idempotency-Key": idempotency_key
        }

        logger.debug(f"Cancel request: DELETE {url}, Idempotency-Key: {idempotency_key}")
        start_time = print_request("DELETE", url, headers)

        response = safe_request('DELETE', url, headers=headers)

        if response is None:
            logger.error("Failed to cancel order after retries")
            print(f"✗ Order cancellation failed after retries")
            return False

        print_response(response, start_time)

        if response.status_code == 200:
            try:
                data = response.json()
            except json.JSONDecodeError as e:
                logger.error(f"Failed to decode cancel response: {e}")
                print(f"✗ Invalid JSON in cancel response: {e}")
                print(f"Raw response: {response.text}")
                return False

            cancelled_order_id = data.get('order_id')
            unfilled_quantity = data.get('unfilled_quantity', 0)

            logger.info(f"Order canceled successfully: ID={cancelled_order_id}, unfilled={unfilled_quantity}")
            print(f"✓ Order canceled successfully")
            print(f"  Order ID: {cancelled_order_id}")
            print(f"  Unfilled Quantity: {unfilled_quantity}")

            return True
        else:
            logger.error(f"Order cancellation failed with status {response.status_code}")
            print_server_error(response, "Cancel Order")
            return False

    except Exception as e:
        logger.error(f"Exception in cancel_order(): {type(e).__name__}: {e}", exc_info=True)
        print(f"\n✗ EXCEPTION in cancel_order():")
        print(f"  Type: {type(e).__name__}")
        print(f"  Message: {str(e)}")
        import traceback
        traceback.print_exc()
        return False

# ============================================================================
# MAIN EXECUTION
# ============================================================================

def main() -> int:
    """Main execution flow"""
    logger.info("=" * 80)
    logger.info("Order Lifecycle Test Script Starting")
    logger.info(f"Account: {ACCOUNT_ID}")
    logger.info(f"Ledger: {LEDGER_ID}")
    logger.info(f"API: {API_BASE}")
    logger.info("=" * 80)

    print(f"\n{SEPARATOR}")
    print(f"  Order Lifecycle Test Script")
    print(f"  Account: {ACCOUNT_ID}")
    print(f"  Ledger: {LEDGER_ID}")
    print(f"  API: {API_BASE}")
    print(f"{SEPARATOR}\n")

    # Track success of each step
    results = {
        'config': False,
        'auth': False,
        'balance': False,
        'calculation': False,
        'submit': False,
        'portfolio': False,
        'cancel': False
    }

    order_id = None
    order_params = None
    portfolio_info = None

    try:
        # Get market configuration
        logger.info("Step: Get market configuration")
        config = get_market_config()
        results['config'] = True

        # Step 1: Authenticate
        logger.info("Step 1: Authentication")
        token = authenticate(ACCOUNT_ID, PRIVATE_KEY_DER_HEX)
        if not token:
            logger.error("Step 1 FAILED: Could not authenticate")
            print(f"\n✗ FAILED: Could not authenticate")
            return EXIT_FAILURE
        results['auth'] = True

        # Step 2: Check portfolio balance
        logger.info("Step 2: Check portfolio balance")
        portfolio_info = get_portfolio_balance(token, ACCOUNT_ID)
        if not portfolio_info:
            logger.error("Step 2 FAILED: Could not retrieve balance")
            print(f"\n✗ FAILED: Could not retrieve balance")
            return EXIT_FAILURE
        results['balance'] = True

        # Calculate order parameters based on available balance
        logger.info("Step 2b: Calculate order parameters")
        order_params = calculate_order_parameters(
            portfolio_info['current_balance_base'],
            config['settlement_decimals'],
            config['trading_decimals']
        )
        if not order_params:
            logger.error("Step 2b FAILED: Insufficient balance to place order")
            print(f"\n✗ FAILED: Insufficient balance to place order")
            return EXIT_FAILURE
        results['calculation'] = True

        # Step 3: Submit limit order
        logger.info("Step 3: Submit limit order")
        order_id = submit_limit_order(token, ACCOUNT_ID, order_params)
        if not order_id:
            logger.error("Step 3 FAILED: Could not submit order")
            print(f"\n✗ FAILED: Could not submit order")
            return EXIT_FAILURE
        results['submit'] = True

        # Step 4: Get account details to view order
        logger.info("Step 4: Get account details")
        portfolio = get_portfolio(token, ACCOUNT_ID)
        if not portfolio:
            logger.warning("Step 4 WARNING: Could not retrieve account details")
            print(f"\n⚠ WARNING: Could not retrieve account details")
        else:
            results['portfolio'] = True

        # Step 5: Cancel the order
        logger.info("Step 5: Cancel order")
        canceled = cancel_order(token, order_id)
        if not canceled:
            logger.warning("Step 5 WARNING: Could not cancel order")
            print(f"\n⚠ WARNING: Could not cancel order")
        else:
            results['cancel'] = True

        # Final summary
        print_header("TEST SUMMARY")
        logger.info("=" * 80)
        logger.info("TEST SUMMARY")
        logger.info("=" * 80)

        print(f"✓ Market Config: {'SUCCESS' if results['config'] else 'FAILED'}")
        logger.info(f"Market Config: {'SUCCESS' if results['config'] else 'FAILED'}")

        print(f"✓ Step 1 - Authentication: {'SUCCESS' if results['auth'] else 'FAILED'}")
        logger.info(f"Step 1 - Authentication: {'SUCCESS' if results['auth'] else 'FAILED'}")

        if portfolio_info:
            print(f"✓ Step 2 - Balance Check: SUCCESS ({portfolio_info['current_balance_base']} base units)")
            logger.info(f"Step 2 - Balance Check: SUCCESS ({portfolio_info['current_balance_base']} base units)")

        if order_params:
            print(f"✓ Step 2b - Order Calculation: SUCCESS")
            print(f"    Quantity: {order_params['quantity_contracts']:.8f} BTC")
            print(f"    Margin Factor: {order_params['margin_factor_fraction']:.2f}x")
            print(f"    Required Balance: {order_params['required_balance_s_tokens']:.6f} S")
            logger.info(f"Step 2b - Order Calculation: SUCCESS")
            logger.info(f"  Quantity: {order_params['quantity_contracts']:.8f} BTC")

        print(f"✓ Step 3 - Order Submission: {'SUCCESS' if results['submit'] else 'FAILED'}")
        logger.info(f"Step 3 - Order Submission: {'SUCCESS' if results['submit'] else 'FAILED'}")
        if order_id:
            print(f"    Order ID: {order_id}")
            logger.info(f"  Order ID: {order_id}")

        print(f"✓ Step 4 - Account View: {'SUCCESS' if results['portfolio'] else 'FAILED'}")
        logger.info(f"Step 4 - Account View: {'SUCCESS' if results['portfolio'] else 'FAILED'}")

        print(f"✓ Step 5 - Order Cancellation: {'SUCCESS' if results['cancel'] else 'FAILED'}")
        logger.info(f"Step 5 - Order Cancellation: {'SUCCESS' if results['cancel'] else 'FAILED'}")

        print(f"\n{SEPARATOR}\n")

        logger.info("=" * 80)
        logger.info("Order Lifecycle Test Script Completed Successfully")
        logger.info("=" * 80)

        return EXIT_SUCCESS

    except KeyboardInterrupt:
        logger.warning("Script interrupted by user (Ctrl+C)")
        print(f"\n\n⚠ Interrupted by user (Ctrl+C)")
        return EXIT_INTERRUPTED

    except Exception as e:
        logger.error(f"FATAL EXCEPTION in main(): {type(e).__name__}: {e}", exc_info=True)
        print(f"\n✗ FATAL EXCEPTION in main():")
        print(f"  Type: {type(e).__name__}")
        print(f"  Message: {str(e)}")
        import traceback
        traceback.print_exc()
        return EXIT_FAILURE

if __name__ == "__main__":
    try:
        exit_code = main()
        sys.exit(exit_code)
    except KeyboardInterrupt:
        logger.warning("Script interrupted by user at top level")
        print(f"\n\n⚠ Interrupted by user (Ctrl+C)")
        sys.exit(EXIT_INTERRUPTED)
    except Exception as e:
        logger.critical(f"UNHANDLED EXCEPTION: {type(e).__name__}: {e}", exc_info=True)
        print(f"\n✗ UNHANDLED EXCEPTION:")
        print(f"  Type: {type(e).__name__}")
        print(f"  Message: {str(e)}")
        import traceback
        traceback.print_exc()
        sys.exit(EXIT_FAILURE)
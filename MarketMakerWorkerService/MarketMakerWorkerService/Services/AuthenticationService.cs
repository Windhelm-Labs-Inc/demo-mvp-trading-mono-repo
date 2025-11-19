using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using MarketMakerWorkerService.Configuration;
using MarketMakerWorkerService.Models;
using Proto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace MarketMakerWorkerService.Services;

public interface IAuthenticationService
{
    Task<string> AuthenticateAsync(CancellationToken cancellationToken);
    Task<string> GetValidTokenAsync(CancellationToken cancellationToken);
}

public class AuthenticationService : IAuthenticationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MarketMakerConfiguration _config;
    private readonly ILogger<AuthenticationService> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    
    private string? _currentToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    
    public AuthenticationService(
        IHttpClientFactory httpClientFactory,
        IOptions<MarketMakerConfiguration> config,
        ILogger<AuthenticationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config.Value;
        _logger = logger;
    }
    
    public async Task<string> GetValidTokenAsync(CancellationToken cancellationToken)
    {
        // Check if current token is still valid (with 60 second buffer)
        if (!string.IsNullOrEmpty(_currentToken) && DateTime.UtcNow < _tokenExpiry.AddSeconds(-60))
        {
            return _currentToken;
        }
        
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring lock
            if (!string.IsNullOrEmpty(_currentToken) && DateTime.UtcNow < _tokenExpiry.AddSeconds(-60))
            {
                return _currentToken;
            }
            
            // Token expired or doesn't exist, get new one
            return await AuthenticateAsync(cancellationToken);
        }
        finally
        {
            _tokenLock.Release();
        }
    }
    
    public async Task<string> AuthenticateAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting authentication for account {AccountId}", _config.AccountId);
        
        var client = _httpClientFactory.CreateClient("PerpetualsAPI");
        
        // Step 1: Get authentication challenge
        var challengeRequest = new AuthChallengeRequest(_config.AccountId, _config.LedgerId, "message");
        var challengeResponse = await SendRequestAsync<AuthChallengeRequest, AuthChallengeResponse>(
            client,
            "/api/v1/auth/challenge",
            challengeRequest,
            null,
            cancellationToken);
        
        _logger.LogInformation("Received challenge: {ChallengeId}", challengeResponse.ChallengeId);
        
        // Step 2: Sign with HIP-820 wrapper
        var hip820Message = BuildHip820Message(Encoding.UTF8.GetBytes(challengeResponse.Message));
        var privateKeyBytes = Convert.FromHexString(_config.PrivateKeyDerHex);
        var signature = SignMessage(hip820Message, privateKeyBytes);
        var publicKey = GetPublicKeyFromPrivate(privateKeyBytes);
        var signatureMap = BuildSignatureMap(publicKey, signature);
        
        // Step 3: Verify signature and get token
        var verifyRequest = new AuthVerifyRequest(
            ChallengeId: challengeResponse.ChallengeId,
            AccountId: _config.AccountId,
            MessageSignedPlainText: challengeResponse.Message,
            SignatureMapBase64: Convert.ToBase64String(signatureMap.ToByteArray()),
            SigType: "ed25519");
        
        var verifyResponse = await SendRequestAsync<AuthVerifyRequest, AuthVerifyResponse>(
            client,
            "/api/v1/auth/verify",
            verifyRequest,
            null,
            cancellationToken);
        
        _currentToken = verifyResponse.AccessToken;
        _tokenExpiry = DateTime.UtcNow.AddSeconds(verifyResponse.ExpiresIn);
        
        _logger.LogInformation("Authentication successful, token expires at {Expiry}", _tokenExpiry);
        
        return _currentToken;
    }
    
    private byte[] BuildHip820Message(byte[] messageBytes)
    {
        // HIP-820: Prepend magic bytes + length + newline + message
        // Format: "\x19Hedera Signed Message:\n" + length + "\n" + message
        var magic = Encoding.ASCII.GetBytes("\x19Hedera Signed Message:\n");
        var lengthBytes = Encoding.ASCII.GetBytes(messageBytes.Length.ToString());
        var newline = new byte[] { (byte)'\n' };
        
        var fullMessage = new byte[magic.Length + lengthBytes.Length + 1 + messageBytes.Length];
        Buffer.BlockCopy(magic, 0, fullMessage, 0, magic.Length);
        Buffer.BlockCopy(lengthBytes, 0, fullMessage, magic.Length, lengthBytes.Length);
        fullMessage[magic.Length + lengthBytes.Length] = (byte)'\n';
        Buffer.BlockCopy(messageBytes, 0, fullMessage, magic.Length + lengthBytes.Length + 1, messageBytes.Length);
        
        return fullMessage;
    }
    
    private byte[] SignMessage(byte[] message, byte[] privateKeyDerBytes)
    {
        // Sign with Ed25519 using Hiero SDK (handles DER format internally)
        var signature = Ed25519.Sign(message, privateKeyDerBytes);
        
        return signature;
    }
    
    private byte[] GetPublicKeyFromPrivate(byte[] privateKeyDerBytes)
    {
        // Derive public key from private key using Hiero SDK
        var publicKey = Ed25519.PublicKeyFromSeed(privateKeyDerBytes);
        
        return publicKey;
    }
    
    private SignatureMap BuildSignatureMap(byte[] publicKey, byte[] signature)
    {
        var signatureMap = new SignatureMap();
        signatureMap.SigPair.Add(new SignaturePair
        {
            PubKeyPrefix = ByteString.CopyFrom(publicKey),
            Ed25519 = ByteString.CopyFrom(signature)
        });
        
        return signatureMap;
    }
    
    private async Task<TResponse> SendRequestAsync<TRequest, TResponse>(
        HttpClient client,
        string endpoint,
        TRequest request,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        var requestJson = JsonSerializer.Serialize(request, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false
        });
        
        _logger.LogDebug("Sending {Method} request to {Endpoint}: {Request}", 
            "POST", endpoint, requestJson);
        
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = content
        };
        
        if (!string.IsNullOrEmpty(bearerToken))
        {
            requestMessage.Headers.Authorization = 
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        }
        
        var response = await client.SendAsync(requestMessage, cancellationToken);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        
        _logger.LogDebug("Received response from {Endpoint}: Status={StatusCode}, Body={Body}", 
            endpoint, (int)response.StatusCode, responseContent);
        
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"API request failed: {response.StatusCode} - {responseContent}");
        }
        
        var result = JsonSerializer.Deserialize<TResponse>(responseContent, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true
        });
        
        return result ?? throw new InvalidOperationException("Failed to deserialize response");
    }
}

// Ed25519 implementation using BouncyCastle (from Hiero SDK)
internal static class Ed25519
{
    public static byte[] Sign(byte[] message, byte[] privateKeyDerBytes)
    {
        // Parse DER-encoded private key using BouncyCastle
        var privateKey = PrivateKeyFactory.CreateKey(privateKeyDerBytes) as Ed25519PrivateKeyParameters
            ?? throw new ArgumentException("Invalid Ed25519 private key");
        
        // Sign using BouncyCastle Ed25519Signer
        var signer = new Ed25519Signer();
        signer.Init(true, privateKey);
        signer.BlockUpdate(message, 0, message.Length);
        var signature = signer.GenerateSignature();
        signer.Reset();
        
        return signature;
    }
    
    public static byte[] PublicKeyFromSeed(byte[] privateKeyDerBytes)
    {
        // Parse DER-encoded private key and derive public key
        var privateKey = PrivateKeyFactory.CreateKey(privateKeyDerBytes) as Ed25519PrivateKeyParameters
            ?? throw new ArgumentException("Invalid Ed25519 private key");
        return privateKey.GeneratePublicKey().GetEncoded();
    }
}


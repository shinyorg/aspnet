namespace Shiny.AspNet.Webhooks;

/// <summary>The hash behind an HMAC signature.</summary>
public enum WebhookHmacAlgorithm
{
    /// <summary>
    /// HMAC-SHA1. Still what some older senders use; HMAC does not inherit SHA-1's collision
    /// weakness, but prefer SHA-256 wherever the sender offers it.
    /// </summary>
    Sha1,
    Sha256,
    Sha512
}

/// <summary>How the signature bytes are written into the header.</summary>
public enum WebhookSignatureEncoding
{
    /// <summary>Hexadecimal, either case — GitHub, Stripe, Slack.</summary>
    Hex,

    /// <summary>Standard base64 — Standard Webhooks, Shopify.</summary>
    Base64
}

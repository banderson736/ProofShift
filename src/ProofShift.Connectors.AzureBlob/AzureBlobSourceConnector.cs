using System.Globalization;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.RemoteObjects;

namespace ProofShift.Connectors.AzureBlob;

/// <summary>Azure Blob Storage (and Azurite) container scope. Read-only.</summary>
public sealed class AzureBlobSourceConnector() : RemoteObjectSourceConnector("azure-blob", new AzureBlobStoreFactory());

public sealed class AzureBlobStoreFactory : IRemoteObjectStoreFactory
{
    public string Transport => "azure-blob";
    public RemoteStoreCapabilities Capabilities { get; } = new(true, true, true);

    public IReadOnlyDictionary<string, string> EndpointProperties { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["account"] = "string",
        ["serviceUrl"] = "string",
        ["container"] = "string",
        ["prefix"] = "string",
        ["authentication"] = "enum:default-credential,account-key,sas",
        ["accountKey"] = "string-or-reference",
        ["sasToken"] = "string-or-reference",
        ["allowInsecureHttp"] = "enum:true,false",
        ["pageSize"] = "positive-integer-string"
    };

    public IReadOnlyCollection<string> RequiredEndpointProperties { get; } = ["container", "authentication"];

    public IRemoteObjectStore Create(ConnectorContext context)
    {
        string Value(string name, string fallback = "") => RemoteSettings.Optional(context, name, fallback);
        var container = Value("container");
        if (string.IsNullOrWhiteSpace(container) || container.Contains('/', StringComparison.Ordinal))
            throw new ConnectorConfigurationException(ConnectorIssueCodes.MissingConfiguration, "A single-segment Azure container name is required.");
        var account = Value("account");
        var serviceUrl = Value("serviceUrl");
        if (serviceUrl.Length == 0)
        {
            if (account.Length == 0)
                throw new ConnectorConfigurationException(ConnectorIssueCodes.MissingConfiguration, "Azure account or serviceUrl is required.");
            serviceUrl = $"https://{account}.blob.core.windows.net";
        }

        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var service) || service.Scheme is not ("http" or "https") ||
            service.UserInfo.Length > 0 || service.Query.Length > 0)
            throw new ConnectorConfigurationException(ConnectorIssueCodes.InsecureRemoteConfiguration,
                "Azure serviceUrl must be an absolute http(s) URL without credentials or a query string.");
        if (service.Scheme == "http" && Value("allowInsecureHttp") != "true")
            throw new ConnectorConfigurationException(ConnectorIssueCodes.InsecureRemoteConfiguration,
                "Plain-HTTP Azure endpoints require allowInsecureHttp=true and must not be used outside isolated test fixtures.");

        var containerUri = new Uri(serviceUrl.TrimEnd('/') + "/" + Uri.EscapeDataString(container));
        var options = new BlobClientOptions { Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(60) } };
        BlobContainerClient client;
        switch (Value("authentication"))
        {
            case "account-key":
                if (account.Length == 0)
                    throw new ConnectorConfigurationException(ConnectorIssueCodes.MissingConfiguration, "Azure account name is required for account-key authentication.");
                client = new BlobContainerClient(containerUri, new StorageSharedKeyCredential(account, RemoteSettings.RequiredSecret(context, "accountKey")), options);
                break;
            case "sas":
                var sas = RemoteSettings.RequiredSecret(context, "sasToken").TrimStart('?');
                client = new BlobContainerClient(new Uri(containerUri + "?" + sas), options);
                break;
            case "default-credential":
                client = new BlobContainerClient(containerUri, new DefaultAzureCredential(), options);
                break;
            default:
                throw new ConnectorConfigurationException(ConnectorIssueCodes.MissingConfiguration, "Azure authentication must be default-credential, account-key or sas.");
        }

        var pageSize = int.TryParse(Value("pageSize", "5000"), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? Math.Clamp(parsed, 1, 5000) : 5000;
        return new AzureBlobObjectStore(client, container, RemoteKeyRules.NormalizePrefix(Value("prefix")), pageSize);
    }
}

public sealed class AzureBlobObjectStore(BlobContainerClient client, string container, string prefix, int pageSize) : IRemoteObjectStore
{
    public string Provider => "azure-blob";
    public string ScopeIdentity => $"azure-blob://{client.AccountName}/{container}/{prefix}";
    public RemoteStoreCapabilities Capabilities { get; } = new(true, true, true);

    public async IAsyncEnumerable<RemoteObjectInfo> ListAsync(string keyPrefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (keyPrefix.Length > 0) RemoteKeyRules.ValidateRelativeKey(keyPrefix.TrimEnd('/').Length == 0 ? "x" : keyPrefix.TrimEnd('/'));
        string? continuation = null;
        do
        {
            var page = await Guard(async token =>
            {
                var pages = client.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix + keyPrefix, token)
                    .AsPages(continuation, pageSize).GetAsyncEnumerator(token);
                try
                {
                    return await pages.MoveNextAsync().ConfigureAwait(false) ? pages.Current : null;
                }
                finally
                {
                    await pages.DisposeAsync().ConfigureAwait(false);
                }
            }, cancellationToken).ConfigureAwait(false);
            if (page is null) yield break;
            foreach (var item in page.Values)
            {
                if (item.Name.EndsWith('/')) continue;
                yield return new RemoteObjectInfo(RemoteKeyRules.ToRelative(prefix, item.Name), item.Properties.ContentLength ?? 0,
                    item.Properties.LastModified, item.Properties.ETag?.ToString(), item.VersionId);
            }

            continuation = string.IsNullOrEmpty(page.ContinuationToken) ? null : page.ContinuationToken;
        }
        while (continuation is not null);
    }

    public async Task<RemoteObjectInfo> StatAsync(string key, string? versionId, CancellationToken cancellationToken)
    {
        var blob = Blob(key, versionId);
        var properties = (await Guard(async token => (await blob.GetPropertiesAsync(cancellationToken: token).ConfigureAwait(false)).Value,
            cancellationToken, versionId is not null).ConfigureAwait(false));
        return new RemoteObjectInfo(key, properties.ContentLength, properties.LastModified, properties.ETag.ToString(),
            versionId ?? (string.IsNullOrEmpty(properties.VersionId) ? null : properties.VersionId));
    }

    public async ValueTask<Stream> OpenReadAsync(RemoteObjectInfo info, long offset, long? length, CancellationToken cancellationToken)
    {
        if (info.Length == 0) return new MemoryStream([], writable: false);
        var blob = Blob(info.Key, info.VersionId);
        var options = new BlobDownloadOptions();
        if (offset > 0 || length is not null) options.Range = new HttpRange(offset, length);
        if (info.VersionId is null && info.ETag is not null) options.Conditions = new BlobRequestConditions { IfMatch = new ETag(info.ETag) };
        var response = await Guard(async token => (await blob.DownloadStreamingAsync(options, token).ConfigureAwait(false)).Value, cancellationToken, info.VersionId is not null)
            .ConfigureAwait(false);
        return response.Content;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private BlobClient Blob(string key, string? versionId)
    {
        var blob = client.GetBlobClient(RemoteKeyRules.ToFull(prefix, key));
        return versionId is null ? blob : blob.WithVersion(versionId);
    }

    private static async Task<T> Guard<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken, bool pinned = false)
    {
        return await RemoteRetryPolicy.Default.ExecuteAsync(async token =>
        {
            try
            {
                return await operation(token).ConfigureAwait(false);
            }
            catch (RequestFailedException exception)
            {
                throw Map(exception.Status, exception.ErrorCode, pinned);
            }
            catch (Exception exception) when (!token.IsCancellationRequested &&
                exception is HttpRequestException or IOException or TimeoutException or TaskCanceledException)
            {
                throw new RemoteStoreException(RemoteFailureKind.Transient, "Azure Blob transport failure.");
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public static RemoteStoreException Map(int status, string? errorCode, bool pinned = false) => status switch
    {
        401 => new(RemoteFailureKind.Authentication, "Azure Blob authentication failed."),
        403 when errorCode is "AuthenticationFailed" or "InvalidAuthenticationInfo" => new(RemoteFailureKind.Authentication, "Azure Blob authentication failed."),
        403 => new(RemoteFailureKind.Authorization, "Azure Blob authorization failed for the configured container or prefix."),
        404 or 400 when pinned && errorCode is "BlobNotFound" or "InvalidVersionForPageBlobOperation" or "InvalidQueryParameterValue" or "InvalidHeaderValue" =>
            new(RemoteFailureKind.VersionUnavailable, "Pinned blob version is not available."),
        404 => new(RemoteFailureKind.NotFound, "Azure container or blob was not found."),
        412 => new(RemoteFailureKind.Changed, "Azure blob changed since it was listed."),
        408 or 429 or >= 500 => new(RemoteFailureKind.Transient, "Azure Blob temporarily unavailable."),
        _ => new(RemoteFailureKind.Failed, "Azure Blob request failed.")
    };
}

using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.RemoteObjects;

namespace ProofShift.Connectors.S3;

/// <summary>Amazon S3 and S3-compatible object storage (selected by the optional <c>serviceUrl</c>). Read-only.</summary>
public sealed class S3SourceConnector() : RemoteObjectSourceConnector("s3", new S3StoreFactory());

public sealed class S3StoreFactory : IRemoteObjectStoreFactory
{
    public string Transport => "s3";
    public RemoteStoreCapabilities Capabilities { get; } = new(true, true, true);

    public IReadOnlyDictionary<string, string> EndpointProperties { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["bucket"] = "string",
        ["prefix"] = "string",
        ["region"] = "string",
        ["serviceUrl"] = "string",
        ["forcePathStyle"] = "enum:true,false",
        ["allowInsecureHttp"] = "enum:true,false",
        ["authentication"] = "enum:default-chain,static",
        ["accessKeyId"] = "string-or-reference",
        ["secretAccessKey"] = "string-or-reference",
        ["sessionToken"] = "string-or-reference",
        ["pageSize"] = "positive-integer-string"
    };

    public IReadOnlyCollection<string> RequiredEndpointProperties { get; } = ["bucket"];

    public IRemoteObjectStore Create(ConnectorContext context)
    {
        var configuration = context.Configuration;
        string Value(string name, string fallback = "") =>
            configuration.TryGet(name, out var setting) ? setting.UseValue(value => value) : fallback;

        var bucket = Value("bucket");
        if (string.IsNullOrWhiteSpace(bucket))
            throw new ConnectorConfigurationException(ConnectorIssueCodes.MissingConfiguration, "S3 bucket is required.");
        var serviceUrl = Value("serviceUrl");
        if (serviceUrl.Length > 0)
        {
            if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
                throw new ConnectorConfigurationException(ConnectorIssueCodes.InsecureRemoteConfiguration,
                    "S3 serviceUrl must be an absolute http(s) URL without embedded credentials.");
            if (uri.Scheme == "http" && Value("allowInsecureHttp") != "true")
                throw new ConnectorConfigurationException(ConnectorIssueCodes.InsecureRemoteConfiguration,
                    "Plain-HTTP S3 endpoints require allowInsecureHttp=true and must not be used outside isolated test fixtures.");
        }

        var authentication = Value("authentication", configuration.TryGet("accessKeyId", out _) ? "static" : "default-chain");
        var config = new AmazonS3Config
        {
            MaxErrorRetry = 0,
            Timeout = TimeSpan.FromSeconds(60),
            ForcePathStyle = Value("forcePathStyle", serviceUrl.Length > 0 ? "true" : "false") == "true"
        };
        var region = Value("region");
        if (serviceUrl.Length > 0)
        {
            config.ServiceURL = serviceUrl;
            if (region.Length > 0) config.AuthenticationRegion = region;
        }
        else
        {
            if (region.Length == 0)
                throw new ConnectorConfigurationException(ConnectorIssueCodes.MissingConfiguration, "S3 region is required unless serviceUrl is configured.");
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        }

        AmazonS3Client client;
        if (authentication == "static")
        {
            var accessKey = RemoteSettings.RequiredSecret(context, "accessKeyId");
            var secretKey = RemoteSettings.RequiredSecret(context, "secretAccessKey");
            AWSCredentials credentials = configuration.TryGet("sessionToken", out var token)
                ? new SessionAWSCredentials(accessKey, secretKey, token.UseValue(value => value))
                : new BasicAWSCredentials(accessKey, secretKey);
            client = new AmazonS3Client(credentials, config);
        }
        else
        {
            client = new AmazonS3Client(config);
        }

        var pageSize = int.TryParse(Value("pageSize", "1000"), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? Math.Clamp(parsed, 1, 1000) : 1000;
        return new S3ObjectStore(client, bucket, RemoteKeyRules.NormalizePrefix(Value("prefix")), pageSize);
    }
}

public sealed class S3ObjectStore(IAmazonS3 client, string bucket, string prefix, int pageSize) : IRemoteObjectStore
{
    public string Provider => "s3";
    public string ScopeIdentity => $"s3://{bucket}/{prefix}";
    public RemoteStoreCapabilities Capabilities { get; } = new(true, true, true);

    public async IAsyncEnumerable<RemoteObjectInfo> ListAsync(string keyPrefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? continuation = null;
        do
        {
            var request = new ListObjectsV2Request
            {
                BucketName = bucket, Prefix = prefix + SafePrefix(keyPrefix),
                MaxKeys = pageSize, ContinuationToken = continuation
            };
            var response = await Guard(token => client.ListObjectsV2Async(request, token), cancellationToken).ConfigureAwait(false);
            foreach (var item in response.S3Objects ?? [])
            {
                if (item.Key.EndsWith('/')) continue;
                var relative = RemoteKeyRules.ToRelative(prefix, item.Key);
                yield return new RemoteObjectInfo(relative, item.Size ?? 0,
                    item.LastModified is { } modified ? new DateTimeOffset(DateTime.SpecifyKind(modified, DateTimeKind.Utc)) : null, item.ETag);
            }

            continuation = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (continuation is not null);
    }

    public async Task<RemoteObjectInfo> StatAsync(string key, string? versionId, CancellationToken cancellationToken)
    {
        var request = new GetObjectMetadataRequest { BucketName = bucket, Key = RemoteKeyRules.ToFull(prefix, key) };
        if (versionId is not null && versionId != "null") request.VersionId = versionId;
        var response = await Guard(token => client.GetObjectMetadataAsync(request, token), cancellationToken).ConfigureAwait(false);
        var returned = response.VersionId;
        if (versionId is not null && returned is not null && returned != versionId)
            throw new RemoteStoreException(RemoteFailureKind.VersionUnavailable, "Pinned object version is not available.");
        return new RemoteObjectInfo(key, response.ContentLength,
            response.LastModified is { } modified ? new DateTimeOffset(DateTime.SpecifyKind(modified, DateTimeKind.Utc)) : null,
            response.ETag, string.IsNullOrEmpty(returned) || returned == "null" ? null : returned);
    }

    public async ValueTask<Stream> OpenReadAsync(RemoteObjectInfo info, long offset, long? length, CancellationToken cancellationToken)
    {
        var request = new GetObjectRequest { BucketName = bucket, Key = RemoteKeyRules.ToFull(prefix, info.Key) };
        if (info.VersionId is not null) request.VersionId = info.VersionId;
        else if (info.ETag is not null) request.EtagToMatch = info.ETag;
        if (offset > 0 || length is not null)
            request.ByteRange = new ByteRange(offset, length is { } count ? offset + count - 1 : long.MaxValue / 2);
        if (info.Length == 0) return new MemoryStream([], writable: false);
        var response = await Guard(token => client.GetObjectAsync(request, token), cancellationToken).ConfigureAwait(false);
        return new ResponseOwningStream(response);
    }

    private static string SafePrefix(string keyPrefix)
    {
        if (keyPrefix.Length > 0) RemoteKeyRules.ValidateRelativeKey(keyPrefix.TrimEnd('/') .Length == 0 ? "x" : keyPrefix.TrimEnd('/'));
        return keyPrefix;
    }

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task<T> Guard<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        return await RemoteRetryPolicy.Default.ExecuteAsync(async token =>
        {
            try
            {
                return await operation(token).ConfigureAwait(false);
            }
            catch (AmazonServiceException exception)
            {
                throw Map(exception);
            }
            catch (AmazonClientException)
            {
                throw new RemoteStoreException(RemoteFailureKind.Transient, "S3 transport failure.");
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or TimeoutException or
                TaskCanceledException && !token.IsCancellationRequested)
            {
                throw new RemoteStoreException(RemoteFailureKind.Transient, "S3 transport failure.");
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public static RemoteStoreException Map(AmazonServiceException exception) => exception.StatusCode switch
    {
        HttpStatusCode.Unauthorized => new(RemoteFailureKind.Authentication, "S3 authentication failed."),
        HttpStatusCode.Forbidden when exception.ErrorCode is "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "ExpiredToken" or "InvalidToken" =>
            new(RemoteFailureKind.Authentication, "S3 authentication failed."),
        HttpStatusCode.Forbidden => new(RemoteFailureKind.Authorization, "S3 authorization failed for the configured bucket or prefix."),
        HttpStatusCode.NotFound when exception.ErrorCode is "NoSuchVersion" => new(RemoteFailureKind.VersionUnavailable, "Pinned object version is not available."),
        HttpStatusCode.NotFound => new(RemoteFailureKind.NotFound, "S3 bucket or object was not found."),
        HttpStatusCode.PreconditionFailed => new(RemoteFailureKind.Changed, "S3 object changed since it was listed."),
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError =>
            new(RemoteFailureKind.Transient, "S3 temporarily unavailable."),
        _ => new(RemoteFailureKind.Failed, "S3 request failed.")
    };

    private sealed class ResponseOwningStream(GetObjectResponse response) : Stream
    {
        private readonly Stream _inner = response.ResponseStream;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) response.Dispose();
            base.Dispose(disposing);
        }
    }
}

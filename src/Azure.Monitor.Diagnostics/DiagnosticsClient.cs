﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Core;
using Azure.Core.Pipeline;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using static System.FormattableString;
using static System.Globalization.CultureInfo;


namespace Azure.Monitor.Diagnostics;

/// <summary>
/// A client for Application Insights diagnostic services. Facilitates
/// the uploading of diagnostic artifacts to the Application Insights
/// diagnostics endpoint.
/// </summary>
public class DiagnosticsClient
{
    private readonly HttpPipeline _pipeline;

    /// <summary>
    /// Construct a new <see cref="DiagnosticsClient"/> instance.
    /// </summary>
    /// <param name="options">Pipeline options for this client.</param>
    public DiagnosticsClient(DiagnosticsClientOptions options)
    {
        DiagnosticsClientPipelineOptions pipelineOptions = new(options);
        _pipeline = HttpPipelineBuilder.Build(pipelineOptions);
    }

    /// <summary>
    /// Get the profile for the given App Insights instrumentation key.
    /// The profile contains static properties about the given <paramref name="iKey"/>.
    /// </summary>
    /// <param name="iKey">The instrumentation key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The profile of the application.</returns>
    public async Task<Response<AppProfile>> GetAppProfileAsync(string iKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(iKey))
        {
            throw new ArgumentException($"'{nameof(iKey)}' cannot be null or empty.", nameof(iKey));
        }

        using HttpMessage message = CreateRequest(RequestMethod.Get, Invariant($"/api/apps/{iKey}/profile"));
        return await SendAsync<AppProfile>(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Obtain a token for uploading a new artifact to the service.
    /// </summary>
    /// <param name="iKey">The instrumentation key.</param>
    /// <param name="artifactKind">The type of artifact.</param>
    /// <param name="artifactId">The ID of the artifact, usually generated by the caller.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A blob URI with a write-only SAS token.</returns>
    public async Task<Response<UploadToken>> GetUploadTokenAsync(string iKey, ArtifactKind artifactKind, Guid artifactId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(iKey))
        {
            throw new ArgumentException($"'{nameof(iKey)}' cannot be null or empty.", nameof(iKey));
        }

        using HttpMessage message = CreateArtifactRequest(iKey, artifactKind, artifactId, IngestionAction.GetToken);
        await _pipeline.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await ThrowIfRequestFailedAsync(message.Response, cancellationToken).ConfigureAwait(false);
        return message.Response.Headers.TryGetValue("Location", out string? location)
            ? Response.FromValue(new UploadToken
            {
                BlobUri = new Uri(location!)
            }, message.Response)
            : throw new RequestFailedException("Response did not set the Location header.");
    }

    /// <summary>
    /// Commit the blob to the service.
    /// </summary>
    /// <param name="iKey">The instrumentation key.</param>
    /// <param name="artifactKind">The type of artifact.</param>
    /// <param name="artifactId">The ID of the artifact, usually generated by the caller.</param>
    /// <param name="eTag">The ETag of the blob just written.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Properties of the accepted artifact.</returns>
    public async Task<Response<ArtifactAccepted>> CommitUploadAsync(string iKey, ArtifactKind artifactKind, Guid artifactId, ETag eTag, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(iKey))
        {
            throw new ArgumentException($"'{nameof(iKey)}' cannot be null or empty.", nameof(iKey));
        }

        using HttpMessage message = CreateArtifactRequest(iKey, artifactKind, artifactId, IngestionAction.Commit)
            .WithHeader(HttpHeader.Names.IfMatch, eTag.ToString("H"));

        return await SendAsync<ArtifactAccepted>(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Try to acquire a lease. Agents should call this to participate in concurrency control.
    /// </summary>
    /// <param name="iKey">The instrumentation key.</param>
    /// <param name="leaseNamespace">Namespace for the lease. See <see cref="LeaseNamespaces"/>.</param>
    /// <param name="duration">The initial duration of the lease. May be between 15 and 60 seconds.</param>
    /// <param name="metadata">Optional metadata to include with the request for diagnostics.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The lease ID if successful.</returns>
    /// <exception cref="ArgumentException">One of the parameters is invalid.</exception>
    /// <exception cref="LeaseUnavailableException">The lease is unavailable. The maximum allowed concurrency has been reached.</exception>
    /// <exception cref="RequestFailedException">The lease could not be acquired.</exception>
    public async Task<Guid> AcquireLeaseAsync(string iKey, string leaseNamespace, TimeSpan duration, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken = default)
    {
        using HttpMessage message = CreateLeaseRequest(iKey, leaseNamespace, "acquire")
            .WithHeader(XmsHeaderNames.LeaseDuration, ((int)duration.TotalSeconds).ToString(InvariantCulture));

        if (metadata?.Count > 0)
        {
            message.Request.Content = RequestContent.Create(BinaryData.FromObjectAsJson(metadata));
        }

        await _pipeline.SendAsync(message, cancellationToken).ConfigureAwait(false);
        switch ((HttpStatusCode)message.Response.Status)
        {
            case HttpStatusCode.Created:
                if (!message.Response.Headers.TryGetValue(XmsHeaderNames.LeaseId, out string? leaseIdString))
                {
                    throw new RequestFailedException("The service did not send a lease ID in the response header.");
                }

                if (!Guid.TryParse(leaseIdString, out Guid leaseId))
                {
                    throw new RequestFailedException("The service returned a lease ID that is not a GUID.");
                }

                return leaseId;

            case HttpStatusCode.Conflict:
                throw new LeaseUnavailableException("The lease is unavailable.");

            default:
                await ThrowIfRequestFailedAsync(message.Response, cancellationToken).ConfigureAwait(false);
                throw new RequestFailedException("The service returned an unexpected response.");
        }
    }

    /// <summary>
    /// Renew a lease.
    /// </summary>
    /// <param name="iKey">The instrumentation key.</param>
    /// <param name="leaseNamespace">Namespace for the lease. See <see cref="LeaseNamespaces"/>.</param>
    /// <param name="leaseId">The lease ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the lease has been renewed.</returns>
    /// <exception cref="LeaseUnavailableException">The lease is unavailable. The most likely case is that it was lost to another caller.</exception>
    public Task RenewLeaseAsync(string iKey, string leaseNamespace, Guid leaseId, CancellationToken cancellationToken = default)
    {
        return RenewOrReleaseLeaseAsync(iKey, leaseNamespace, leaseId, "renew", cancellationToken);
    }

    /// <summary>
    /// Release the lease.
    /// </summary>
    /// <param name="iKey">The instrumentation key.</param>
    /// <param name="leaseNamespace">Namespace for the lease. See <see cref="LeaseNamespaces"/>.</param>
    /// <param name="leaseId">The lease ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the lease has been released.</returns>
    /// <exception cref="LeaseUnavailableException">The lease is unavailable. The most likely case is that it was lost to another caller.</exception>
    public Task ReleaseLeaseAsync(string iKey, string leaseNamespace, Guid leaseId, CancellationToken cancellationToken = default)
    {
        return RenewOrReleaseLeaseAsync(iKey, leaseNamespace, leaseId, "release", cancellationToken);
    }

    private async Task RenewOrReleaseLeaseAsync(string iKey, string leaseNamespace, Guid leaseId, string action, CancellationToken cancellationToken)
    {
        using HttpMessage message = CreateLeaseRequest(iKey, leaseNamespace, action)
            .WithHeader(XmsHeaderNames.LeaseId, leaseId.ToString("D", InvariantCulture));

        await _pipeline.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await ThrowIfRequestFailedAsync(message.Response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a request for uploading an artifact.
    /// </summary>
    /// <param name="iKey">The instrumentation key for the associated Application Insights resource.</param>
    /// <param name="artifactKind">The kind of artifact being uploaded.</param>
    /// <param name="artifactId">A client generated unique identifier for the artifact.</param>
    /// <param name="action">The action. One of the constant strings from <see cref="IngestionAction"/>.</param>
    /// <returns></returns>
    private HttpMessage CreateArtifactRequest(string iKey, ArtifactKind artifactKind, Guid artifactId, string action)
        => CreateRequest(RequestMethod.Post, Invariant($"/api/apps/{iKey}/artifactkinds/{artifactKind}/artifacts/{artifactId:N}"))
           .WithQuery("action", action);

    private HttpMessage CreateLeaseRequest(string iKey, string leaseNamespace, string action)
    {
        if (string.IsNullOrEmpty(iKey))
        {
            throw new ArgumentException($"'{nameof(iKey)}' cannot be null or empty.", nameof(iKey));
        }

        if (string.IsNullOrEmpty(leaseNamespace))
        {
            throw new ArgumentException($"'{nameof(leaseNamespace)}' cannot be null or empty.", nameof(leaseNamespace));
        }

        if (string.IsNullOrEmpty(action))
        {
            throw new ArgumentException($"'{nameof(action)}' cannot be null or empty.", nameof(action));
        }

        return CreateRequest(RequestMethod.Put, Invariant($"/api/apps/{iKey}/leases/{leaseNamespace}"))
            .WithHeader(XmsHeaderNames.LeaseAction, action);
    }

    private HttpMessage CreateRequest(RequestMethod method, string path)
    {
        HttpMessage message = _pipeline.CreateMessage();
        Request request = message.Request;
        request.Method = method;
        request.Headers.Add(HttpHeader.Common.JsonAccept);
        request.Headers.Add(HttpHeader.Common.JsonContentType);
        request.Uri.AppendPath(path, escape: false);
        return message;
    }

    private async ValueTask<Response<T>> SendAsync<T>(HttpMessage message, CancellationToken cancellationToken)
    {
        await _pipeline.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await ThrowIfRequestFailedAsync(message.Response, cancellationToken).ConfigureAwait(false);
        T value = await JsonSerializer.DeserializeAsync<T>(message.Response.ContentStream, s_jsonSerializerOptions, cancellationToken).ConfigureAwait(false);
        return Response.FromValue(value, message.Response);
    }

    /// <summary>
    /// Json serializer options.
    /// </summary>
    private static readonly JsonSerializerOptions s_jsonSerializerOptions = CreateJsonSerializerOptions();

    /// <summary>
    /// Create <see cref="JsonSerializerOptions"/> with case-insensitive property names
    /// and string-to-enum conversion.
    /// </summary>
    /// <returns>The serializer options.</returns>
    private static JsonSerializerOptions CreateJsonSerializerOptions()
    {
        JsonSerializerOptions jsonSerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        jsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        return jsonSerializerOptions;
    }

    private static async ValueTask ThrowIfRequestFailedAsync(Response response, CancellationToken cancellationToken)
    {
        int status = response.Status;
        if (status is >= 200 and <= 299)
        {
            return;
        }

        ErrorResponseError? error = null;
        try
        {
            ErrorResponse errorResponse = await JsonSerializer.DeserializeAsync<ErrorResponse>(response.ContentStream, s_jsonSerializerOptions, cancellationToken).ConfigureAwait(false);
            error = errorResponse.Error;
        }
        catch
        {
        }

        StringBuilder sb = new StringBuilder()
            .AppendLine("Request failed.")
            .Append("Status: ")
            .Append(status.ToString(InvariantCulture));

        if (!string.IsNullOrEmpty(response.ReasonPhrase))
        {
            sb.Append(" (").Append(response.ReasonPhrase).AppendLine(")");
        }
        else
        {
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(error?.Code))
        {
            sb.Append("ErrorCode: ").AppendLine(error!.Code);
        }

        if (!string.IsNullOrWhiteSpace(error?.Message))
        {
            sb.Append("Message: ").AppendLine(error!.Message);
        }

        throw new RequestFailedException(status, sb.ToString());
    }
}

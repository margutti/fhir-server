﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Abstractions.Exceptions;
using Microsoft.Health.CosmosDb.Configs;
using Microsoft.Health.CosmosDb.Features.Storage;
using Microsoft.Health.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.Fhir.Core.Features.Operations.Export.Models;
using Microsoft.Health.Fhir.Core.Features.Operations.Reindex.Models;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.CosmosDb.Features.Storage.Operations.Export;
using Microsoft.Health.Fhir.CosmosDb.Features.Storage.Operations.Reindex;
using Microsoft.Health.Fhir.CosmosDb.Features.Storage.StoredProcedures.AcquireExportJobs;

namespace Microsoft.Health.Fhir.CosmosDb.Features.Storage.Operations
{
    public sealed class CosmosFhirOperationDataStore : IFhirOperationDataStore
    {
        private const string HashParameterName = "@hash";

        private static readonly string GetJobByHashQuery =
            $"SELECT TOP 1 * FROM ROOT r WHERE r.{JobRecordProperties.JobRecord}.{JobRecordProperties.Hash} = {HashParameterName} AND r.{JobRecordProperties.JobRecord}.{JobRecordProperties.Status} IN ('{OperationStatus.Queued}', '{OperationStatus.Running}') ORDER BY r.{KnownDocumentProperties.Timestamp} ASC";

        private static readonly string CheckActiveJobsByStatusQuery =
            $"SELECT TOP 1 * FROM ROOT r WHERE r.{JobRecordProperties.JobRecord}.{JobRecordProperties.Status} IN ('{OperationStatus.Queued}', '{OperationStatus.Running}', '{OperationStatus.Paused}')";

        private readonly IScoped<Container> _documentClientScope;
        private readonly RetryExceptionPolicyFactory _retryExceptionPolicyFactory;
        private readonly ICosmosQueryFactory _queryFactory;
        private readonly ILogger _logger;

        private static readonly AcquireExportJobs _acquireExportJobs = new AcquireExportJobs();

        /// <summary>
        /// Initializes a new instance of the <see cref="CosmosFhirOperationDataStore"/> class.
        /// </summary>
        /// <param name="documentClientScope">The factory for <see cref="IDocumentClient"/>.</param>
        /// <param name="cosmosDataStoreConfiguration">The data store configuration.</param>
        /// <param name="namedCosmosCollectionConfigurationAccessor">The IOptions accessor to get a named version.</param>
        /// <param name="retryExceptionPolicyFactory">The retry exception policy factory.</param>
        /// <param name="queryFactory">The Query Factory</param>
        /// <param name="logger">The logger.</param>
        public CosmosFhirOperationDataStore(
            IScoped<Container> documentClientScope,
            CosmosDataStoreConfiguration cosmosDataStoreConfiguration,
            IOptionsMonitor<CosmosCollectionConfiguration> namedCosmosCollectionConfigurationAccessor,
            RetryExceptionPolicyFactory retryExceptionPolicyFactory,
            ICosmosQueryFactory queryFactory,
            ILogger<CosmosFhirOperationDataStore> logger)
        {
            EnsureArg.IsNotNull(documentClientScope, nameof(documentClientScope));
            EnsureArg.IsNotNull(cosmosDataStoreConfiguration, nameof(cosmosDataStoreConfiguration));
            EnsureArg.IsNotNull(namedCosmosCollectionConfigurationAccessor, nameof(namedCosmosCollectionConfigurationAccessor));
            EnsureArg.IsNotNull(retryExceptionPolicyFactory, nameof(retryExceptionPolicyFactory));
            EnsureArg.IsNotNull(logger, nameof(logger));

            _documentClientScope = documentClientScope;
            _retryExceptionPolicyFactory = retryExceptionPolicyFactory;
            _queryFactory = queryFactory;
            _logger = logger;

            CosmosCollectionConfiguration collectionConfiguration = namedCosmosCollectionConfigurationAccessor.Get(Constants.CollectionConfigurationName);

            DatabaseId = cosmosDataStoreConfiguration.DatabaseId;
            CollectionId = collectionConfiguration.CollectionId;
        }

        private string DatabaseId { get; }

        private string CollectionId { get; }

        public async Task<ExportJobOutcome> CreateExportJobAsync(ExportJobRecord jobRecord, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(jobRecord, nameof(jobRecord));

            var cosmosExportJob = new CosmosExportJobRecordWrapper(jobRecord);

            try
            {
                var result = await _documentClientScope.Value.CreateItemAsync(
                    cosmosExportJob,
                    new PartitionKey(CosmosDbExportConstants.ExportJobPartitionKey),
                    cancellationToken: cancellationToken);

                return new ExportJobOutcome(jobRecord, WeakETag.FromVersionId(result.Resource.ETag));
            }
            catch (CosmosException dce)
            {
                if (dce.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new RequestRateExceededException(dce.RetryAfter);
                }

                _logger.LogError(dce, "Failed to create an export job.");
                throw;
            }
        }

        public async Task<ExportJobOutcome> GetExportJobByIdAsync(string id, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNullOrWhiteSpace(id, nameof(id));

            try
            {
                ItemResponse<CosmosExportJobRecordWrapper> cosmosExportJobRecord = await _documentClientScope.Value.ReadItemAsync<CosmosExportJobRecordWrapper>(
                    id,
                    new PartitionKey(CosmosDbExportConstants.ExportJobPartitionKey),
                    cancellationToken: cancellationToken);

                var outcome = new ExportJobOutcome(cosmosExportJobRecord.Resource.JobRecord, WeakETag.FromVersionId(cosmosExportJobRecord.Resource.ETag));

                return outcome;
            }
            catch (CosmosException dce)
            {
                if (dce.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new RequestRateExceededException(dce.RetryAfter);
                }
                else if (dce.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new JobNotFoundException(string.Format(Core.Resources.JobNotFound, id));
                }

                _logger.LogError(dce, "Failed to get an export job by id.");
                throw;
            }
        }

        public async Task<ExportJobOutcome> GetExportJobByHashAsync(string hash, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNullOrWhiteSpace(hash, nameof(hash));

            try
            {
                var query = _queryFactory.Create<CosmosExportJobRecordWrapper>(
                    _documentClientScope.Value,
                    new CosmosQueryContext(
                        new QueryDefinition(GetJobByHashQuery)
                            .WithParameter(HashParameterName, hash),
                        new QueryRequestOptions { PartitionKey = new PartitionKey(CosmosDbExportConstants.ExportJobPartitionKey) }));

                FeedResponse<CosmosExportJobRecordWrapper> result = await query.ExecuteNextAsync();

                if (result.Count == 1)
                {
                    // We found an existing job that matches the hash.
                    CosmosExportJobRecordWrapper wrapper = result.First();

                    return new ExportJobOutcome(wrapper.JobRecord, WeakETag.FromVersionId(wrapper.ETag));
                }

                return null;
            }
            catch (CosmosException dce)
            {
                if (dce.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new RequestRateExceededException(dce.RetryAfter);
                }

                _logger.LogError(dce, "Failed to get an export job by hash.");
                throw;
            }
        }

        public async Task<ExportJobOutcome> UpdateExportJobAsync(ExportJobRecord jobRecord, WeakETag eTag, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(jobRecord, nameof(jobRecord));

            var cosmosExportJob = new CosmosExportJobRecordWrapper(jobRecord);
            var requestOptions = new ItemRequestOptions();

            // Create access condition so that record is replaced only if eTag matches.
            if (eTag != null)
            {
                requestOptions.IfMatchEtag = eTag.VersionId;
            }

            try
            {
                var replaceResult = await _retryExceptionPolicyFactory.CreateRetryPolicy().ExecuteAsync(
                    () => _documentClientScope.Value.ReplaceItemAsync(
                        cosmosExportJob,
                        jobRecord.Id,
                        new PartitionKey(CosmosDbExportConstants.ExportJobPartitionKey),
                        cancellationToken: cancellationToken));

                var latestETag = replaceResult.Resource.ETag;
                return new ExportJobOutcome(jobRecord, WeakETag.FromVersionId(latestETag));
            }
            catch (CosmosException dce)
            {
                if (dce.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new RequestRateExceededException(dce.RetryAfter);
                }
                else if (dce.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    throw new JobConflictException();
                }
                else if (dce.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new JobNotFoundException(string.Format(Core.Resources.JobNotFound, jobRecord.Id));
                }

                _logger.LogError(dce, "Failed to update an export job.");
                throw;
            }
        }

        public async Task<IReadOnlyCollection<ExportJobOutcome>> AcquireExportJobsAsync(
            ushort maximumNumberOfConcurrentJobsAllowed,
            TimeSpan jobHeartbeatTimeoutThreshold,
            CancellationToken cancellationToken)
        {
            try
            {
                var response = await _retryExceptionPolicyFactory.CreateRetryPolicy().ExecuteAsync(
                    async ct => await _acquireExportJobs.ExecuteAsync(
                        _documentClientScope.Value.Scripts,
                        maximumNumberOfConcurrentJobsAllowed,
                        (ushort)jobHeartbeatTimeoutThreshold.TotalSeconds,
                        ct),
                    cancellationToken);

                return response.Resource.Select(wrapper => new ExportJobOutcome(wrapper.JobRecord, WeakETag.FromVersionId(wrapper.ETag))).ToList();
            }
            catch (CosmosException dce)
            {
                if (dce.GetSubStatusCode() == HttpStatusCode.RequestEntityTooLarge)
                {
                    throw new RequestRateExceededException(null);
                }

                _logger.LogError(dce, "Failed to acquire export jobs.");
                throw;
            }
        }

        public async Task<ReindexJobWrapper> CreateReindexJobAsync(ReindexJobRecord jobRecord, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(jobRecord, nameof(jobRecord));

            var cosmosReindexJob = new CosmosReindexJobRecordWrapper(jobRecord);

            try
            {
                var result = await _documentClientScope.Value.CreateItemAsync(
                    cosmosReindexJob,
                    new PartitionKey(CosmosDbReindexConstants.ReindexJobPartitionKey),
                    cancellationToken: cancellationToken);

                return new ReindexJobWrapper(jobRecord, WeakETag.FromVersionId(result.Resource.ETag));
            }
            catch (CosmosException dce)
            {
                if (dce.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new RequestRateExceededException(dce.RetryAfter);
                }

                _logger.LogError(dce, "Failed to create an reindex job.");
                throw;
            }
        }

        public async Task<IReadOnlyCollection<ReindexJobWrapper>> AcquireReindexJobsAsync(ushort maximumNumberOfConcurrentJobsAllowed, TimeSpan jobHeartbeatTimeoutThreshold, CancellationToken cancellationToken)
        {
            // TODO: Shell for testing

            var query = _queryFactory.Create<CosmosReindexJobRecordWrapper>(
                _documentClientScope.Value,
                new CosmosQueryContext(
                    new QueryDefinition(CheckActiveJobsByStatusQuery),
                    new QueryRequestOptions { PartitionKey = new PartitionKey(CosmosDbReindexConstants.ReindexJobPartitionKey) }));

            FeedResponse<CosmosReindexJobRecordWrapper> result = await query.ExecuteNextAsync();
            var jobList = new List<ReindexJobWrapper>();
            CosmosReindexJobRecordWrapper cosmosJob = result.FirstOrDefault();
            if (cosmosJob != null)
            {
                jobList.Add(new ReindexJobWrapper(cosmosJob.JobRecord, WeakETag.FromVersionId(cosmosJob.ETag)));
            }

            return jobList;
        }

        public async Task<bool> CheckActiveReindexJobsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var query = _queryFactory.Create<CosmosReindexJobRecordWrapper>(
                    _documentClientScope.Value,
                    new CosmosQueryContext(
                        new QueryDefinition(CheckActiveJobsByStatusQuery),
                        new QueryRequestOptions { PartitionKey = new PartitionKey(CosmosDbReindexConstants.ReindexJobPartitionKey) }));

                FeedResponse<CosmosReindexJobRecordWrapper> result = await query.ExecuteNextAsync();

                if (result.Any())
                {
                    return true;
                }

                return false;
            }
            catch (CosmosException dce)
            {
                if (dce.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new RequestRateExceededException(dce.RetryAfter);
                }

                _logger.LogError(dce, "Failed to check if any reindex jobs are active.");
                throw;
            }
        }

        public Task<ReindexJobWrapper> GetReindexJobByIdAsync(string jobId, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public async Task<ReindexJobWrapper> UpdateReindexJobAsync(ReindexJobRecord jobRecord, WeakETag eTag, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(jobRecord, nameof(jobRecord));

            var cosmosReindexJob = new CosmosReindexJobRecordWrapper(jobRecord);
            var requestOptions = new ItemRequestOptions();

            // Create access condition so that record is replaced only if eTag matches.
            if (eTag != null)
            {
                requestOptions.IfMatchEtag = eTag.VersionId;
            }

            try
            {
                var replaceResult = await _retryExceptionPolicyFactory.CreateRetryPolicy().ExecuteAsync(
                    () => _documentClientScope.Value.ReplaceItemAsync(
                        cosmosReindexJob,
                        jobRecord.Id,
                        new PartitionKey(CosmosDbReindexConstants.ReindexJobPartitionKey),
                        requestOptions,
                        cancellationToken));

                var latestETag = replaceResult.Resource.ETag;
                return new ReindexJobWrapper(jobRecord, WeakETag.FromVersionId(latestETag));
            }
            catch (CosmosException dce)
            {
                if (dce.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new RequestRateExceededException(dce.RetryAfter);
                }
                else if (dce.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    throw new JobConflictException();
                }
                else if (dce.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new JobNotFoundException(string.Format(Core.Resources.JobNotFound, jobRecord.Id));
                }

                _logger.LogError(dce, "Failed to update a reindex job.");
                throw;
            }
        }
    }
}

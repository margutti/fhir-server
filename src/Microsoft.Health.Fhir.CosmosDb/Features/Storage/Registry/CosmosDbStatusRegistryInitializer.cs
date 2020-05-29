// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Linq;
using Microsoft.Extensions.Options;
using Microsoft.Health.Abstractions.Exceptions;
using Microsoft.Health.CosmosDb.Configs;
using Microsoft.Health.CosmosDb.Features.Storage;
using Microsoft.Health.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Core.Features.Search.Registry;

namespace Microsoft.Health.Fhir.CosmosDb.Features.Storage.Registry
{
    public class CosmosDbStatusRegistryInitializer : IStatusRegistryInitializer
    {
        private readonly IStatusRegistryDataStore _filebasedRegistry;
        private readonly IScoped<IDocumentClient> _documentClientScope;
        private readonly RetryExceptionPolicyFactory _retryExceptionPolicyFactory; // TODO: remove if unused.

        public CosmosDbStatusRegistryInitializer(
            FilebasedSearchParameterRegistryDataStore.Resolver filebasedRegistry,
            IScoped<IDocumentClient> documentClientScope,
            CosmosDataStoreConfiguration cosmosDataStoreConfiguration,
            IOptionsMonitor<CosmosCollectionConfiguration> namedCosmosCollectionConfigurationAccessor,
            RetryExceptionPolicyFactory retryExceptionPolicyFactory)
        {
            EnsureArg.IsNotNull(filebasedRegistry, nameof(filebasedRegistry));
            EnsureArg.IsNotNull(documentClientScope, nameof(documentClientScope));
            EnsureArg.IsNotNull(cosmosDataStoreConfiguration, nameof(cosmosDataStoreConfiguration));
            EnsureArg.IsNotNull(namedCosmosCollectionConfigurationAccessor, nameof(namedCosmosCollectionConfigurationAccessor));
            EnsureArg.IsNotNull(retryExceptionPolicyFactory, nameof(retryExceptionPolicyFactory));

            _filebasedRegistry = filebasedRegistry.Invoke();
            _documentClientScope = documentClientScope;
            _retryExceptionPolicyFactory = retryExceptionPolicyFactory;

            CosmosCollectionConfiguration collectionConfiguration = namedCosmosCollectionConfigurationAccessor.Get(Constants.CollectionConfigurationName);

            CollectionUri = cosmosDataStoreConfiguration.GetRelativeCollectionUri(collectionConfiguration.CollectionId);
        }

        private Uri CollectionUri { get; }

        public async Task EnsureInitialized()
        {
            try
            {
                // Detect if registry has been initialized
                IDocumentQuery<dynamic> query = _documentClientScope.Value.CreateDocumentQuery<dynamic>(
                        CollectionUri,
                        new SqlQuerySpec($"SELECT TOP 1 * FROM c where c.{KnownDocumentProperties.PartitionKey} = '{SearchParameterStatusWrapper.SearchParameterStatusPartitionKey}'"))
                    .AsDocumentQuery();

                var results = await query.ExecuteNextAsync();

                if (!results.Any())
                {
                    var statuses = await _filebasedRegistry.GetSearchParameterStatuses();

                    foreach (SearchParameterStatusWrapper status in statuses.Select(x => x.ToSearchParameterStatusWrapper()))
                    {
                        await _documentClientScope.Value.UpsertDocumentAsync(CollectionUri, status);
                    }
                }
            }
            catch (DocumentClientException dce)
            {
                // TODO: Catch other exceptions?
                if (dce.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new RequestRateExceededException(dce.RetryAfter);
                }

                throw;
            }
        }
    }
}

﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Health.CosmosDb.Configs;
using Microsoft.Health.CosmosDb.Features.Storage;
using Microsoft.Health.CosmosDb.Features.Storage.Versioning;
using Microsoft.Health.Fhir.CosmosDb.Features.Storage.Versioning;
using NSubstitute;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.CosmosDb.UnitTests.Features.Storage.Versioning
{
    public class CollectionUpgradeManagerTests
    {
        private readonly CosmosDataStoreConfiguration _cosmosDataStoreConfiguration = new CosmosDataStoreConfiguration
        {
            AllowDatabaseCreation = false,
            ConnectionMode = ConnectionMode.Direct,
            DatabaseId = "testdatabaseid",
            Host = "https://fakehost",
            Key = "ZmFrZWtleQ==",   // "fakekey"
            PreferredLocations = null,
        };

        private readonly CosmosCollectionConfiguration _cosmosCollectionConfiguration = new CosmosCollectionConfiguration
        {
            CollectionId = "testcollectionid",
        };

        private readonly FhirCollectionUpgradeManager _manager;
        private readonly Container _client;

        public CollectionUpgradeManagerTests()
        {
            var factory = Substitute.For<ICosmosDbDistributedLockFactory>();
            var cosmosDbDistributedLock = Substitute.For<ICosmosDbDistributedLock>();
            var optionsMonitor = Substitute.For<IOptionsMonitor<CosmosCollectionConfiguration>>();

            optionsMonitor.Get(Constants.CollectionConfigurationName).Returns(_cosmosCollectionConfiguration);

            factory.Create(Arg.Any<Container>(), Arg.Any<string>()).Returns(cosmosDbDistributedLock);
            cosmosDbDistributedLock.TryAcquireLock().Returns(true);

            _client = Substitute.For<Container>();

            var collectionVersionWrappers = Substitute.ForPartsOf<FeedIterator<CollectionVersion>>();

            _client.GetItemQueryIterator<CollectionVersion>(Arg.Any<QueryDefinition>())
                .Returns(collectionVersionWrappers);

            collectionVersionWrappers.ReadNextAsync()
                .Returns(Substitute.ForPartsOf<FeedResponse<CollectionVersion>>());

            var updaters = new IFhirCollectionUpdater[] { new FhirCollectionSettingsUpdater(_cosmosDataStoreConfiguration, optionsMonitor, NullLogger<FhirCollectionSettingsUpdater>.Instance), };
            _manager = new FhirCollectionUpgradeManager(updaters, _cosmosDataStoreConfiguration, optionsMonitor, factory, NullLogger<FhirCollectionUpgradeManager>.Instance);
        }

        [Fact]
        public async Task GivenACollection_WhenSettingUpCollection_ThenTheCollectionIndexIsUpdated()
        {
            await UpdateCollectionAsync();

            await _client.Received(1).ReplaceContainerAsync(Arg.Any<ContainerProperties>());
        }

        [Fact]
        public async Task GivenACollection_WhenSettingUpCollection_ThenTheCollectionVersionWrapperIsSaved()
        {
            await UpdateCollectionAsync();

            await _client.Received(1).UpsertItemAsync(Arg.Is<CollectionVersion>(x => x.Version == _manager.CollectionSettingsVersion));
        }

        [Fact]
        public async Task GivenACollection_WhenSettingUpCollection_ThenTheCollectionTTLIsSetToNeg1()
        {
            ContainerResponse containerResponse = Substitute.ForPartsOf<ContainerResponse>();
            _client.ReadContainerAsync().Returns(containerResponse);

            await UpdateCollectionAsync();

            Assert.Equal(-1, containerResponse.Resource.DefaultTimeToLive);
        }

        private async Task UpdateCollectionAsync()
        {
            await _manager.SetupCollectionAsync(_client);
        }
    }
}

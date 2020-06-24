﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Health.Core;
using Microsoft.Health.CosmosDb.Features.Queries;
using Microsoft.Health.CosmosDb.Features.Storage;
using Microsoft.Health.Fhir.Core.Features.Search.Registry;
using Microsoft.Health.Fhir.CosmosDb.Features.Storage.Registry;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.CosmosDb.UnitTests.Features.Storage.Registry
{
    public class CosmosDbStatusRegistryInitializerTests
    {
        private readonly CosmosDbStatusRegistryInitializer _initializer;
        private readonly ICosmosQueryFactory _cosmosDocumentQueryFactory;
        private readonly Uri _testParameterUri;

        public CosmosDbStatusRegistryInitializerTests()
        {
            ISearchParameterRegistry searchParameterRegistry = Substitute.For<ISearchParameterRegistry>();
            _cosmosDocumentQueryFactory = Substitute.For<ICosmosQueryFactory>();

            _initializer = new CosmosDbStatusRegistryInitializer(
                () => searchParameterRegistry,
                _cosmosDocumentQueryFactory);

            _testParameterUri = new Uri("/test", UriKind.Relative);
            searchParameterRegistry
                .GetSearchParameterStatuses()
                .Returns(new[]
                {
                    new ResourceSearchParameterStatus
                    {
                      Uri = _testParameterUri,
                      Status = SearchParameterStatus.Enabled,
                      LastUpdated = Clock.UtcNow,
                      IsPartiallySupported = false,
                    },
                });
        }

        [Fact]
        public async Task GivenARegistryInitializer_WhenDatabaseIsNew_SearchParametersShouldBeUpserted()
        {
            ICosmosQuery<dynamic> documentQuery = Substitute.For<ICosmosQuery<dynamic>>();
            _cosmosDocumentQueryFactory.Create<dynamic>(Arg.Any<Container>(), Arg.Any<CosmosQueryContext>())
                .Returns(documentQuery);

            documentQuery
                .ExecuteNextAsync()
                .Returns(Substitute.ForPartsOf<FeedResponse<dynamic>>());

            Container documentClient = Substitute.For<Container>();
            var relativeCollectionUri = new Uri("/collection1", UriKind.Relative);

            await _initializer.ExecuteAsync(documentClient);

            await documentClient.Received().UpsertItemAsync(
                Arg.Is<SearchParameterStatusWrapper>(x => x.Uri == _testParameterUri));
        }

        [Fact]
        public async Task GivenARegistryInitializer_WhenDatabaseIsExisting_NothingNeedsToBeDone()
        {
            ICosmosQuery<dynamic> documentQuery = Substitute.For<ICosmosQuery<dynamic>>();
            _cosmosDocumentQueryFactory.Create<object>(Arg.Any<Container>(), Arg.Any<CosmosQueryContext>())
                .Returns(documentQuery);

            var response = Substitute.ForPartsOf<FeedResponse<object>>();
            response.GetEnumerator()
                .Returns(new object[] { new SearchParameterStatusWrapper() }.GetEnumerator());

            documentQuery
                .ExecuteNextAsync()
                .Returns(info => response);

            Container documentClient = Substitute.For<Container>();

            await _initializer.ExecuteAsync(documentClient);

            await documentClient.DidNotReceive().UpsertItemAsync(
                Arg.Any<SearchParameterStatusWrapper>());
        }
    }
}
